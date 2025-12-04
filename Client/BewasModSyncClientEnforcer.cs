using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BepInEx;
using BepInEx.Logging;
using BewasModSync.ClientEnforcer.Models;
using Comfort.Common;
using EFT.UI;
using Newtonsoft.Json;
using UnityEngine;

namespace BewasModSync.ClientEnforcer
{
    [BepInPlugin("com.bewas.modsync.clientenforcer", "BewasModSync Client Enforcer", "1.0.0")]
    public class BewasModSyncClientEnforcerPlugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;

        private static readonly string SptRoot = Path.GetDirectoryName(Application.dataPath);
        
        // Config files are stored in BewasModSyncInternalData folder
        private static readonly string InternalDataFolder = Path.Combine(SptRoot, "BewasModSyncInternalData");
        private static readonly string ConfigPath = Path.Combine(InternalDataFolder, "BewasModSyncClient.json");
        private static readonly string ModsDownloadedPath = Path.Combine(InternalDataFolder, "modsDownloaded.json");
        
        // Mod updater exe is at SPT root
        private static readonly string UpdaterExePath = Path.Combine(SptRoot, "BewasModUpdater.exe");

        // Directories to scan for extra files
        private static readonly string BepInExPluginsPath = Path.Combine(SptRoot, "BepInEx", "plugins");
        private static readonly string SptUserModsPath = Path.Combine(SptRoot, "SPT", "user", "mods");

        private void Awake()
        {
            LogSource = Logger;
            LogSource.LogInfo("BewasModSync Client Enforcer loaded!");

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
                LogSource.LogError("Please run BewasModUpdater.exe to sync your mods.");
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

            // Log issues by category
            var missingFiles = issues.Where(i => i.Type == FileIssueType.Missing).ToList();
            var hashMismatches = issues.Where(i => i.Type == FileIssueType.HashMismatch).ToList();
            var extraFiles = issues.Where(i => i.Type == FileIssueType.ExtraFile).ToList();

            LogSource.LogError("========================================");
            LogSource.LogError("BewasModSync: FILE VERIFICATION ISSUES DETECTED!");
            LogSource.LogError("========================================");

            if (missingFiles.Any())
            {
                LogSource.LogError($"Missing Files ({missingFiles.Count}):");
                foreach (var issue in missingFiles)
                {
                    LogSource.LogError($"  - [{issue.ModName}] {issue.FilePath}");
                }
            }

            if (hashMismatches.Any())
            {
                LogSource.LogWarning($"Modified Files ({hashMismatches.Count}):");
                foreach (var issue in hashMismatches)
                {
                    LogSource.LogWarning($"  - [{issue.ModName}] {issue.FilePath}");
                }
            }

            if (extraFiles.Any())
            {
                LogSource.LogInfo($"Extra Files ({extraFiles.Count}):");
                foreach (var issue in extraFiles)
                {
                    LogSource.LogInfo($"  - {issue.FilePath}");
                }
            }

            LogSource.LogError("========================================");

            // Wait for UI to be ready before showing warning
            LogSource.LogInfo("BewasModSync: Waiting for game UI to initialize...");
            yield return new WaitUntil(() => Singleton<CommonUI>.Instantiated);
            LogSource.LogInfo("BewasModSync: UI ready, showing warning...");

            // Show popup to user - includes extra files warning
            ShowSyncWarning(issues);
        }

