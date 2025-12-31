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
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using ModGod3.ClientEnforcer.Models;
using Comfort.Common;
using EFT.UI;
using Newtonsoft.Json;
using UnityEngine;

namespace ModGod3.ClientEnforcer
{
    [BepInPlugin("com.modgod.clientenforcer", "ModGod Client Enforcer", "3.0.0")]
    public class ModGodClientEnforcerPlugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;

        private static readonly string ClientVersion =
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        private static readonly string SptRoot = Path.GetDirectoryName(Application.dataPath);
        private static readonly string InternalDataFolder = Path.Combine(SptRoot, "ModGodData");
        private static readonly string ConfigPath = Path.Combine(InternalDataFolder, "ModGodClient.json");
        private static readonly string UpdaterExePath = Path.Combine(SptRoot, "ModGodUpdater.exe");

        private static readonly Dictionary<string, Regex> _globCache =
            new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);

        private void Awake()
        {
            LogSource = Logger;
            LogSource.LogInfo("ModGod Client Enforcer 3.0 loaded!");
            ServicePointManager.ServerCertificateValidationCallback = AcceptAllCertificates;
        }

        private void Start()
        {
            StartCoroutine(VerifyModsCoroutine());
        }

        private static bool AcceptAllCertificates(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            return true;
        }

        private IEnumerator VerifyModsCoroutine()
        {
            LogSource.LogInfo("ModGod: Starting verification...");

            bool setupRequired;
            var issues = VerifyMods(out setupRequired);

            if (setupRequired)
            {
                LogSource.LogError("ModGod: SETUP REQUIRED!");
                yield return new WaitUntil(() => Singleton<CommonUI>.Instantiated);
                ShowSetupRequiredWarning();
                yield break;
            }

            if (issues.Count == 0)
            {
                LogSource.LogInfo("ModGod: All files verified successfully!");
                yield break;
            }

            LogSource.LogError($"ModGod: Found {issues.Count} issue(s)!");
            yield return new WaitUntil(() => Singleton<CommonUI>.Instantiated);
            ShowSyncWarning(issues);
        }

        private List<FileIssue> VerifyMods(out bool setupRequired)
        {
            var issues = new List<FileIssue>();
            setupRequired = false;

            try
            {
                if (!Directory.Exists(InternalDataFolder))
                {
                    setupRequired = true;
                    return issues;
                }

                if (!File.Exists(ConfigPath))
                {
                    setupRequired = true;
                    return issues;
                }

                var clientConfig = JsonConvert.DeserializeObject<ClientConfig>(File.ReadAllText(ConfigPath));
                if (clientConfig == null || string.IsNullOrWhiteSpace(clientConfig.ServerUrl))
                {
                    return issues;
                }

                // Build opted-in query
                var optedInParam = clientConfig.OptedInItems?.Count > 0
                    ? string.Join(",", clientConfig.OptedInItems.Select(Uri.EscapeDataString))
                    : null;

                FileManifest manifest;
                try
                {
                    manifest = FetchManifest(clientConfig.ServerUrl, optedInParam);
                    LogSource.LogInfo($"ModGod: Fetched manifest with {manifest.Files.Count} files");
                }
                catch (Exception ex)
                {
                    LogSource.LogWarning($"ModGod: Could not fetch manifest: {ex.Message}");
                    return issues;
                }

                // Version check
                if (!string.IsNullOrEmpty(manifest.ModGodVersion) && manifest.ModGodVersion != ClientVersion)
                {
                    LogSource.LogError($"ModGod: VERSION MISMATCH! Server={manifest.ModGodVersion}, Client={ClientVersion}");
                    issues.Add(new FileIssue
                    {
                        Type = FileIssueType.VersionMismatch,
                        FilePath = "ModGodUpdater.exe",
                        SourceItem = "ModGod",
                        Required = true,
                        Details = $"Server: {manifest.ModGodVersion}, Client: {ClientVersion}"
                    });
                    return issues;
                }

                var exclusions = BuildExclusionSet(manifest.Exclusions);

                // Verify manifest files
                issues.AddRange(VerifyManifestFiles(manifest, exclusions));

                // Scan for extra files
                issues.AddRange(ScanForExtraFiles(manifest, exclusions));
            }
            catch (Exception ex)
            {
                LogSource.LogError($"ModGod: Error during verification: {ex.Message}");
            }

            return issues;
        }

        private FileManifest FetchManifest(string serverUrl, string optedInParam)
        {
            serverUrl = serverUrl.TrimEnd('/');
            var url = $"{serverUrl}/modgod/api/manifest";
            if (!string.IsNullOrEmpty(optedInParam))
                url += $"?optedIn={optedInParam}";

            using (var client = new WebClient())
            {
                client.Headers.Add("User-Agent", "ModGod/3.0");
                var json = client.DownloadString(url);
                return JsonConvert.DeserializeObject<FileManifest>(json);
            }
        }

        private List<FileIssue> VerifyManifestFiles(FileManifest manifest, HashSet<string> exclusions)
        {
            var issues = new List<FileIssue>();

            foreach (var kvp in manifest.Files)
            {
                var relativePath = kvp.Key;
                var entry = kvp.Value;

                if (IsExcludedPath(relativePath, exclusions))
                    continue;

                var fullPath = Path.Combine(SptRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(fullPath))
                {
                    issues.Add(new FileIssue
                    {
                        Type = FileIssueType.Missing,
                        FilePath = relativePath,
                        SourceItem = entry.SourceItem,
                        Required = entry.Required,
                        Details = "File not found"
                    });
                    continue;
                }

                try
                {
                    var localHash = ComputeFileHash(fullPath);
                    if (!string.Equals(localHash, entry.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new FileIssue
                        {
                            Type = FileIssueType.HashMismatch,
                            FilePath = relativePath,
                            SourceItem = entry.SourceItem,
                            Required = entry.Required,
                            Details = "Hash mismatch"
                        });
                    }
                }
                catch (Exception ex)
                {
                    LogSource.LogWarning($"ModGod: Failed to hash '{relativePath}': {ex.Message}");
                }
            }

            return issues;
        }

        private List<FileIssue> ScanForExtraFiles(FileManifest manifest, HashSet<string> exclusions)
        {
            var issues = new List<FileIssue>();

            var expectedFiles = new HashSet<string>(
                manifest.Files.Keys.Select(p => NormalizePath(Path.Combine(SptRoot, p))),
                StringComparer.OrdinalIgnoreCase);

            var syncRoots = manifest.SyncRoots?.Count > 0
                ? manifest.SyncRoots
                : new List<string> { "BepInEx/plugins", "SPT/user/mods" };

            foreach (var syncRoot in syncRoots)
            {
                var fullPath = Path.Combine(SptRoot, syncRoot.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(fullPath))
                {
                    ScanDirectoryForExtraFiles(fullPath, expectedFiles, issues, exclusions);
                }
            }

            return issues;
        }

        private void ScanDirectoryForExtraFiles(string directory, HashSet<string> expectedFiles, List<FileIssue> issues, HashSet<string> exclusions)
        {
            try
            {
                foreach (var file in Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories))
                {
                    var normalizedPath = NormalizePath(file);
                    var relativePath = GetRelativePath(file, SptRoot).Replace('\\', '/');

                    if (IsExcludedPath(relativePath, exclusions))
                        continue;

                    if (expectedFiles.Contains(normalizedPath))
                        continue;

                    // Skip ModGod's own files
                    if (normalizedPath.IndexOf("ModGod", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    // Skip SPT core files
                    var fileName = Path.GetFileName(file);
                    if (fileName.StartsWith("spt-", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("0Harmony", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase))
                        continue;

                    issues.Add(new FileIssue
                    {
                        Type = FileIssueType.ExtraFile,
                        FilePath = relativePath,
                        SourceItem = "Unknown",
                        Required = false,
                        Details = "Not in server manifest"
                    });
                }
            }
            catch (Exception ex)
            {
                LogSource.LogWarning($"ModGod: Error scanning {directory}: {ex.Message}");
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

        private HashSet<string> BuildExclusionSet(IEnumerable<string> exclusions)
        {
            return new HashSet<string>(
                (exclusions ?? new List<string>()).Select(p => p.Replace('\\', '/').TrimStart('/')),
                StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsExcludedPath(string relativePath, HashSet<string> exclusions)
        {
            var norm = relativePath.Replace('\\', '/').TrimStart('/');

            foreach (var pattern in exclusions)
            {
                if (pattern.Contains("*") || pattern.Contains("?"))
                {
                    if (GlobMatch(norm, pattern))
                        return true;
                }
                else
                {
                    if (norm.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                        norm.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        private static bool GlobMatch(string path, string pattern)
        {
            try
            {
                if (!_globCache.TryGetValue(pattern, out var regex))
                {
                    regex = CompileGlobPattern(pattern);
                    if (regex != null)
                        _globCache[pattern] = regex;
                }
                return regex?.IsMatch(path) ?? false;
            }
            catch { return false; }
        }

        private static Regex CompileGlobPattern(string pattern)
        {
            try
            {
                var regexPattern = "^";
                pattern = pattern.Replace('\\', '/').TrimStart('/');

                for (int i = 0; i < pattern.Length; i++)
                {
                    var c = pattern[i];
                    if (c == '*')
                    {
                        if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                        {
                            if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                            {
                                regexPattern += "(.*/)?";
                                i += 2;
                            }
                            else
                            {
                                regexPattern += ".*";
                                i++;
                            }
                        }
                        else { regexPattern += "[^/]*"; }
                    }
                    else if (c == '?') { regexPattern += "[^/]"; }
                    else if (c == '.') { regexPattern += "\\."; }
                    else if (c == '/' || c == '\\') { regexPattern += "/"; }
                    else if ("[](){}+^$|".IndexOf(c) >= 0) { regexPattern += "\\" + c; }
                    else { regexPattern += c; }
                }

                regexPattern += "$";
                return new Regex(regexPattern, RegexOptions.IgnoreCase);
            }
            catch { return null; }
        }

        private void ShowSyncWarning(List<FileIssue> issues)
        {
            var warningObject = new GameObject("ModGodWarning");
            var warning = warningObject.AddComponent<SyncWarningGui>();
            warning.Issues = issues;
            DontDestroyOnLoad(warningObject);
        }

        private void ShowSetupRequiredWarning()
        {
            var warningObject = new GameObject("ModGodSetup");
            warningObject.AddComponent<SetupRequiredGui>();
            DontDestroyOnLoad(warningObject);
        }
    }

    public class SyncWarningGui : MonoBehaviour
    {
        private static readonly string SptRoot = Path.GetDirectoryName(Application.dataPath);
        private static readonly string UpdaterExePath = Path.Combine(SptRoot, "ModGodUpdater.exe");

        public List<FileIssue> Issues = new List<FileIssue>();
        private bool _showWarning = true;
        private bool _updaterExists;
        private Rect _windowRect;
        private Vector2 _scrollPosition;

        private void Start()
        {
            _windowRect = new Rect(Screen.width / 2 - 450, Screen.height / 2 - 275, 900, 550);
            _updaterExists = File.Exists(UpdaterExePath);
        }

        private void Update()
        {
            if (_showWarning && Issues.Count > 0)
                HideGameUI(true);
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

            var bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                wordWrap = true,
                normal = { textColor = Color.white }
            };

            var versionMismatch = Issues.FirstOrDefault(i => i.Type == FileIssueType.VersionMismatch);

            GUILayout.Space(15);

            if (versionMismatch != null)
            {
                GUILayout.Label("ModGod 3.0 - Update Required", titleStyle);
                GUILayout.Space(10);
                GUILayout.Label($"Version mismatch detected.\n\n{versionMismatch.Details}\n\nPlease update ModGod.", bodyStyle);
            }
            else
            {
                GUILayout.Label("ModGod 3.0 - File Verification Issues", titleStyle);
                GUILayout.Space(10);

                var missing = Issues.Where(i => i.Type == FileIssueType.Missing).ToList();
                var modified = Issues.Where(i => i.Type == FileIssueType.HashMismatch).ToList();
                var extra = Issues.Where(i => i.Type == FileIssueType.ExtraFile).ToList();

                GUILayout.Label($"Found {Issues.Count} issue(s):", bodyStyle);
                GUILayout.Space(5);

                _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(300));

                if (missing.Any())
                {
                    GUILayout.Label($"Missing Files ({missing.Count}):", bodyStyle);
                    foreach (var issue in missing.Take(20))
                        GUILayout.Label($"  - {issue.FilePath}", bodyStyle);
                    if (missing.Count > 20)
                        GUILayout.Label($"  ... and {missing.Count - 20} more", bodyStyle);
                    GUILayout.Space(10);
                }

                if (modified.Any())
                {
                    GUILayout.Label($"Modified Files ({modified.Count}):", bodyStyle);
                    foreach (var issue in modified.Take(10))
                        GUILayout.Label($"  - {issue.FilePath}", bodyStyle);
                    if (modified.Count > 10)
                        GUILayout.Label($"  ... and {modified.Count - 10} more", bodyStyle);
                    GUILayout.Space(10);
                }

                if (extra.Any())
                {
                    GUILayout.Label($"Extra Files ({extra.Count}):", bodyStyle);
                    foreach (var issue in extra.Take(10))
                        GUILayout.Label($"  - {issue.FilePath}", bodyStyle);
                    if (extra.Count > 10)
                        GUILayout.Label($"  ... and {extra.Count - 10} more", bodyStyle);
                }

                GUILayout.EndScrollView();

                GUILayout.Space(10);
                GUILayout.Label("Run ModGodUpdater.exe to sync your files with the server.", bodyStyle);
            }

            GUILayout.FlexibleSpace();

            var buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold };

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Continue", buttonStyle, GUILayout.Width(120), GUILayout.Height(35)))
            {
                _showWarning = false;
                HideGameUI(false);
            }

            GUILayout.Space(15);

            if (_updaterExists && GUILayout.Button("Exit & Run Updater", buttonStyle, GUILayout.Width(160), GUILayout.Height(35)))
            {
                LaunchUpdaterAndQuit();
            }

            GUILayout.Space(15);

            if (GUILayout.Button("Exit", buttonStyle, GUILayout.Width(100), GUILayout.Height(35)))
            {
                Application.Quit();
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(15);
        }

        private void LaunchUpdaterAndQuit()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = UpdaterExePath,
                    WorkingDirectory = SptRoot,
                    UseShellExecute = true
                });
                Application.Quit();
            }
            catch (Exception ex)
            {
                ModGodClientEnforcerPlugin.LogSource.LogError($"Failed to launch updater: {ex.Message}");
            }
        }

        private void HideGameUI(bool hide)
        {
            try
            {
                if (Singleton<LoginUI>.Instantiated) Singleton<LoginUI>.Instance.gameObject.SetActive(!hide);
                if (Singleton<PreloaderUI>.Instantiated) Singleton<PreloaderUI>.Instance.gameObject.SetActive(!hide);
                if (Singleton<CommonUI>.Instantiated) Singleton<CommonUI>.Instance.gameObject.SetActive(!hide);
            }
            catch { }
        }

        private void OnDestroy() => HideGameUI(false);
    }

    public class SetupRequiredGui : MonoBehaviour
    {
        private static readonly string SptRoot = Path.GetDirectoryName(Application.dataPath);
        private static readonly string UpdaterExePath = Path.Combine(SptRoot, "ModGodUpdater.exe");

        private Rect _windowRect;
        private bool _updaterExists;

        private void Start()
        {
            _windowRect = new Rect(Screen.width / 2 - 275, Screen.height / 2 - 175, 550, 350);
            _updaterExists = File.Exists(UpdaterExePath);
        }

        private void Update() => HideGameUI(true);

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
                fontSize = 24, fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 0.5f, 0.2f) }
            };

            var bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14, wordWrap = true,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };

            GUILayout.Space(20);
            GUILayout.Label("ModGod 3.0 - Setup Required", titleStyle);
            GUILayout.Space(20);
            GUILayout.Label("This server requires ModGod.\n\nRun ModGodUpdater.exe to sync your files.", bodyStyle);

            GUILayout.FlexibleSpace();

            var buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 14, fontStyle = FontStyle.Bold };

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (_updaterExists && GUILayout.Button("Exit & Run Updater", buttonStyle, GUILayout.Width(180), GUILayout.Height(40)))
            {
                LaunchUpdaterAndQuit();
            }

            GUILayout.Space(20);

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
                Process.Start(new ProcessStartInfo
                {
                    FileName = UpdaterExePath,
                    WorkingDirectory = SptRoot,
                    UseShellExecute = true
                });
                Application.Quit();
            }
            catch { }
        }

        private void HideGameUI(bool hide)
        {
            try
            {
                if (Singleton<LoginUI>.Instantiated) Singleton<LoginUI>.Instance.gameObject.SetActive(!hide);
                if (Singleton<PreloaderUI>.Instantiated) Singleton<PreloaderUI>.Instance.gameObject.SetActive(!hide);
                if (Singleton<CommonUI>.Instantiated) Singleton<CommonUI>.Instance.gameObject.SetActive(!hide);
            }
            catch { }
        }

        private void OnDestroy() => HideGameUI(false);
    }
}
