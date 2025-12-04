using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using BepInEx;
using BepInEx.Logging;
using BewasModSync.Models;
using Comfort.Common;
using EFT.UI;
using Newtonsoft.Json;
using UnityEngine;

namespace BewasModSync
{
    [BepInPlugin("com.bewas.modsync.client", "BewasModSync", "1.0.0")]
    public class BewasModSyncPlugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;

        private static readonly string SptRoot = Path.GetDirectoryName(Application.dataPath);
        // Config files are stored in BewasModSync subfolder
        private static readonly string SyncDataFolder = Path.Combine(SptRoot, "BewasModSync");
        private static readonly string ConfigPath = Path.Combine(SyncDataFolder, "BewasModSyncClient.json");
        private static readonly string ModsDownloadedPath = Path.Combine(SyncDataFolder, "modsDownloaded.json");

        private void Awake()
        {
            LogSource = Logger;
            LogSource.LogInfo("BewasModSync Client loaded!");

            // Accept self-signed SSL certificates (SPT 4.0 uses HTTPS)
            ServicePointManager.ServerCertificateValidationCallback = AcceptAllCertificates;
        }

        private void Start()
        {
            // Start verification coroutine
            StartCoroutine(VerifyModsCoroutine());
        }

        private static bool AcceptAllCertificates(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            return true; // Accept all certificates for SPT's self-signed cert
        }

        private IEnumerator VerifyModsCoroutine()
        {
            LogSource.LogInfo("BewasModSync: Starting mod verification...");

            // Run verification
            bool setupRequired;
            var issues = VerifyMods(out setupRequired);

            // If setup is required, show setup warning
            if (setupRequired)
            {
                LogSource.LogError("========================================");
                LogSource.LogError("BewasModSync: SETUP REQUIRED!");
                LogSource.LogError("========================================");
                LogSource.LogError("BewasModSync has not been set up yet.");
                LogSource.LogError("Please run the BewasModSync.exe tool to sync your mods.");
                LogSource.LogError("========================================");

                // Wait for UI to be ready
                yield return new WaitUntil(() => Singleton<CommonUI>.Instantiated);
                ShowSetupRequiredWarning();
                yield break;
            }

            if (issues.Count == 0)
            {
                LogSource.LogInfo("BewasModSync: All required mods verified successfully!");
                yield break;
            }

            // Log issues
            LogSource.LogError("========================================");
            LogSource.LogError("BewasModSync: MOD SYNC ISSUES DETECTED!");
            LogSource.LogError("========================================");
            foreach (var issue in issues)
            {
                LogSource.LogError($"  - {issue}");
            }
            LogSource.LogError("Please run the BewasModSync tool to fix these issues.");
            LogSource.LogError("========================================");

            // Wait for UI to be ready before showing warning
            LogSource.LogInfo("BewasModSync: Waiting for game UI to initialize...");
            yield return new WaitUntil(() => Singleton<CommonUI>.Instantiated);
            LogSource.LogInfo("BewasModSync: UI ready, showing warning...");

            // Show popup to user
            ShowSyncWarning(issues);
        }