        private List<FileIssue> VerifyMods(out bool setupRequired)
        {
            var issues = new List<FileIssue>();
            setupRequired = false;

            try
            {
                // Check if internal data folder exists
                if (!Directory.Exists(InternalDataFolder))
                {
                    LogSource.LogWarning($"BewasModSync: Internal data folder not found at {InternalDataFolder}. Run BewasModUpdater.exe first.");
                    setupRequired = true;
                    return issues;
                }

                // Check if we have client config
                if (!File.Exists(ConfigPath))
                {
                    LogSource.LogWarning("BewasModSync: No client config found. Run BewasModUpdater.exe first.");
                    setupRequired = true;
                    return issues;
                }

                var clientConfig = JsonConvert.DeserializeObject<ClientConfig>(File.ReadAllText(ConfigPath));
                if (clientConfig == null || string.IsNullOrWhiteSpace(clientConfig.ServerUrl))
                {
                    LogSource.LogWarning("BewasModSync: Invalid client config.");
                    return issues;
                }

                // Try to fetch manifest from server
                FileManifest manifest = null;
                try
                {
                    manifest = FetchManifest(clientConfig.ServerUrl);
                    LogSource.LogInfo($"BewasModSync: Fetched manifest with {manifest.Files.Count} files (generated in {manifest.GenerationTimeMs}ms)");
                }
                catch (Exception ex)
                {
                    LogSource.LogWarning($"BewasModSync: Could not fetch manifest from server: {ex.Message}");
                    // Fall back to legacy verification
                    return LegacyVerifyMods(clientConfig);
                }

                // Verify files from manifest
                issues.AddRange(VerifyManifestFiles(manifest));

                // Scan for extra files
                issues.AddRange(ScanForExtraFiles(manifest));
            }
            catch (Exception ex)
            {
                LogSource.LogError($"BewasModSync: Error during verification: {ex.Message}");
            }

            return issues;
        }

        private FileManifest FetchManifest(string serverUrl)
        {
            serverUrl = serverUrl.TrimEnd('/');
            var url = $"{serverUrl}/bewasmodsync/api/manifest";

            using (var client = new WebClient())
            {
                client.Headers.Add("User-Agent", "BewasModSync/1.0");
                var json = client.DownloadString(url);
                return JsonConvert.DeserializeObject<FileManifest>(json);
            }
        }

