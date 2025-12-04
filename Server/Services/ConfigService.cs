using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using BewasModSync.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace BewasModSync.Services;

[Injectable(InjectionType = InjectionType.Singleton, TypePriority = OnLoadOrder.PreSptModLoader)]
public class ConfigService : IOnLoad
{
    private readonly ModHelper _modHelper;
    private readonly JsonUtil _jsonUtil;
    private readonly FileUtil _fileUtil;
    private readonly ISptLogger<ConfigService> _logger;

    private string _modPath = string.Empty;
    private string _dataPath = string.Empty;
    private string _sptRoot = string.Empty;

    public ServerConfig Config { get; private set; } = new();
    public StagingIndex Staging { get; private set; } = new();
    public PendingOperations PendingOps { get; private set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ConfigService(
        ModHelper modHelper,
        JsonUtil jsonUtil,
        FileUtil fileUtil,
        ISptLogger<ConfigService> logger)
    {
        _modHelper = modHelper;
        _jsonUtil = jsonUtil;
        _fileUtil = fileUtil;
        _logger = logger;
    }

    public string ModPath => _modPath;
    public string DataPath => _dataPath;
    public string SptRoot => _sptRoot;
    public string StagingPath => Path.Combine(_dataPath, "staging");
    public string ConfigPath => Path.Combine(_dataPath, "serverConfig.json");
    public string StagingIndexPath => Path.Combine(_dataPath, "stagingIndex.json");
    public string PendingOpsPath => Path.Combine(_dataPath, "pendingOperations.json");

    // Actual SPT installation paths
    public string BepInExPluginsPath => Path.Combine(_sptRoot, "BepInEx", "plugins");
    public string SptUserModsPath => Path.Combine(_sptRoot, "SPT", "user", "mods");

    public async Task OnLoad()
    {
        _modPath = _modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        
        // SPT root is 4 levels up from mod folder: <SPT_ROOT>/SPT/user/mods/BewasModSyncServer
        // Example: C:\SPT\SPT\user\mods\BewasModSyncServer -> C:\SPT
        _sptRoot = Path.GetFullPath(Path.Combine(_modPath, "..", "..", "..", ".."));
        
        // IMPORTANT: Data folder must be OUTSIDE of SPT/user/mods/ to prevent SPT from
        // scanning extracted DLLs in staging as server mods!
        _dataPath = Path.Combine(_sptRoot, "BewaModSyncData");

        // Ensure data directory exists
        Directory.CreateDirectory(_dataPath);
        Directory.CreateDirectory(StagingPath);

        await LoadConfigAsync();
        await LoadStagingIndexAsync();
        await LoadPendingOpsAsync();
        
        // Apply any pending operations from previous session
        await ApplyPendingOperationsOnStartupAsync();

        _logger.Success($"BewasModSync ConfigService loaded!");
        _logger.Info($"  SPT Root: {_sptRoot}");
        _logger.Info($"  Data Path: {_dataPath}");
    }

    #region Config Management

    public async Task LoadConfigAsync()
    {
        if (File.Exists(ConfigPath))
        {
            var json = await File.ReadAllTextAsync(ConfigPath);
            Config = JsonSerializer.Deserialize<ServerConfig>(json, JsonOptions) ?? new ServerConfig();
        }
        else
        {
            Config = new ServerConfig();
            await SaveConfigAsync();
        }
    }

    public async Task SaveConfigAsync()
    {
        var json = JsonSerializer.Serialize(Config, JsonOptions);
        await File.WriteAllTextAsync(ConfigPath, json);
    }

    #endregion

    #region Staging Management

    public async Task LoadStagingIndexAsync()
    {
        if (File.Exists(StagingIndexPath))
        {
            var json = await File.ReadAllTextAsync(StagingIndexPath);
            Staging = JsonSerializer.Deserialize<StagingIndex>(json, JsonOptions) ?? new StagingIndex();
        }
        else
        {
            Staging = new StagingIndex();
        }
    }

    public async Task SaveStagingIndexAsync()
    {
        var json = JsonSerializer.Serialize(Staging, JsonOptions);
        await File.WriteAllTextAsync(StagingIndexPath, json);
    }

    public string GetStagingPathForUrl(string url)
    {
        // Create a hash of the URL for the folder name
        var hash = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(url)))
            .Replace("/", "_")
            .Replace("+", "-")
            .Substring(0, 16);