        private List<string> VerifyMods(out bool setupRequired)
        {
            var issues = new List<string>();
            setupRequired = false;

            try
            {
                // Check if sync folder exists
                if (!Directory.Exists(SyncDataFolder))
                {
                    LogSource.LogWarning($"BewasModSync: Sync folder not found at {SyncDataFolder}. Run the sync tool first.");
                    setupRequired = true;
                    return issues;
                }

                // Check if we have client config
                if (!File.Exists(ConfigPath))
                {
                    LogSource.LogWarning("BewasModSync: No client config found. Run the sync tool first.");
                    setupRequired = true;
                    return issues;
                }

                // Check if we have mods downloaded list
                if (!File.Exists(ModsDownloadedPath))
                {
                    LogSource.LogWarning("BewasModSync: No mods downloaded list found. Run the sync tool first.");
                    setupRequired = true;
                    return issues;
                }

                var clientConfig = JsonConvert.DeserializeObject<ClientConfig>(File.ReadAllText(ConfigPath));
                if (clientConfig == null || string.IsNullOrWhiteSpace(clientConfig.ServerUrl))
                {
                    LogSource.LogWarning("BewasModSync: Invalid client config.");
                    return issues;
                }

                var modsDownloaded = JsonConvert.DeserializeObject<List<DownloadedMod>>(File.ReadAllText(ModsDownloadedPath));
                if (modsDownloaded == null)
                {
                    modsDownloaded = new List<DownloadedMod>();
                }

                // Try to fetch server config
                ServerConfig serverConfig = null;
                try
                {
                    serverConfig = FetchServerConfig(clientConfig.ServerUrl);
                }
                catch (Exception ex)
                {
                    LogSource.LogWarning($"BewasModSync: Could not connect to server for verification: {ex.Message}");
                    // Continue with local verification only
                }

                if (serverConfig != null)
                {
                    // Verify required mods
                    var requiredMods = serverConfig.ModList.Where(m => !m.Optional).ToList();

                    foreach (var requiredMod in requiredMods)
                    {
                        var downloaded = modsDownloaded.FirstOrDefault(d => d.DownloadUrl == requiredMod.DownloadUrl);

                        if (downloaded == null)
                        {
                            issues.Add($"Missing required mod: {requiredMod.ModName}");
                            continue;
                        }

                        if (downloaded.LastUpdated != requiredMod.LastUpdated)
                        {
                            issues.Add($"Outdated mod: {requiredMod.ModName}");
                            continue;
                        }

                        // Verify sync paths exist
                        foreach (var syncPath in requiredMod.SyncPaths)
                        {
                            var targetPath = syncPath[1].Replace("<SPT_ROOT>", SptRoot);
                            
                            if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
                            {
                                issues.Add($"Missing files for mod: {requiredMod.ModName}");
                                break;
                            }
                        }
                    }
                }
                else
                {
                    // Offline mode - just verify local files exist
                    LogSource.LogInfo("BewasModSync: Running in offline mode. Skipping server verification.");
                }
            }
            catch (Exception ex)
            {
                LogSource.LogError($"BewasModSync: Error during verification: {ex.Message}");
            }

            return issues;
        }

        private ServerConfig FetchServerConfig(string serverUrl)
        {
            // Ensure no trailing slash
            serverUrl = serverUrl.TrimEnd('/');
            var url = $"{serverUrl}/bewasmodsync/api/config";

            using (var client = new WebClient())
            {
                client.Headers.Add("User-Agent", "BewasModSync/1.0");
                var json = client.DownloadString(url);
                return JsonConvert.DeserializeObject<ServerConfig>(json);
            }
        }

        private void ShowSyncWarning(List<string> issues)
        {
            // Create a simple GUI warning that will be shown
            var warningObject = new GameObject("BewasModSyncWarning");
            var warning = warningObject.AddComponent<SyncWarningGui>();
            warning.Issues = issues;
            DontDestroyOnLoad(warningObject);
        }