        private List<FileIssue> VerifyManifestFiles(FileManifest manifest)
        {
            var issues = new List<FileIssue>();

            foreach (var kvp in manifest.Files)
            {
                var relativePath = kvp.Key;
                var entry = kvp.Value;

                // Build full path
                var fullPath = Path.Combine(SptRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

                // Check if file exists
                if (!File.Exists(fullPath))
                {
                    issues.Add(new FileIssue
                    {
                        Type = FileIssueType.Missing,
                        FilePath = relativePath,
                        ModName = entry.ModName,
                        Required = entry.Required,
                        Details = "File not found"
                    });
                    continue;
                }

                // Check hash
                try
                {
                    var localHash = ComputeFileHash(fullPath);
                    if (!string.Equals(localHash, entry.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new FileIssue
                        {
                            Type = FileIssueType.HashMismatch,
                            FilePath = relativePath,
                            ModName = entry.ModName,
                            Required = entry.Required,
                            Details = $"Hash mismatch (local: {localHash.Substring(0, 8)}..., expected: {entry.Hash.Substring(0, 8)}...)"
                        });
                    }
                }
                catch (Exception ex)
                {
                    LogSource.LogWarning($"BewasModSync: Failed to hash file '{relativePath}': {ex.Message}");
                }
            }

            return issues;
        }

        private List<FileIssue> ScanForExtraFiles(FileManifest manifest)
        {
            var issues = new List<FileIssue>();

            // Build set of expected file paths (normalized)
            var expectedFiles = new HashSet<string>(
                manifest.Files.Keys.Select(p => NormalizePath(Path.Combine(SptRoot, p))),
                StringComparer.OrdinalIgnoreCase);

            // Scan BepInEx/plugins for .dll files
            if (Directory.Exists(BepInExPluginsPath))
            {
                ScanDirectoryForExtraFiles(BepInExPluginsPath, "*.dll", expectedFiles, issues, "BepInEx/plugins");
            }

            // Scan SPT/user/mods for .dll files
            if (Directory.Exists(SptUserModsPath))
            {
                ScanDirectoryForExtraFiles(SptUserModsPath, "*.dll", expectedFiles, issues, "SPT/user/mods");
            }

            return issues;
        }

        private void ScanDirectoryForExtraFiles(string directory, string pattern, HashSet<string> expectedFiles, List<FileIssue> issues, string displayPrefix)
        {
            try
            {
                var files = Directory.GetFiles(directory, pattern, SearchOption.AllDirectories);

                foreach (var file in files)
                {
                    var normalizedPath = NormalizePath(file);

                    // Skip if this file is in the manifest
                    if (expectedFiles.Contains(normalizedPath))
                        continue;

                    // Skip BewasModSync's own files
                    if (normalizedPath.IndexOf("BewasModSync", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    // Skip SPT core files (spt-common, spt-core, etc.)
                    var fileName = Path.GetFileName(file);
                    if (fileName.StartsWith("spt-", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("0Harmony", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // This is an extra file
                    var relativePath = GetRelativePath(file, SptRoot);
                    issues.Add(new FileIssue
                    {
                        Type = FileIssueType.ExtraFile,
                        FilePath = relativePath,
                        ModName = "Unknown",
                        Required = false,
                        Details = "File not in server manifest"
                    });
                }
            }
            catch (Exception ex)
            {
                LogSource.LogWarning($"BewasModSync: Error scanning {displayPrefix}: {ex.Message}");
            }
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        }

        private static string GetRelativePath(string fullPath, string basePath)
        {
            var fullUri = new Uri(fullPath);
            var baseUri = new Uri(basePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString().Replace('/', Path.DirectorySeparatorChar));
        }

        private static string ComputeFileHash(string filePath)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hashBytes = sha256.ComputeHash(stream);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// Legacy verification for when manifest is not available
        /// </summary>
        private List<FileIssue> LegacyVerifyMods(ClientConfig clientConfig)
        {
            var issues = new List<FileIssue>();

            // Check if we have mods downloaded list
            if (!File.Exists(ModsDownloadedPath))
            {
                LogSource.LogWarning("BewasModSync: No mods downloaded list found.");
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
                LogSource.LogWarning($"BewasModSync: Could not connect to server: {ex.Message}");
                return issues;
            }

            if (serverConfig != null)
            {
                var requiredMods = serverConfig.ModList.Where(m => !m.Optional).ToList();

                foreach (var requiredMod in requiredMods)
                {
                    var downloaded = modsDownloaded.FirstOrDefault(d => d.DownloadUrl == requiredMod.DownloadUrl);

                    if (downloaded == null)
                    {
                        issues.Add(new FileIssue
                        {
                            Type = FileIssueType.Missing,
                            FilePath = requiredMod.ModName,
                            ModName = requiredMod.ModName,
                            Required = true,
                            Details = "Mod not downloaded"
                        });
                        continue;
                    }

                    if (downloaded.LastUpdated != requiredMod.LastUpdated)
                    {
                        issues.Add(new FileIssue
                        {
                            Type = FileIssueType.HashMismatch,
                            FilePath = requiredMod.ModName,
                            ModName = requiredMod.ModName,
                            Required = true,
                            Details = "Mod outdated"
                        });
                    }
                }
            }

            return issues;
        }

        private ServerConfig FetchServerConfig(string serverUrl)
        {
            serverUrl = serverUrl.TrimEnd('/');
            var url = $"{serverUrl}/bewasmodsync/api/config";

            using (var client = new WebClient())
            {
                client.Headers.Add("User-Agent", "BewasModSync/1.0");
                var json = client.DownloadString(url);
                return JsonConvert.DeserializeObject<ServerConfig>(json);
            }
        }

        private void ShowSyncWarning(List<FileIssue> issues)
        {
            var warningObject = new GameObject("BewasModSyncWarning");
            var warning = warningObject.AddComponent<SyncWarningGui>();
            warning.Issues = issues;
            DontDestroyOnLoad(warningObject);
        }

        private void ShowSetupRequiredWarning()
        {
            var warningObject = new GameObject("BewasModSyncSetup");
            warningObject.AddComponent<SetupRequiredGui>();
            DontDestroyOnLoad(warningObject);
        }
    }

    public class SyncWarningGui : MonoBehaviour
    {
        private static readonly string SptRoot = Path.GetDirectoryName(Application.dataPath);
        private static readonly string UpdaterExePath = Path.Combine(SptRoot, "BewasModUpdater.exe");
        private static readonly string PluginsPath = Path.Combine(SptRoot, "BepInEx", "plugins");
        private static readonly string ServerModsPath = Path.Combine(SptRoot, "SPT", "user", "mods");
        
        public List<FileIssue> Issues = new List<FileIssue>();
        private bool _showWarning = true;
        private bool _updaterExists;
        private Rect _windowRect;
        private Vector2 _scrollPosition;

        private void Start()
        {
            _windowRect = new Rect(Screen.width / 2 - 375, Screen.height / 2 - 250, 750, 500);
            _updaterExists = File.Exists(UpdaterExePath);
        }

        private void Update()
        {
            if (_showWarning && Issues.Count > 0)
            {
                HideGameUI(true);
            }
        }

        private void OnGUI()
        {
            if (!_showWarning || Issues.Count == 0) return;
            if (!Singleton<CommonUI>.Instantiated) return;

            GUI.color = new Color(0, 0, 0, 0.85f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            _windowRect = GUI.Window(12345, _windowRect, DrawWindow, "");
        }

        private void DrawWindow(int windowId)
        {
            var titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 0.4f, 0.4f) }
            };

            var headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.85f, 0.4f) }
            };

            var bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                wordWrap = true,
                normal = { textColor = Color.white }
            };

            var missingStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                wordWrap = true,
                normal = { textColor = new Color(1f, 0.5f, 0.5f) }
            };