        return Path.Combine(StagingPath, hash);
    }

    public bool IsUrlStaged(string url)
    {
        return Staging.UrlToPath.ContainsKey(url) &&
               Directory.Exists(Staging.UrlToPath[url]);
    }

    /// <summary>
    /// Clear staging folder for a specific URL
    /// </summary>
    public void ClearStagingForUrl(string url)
    {
        if (Staging.UrlToPath.TryGetValue(url, out var path) && Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, true);
                _logger.Info($"Cleared staging for: {url}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to clear staging: {ex.Message}");
            }
        }
        Staging.UrlToPath.Remove(url);
    }

    #endregion

    #region Pending Operations

    public async Task LoadPendingOpsAsync()
    {
        if (File.Exists(PendingOpsPath))
        {
            var json = await File.ReadAllTextAsync(PendingOpsPath);
            PendingOps = JsonSerializer.Deserialize<PendingOperations>(json, JsonOptions) ?? new PendingOperations();
        }
        else
        {
            PendingOps = new PendingOperations();
        }
    }

    public async Task SavePendingOpsAsync()
    {
        var json = JsonSerializer.Serialize(PendingOps, JsonOptions);
        await File.WriteAllTextAsync(PendingOpsPath, json);
    }

    /// <summary>
    /// Check for pending operations on server startup and log status
    /// </summary>
    private async Task ApplyPendingOperationsOnStartupAsync()
    {
        var pendingRemovals = Config.ModList.Where(m => m.Status == ModStatus.PendingRemoval).ToList();
        var pendingDeletions = PendingOps.PathsToDelete.Count;

        // Only apply deletions - these are for mods that were removed via UI
        if (pendingDeletions > 0 || pendingRemovals.Count > 0)
        {
            _logger.Info("========================================");
            _logger.Info("BewasModSync: Processing pending removals...");
            _logger.Info("========================================");
            await ApplyPendingDeletionsAsync();
        }

        // Check if any pending mods have been installed by the auto-installer
        await CheckAndMarkInstalledModsAsync();

        // Get remaining pending installs after checking
        var pendingInstalls = Config.ModList.Where(m => m.Status == ModStatus.Pending).ToList();

        // If there are still pending mods, launch the auto-install script
        if (pendingInstalls.Count > 0)
        {
            // Generate and launch the auto-install script
            var scriptPath = await GenerateInstallScriptAsync();
            
            _logger.Warning("========================================");
            _logger.Warning($"BewasModSync: {pendingInstalls.Count} mod(s) pending installation:");
            foreach (var mod in pendingInstalls)
            {
                _logger.Warning($"  • {mod.ModName}");
            }
            _logger.Warning("");
            _logger.Warning("Auto-installer launched in separate window.");
            _logger.Warning("Mods will be installed automatically when you stop the server.");
            _logger.Warning("========================================");
            
            // Launch the script in a new window
            LaunchInstallScript();
        }
        else
        {
            // No pending mods - delete the install script if it exists
            var scriptPath = Path.Combine(_dataPath, "install-pending-mods.ps1");
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }
        }
    }
    
    /// <summary>
    /// Check if pending mods have been installed (via the completion marker file)
    /// </summary>
    private async Task CheckAndMarkInstalledModsAsync()
    {
        var completedPath = Path.Combine(_dataPath, "completed-installs.json");
        
        if (!File.Exists(completedPath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(completedPath);
            var completedUrls = JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new();
            
            if (completedUrls.Count == 0)
            {
                File.Delete(completedPath);
                return;
            }

            _logger.Info("========================================");
            _logger.Info("BewasModSync: Processing completed installations...");
            _logger.Info("========================================");

            var markedCount = 0;
            
            foreach (var url in completedUrls)
            {
                var mod = Config.ModList.Find(m => m.DownloadUrl == url);
                if (mod != null && mod.Status == ModStatus.Pending)
                {
                    mod.Status = ModStatus.Installed;
                    mod.LastUpdated = DateTime.UtcNow.ToString("o");
                    
                    // Clear staging for this mod
                    if (IsUrlStaged(mod.DownloadUrl))
                    {
                        ClearStagingForUrl(mod.DownloadUrl);
                    }
                    
                    markedCount++;
                    _logger.Success($"  ✓ {mod.ModName}");
                }
            }

            if (markedCount > 0)
            {
                _logger.Info($"Marked {markedCount} mod(s) as installed");
                await SaveStagingIndexAsync();
                await SaveConfigAsync();
            }
            
            // Delete the completion file
            File.Delete(completedPath);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to process completion file: {ex.Message}");
        }
    }

    /// <summary>
    /// Apply pending deletions that were queued from a previous session
    /// </summary>
    private async Task ApplyPendingDeletionsAsync()
    {
        // Handle explicit path deletions
        if (PendingOps.PathsToDelete.Count > 0)
        {
            _logger.Info($"Deleting {PendingOps.PathsToDelete.Count} queued path(s)...");

            var failed = new List<string>();
            foreach (var path in PendingOps.PathsToDelete)
            {
                try
                {
                    var fullPath = path.Replace("<SPT_ROOT>", _sptRoot);
                    if (Directory.Exists(fullPath))
                    {
                        Directory.Delete(fullPath, true);
                        _logger.Info($"  Deleted directory: {fullPath}");
                    }
                    else if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                        _logger.Info($"  Deleted file: {fullPath}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"  Failed to delete {path}: {ex.Message}");
                    failed.Add(path);
                }
            }

            // Keep only failed deletions for next time
            PendingOps.PathsToDelete = failed;
            await SavePendingOpsAsync();
        }

        // Remove mods that were pending removal
        var removedCount = Config.ModList.RemoveAll(m => m.Status == ModStatus.PendingRemoval);
        if (removedCount > 0)
        {
            _logger.Info($"Removed {removedCount} mod(s) from configuration");
            await SaveConfigAsync();
        }
    }

    /// <summary>
    /// Helper to recursively copy a directory
    /// </summary>
    private void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var targetFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, targetFile, true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var targetSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectoryRecursive(subDir, targetSubDir);
        }
    }

    /// <summary>
    /// Generate a PowerShell script that auto-polls the server and installs mods when it shuts down
    /// </summary>
    public async Task<string?> GenerateInstallScriptAsync(string serverUrl = "https://127.0.0.1:6969")
    {
        var pendingMods = Config.ModList
            .Where(m => m.Status == ModStatus.Pending && IsUrlStaged(m.DownloadUrl))
            .ToList();

        var scriptPath = Path.Combine(_dataPath, "install-pending-mods.ps1");

        if (pendingMods.Count == 0)
        {
            // No pending mods - delete script if it exists
            if (File.Exists(scriptPath))
                File.Delete(scriptPath);
            return null;
        }

        var sb = new System.Text.StringBuilder();
        
        // Header
        sb.AppendLine("# BewasModSync - Auto-Install Pending Mods");
        sb.AppendLine("# This script polls the SPT server and installs mods when it shuts down");
        sb.AppendLine("# Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("# Close this window to cancel");
        sb.AppendLine("");
        
        // Configuration
        sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        sb.AppendLine($"$ServerUrl = '{serverUrl}'");
        sb.AppendLine("$StatusEndpoint = \"$ServerUrl/bewasmodsync/api/status\"");
        sb.AppendLine("$PollIntervalSeconds = 2");
        sb.AppendLine($"$SptRoot = '{_sptRoot}'");
        sb.AppendLine("");
        
        // SSL certificate bypass for self-signed certs
        sb.AppendLine("# Bypass SSL certificate validation (SPT uses self-signed certs)");
        sb.AppendLine("Add-Type @\"");
        sb.AppendLine("using System.Net;");
        sb.AppendLine("using System.Security.Cryptography.X509Certificates;");
        sb.AppendLine("public class TrustAllCertsPolicy : ICertificatePolicy {");
        sb.AppendLine("    public bool CheckValidationResult(ServicePoint srvPoint, X509Certificate certificate,");
        sb.AppendLine("        WebRequest request, int certificateProblem) { return true; }");
        sb.AppendLine("}");
        sb.AppendLine("\"@");
        sb.AppendLine("[System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAllCertsPolicy");
        sb.AppendLine("[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12");
        sb.AppendLine("");
        
        // Title and info
        sb.AppendLine("$Host.UI.RawUI.WindowTitle = 'BewasModSync - Waiting for Server Shutdown'");
        sb.AppendLine("Clear-Host");
        sb.AppendLine("Write-Host '======================================' -ForegroundColor Cyan");
        sb.AppendLine("Write-Host '  BewasModSync - Auto Mod Installer   ' -ForegroundColor Cyan");
        sb.AppendLine("Write-Host '======================================' -ForegroundColor Cyan");
        sb.AppendLine("Write-Host ''");
        sb.AppendLine($"Write-Host 'Pending mods to install: {pendingMods.Count}' -ForegroundColor Yellow");
        
        // List pending mods
        foreach (var mod in pendingMods)
        {
            sb.AppendLine($"Write-Host '  - {mod.ModName}' -ForegroundColor Gray");
        }
        
        sb.AppendLine("Write-Host ''");
        sb.AppendLine("Write-Host 'Mods will be installed automatically when SPT server shuts down.' -ForegroundColor Green");
        sb.AppendLine("Write-Host 'Close this window to cancel.' -ForegroundColor DarkGray");
        sb.AppendLine("Write-Host ''");
        sb.AppendLine("");
        
        // Polling loop
        sb.AppendLine("# Poll until server shuts down");
        sb.AppendLine("$serverWasUp = $false");
        sb.AppendLine("$spinChars = @('|', '/', '-', '\\')");
        sb.AppendLine("$spinIndex = 0");
        sb.AppendLine("");
        sb.AppendLine("while ($true) {");
        sb.AppendLine("    try {");
        sb.AppendLine("        $response = Invoke-WebRequest -Uri $StatusEndpoint -TimeoutSec 3 -UseBasicParsing");
        sb.AppendLine("        $serverWasUp = $true");
        sb.AppendLine("        $spin = $spinChars[$spinIndex % 4]");
        sb.AppendLine("        $spinIndex++");
        sb.AppendLine("        Write-Host \"`r[$spin] Server is running... waiting for shutdown    \" -NoNewline -ForegroundColor Gray");
        sb.AppendLine("        Start-Sleep -Seconds $PollIntervalSeconds");
        sb.AppendLine("    }");
        sb.AppendLine("    catch {");
        sb.AppendLine("        if ($serverWasUp) {");
        sb.AppendLine("            # Server was up, now it's down - time to install!");
        sb.AppendLine("            Write-Host ''");
        sb.AppendLine("            Write-Host ''");
        sb.AppendLine("            Write-Host 'Server shutdown detected!' -ForegroundColor Green");
        sb.AppendLine("            Write-Host ''");
        sb.AppendLine("            break");
        sb.AppendLine("        }");
        sb.AppendLine("        # Server not up yet, keep waiting");
        sb.AppendLine("        $spin = $spinChars[$spinIndex % 4]");
        sb.AppendLine("        $spinIndex++");
        sb.AppendLine("        Write-Host \"`r[$spin] Waiting for server to start...              \" -NoNewline -ForegroundColor Yellow");
        sb.AppendLine("        Start-Sleep -Seconds $PollIntervalSeconds");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine("");
        
        // Installation section
        sb.AppendLine("# Install mods");
        sb.AppendLine("Write-Host 'Installing mods...' -ForegroundColor Cyan");
        sb.AppendLine("Write-Host ''");
        sb.AppendLine("");

        foreach (var mod in pendingMods)
        {
            var stagingPath = Staging.UrlToPath[mod.DownloadUrl];
            var extractedPath = Path.Combine(stagingPath, "extracted");

            sb.AppendLine($"# {mod.ModName}");
            sb.AppendLine($"Write-Host 'Installing: {mod.ModName}' -ForegroundColor Yellow");

            foreach (var installPath in mod.InstallPaths)
            {
                var sourcePath = installPath[0];
                var targetPath = installPath[1].Replace("<SPT_ROOT>", "$SptRoot");
                var fullSourcePath = Path.Combine(extractedPath, sourcePath);

                sb.AppendLine($"if (Test-Path '{fullSourcePath}') {{");
                sb.AppendLine($"    try {{");
                sb.AppendLine($"        Copy-Item -Path '{fullSourcePath}\\*' -Destination '{targetPath}' -Recurse -Force -ErrorAction Stop");
                sb.AppendLine($"        Write-Host '  [OK] Copied {sourcePath}' -ForegroundColor Green");
                sb.AppendLine($"    }} catch {{");
                sb.AppendLine($"        Write-Host '  [FAIL] {sourcePath}: ' $_.Exception.Message -ForegroundColor Red");
                sb.AppendLine($"    }}");
                sb.AppendLine("}");
            }
            sb.AppendLine("");
        }

        // Write completion marker file
        var completedPath = Path.Combine(_dataPath, "completed-installs.json");
        var urlsJson = JsonSerializer.Serialize(pendingMods.Select(m => m.DownloadUrl).ToList(), JsonOptions);
        
        sb.AppendLine("# Write completion marker file");
        sb.AppendLine($"$completedUrls = @'");
        sb.AppendLine(urlsJson);
        sb.AppendLine("'@");
        sb.AppendLine($"$completedUrls | Out-File -FilePath '{completedPath}' -Encoding UTF8");
        sb.AppendLine("Write-Host 'Wrote completion marker for server.' -ForegroundColor DarkGray");
        sb.AppendLine("");
        
        // Completion
        sb.AppendLine("Write-Host ''");
        sb.AppendLine("Write-Host '======================================' -ForegroundColor Green");
        sb.AppendLine("Write-Host '  Installation Complete!              ' -ForegroundColor Green");
        sb.AppendLine("Write-Host '======================================' -ForegroundColor Green");
        sb.AppendLine("Write-Host ''");
        sb.AppendLine("Write-Host 'You can now start the SPT server.' -ForegroundColor Cyan");
        sb.AppendLine("Write-Host 'The server will automatically mark these mods as installed.' -ForegroundColor DarkGray");
        sb.AppendLine("Write-Host ''");
        sb.AppendLine("Write-Host 'This window will close in 10 seconds...' -ForegroundColor DarkGray");
        sb.AppendLine("Start-Sleep -Seconds 10");

        await File.WriteAllTextAsync(scriptPath, sb.ToString());
        _logger.Info($"Generated install script: {scriptPath}");
        
        return scriptPath;
    }
    
    /// <summary>
    /// Launch the install script in a new PowerShell window
    /// </summary>
    public void LaunchInstallScript()
    {
        var scriptPath = Path.Combine(_dataPath, "install-pending-mods.ps1");
        
        if (!File.Exists(scriptPath))
        {
            _logger.Warning("Install script not found, cannot launch");
            return;
        }
        
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -NoProfile -File \"{scriptPath}\"",
                UseShellExecute = true,
                CreateNoWindow = false
            };
            
            System.Diagnostics.Process.Start(startInfo);
            _logger.Info("Launched install script in new window");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to launch install script: {ex.Message}");
        }
    }

    /// <summary>
    /// Queue paths for deletion on next startup
    /// </summary>
    public async Task QueueDeletionsAsync(List<string> paths)
    {
        PendingOps.PathsToDelete.AddRange(paths);
        await SavePendingOpsAsync();
    }

    #endregion

    #region SPT Server Mod Manager

    public async Task AddModAsync(ModEntry mod)
    {
        // Check if mod already exists by URL
        var existing = Config.ModList.FindIndex(m => m.DownloadUrl == mod.DownloadUrl);
        if (existing >= 0)
        {
            Config.ModList[existing] = mod;
        }
        else
        {
            Config.ModList.Add(mod);
        }

        await SaveConfigAsync();
    }

    /// <summary>
    /// Mark a mod for removal (will be deleted on apply/restart)
    /// </summary>
    public async Task MarkModForRemovalAsync(string downloadUrl)
    {
        var mod = Config.ModList.Find(m => m.DownloadUrl == downloadUrl);
        if (mod != null)
        {
            mod.Status = ModStatus.PendingRemoval;
            await SaveConfigAsync();
        }
    }

    /// <summary>
    /// Remove a pending (not yet installed) mod entirely
    /// </summary>
    public async Task RemovePendingModAsync(string downloadUrl)
    {
        var mod = Config.ModList.Find(m => m.DownloadUrl == downloadUrl);
        if (mod != null && mod.Status == ModStatus.Pending)
        {
            Config.ModList.Remove(mod);
            ClearStagingForUrl(downloadUrl);
            await SaveStagingIndexAsync();
            await SaveConfigAsync();
        }
    }

    public async Task UpdateModStatusAsync(string downloadUrl, ModStatus status)
    {
        var mod = Config.ModList.Find(m => m.DownloadUrl == downloadUrl);
        if (mod != null)
        {
            mod.Status = status;
            await SaveConfigAsync();
        }
    }

    public async Task UpdateModTimestampAsync(string downloadUrl)
    {
        var mod = Config.ModList.Find(m => m.DownloadUrl == downloadUrl);
        if (mod != null)
        {
            mod.LastUpdated = DateTime.UtcNow.ToString("o");
            await SaveConfigAsync();
        }
    }

    /// <summary>
    /// Check if there are pending changes to apply
    /// </summary>
    public bool HasPendingChanges()
    {
        return Config.ModList.Any(m => m.Status == ModStatus.Pending || m.Status == ModStatus.PendingRemoval);
    }

    #endregion
}