        private void ShowSetupRequiredWarning()
        {
            // Create setup required warning
            var warningObject = new GameObject("BewasModSyncSetup");
            warningObject.AddComponent<SetupRequiredGui>();
            DontDestroyOnLoad(warningObject);
        }
    }

    public class SyncWarningGui : MonoBehaviour
    {
        private static readonly string SptRoot = Path.GetDirectoryName(Application.dataPath);
        private static readonly string SyncToolPath = Path.Combine(SptRoot, "BewasModSync", "BewasModSync.exe");
        
        public List<string> Issues = new List<string>();
        private bool _showWarning = true;
        private bool _syncToolExists;
        private Rect _windowRect;

        private void Start()
        {
            // Center the window
            _windowRect = new Rect(Screen.width / 2 - 325, Screen.height / 2 - 175, 650, 380);
            
            // Check if sync tool exists
            _syncToolExists = File.Exists(SyncToolPath);
        }

        private void Update()
        {
            // Hide game UI while our warning is showing
            if (_showWarning && Issues.Count > 0)
            {
                HideGameUI(true);
            }
        }

        private void OnGUI()
        {
            if (!_showWarning || Issues.Count == 0) return;

            // Wait for CommonUI to be ready
            if (!Singleton<CommonUI>.Instantiated) return;

            // Dark background overlay
            GUI.color = new Color(0, 0, 0, 0.85f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            // Draw the window
            _windowRect = GUI.Window(12345, _windowRect, DrawWindow, "");
        }

        private void DrawWindow(int windowId)
        {
            // Title bar style
            var titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 0.4f, 0.4f) }
            };

            // Header style
            var headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.85f, 0.4f) }
            };

            // Body text style
            var bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                wordWrap = true,
                normal = { textColor = Color.white }
            };

            // Issue style
            var issueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                wordWrap = true,
                normal = { textColor = new Color(1f, 0.6f, 0.6f) }
            };

            GUILayout.Space(15);
            GUILayout.Label("⚠ BewasModSync - Sync Required!", titleStyle);
            GUILayout.Space(15);

            GUILayout.Label("Your mods are out of sync with the server!", headerStyle);
            GUILayout.Space(10);

            GUILayout.Label("Issues detected:", bodyStyle);
            GUILayout.Space(5);

            // Show issues with bullets
            foreach (var issue in Issues.Take(6))
            {
                GUILayout.Label($"  • {issue}", issueStyle);
            }

            if (Issues.Count > 6)
            {
                GUILayout.Label($"  ... and {Issues.Count - 6} more issues", issueStyle);
            }

            GUILayout.Space(10);
            GUILayout.Label("Run the BewasModSync tool to fix these issues.", bodyStyle);

            GUILayout.FlexibleSpace();

            // Buttons
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            var buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold
            };

            if (GUILayout.Button("Continue Anyway", buttonStyle, GUILayout.Width(130), GUILayout.Height(35)))
            {
                _showWarning = false;
                HideGameUI(false);
            }

            GUILayout.Space(15);

            // Run Sync Tool button (only if the exe exists)
            if (_syncToolExists)
            {
                if (GUILayout.Button("Run Sync Tool & Exit", buttonStyle, GUILayout.Width(160), GUILayout.Height(35)))
                {
                    LaunchSyncToolAndQuit();
                }
                GUILayout.Space(15);
            }

            if (GUILayout.Button("Quit Game", buttonStyle, GUILayout.Width(110), GUILayout.Height(35)))
            {
                Application.Quit();
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(15);
        }

        private void LaunchSyncToolAndQuit()
        {
            try
            {
                BewasModSyncPlugin.LogSource.LogInfo($"Launching sync tool: {SyncToolPath}");
                
                var startInfo = new ProcessStartInfo
                {
                    FileName = SyncToolPath,
                    WorkingDirectory = Path.GetDirectoryName(SyncToolPath),
                    UseShellExecute = true // Use shell to open in new window
                };

                Process.Start(startInfo);
                Application.Quit();
            }
            catch (Exception ex)
            {
                BewasModSyncPlugin.LogSource.LogError($"Failed to launch sync tool: {ex.Message}");
            }
        }

        private void HideGameUI(bool hide)
        {
            try
            {
                if (Singleton<LoginUI>.Instantiated)
                {
                    Singleton<LoginUI>.Instance.gameObject.SetActive(!hide);
                }
                if (Singleton<PreloaderUI>.Instantiated)
                {
                    Singleton<PreloaderUI>.Instance.gameObject.SetActive(!hide);
                }
                if (Singleton<CommonUI>.Instantiated)
                {
                    Singleton<CommonUI>.Instance.gameObject.SetActive(!hide);
                }
            }
            catch
            {
                // Ignore errors when manipulating UI
            }
        }

        private void OnDestroy()
        {
            // Make sure game UI is visible when we're destroyed
            HideGameUI(false);
        }
    }

    /// <summary>
    /// Shows a warning that BewasModSync needs to be set up before playing.
    /// This does NOT allow "Continue Anyway" - the user must run the sync tool first.
    /// </summary>
    public class SetupRequiredGui : MonoBehaviour
    {
        private static readonly string SptRoot = Path.GetDirectoryName(Application.dataPath);
        private static readonly string SyncToolPath = Path.Combine(SptRoot, "BewasModSync", "BewasModSync.exe");
        
        private Rect _windowRect;
        private bool _syncToolExists;

        private void Start()
        {
            // Center the window
            _windowRect = new Rect(Screen.width / 2 - 275, Screen.height / 2 - 175, 550, 350);
            
            // Check if sync tool exists
            _syncToolExists = File.Exists(SyncToolPath);
        }

        private void Update()
        {
            // Hide game UI while our warning is showing
            HideGameUI(true);
        }

        private void OnGUI()
        {
            // Wait for CommonUI to be ready
            if (!Singleton<CommonUI>.Instantiated) return;

            // Dark background overlay
            GUI.color = new Color(0, 0, 0, 0.9f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            // Draw the window
            _windowRect = GUI.Window(12346, _windowRect, DrawWindow, "");
        }

        private void DrawWindow(int windowId)
        {
            // Title style
            var titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 24,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 0.5f, 0.2f) }
            };

            // Header style
            var headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };

            // Body text style
            var bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                wordWrap = true,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };

            // Path style
            var pathStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.5f, 0.8f, 1f) }
            };

            GUILayout.Space(20);
            GUILayout.Label("⚠ BewasModSync - Setup Required", titleStyle);
            GUILayout.Space(20);

            GUILayout.Label("This server requires BewasModSync", headerStyle);
            GUILayout.Space(15);

            GUILayout.Label("Before you can play, you need to run the sync tool\nto download the required mods.", bodyStyle);
            GUILayout.Space(15);

            GUILayout.Label("Run BewasModSync.exe in <SPT_ROOT>\\BewasModSync. If missing, reinstall the mod.", bodyStyle);
            GUILayout.Space(5);
            GUILayout.Label("<SPT_ROOT>\\BewasModSync\\BewasModSync.exe", pathStyle);

            GUILayout.FlexibleSpace();

            // Buttons
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            var buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold
            };

            // Run Sync Tool button (only if the exe exists)
            if (_syncToolExists)
            {
                if (GUILayout.Button("Exit & Run Sync Tool", buttonStyle, GUILayout.Width(180), GUILayout.Height(40)))
                {
                    LaunchSyncToolAndQuit();
                }
                GUILayout.Space(20);
            }

            if (GUILayout.Button("Quit Game", buttonStyle, GUILayout.Width(140), GUILayout.Height(40)))
            {
                Application.Quit();
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(20);
        }

        private void LaunchSyncToolAndQuit()
        {
            try
            {
                BewasModSyncPlugin.LogSource.LogInfo($"Launching sync tool: {SyncToolPath}");
                
                var startInfo = new ProcessStartInfo
                {
                    FileName = SyncToolPath,
                    WorkingDirectory = Path.GetDirectoryName(SyncToolPath),
                    UseShellExecute = true // Use shell to open in new window
                };

                Process.Start(startInfo);
                Application.Quit();
            }
            catch (Exception ex)
            {
                BewasModSyncPlugin.LogSource.LogError($"Failed to launch sync tool: {ex.Message}");
            }
        }

        private void HideGameUI(bool hide)
        {
            try
            {
                if (Singleton<LoginUI>.Instantiated)
                {
                    Singleton<LoginUI>.Instance.gameObject.SetActive(!hide);
                }
                if (Singleton<PreloaderUI>.Instantiated)
                {
                    Singleton<PreloaderUI>.Instance.gameObject.SetActive(!hide);
                }
                if (Singleton<CommonUI>.Instantiated)
                {
                    Singleton<CommonUI>.Instance.gameObject.SetActive(!hide);
                }
            }
            catch
            {
                // Ignore errors when manipulating UI
            }
        }

        private void OnDestroy()
        {
            HideGameUI(false);
        }
    }
}
