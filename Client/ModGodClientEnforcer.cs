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
using ModGod.ClientEnforcer.Models;
using Comfort.Common;
using EFT.UI;
using Newtonsoft.Json;
using UnityEngine;

namespace ModGod.ClientEnforcer
{
    [BepInPlugin("com.modgod.clientenforcer", "ModGod Client Enforcer", "1.0.0")]
    public class ModGodClientEnforcerPlugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;

        private static readonly string SptRoot = Path.GetDirectoryName(Application.dataPath);
        
        // Config files are stored in ModGodData folder
        private static readonly string InternalDataFolder = Path.Combine(SptRoot, "ModGodData");
        private static readonly string ConfigPath = Path.Combine(InternalDataFolder, "ModGodClient.json");
        private static readonly string ModsDownloadedPath = Path.Combine(InternalDataFolder, "modsDownloaded.json");
        
        // Mod updater exe is at SPT root
        private static readonly string UpdaterExePath = Path.Combine(SptRoot, "ModGodUpdater.exe");

        // Directories to scan for extra files
        private static readonly string BepInExPluginsPath = Path.Combine(SptRoot, "BepInEx", "plugins");
        private static readonly string SptUserModsPath = Path.Combine(SptRoot, "SPT", "user", "mods");
        
        // Cache for compiled glob pattern regexes
        private static readonly Dictionary<string, System.Text.RegularExpressions.Regex> _globCache = 
            new Dictionary<string, System.Text.RegularExpressions.Regex>(StringComparer.OrdinalIgnoreCase);

        private void Awake()
        {
            LogSource = Logger;
            LogSource.LogInfo("ModGod Client Enforcer loaded!");

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
            LogSource.LogInfo("ModGod: Starting mod verification...");

            // Run verification
            bool setupRequired;
            var issues = VerifyMods(out setupRequired);

            // If setup is required, show setup warning
            if (setupRequired)
            {
                LogSource.LogError("========================================");
                LogSource.LogError("ModGod: SETUP REQUIRED!");
                LogSource.LogError("========================================");
                LogSource.LogError("ModGod has not been set up yet.");
                LogSource.LogError("Please run ModGodUpdater.exe to sync your mods.");
                LogSource.LogError("========================================");

                // Wait for UI to be ready
                yield return new WaitUntil(() => Singleton<CommonUI>.Instantiated);
                ShowSetupRequiredWarning();
                yield break;
            }

            if (issues.Count == 0)
            {
                LogSource.LogInfo("ModGod: All required mods verified successfully!");
                yield break;
            }

            // Log issues by category
            var missingFiles = issues.Where(i => i.Type == FileIssueType.Missing).ToList();
            var hashMismatches = issues.Where(i => i.Type == FileIssueType.HashMismatch).ToList();
            var extraFiles = issues.Where(i => i.Type == FileIssueType.ExtraFile).ToList();

            LogSource.LogError("========================================");
            LogSource.LogError("ModGod: FILE VERIFICATION ISSUES DETECTED!");
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
            LogSource.LogInfo("ModGod: Waiting for game UI to initialize...");
            yield return new WaitUntil(() => Singleton<CommonUI>.Instantiated);
            LogSource.LogInfo("ModGod: UI ready, showing warning...");

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
                    LogSource.LogWarning($"ModGod: Internal data folder not found at {InternalDataFolder}. Run ModGodUpdater.exe first.");
                    setupRequired = true;
                    return issues;
                }

                // Check if we have client config
                if (!File.Exists(ConfigPath))
                {
                    LogSource.LogWarning("ModGod: No client config found. Run ModGodUpdater.exe first.");
                    setupRequired = true;
                    return issues;
                }

                var clientConfig = JsonConvert.DeserializeObject<ClientConfig>(File.ReadAllText(ConfigPath));
                if (clientConfig == null || string.IsNullOrWhiteSpace(clientConfig.ServerUrl))
                {
                    LogSource.LogWarning("ModGod: Invalid client config.");
                    return issues;
                }