            var modifiedStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                wordWrap = true,
                normal = { textColor = new Color(1f, 0.8f, 0.4f) }
            };

            var extraStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                wordWrap = true,
                normal = { textColor = new Color(0.6f, 0.8f, 1f) }
            };

            var missingFiles = Issues.Where(i => i.Type == FileIssueType.Missing).ToList();
            var hashMismatches = Issues.Where(i => i.Type == FileIssueType.HashMismatch).ToList();
            var extraFiles = Issues.Where(i => i.Type == FileIssueType.ExtraFile).ToList();
            
            bool hasOnlyExtras = extraFiles.Count > 0 && missingFiles.Count == 0 && hashMismatches.Count == 0;

            GUILayout.Space(15);
            
            // Dynamic title based on issue type
            if (hasOnlyExtras)
            {
                var extraTitleStyle = new GUIStyle(titleStyle) { normal = { textColor = new Color(0.6f, 0.8f, 1f) } };
                GUILayout.Label("⚠ BewasModSync - Extra Mods Detected", extraTitleStyle);
            }
            else
            {
                GUILayout.Label("⚠ BewasModSync - File Verification Issues", titleStyle);
            }
            GUILayout.Space(10);

            // Summary with context
            if (hasOnlyExtras)
            {
                GUILayout.Label($"Found {extraFiles.Count} mod file(s) not managed by the server:", headerStyle);
                GUILayout.Label("These mods are not in the server's mod list - you may want to remove them.", bodyStyle);
            }
            else
            {
                GUILayout.Label($"Found {Issues.Count} issue(s):", headerStyle);
            }
            GUILayout.Space(5);

            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(200));

            // Missing files
            if (missingFiles.Any())
            {
                GUILayout.Label($"Missing Files ({missingFiles.Count}):", bodyStyle);
                foreach (var issue in missingFiles)
                {
                    GUILayout.Label($"  • [{issue.ModName}] {TruncatePath(issue.FilePath, 50)}", missingStyle);
                }
                GUILayout.Space(10);
            }

            // Hash mismatches
            if (hashMismatches.Any())
            {
                GUILayout.Label($"Modified Files ({hashMismatches.Count}):", bodyStyle);
                foreach (var issue in hashMismatches)
                {
                    GUILayout.Label($"  • [{issue.ModName}] {TruncatePath(issue.FilePath, 50)}", modifiedStyle);
                }
                GUILayout.Space(10);
            }

            // Extra files
            if (extraFiles.Any())
            {
                if (!hasOnlyExtras)
                {
                    GUILayout.Label($"Extra Files ({extraFiles.Count}) - not in server mod list:", bodyStyle);
                }
                foreach (var issue in extraFiles)
                {
                    GUILayout.Label($"  • {TruncatePath(issue.FilePath, 60)}", extraStyle);
                }
            }

            GUILayout.EndScrollView();

            GUILayout.Space(10);
            
            // Context-aware help text
            if (hasOnlyExtras)
            {
                GUILayout.Label("To fix: Quit the game and remove these mod files from BepInEx\\plugins", bodyStyle);
            }
            else if (missingFiles.Any() || hashMismatches.Any())
            {
                GUILayout.Label("Run BewasModUpdater.exe to download/update missing mods.", bodyStyle);
            }

            GUILayout.FlexibleSpace();

            // Check which folders have extra files
            bool hasPluginExtras = extraFiles.Any(f => f.FilePath.IndexOf("BepInEx", StringComparison.OrdinalIgnoreCase) >= 0);
            bool hasServerModExtras = extraFiles.Any(f => f.FilePath.IndexOf("SPT", StringComparison.OrdinalIgnoreCase) >= 0);

            // Buttons - organized in rows for better layout
            var buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold
            };

            // Row 1: Action buttons
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Continue Anyway", buttonStyle, GUILayout.Width(130), GUILayout.Height(35)))
            {
                _showWarning = false;
                HideGameUI(false);
            }

            GUILayout.Space(10);

            // Show folder buttons based on where extra files are
            if (hasPluginExtras)
            {
                if (GUILayout.Button("Open Plugins Folder", buttonStyle, GUILayout.Width(145), GUILayout.Height(35)))
                {
                    OpenFolder(PluginsPath);
                }
                GUILayout.Space(10);
            }

            if (hasServerModExtras)
            {
                if (GUILayout.Button("Open Server Mods", buttonStyle, GUILayout.Width(140), GUILayout.Height(35)))
                {
                    OpenFolder(ServerModsPath);
                }
                GUILayout.Space(10);
            }

            if (_updaterExists && (missingFiles.Any() || hashMismatches.Any()))
            {
                if (GUILayout.Button("Run Updater & Exit", buttonStyle, GUILayout.Width(145), GUILayout.Height(35)))
                {
                    LaunchUpdaterAndQuit();
                }
                GUILayout.Space(10);
            }

            if (GUILayout.Button("Quit Game", buttonStyle, GUILayout.Width(100), GUILayout.Height(35)))
            {
                Application.Quit();
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(15);
        }

        private void OpenFolder(string path)
        {
            try
            {
                BewasModSyncClientEnforcerPlugin.LogSource.LogInfo($"Opening folder: {path}");
                Process.Start("explorer.exe", path);
            }
            catch (Exception ex)
            {
                BewasModSyncClientEnforcerPlugin.LogSource.LogError($"Failed to open folder: {ex.Message}");
            }
        }

        private string TruncatePath(string path, int maxLength)
        {
            if (path.Length <= maxLength) return path;
            return "..." + path.Substring(path.Length - maxLength + 3);
        }

        private void LaunchUpdaterAndQuit()
        {
            try
            {
                BewasModSyncClientEnforcerPlugin.LogSource.LogInfo($"Launching updater: {UpdaterExePath}");
                
                var startInfo = new ProcessStartInfo
                {
                    FileName = UpdaterExePath,
                    WorkingDirectory = SptRoot,
                    UseShellExecute = true
                };

                Process.Start(startInfo);
                Application.Quit();
            }
            catch (Exception ex)
            {
                BewasModSyncClientEnforcerPlugin.LogSource.LogError($"Failed to launch updater: {ex.Message}");
            }
        }

        private void HideGameUI(bool hide)
        {
            try
            {
                if (Singleton<LoginUI>.Instantiated)
                    Singleton<LoginUI>.Instance.gameObject.SetActive(!hide);
                if (Singleton<PreloaderUI>.Instantiated)
                    Singleton<PreloaderUI>.Instance.gameObject.SetActive(!hide);
                if (Singleton<CommonUI>.Instantiated)
                    Singleton<CommonUI>.Instance.gameObject.SetActive(!hide);
            }
            catch { }
        }

        private void OnDestroy()
        {
            HideGameUI(false);
        }
    }

    public class SetupRequiredGui : MonoBehaviour
    {
        private static readonly string SptRoot = Path.GetDirectoryName(Application.dataPath);
        private static readonly string UpdaterExePath = Path.Combine(SptRoot, "BewasModUpdater.exe");
        
        private Rect _windowRect;
        private bool _updaterExists;

        private void Start()
        {
            _windowRect = new Rect(Screen.width / 2 - 275, Screen.height / 2 - 175, 550, 350);
            _updaterExists = File.Exists(UpdaterExePath);
        }

        private void Update()
        {
            HideGameUI(true);
        }

        private void OnGUI()
        {
            if (!Singleton<CommonUI>.Instantiated) return;

            GUI.color = new Color(0, 0, 0, 0.9f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            _windowRect = GUI.Window(12346, _windowRect, DrawWindow, "");
        }

        private void DrawWindow(int windowId)
        {
            var titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 24,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 0.5f, 0.2f) }
            };

            var headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };

            var bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                wordWrap = true,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };

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

            GUILayout.Label("Before you can play, you need to run the updater\nto download the required mods.", bodyStyle);
            GUILayout.Space(15);

            GUILayout.Label("Run BewasModUpdater.exe in your SPT root folder.", bodyStyle);
            GUILayout.Space(5);
            GUILayout.Label("<SPT_ROOT>\\BewasModUpdater.exe", pathStyle);

            GUILayout.FlexibleSpace();

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            var buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold
            };

            if (_updaterExists)
            {
                if (GUILayout.Button("Exit & Run Updater", buttonStyle, GUILayout.Width(180), GUILayout.Height(40)))
                {
                    LaunchUpdaterAndQuit();
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

        private void LaunchUpdaterAndQuit()
        {
            try
            {
                BewasModSyncClientEnforcerPlugin.LogSource.LogInfo($"Launching updater: {UpdaterExePath}");
                
                var startInfo = new ProcessStartInfo
                {
                    FileName = UpdaterExePath,
                    WorkingDirectory = SptRoot,
                    UseShellExecute = true
                };

                Process.Start(startInfo);
                Application.Quit();
            }
            catch (Exception ex)
            {
                BewasModSyncClientEnforcerPlugin.LogSource.LogError($"Failed to launch updater: {ex.Message}");
            }
        }

        private void HideGameUI(bool hide)
        {
            try
            {
                if (Singleton<LoginUI>.Instantiated)
                    Singleton<LoginUI>.Instance.gameObject.SetActive(!hide);
                if (Singleton<PreloaderUI>.Instantiated)
                    Singleton<PreloaderUI>.Instance.gameObject.SetActive(!hide);
                if (Singleton<CommonUI>.Instantiated)
                    Singleton<CommonUI>.Instance.gameObject.SetActive(!hide);
            }
            catch { }
        }

        private void OnDestroy()
        {
            HideGameUI(false);
        }
    }
}