                // Try to fetch manifest from server
                FileManifest manifest = null;
                try
                {
                    manifest = FetchManifest(clientConfig.ServerUrl);
                    LogSource.LogInfo($"ModGod: Fetched manifest with {manifest.Files.Count} files (generated in {manifest.GenerationTimeMs}ms)");
                }
                catch (Exception ex)
                {
                    LogSource.LogWarning($"ModGod: Could not fetch manifest from server: {ex.Message}");
                    // Fall back to legacy verification
                    return LegacyVerifyMods(clientConfig);
                }

                var exclusions = BuildExclusionSet(manifest.SyncExclusions);

                // Verify files from manifest
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

        private FileManifest FetchManifest(string serverUrl)
        {
            serverUrl = serverUrl.TrimEnd('/');
            var url = $"{serverUrl}/modgod/api/manifest";

            using (var client = new WebClient())
            {
                client.Headers.Add("User-Agent", "ModGod/1.0");
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
                {
                    continue;
                }

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
                    LogSource.LogWarning($"ModGod: Failed to hash file '{relativePath}': {ex.Message}");
                }
            }

            return issues;
        }

        private List<FileIssue> ScanForExtraFiles(FileManifest manifest, HashSet<string> exclusions)
        {
            var issues = new List<FileIssue>();

            // Build set of expected file paths (normalized)
            var expectedFiles = new HashSet<string>(
                manifest.Files.Keys.Select(p => NormalizePath(Path.Combine(SptRoot, p))),
                StringComparer.OrdinalIgnoreCase);

            // Scan BepInEx/plugins for .dll files
            if (Directory.Exists(BepInExPluginsPath))
            {
                ScanDirectoryForExtraFiles(BepInExPluginsPath, "*.dll", expectedFiles, issues, "BepInEx/plugins", exclusions);
            }

            // Scan SPT/user/mods for .dll files
            if (Directory.Exists(SptUserModsPath))
            {
                ScanDirectoryForExtraFiles(SptUserModsPath, "*.dll", expectedFiles, issues, "SPT/user/mods", exclusions);
            }

            return issues;
        }

        private void ScanDirectoryForExtraFiles(string directory, string pattern, HashSet<string> expectedFiles, List<FileIssue> issues, string displayPrefix, HashSet<string> exclusions)
        {
            try
            {
                var files = Directory.GetFiles(directory, pattern, SearchOption.AllDirectories);

                foreach (var file in files)
                {
                    var normalizedPath = NormalizePath(file);
                    var relativePath = NormalizeRelativePath(GetRelativePath(file, SptRoot));

                    if (IsExcludedPath(relativePath, exclusions))
                        continue;

                    // Skip if this file is in the manifest
                    if (expectedFiles.Contains(normalizedPath))
                        continue;

                    // Skip ModGod's own files
                    if (normalizedPath.IndexOf("ModGod", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    // Skip SPT core files (spt-common, spt-core, etc.)
                    var fileName = Path.GetFileName(file);
                    if (fileName.StartsWith("spt-", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("0Harmony", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // This is an extra file
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
                LogSource.LogWarning($"ModGod: Error scanning {displayPrefix}: {ex.Message}");
            }
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        }

        private static string NormalizeRelativePath(string path)
        {
            return path.Replace('\\', '/').TrimStart('/');
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
                LogSource.LogWarning("ModGod: No mods downloaded list found.");
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
                LogSource.LogWarning($"ModGod: Could not connect to server: {ex.Message}");
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

        private HashSet<string> BuildExclusionSet(IEnumerable<string> exclusions)
        {
            return new HashSet<string>(
                (exclusions ?? Array.Empty<string>()).Select(NormalizeRelativePath),
                StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsExcludedPath(string relativePath, HashSet<string> exclusions)
        {
            var norm = NormalizeRelativePath(relativePath);
            
            foreach (var pattern in exclusions)
            {
                // Check if it's a glob pattern (contains *, ?, or **)
                if (pattern.Contains("*") || pattern.Contains("?"))
                {
                    if (GlobMatch(norm, pattern))
                        return true;
                }
                else
                {
                    // Exact match or prefix match for non-glob patterns
                    if (norm.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                        norm.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Simple glob pattern matching for exclusions.
        /// Supports: * (any chars except /), ** (any chars including /), ? (single char)
        /// </summary>
        private static bool GlobMatch(string path, string pattern)
        {
            try
            {
                // Check cache first
                if (!_globCache.TryGetValue(pattern, out var regex))
                {
                    regex = CompileGlobPattern(pattern);
                    if (regex != null)
                        _globCache[pattern] = regex;
                }
                
                return regex?.IsMatch(path) ?? false;
            }
            catch
            {
                return false;
            }
        }
        
        private static System.Text.RegularExpressions.Regex CompileGlobPattern(string pattern)
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
                                i += 2; // Skip ** and /, loop will add 1
                            }
                            else
                            {
                                regexPattern += ".*";
                                i++; // Skip second *, loop will add 1
                            }
                        }
                        else
                        {
                            regexPattern += "[^/]*";
                        }
                    }
                    else if (c == '?')
                    {
                        regexPattern += "[^/]";
                    }
                    else if (c == '.')
                    {
                        regexPattern += "\\.";
                    }
                    else if (c == '/' || c == '\\')
                    {
                        regexPattern += "/";
                    }
                    else if ("[](){}+^$|".IndexOf(c) >= 0)
                    {
                        regexPattern += "\\" + c;
                    }
                    else
                    {
                        regexPattern += c;
                    }
                }
                
                regexPattern += "$";
                
                return new System.Text.RegularExpressions.Regex(
                    regexPattern, 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch
            {
                return null;
            }
        }

        private ServerConfig FetchServerConfig(string serverUrl)
        {
            serverUrl = serverUrl.TrimEnd('/');
            var url = $"{serverUrl}/modgod/api/config";

            using (var client = new WebClient())
            {
                client.Headers.Add("User-Agent", "ModGod/1.0");
                var json = client.DownloadString(url);
                return JsonConvert.DeserializeObject<ServerConfig>(json);
            }
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
            // Wider window to show full paths
            _windowRect = new Rect(Screen.width / 2 - 450, Screen.height / 2 - 275, 900, 550);
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
                GUILayout.Label("⚠ ModGod - Extra Mods Detected", extraTitleStyle);
            }
            else
            {
                GUILayout.Label("⚠ ModGod - File Verification Issues", titleStyle);
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

            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(250));

            // Missing files - grouped by folder (derive mod name from path)
            if (missingFiles.Any())
            {
                GUILayout.Label($"Missing Files ({missingFiles.Count}):", bodyStyle);
                var groupedMissing = missingFiles.GroupBy(f => GetModFolderFromPath(f.FilePath));
                foreach (var group in groupedMissing.OrderBy(g => g.Key))
                {
                    GUILayout.Label($"  [{group.Key}]:", missingStyle);
                    foreach (var issue in group)
                    {
                        GUILayout.Label($"    • {issue.FilePath}", missingStyle);
                    }
                }
                GUILayout.Space(10);
            }

            // Hash mismatches - grouped by folder (derive mod name from path)
            if (hashMismatches.Any())
            {
                GUILayout.Label($"Modified Files ({hashMismatches.Count}):", bodyStyle);
                var groupedModified = hashMismatches.GroupBy(f => GetModFolderFromPath(f.FilePath));
                foreach (var group in groupedModified.OrderBy(g => g.Key))
                {
                    GUILayout.Label($"  [{group.Key}]:", modifiedStyle);
                    foreach (var issue in group)
                    {
                        GUILayout.Label($"    • {issue.FilePath}", modifiedStyle);
                    }
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
                    GUILayout.Label($"  • {issue.FilePath}", extraStyle);
                }
            }

            GUILayout.EndScrollView();

            GUILayout.Space(10);
            
            // Help text - updater handles all issues now
            GUILayout.Label("Run ModGodUpdater.exe to sync your mods with the server.", bodyStyle);

            GUILayout.FlexibleSpace();

            // Simplified buttons: Continue, Exit & Run Updater, Quit
            var buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold
            };

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Continue", buttonStyle, GUILayout.Width(120), GUILayout.Height(35)))
            {
                _showWarning = false;
                HideGameUI(false);
            }

            GUILayout.Space(15);

            if (_updaterExists)
            {
                if (GUILayout.Button("Exit & Run Updater", buttonStyle, GUILayout.Width(160), GUILayout.Height(35)))
                {
                    LaunchUpdaterAndQuit();
                }
                GUILayout.Space(15);
            }

            if (GUILayout.Button("Exit", buttonStyle, GUILayout.Width(100), GUILayout.Height(35)))
            {
                Application.Quit();
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(15);
        }

        private string TruncatePath(string path, int maxLength)
        {
            if (path.Length <= maxLength) return path;
            return "..." + path.Substring(path.Length - maxLength + 3);
        }

        /// <summary>
        /// Extract mod folder name from file path for grouping purposes.
        /// e.g., "BepInEx/plugins/acidphantasm-stattrack/file.dll" -> "acidphantasm-stattrack"
        /// e.g., "SPT/user/mods/mymod/src/file.js" -> "mymod"
        /// </summary>
        private static string GetModFolderFromPath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return "Unknown";
            
            var normalized = filePath.Replace('\\', '/');
            var parts = normalized.Split('/');
            
            // For "BepInEx/plugins/{mod-folder}/..." pattern
            if (parts.Length >= 3 && 
                parts[0].Equals("BepInEx", StringComparison.OrdinalIgnoreCase) &&
                parts[1].Equals("plugins", StringComparison.OrdinalIgnoreCase))
            {
                return parts[2];
            }
            
            // For "SPT/user/mods/{mod-folder}/..." pattern
            if (parts.Length >= 4 && 
                parts[0].Equals("SPT", StringComparison.OrdinalIgnoreCase) &&
                parts[1].Equals("user", StringComparison.OrdinalIgnoreCase) &&
                parts[2].Equals("mods", StringComparison.OrdinalIgnoreCase))
            {
                return parts[3];
            }
            
            // Fallback: return the first directory after a known root, or "Unknown"
            return parts.Length > 1 ? parts[1] : "Unknown";
        }

        private void LaunchUpdaterAndQuit()
        {
            try
            {
                ModGodClientEnforcerPlugin.LogSource.LogInfo($"Launching updater: {UpdaterExePath}");
                
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
                ModGodClientEnforcerPlugin.LogSource.LogError($"Failed to launch updater: {ex.Message}");
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
        private static readonly string UpdaterExePath = Path.Combine(SptRoot, "ModGodUpdater.exe");
        
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
            GUILayout.Label("⚠ ModGod - Setup Required", titleStyle);
            GUILayout.Space(20);

            GUILayout.Label("This server requires ModGod", headerStyle);
            GUILayout.Space(15);

            GUILayout.Label("Before you can play, you need to run the updater\nto download the required mods.", bodyStyle);
            GUILayout.Space(15);

            GUILayout.Label("Run ModGodUpdater.exe in your SPT root folder.", bodyStyle);
            GUILayout.Space(5);
            GUILayout.Label("<SPT_ROOT>\\ModGodUpdater.exe", pathStyle);

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
                ModGodClientEnforcerPlugin.LogSource.LogInfo($"Launching updater: {UpdaterExePath}");
                
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
                ModGodClientEnforcerPlugin.LogSource.LogError($"Failed to launch updater: {ex.Message}");
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

