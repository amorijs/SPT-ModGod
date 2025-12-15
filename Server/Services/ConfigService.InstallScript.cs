using System.Runtime.InteropServices;
using System.Text.Json;
using ModGod.Models;

namespace ModGod.Services;

/// <summary>
/// Partial class for pending operations and install script generation.
/// </summary>
public partial class ConfigService
{
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
        var pendingDeletions = PendingOps.PathsToDelete.Count;

        // Apply any queued deletions from previous session
        if (pendingDeletions > 0)
        {
            _logger.Info("========================================");
            _logger.Info("ModGod: Processing pending deletions...");
            _logger.Info("========================================");
            await ApplyPendingDeletionsAsync();
        }

        // Check if any operations completed by the auto-installer
        await CheckAndMarkInstalledModsAsync();

        // Check for staged changes that need applying
        var stagedChanges = CalculateStagedChanges();

        // If there are staged changes with downloaded files, show a warning
        var stagedInstalls = stagedChanges.ModsToInstall.Where(m => IsUrlStaged(m.DownloadUrl)).ToList();
        if (stagedInstalls.Count > 0 || stagedChanges.ModsToRemove.Count > 0)
        {
            _logger.Warning("========================================");
            _logger.Warning($"ModGod: You have unapplied changes in the staged config:");
            foreach (var mod in stagedInstalls)
            {
                _logger.Warning($"  + {mod.ModName} (to install)");
            }
            foreach (var mod in stagedChanges.ModsToRemove.Where(m => !m.IsProtected))
            {
                _logger.Warning($"  - {mod.ModName} (to remove)");
            }
            _logger.Warning("");
            _logger.Warning("Open the ModGod web UI and click 'Apply Changes' to install these mods.");
            _logger.Warning("========================================");
        }
        else
        {
            // No staged changes - delete the install script if it exists
            var scriptPath = Path.Combine(_dataPath, "install-pending-mods.ps1");
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }
        }
    }
    
    /// <summary>
    /// Check if pending mods have been installed/removed (via the completion marker file).
    /// Updates both live and staged config to match the actual installed state.
    /// </summary>
    private async Task CheckAndMarkInstalledModsAsync()
    {
        var completedPath = Path.Combine(_dataPath, "completed-installs.json");
        
        if (!File.Exists(completedPath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(completedPath);
            
            // Try new format first (object with installed/removed arrays)
            CompletionData? completionData = null;
            try
            {
                completionData = JsonSerializer.Deserialize<CompletionData>(json, JsonOptions);
            }
            catch
            {
                // Fall back to old format (just a list of URLs for installs)
                var legacyUrls = JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new();
                completionData = new CompletionData { Installed = legacyUrls };
            }

            if (completionData == null || (completionData.Installed.Count == 0 && completionData.Removed.Count == 0))
            {
                File.Delete(completedPath);
                return;
            }

            _logger.Info("========================================");
            _logger.Info("ModGod: Processing completed operations...");
            _logger.Info("========================================");

            var installedCount = 0;
            var removedCount = 0;
            
            // Process installations - update live config (staged was already updated when Apply was clicked)
            foreach (var url in completionData.Installed)
            {
                // Update in live config
                var mod = Config.ModList.Find(m => m.DownloadUrl == url);
                if (mod != null)
                {
                    mod.Status = ModStatus.Installed;
                    mod.LastUpdated = DateTime.UtcNow.ToString("o");
                    installedCount++;
                    _logger.Success($"  ✓ Installed: {mod.ModName}");
                }
                    
                    // Clear staging for this mod
                if (IsUrlStaged(url))
                    {
                    ClearStagingForUrl(url);
                }
            }
            
            // Process removals - ensure removed from both configs
            foreach (var url in completionData.Removed)
            {
                // Remove from live config
                var modIndex = Config.ModList.FindIndex(m => m.DownloadUrl == url);
                if (modIndex >= 0)
                {
                    var modName = Config.ModList[modIndex].ModName;
                    Config.ModList.RemoveAt(modIndex);
                    removedCount++;
                    _logger.Success($"  ✓ Removed: {modName}");
                }
                
                // Also remove from staged config to keep them in sync
                var stagedIndex = StagedConfig.ModList.FindIndex(m => m.DownloadUrl == url);
                if (stagedIndex >= 0)
                {
                    StagedConfig.ModList.RemoveAt(stagedIndex);
                }
                    
                    // Clear staging if any
                    if (IsUrlStaged(url))
                    {
                        ClearStagingForUrl(url);
                }
            }
            
            // Clear the pending deletions list since they've been processed
            if (completionData.Removed.Count > 0)
            {
                PendingOps.PathsToDelete.Clear();
                await SavePendingOpsAsync();
            }

            if (installedCount > 0 || removedCount > 0)
            {
                _logger.Info($"Processed {installedCount} installation(s), {removedCount} removal(s)");
                await SaveStagingIndexAsync();
                await SaveConfigAsync();
                // Note: We only update staged config in-memory, don't create staged file
                // The staged file was already deleted when Apply was clicked
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
            
            // First pass: delete files
            foreach (var path in PendingOps.PathsToDelete)
            {
                try
                {
                    var fullPath = path.Replace("<SPT_ROOT>", _sptRoot);
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                        _logger.Info($"  Deleted file: {fullPath}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"  Failed to delete file {path}: {ex.Message}");
                    failed.Add(path);
                }
            }

            // Second pass: try to delete directories (only if empty - safe for shared dirs)
            foreach (var path in PendingOps.PathsToDelete)
            {
                try
                {
                    var fullPath = path.Replace("<SPT_ROOT>", _sptRoot);
                    if (Directory.Exists(fullPath))
                    {
                        // Only delete if directory is empty
                        if (!Directory.EnumerateFileSystemEntries(fullPath).Any())
                        {
                            Directory.Delete(fullPath);
                            _logger.Info($"  Deleted empty directory: {fullPath}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"  Failed to delete directory {path}: {ex.Message}");
                    // Don't add to failed - we don't want to retry directory deletions
                }
            }

            // Keep only failed file deletions for next time
            PendingOps.PathsToDelete = failed;
            await SavePendingOpsAsync();
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
    /// Queue paths for deletion on next startup
    /// </summary>
    public async Task QueueDeletionsAsync(List<string> paths)
    {
        PendingOps.PathsToDelete.AddRange(paths);
        await SavePendingOpsAsync();
    }

    #endregion

    #region Install Script Generation

    /// <summary>
    /// Generate install scripts (PowerShell for Windows, Bash for Linux) to auto-apply installs/removals on server shutdown.
    /// Uses staged changes to determine what needs to be installed/removed.
    /// </summary>
    public async Task<string?> GenerateInstallScriptAsync(string serverUrl = "https://127.0.0.1:6969")
    {
        var stagedChanges = CalculateStagedChanges();
        return await GenerateInstallScriptAsync(stagedChanges, serverUrl);
    }
    
    /// <summary>
    /// Generate install scripts with explicitly provided changes.
    /// </summary>
    public async Task<string?> GenerateInstallScriptAsync(StagedChanges stagedChanges, string serverUrl = "https://127.0.0.1:6969")
    {
        // Filter to only staged mods that have files downloaded
        var pendingInstalls = stagedChanges.ModsToInstall
            .Where(m => IsUrlStaged(m.DownloadUrl))
            .ToList();

        var pendingRemovals = stagedChanges.ModsToRemove
            .Where(m => !m.IsProtected) // Don't remove protected mods
            .ToList();

        var pathsToDelete = PendingOps.PathsToDelete.ToList();

        var scriptPathPs1 = Path.Combine(_dataPath, "install-pending-mods.ps1");
        var scriptPathSh = Path.Combine(_dataPath, "install-pending-mods.sh");

        // Nothing to do -> delete scripts
        if (pendingInstalls.Count == 0 && pendingRemovals.Count == 0 && pathsToDelete.Count == 0)
        {
            if (File.Exists(scriptPathPs1)) File.Delete(scriptPathPs1);
            if (File.Exists(scriptPathSh)) File.Delete(scriptPathSh);
            return null;
        }

        // Generate both scripts
        await GeneratePowerShellScriptAsync(scriptPathPs1, pendingInstalls, pendingRemovals, pathsToDelete, serverUrl);
        await GenerateBashScriptAsync(scriptPathSh, pendingInstalls, pendingRemovals, pathsToDelete, serverUrl);

        // Return script path for current OS
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        return isWindows ? scriptPathPs1 : scriptPathSh;
    }

    private async Task GeneratePowerShellScriptAsync(
        string scriptPath,
        List<ModEntry> pendingInstalls,
        List<ModEntry> pendingRemovals,
        List<string> pathsToDelete,
        string serverUrl)
    {
        var sb = new System.Text.StringBuilder();

        // Header
        sb.AppendLine("# ModGod - Auto-Install Pending Mods");
        sb.AppendLine("# This script polls the SPT server and installs/removes mods when it shuts down");
        sb.AppendLine("# Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("# Close this window to cancel");
        sb.AppendLine("");

        // Configuration
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$script:HasCriticalError = $false");
        sb.AppendLine($"$ServerUrl = '{serverUrl}'");
        sb.AppendLine("$StatusEndpoint = \"$ServerUrl/modgod/api/status\"");
        sb.AppendLine("$PollIntervalSeconds = 2");
        sb.AppendLine($"$SptRoot = '{_sptRoot}'");
        sb.AppendLine("");

        // Global error handler
        sb.AppendLine("# Global error handler to keep window open on critical errors");
        sb.AppendLine("trap {");
        sb.AppendLine("    Write-Host ''");
        sb.AppendLine("    Write-Host '======================================' -ForegroundColor Red");
        sb.AppendLine("    Write-Host '  CRITICAL ERROR                      ' -ForegroundColor Red");
        sb.AppendLine("    Write-Host '======================================' -ForegroundColor Red");
        sb.AppendLine("    Write-Host $_.Exception.Message -ForegroundColor Red");
        sb.AppendLine("    Write-Host ''");
        sb.AppendLine("    Write-Host 'Script Location:' $_.InvocationInfo.ScriptName -ForegroundColor Yellow");
        sb.AppendLine("    Write-Host 'Line:' $_.InvocationInfo.ScriptLineNumber -ForegroundColor Yellow");
        sb.AppendLine("    Write-Host ''");
        sb.AppendLine("    Write-Host 'Press any key to close this window...' -ForegroundColor Cyan");
        sb.AppendLine("    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')");
        sb.AppendLine("    exit 1");
        sb.AppendLine("}");
        sb.AppendLine("");
        sb.AppendLine("# Reset error action for non-critical operations");
        sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
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
        sb.AppendLine("$Host.UI.RawUI.WindowTitle = 'ModGod - Waiting for Server Shutdown'");
        sb.AppendLine("Clear-Host");
        sb.AppendLine("Write-Host '======================================' -ForegroundColor Cyan");
        sb.AppendLine("Write-Host '  ModGod - Auto Mod Manager     ' -ForegroundColor Cyan");
        sb.AppendLine("Write-Host '======================================' -ForegroundColor Cyan");
        sb.AppendLine("Write-Host ''");

        if (pendingInstalls.Count > 0)
        {
            sb.AppendLine($"Write-Host 'Pending mods to install: {pendingInstalls.Count}' -ForegroundColor Yellow");
            foreach (var mod in pendingInstalls)
            {
                sb.AppendLine($"Write-Host '  + {mod.ModName}' -ForegroundColor Green");
            }
            sb.AppendLine("Write-Host ''");
        }

        if (pendingRemovals.Count > 0)
        {
            sb.AppendLine($"Write-Host 'Pending mods to remove: {pendingRemovals.Count}' -ForegroundColor Yellow");
            foreach (var mod in pendingRemovals)
            {
                sb.AppendLine($"Write-Host '  - {mod.ModName}' -ForegroundColor Red");
            }
            sb.AppendLine("Write-Host ''");
        }

        sb.AppendLine("Write-Host 'Changes will be applied automatically when SPT server shuts down.' -ForegroundColor Green");
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

        // Removal section
        if (pathsToDelete.Count > 0)
        {
            sb.AppendLine("# Remove mods");
            sb.AppendLine("Write-Host 'Removing mods...' -ForegroundColor Cyan");
            sb.AppendLine("Write-Host ''");
            sb.AppendLine("");

            foreach (var pathToDelete in pathsToDelete)
            {
                var fullPath = pathToDelete.Replace("<SPT_ROOT>", "$SptRoot");
                sb.AppendLine($"if (Test-Path \"{fullPath}\") {{");
                sb.AppendLine($"    try {{");
                sb.AppendLine($"        Remove-Item -Path \"{fullPath}\" -Recurse -Force -ErrorAction Stop");
                sb.AppendLine($"        Write-Host '  [OK] Removed {Path.GetFileName(pathToDelete.TrimEnd('/', '\\'))}' -ForegroundColor Green");
                sb.AppendLine($"    }} catch {{");
                sb.AppendLine($"        Write-Host '  [FAIL] {pathToDelete}: ' $_.Exception.Message -ForegroundColor Red");
                sb.AppendLine($"    }}");
                sb.AppendLine("}");
                sb.AppendLine("");
            }
        }

        // Installation section
        if (pendingInstalls.Count > 0)
        {
            sb.AppendLine("# Install mods");
            sb.AppendLine("Write-Host 'Installing mods...' -ForegroundColor Cyan");
            sb.AppendLine("Write-Host ''");
            sb.AppendLine("");
            
            // Helper function for selective copy with ignore rules
            sb.AppendLine("# Helper function to copy with ignore rules");
            sb.AppendLine("function Copy-WithIgnoreRules {");
            sb.AppendLine("    param(");
            sb.AppendLine("        [string]$Source,");
            sb.AppendLine("        [string]$Destination,");
            sb.AppendLine("        [string[]]$IgnorePaths");
            sb.AppendLine("    )");
            sb.AppendLine("    ");
            sb.AppendLine("    if (-not (Test-Path $Source)) { return }");
            sb.AppendLine("    ");
            sb.AppendLine("    # If no ignore rules, just do a simple copy");
            sb.AppendLine("    if ($IgnorePaths.Count -eq 0) {");
            sb.AppendLine("        Copy-Item -Path \"$Source\\*\" -Destination $Destination -Recurse -Force -ErrorAction Stop");
            sb.AppendLine("        return");
            sb.AppendLine("    }");
            sb.AppendLine("    ");
            sb.AppendLine("    # Get all items recursively");
            sb.AppendLine("    $items = Get-ChildItem -Path $Source -Recurse -Force");
            sb.AppendLine("    ");
            sb.AppendLine("    foreach ($item in $items) {");
            sb.AppendLine("        # Get relative path from source");
            sb.AppendLine("        $relativePath = $item.FullName.Substring($Source.Length).TrimStart('\\', '/').Replace('\\', '/')");
            sb.AppendLine("        ");
            sb.AppendLine("        # Check if this path should be ignored");
            sb.AppendLine("        $shouldIgnore = $false");
            sb.AppendLine("        foreach ($ignorePath in $IgnorePaths) {");
            sb.AppendLine("            if ($relativePath -eq $ignorePath -or $relativePath -like \"$ignorePath/*\" -or $relativePath -like \"$ignorePath\\*\") {");
            sb.AppendLine("                $shouldIgnore = $true");
            sb.AppendLine("                break");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("        ");
            sb.AppendLine("        if ($shouldIgnore) {");
            sb.AppendLine("            Write-Host \"    [SKIP] $relativePath\" -ForegroundColor DarkGray");
            sb.AppendLine("            continue");
            sb.AppendLine("        }");
            sb.AppendLine("        ");
            sb.AppendLine("        # Calculate destination path");
            sb.AppendLine("        $destPath = Join-Path $Destination $relativePath");
            sb.AppendLine("        ");
            sb.AppendLine("        if ($item.PSIsContainer) {");
            sb.AppendLine("            # Create directory if needed");
            sb.AppendLine("            if (-not (Test-Path $destPath)) {");
            sb.AppendLine("                New-Item -ItemType Directory -Path $destPath -Force | Out-Null");
            sb.AppendLine("            }");
            sb.AppendLine("        } else {");
            sb.AppendLine("            # Copy file");
            sb.AppendLine("            $destDir = Split-Path $destPath -Parent");
            sb.AppendLine("            if (-not (Test-Path $destDir)) {");
            sb.AppendLine("                New-Item -ItemType Directory -Path $destDir -Force | Out-Null");
            sb.AppendLine("            }");
            sb.AppendLine("            Copy-Item -Path $item.FullName -Destination $destPath -Force -ErrorAction Stop");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine("");

            foreach (var mod in pendingInstalls)
            {
                var stagingPath = Staging.UrlToPath[mod.DownloadUrl];
                var extractedPath = Path.Combine(stagingPath, "extracted");
                
                // Get ignore paths for this mod
                var ignoreRules = mod.FileRules?
                    .Where(r => r.State == FileCopyRuleState.Ignore && !string.IsNullOrWhiteSpace(r.Path))
                    .Select(r => r.Path.Replace("\\", "/"))
                    .ToList() ?? new List<string>();

                sb.AppendLine($"# {mod.ModName}");
                sb.AppendLine($"Write-Host 'Installing: {mod.ModName}' -ForegroundColor Yellow");
                
                if (ignoreRules.Count > 0)
                {
                    sb.AppendLine($"Write-Host '  ({ignoreRules.Count} file(s) will be skipped)' -ForegroundColor DarkGray");
                }

                foreach (var installPath in mod.InstallPaths)
                {
                    var sourcePath = installPath[0];
                    var targetPath = installPath[1].Replace("<SPT_ROOT>", "$SptRoot");
                    var fullSourcePath = Path.Combine(extractedPath, sourcePath);
                    
                    // Filter ignore rules to only those relevant to this install path
                    var relevantIgnores = ignoreRules
                        .Where(r => r.StartsWith(sourcePath + "/", StringComparison.OrdinalIgnoreCase) || 
                                    r.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
                        .Select(r => r.StartsWith(sourcePath + "/", StringComparison.OrdinalIgnoreCase) 
                            ? r.Substring(sourcePath.Length + 1) 
                            : "")
                        .Where(r => !string.IsNullOrEmpty(r))
                        .ToList();

                    sb.AppendLine($"if (Test-Path '{fullSourcePath}') {{");
                    sb.AppendLine($"    try {{");
                    
                    if (relevantIgnores.Count > 0)
                    {
                        // Use the helper function with ignore rules
                        var ignoreArray = string.Join("', '", relevantIgnores.Select(r => r.Replace("'", "''")));
                        sb.AppendLine($"        $ignoreList = @('{ignoreArray}')");
                        sb.AppendLine($"        Copy-WithIgnoreRules -Source '{fullSourcePath}' -Destination \"{targetPath}\" -IgnorePaths $ignoreList");
                    }
                    else
                    {
                        // Simple copy when no ignores
                        sb.AppendLine($"        Copy-Item -Path '{fullSourcePath}\\*' -Destination \"{targetPath}\" -Recurse -Force -ErrorAction Stop");
                    }
                    
                    sb.AppendLine($"        Write-Host '  [OK] Copied {sourcePath}' -ForegroundColor Green");
                    sb.AppendLine($"    }} catch {{");
                    sb.AppendLine($"        Write-Host '  [FAIL] {sourcePath}: ' $_.Exception.Message -ForegroundColor Red");
                    sb.AppendLine($"    }}");
                    sb.AppendLine("}");
                }
                sb.AppendLine("");
            }
        }

        // Write completion marker file
        var completedPath = Path.Combine(_dataPath, "completed-installs.json");
        var completionData = new
        {
            installed = pendingInstalls.Select(m => m.DownloadUrl).ToList(),
            removed = pendingRemovals.Select(m => m.DownloadUrl).ToList()
        };
        var urlsJson = JsonSerializer.Serialize(completionData, JsonOptions);

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
    }

    private async Task GenerateBashScriptAsync(
        string scriptPath,
        List<ModEntry> pendingInstalls,
        List<ModEntry> pendingRemovals,
        List<string> pathsToDelete,
        string serverUrl)
    {
        var sbSh = new System.Text.StringBuilder();
        var sptRootUnix = _sptRoot.Replace("\\", "/");
        var completedPath = Path.Combine(_dataPath, "completed-installs.json");
        var completionPathUnix = completedPath.Replace("\\", "/");

        sbSh.AppendLine("#!/usr/bin/env bash");
        sbSh.AppendLine("set -euo pipefail");
        sbSh.AppendLine("");
        sbSh.AppendLine("# Timestamp function for logging");
        sbSh.AppendLine("log() { echo \"[$(date '+%Y-%m-%d %H:%M:%S')] $*\"; }");
        sbSh.AppendLine("");
        sbSh.AppendLine($"SERVER_URL=\"{serverUrl}\"");
        sbSh.AppendLine("STATUS_ENDPOINT=\"$SERVER_URL/modgod/api/status\"");
        sbSh.AppendLine("POLL_INTERVAL=2");
        sbSh.AppendLine($"SPT_ROOT=\"{sptRootUnix}\"");
        sbSh.AppendLine($"COMPLETION_FILE=\"{completionPathUnix}\"");
        sbSh.AppendLine("");
        sbSh.AppendLine("# Trap to log errors (no interactive prompts for headless/background use)");
        sbSh.AppendLine("trap '");
        sbSh.AppendLine("  log \"======================================\";");
        sbSh.AppendLine("  log \"  CRITICAL ERROR\";");
        sbSh.AppendLine("  log \"======================================\";");
        sbSh.AppendLine("  log \"Command: $BASH_COMMAND\";");
        sbSh.AppendLine("  log \"Status : $?\";");
        sbSh.AppendLine("  exit 1;");
        sbSh.AppendLine("' ERR");
        sbSh.AppendLine("");
        sbSh.AppendLine("log \"======================================\"");
        sbSh.AppendLine("log \"  ModGod - Auto Mod Manager\"");
        sbSh.AppendLine("log \"======================================\"");
        sbSh.AppendLine("log \"\"");

        if (pendingInstalls.Count > 0)
        {
            sbSh.AppendLine($"log \"Pending mods to install: {pendingInstalls.Count}\"");
            foreach (var mod in pendingInstalls)
            {
                sbSh.AppendLine($"log \"  + {mod.ModName}\"");
            }
        }

        if (pendingRemovals.Count > 0)
        {
            sbSh.AppendLine($"log \"Pending mods to remove: {pendingRemovals.Count}\"");
            foreach (var mod in pendingRemovals)
            {
                sbSh.AppendLine($"log \"  - {mod.ModName}\"");
            }
        }

        sbSh.AppendLine("log \"\"");
        sbSh.AppendLine("log \"Changes will be applied automatically when SPT server shuts down.\"");
        sbSh.AppendLine("log \"Polling server status every ${POLL_INTERVAL}s...\"");
        sbSh.AppendLine("log \"\"");
        sbSh.AppendLine("");

        // Polling loop (log-friendly, no spinner)
        sbSh.AppendLine("server_was_up=false");
        sbSh.AppendLine("last_status=\"\"");
        sbSh.AppendLine("while true; do");
        sbSh.AppendLine("  code=$(curl -k -s -o /dev/null -w \"%{http_code}\" \"$STATUS_ENDPOINT\" || true)");
        sbSh.AppendLine("  if [[ \"$code\" == \"200\" ]]; then");
        sbSh.AppendLine("    server_was_up=true");
        sbSh.AppendLine("    if [[ \"$last_status\" != \"running\" ]]; then");
        sbSh.AppendLine("      log \"Server is running... waiting for shutdown\"");
        sbSh.AppendLine("      last_status=\"running\"");
        sbSh.AppendLine("    fi");
        sbSh.AppendLine("    sleep $POLL_INTERVAL");
        sbSh.AppendLine("  else");
        sbSh.AppendLine("    if $server_was_up; then");
        sbSh.AppendLine("      log \"\"");
        sbSh.AppendLine("      log \"Server shutdown detected!\"");
        sbSh.AppendLine("      log \"\"");
        sbSh.AppendLine("      break");
        sbSh.AppendLine("    else");
        sbSh.AppendLine("      if [[ \"$last_status\" != \"waiting\" ]]; then");
        sbSh.AppendLine("        log \"Waiting for server to start...\"");
        sbSh.AppendLine("        last_status=\"waiting\"");
        sbSh.AppendLine("      fi");
        sbSh.AppendLine("      sleep $POLL_INTERVAL");
        sbSh.AppendLine("    fi");
        sbSh.AppendLine("  fi");
        sbSh.AppendLine("done");
        sbSh.AppendLine("");

        // Removal section
        if (pathsToDelete.Count > 0)
        {
            sbSh.AppendLine("log \"Removing mods...\"");
            foreach (var pathToDelete in pathsToDelete)
            {
                var fullPath = pathToDelete.Replace("<SPT_ROOT>", "$SPT_ROOT").Replace("\\", "/");
                var name = Path.GetFileName(pathToDelete.TrimEnd('/', '\\'));
                sbSh.AppendLine($"if [ -e \"{fullPath}\" ]; then");
                sbSh.AppendLine($"  if rm -rf \"{fullPath}\"; then");
                sbSh.AppendLine($"    log \"  [OK] Removed {name}\"");
                sbSh.AppendLine("  else");
                sbSh.AppendLine($"    log \"  [FAIL] {pathToDelete}\"");
                sbSh.AppendLine("  fi");
                sbSh.AppendLine("fi");
                sbSh.AppendLine("");
            }
        }

        // Install section
        if (pendingInstalls.Count > 0)
        {
            // Helper function for copying with ignore rules
            sbSh.AppendLine("# Helper function to copy with ignore rules");
            sbSh.AppendLine("copy_with_ignores() {");
            sbSh.AppendLine("  local src=\"$1\"");
            sbSh.AppendLine("  local dest=\"$2\"");
            sbSh.AppendLine("  shift 2");
            sbSh.AppendLine("  local ignores=(\"$@\")");
            sbSh.AppendLine("  ");
            sbSh.AppendLine("  if [ ${#ignores[@]} -eq 0 ]; then");
            sbSh.AppendLine("    # No ignores, simple copy");
            sbSh.AppendLine("    cp -a \"$src/.\" \"$dest/\"");
            sbSh.AppendLine("    return");
            sbSh.AppendLine("  fi");
            sbSh.AppendLine("  ");
            sbSh.AppendLine("  # Build rsync exclude arguments");
            sbSh.AppendLine("  local exclude_args=\"\"");
            sbSh.AppendLine("  for ignore in \"${ignores[@]}\"; do");
            sbSh.AppendLine("    exclude_args=\"$exclude_args --exclude=$ignore\"");
            sbSh.AppendLine("  done");
            sbSh.AppendLine("  ");
            sbSh.AppendLine("  # Use rsync if available, otherwise fall back to find+cp");
            sbSh.AppendLine("  if command -v rsync &> /dev/null; then");
            sbSh.AppendLine("    eval rsync -a $exclude_args \"$src/\" \"$dest/\"");
            sbSh.AppendLine("  else");
            sbSh.AppendLine("    # Fallback: copy everything, then delete ignored");
            sbSh.AppendLine("    cp -a \"$src/.\" \"$dest/\"");
            sbSh.AppendLine("    for ignore in \"${ignores[@]}\"; do");
            sbSh.AppendLine("      rm -rf \"$dest/$ignore\" 2>/dev/null || true");
            sbSh.AppendLine("      log \"    [SKIP] $ignore\"");
            sbSh.AppendLine("    done");
            sbSh.AppendLine("  fi");
            sbSh.AppendLine("}");
            sbSh.AppendLine("");
            
            sbSh.AppendLine("log \"Installing mods...\"");
            foreach (var mod in pendingInstalls)
            {
                var stagingPath = Staging.UrlToPath[mod.DownloadUrl].Replace("\\", "/");
                var extractedPath = Path.Combine(stagingPath, "extracted").Replace("\\", "/");
                
                // Get ignore paths for this mod
                var ignoreRules = mod.FileRules?
                    .Where(r => r.State == FileCopyRuleState.Ignore && !string.IsNullOrWhiteSpace(r.Path))
                    .Select(r => r.Path.Replace("\\", "/"))
                    .ToList() ?? new List<string>();
                
                sbSh.AppendLine($"log \"Installing: {mod.ModName}\"");
                
                if (ignoreRules.Count > 0)
                {
                    sbSh.AppendLine($"log \"  ({ignoreRules.Count} file(s) will be skipped)\"");
                }
                
                foreach (var installPath in mod.InstallPaths)
                {
                    var sourcePath = installPath[0];
                    var targetPath = installPath[1].Replace("<SPT_ROOT>", "$SPT_ROOT");
                    var fullSourcePath = Path.Combine(extractedPath, sourcePath).Replace("\\", "/");
                    
                    // Filter ignore rules to only those relevant to this install path
                    var relevantIgnores = ignoreRules
                        .Where(r => r.StartsWith(sourcePath + "/", StringComparison.OrdinalIgnoreCase) || 
                                    r.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
                        .Select(r => r.StartsWith(sourcePath + "/", StringComparison.OrdinalIgnoreCase) 
                            ? r.Substring(sourcePath.Length + 1) 
                            : "")
                        .Where(r => !string.IsNullOrEmpty(r))
                        .ToList();
                    
                    sbSh.AppendLine($"if [ -d \"{fullSourcePath}\" ]; then");
                    sbSh.AppendLine($"  mkdir -p \"{targetPath}\"");
                    
                    if (relevantIgnores.Count > 0)
                    {
                        var ignoreArray = string.Join("\" \"", relevantIgnores);
                        sbSh.AppendLine($"  copy_with_ignores \"{fullSourcePath}\" \"{targetPath}\" \"{ignoreArray}\" && log \"  [OK] Copied {sourcePath}\" || log \"  [FAIL] {sourcePath}\"");
                    }
                    else
                    {
                        sbSh.AppendLine($"  cp -a \"{fullSourcePath}/.\" \"{targetPath}/\" && log \"  [OK] Copied {sourcePath}\" || log \"  [FAIL] {sourcePath}\"");
                    }
                    
                    sbSh.AppendLine("fi");
                    sbSh.AppendLine("");
                }
            }
        }

        // Write completion marker file
        var completionData = new
        {
            installed = pendingInstalls.Select(m => m.DownloadUrl).ToList(),
            removed = pendingRemovals.Select(m => m.DownloadUrl).ToList()
        };
        var urlsJson = JsonSerializer.Serialize(completionData, JsonOptions);

        sbSh.AppendLine("log \"\"");
        sbSh.AppendLine("log \"Writing completion marker...\"");
        sbSh.AppendLine($"cat > \"$COMPLETION_FILE\" <<'EOF'");
        sbSh.AppendLine(urlsJson);
        sbSh.AppendLine("EOF");
        sbSh.AppendLine("log \"Done.\"");
        sbSh.AppendLine("");

        sbSh.AppendLine("log \"======================================\"");
        sbSh.AppendLine("log \"  Installation Complete!\"");
        sbSh.AppendLine("log \"======================================\"");
        sbSh.AppendLine("log \"You can now start the SPT server.\"");
        sbSh.AppendLine("log \"The server will automatically mark these mods as installed/removed.\"");

        await File.WriteAllTextAsync(scriptPath, sbSh.ToString());
        try { File.SetAttributes(scriptPath, FileAttributes.Normal); } catch { }
        _logger.Info($"Generated install script: {scriptPath}");
    }
    
    /// <summary>
    /// Launch the install script in a new window (PowerShell on Windows, bash on Linux)
    /// </summary>
    public void LaunchInstallScript()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var scriptPath = Path.Combine(_dataPath, isWindows ? "install-pending-mods.ps1" : "install-pending-mods.sh");
        var logPath = Path.Combine(_dataPath, "install-script.log");
        
        if (!File.Exists(scriptPath))
        {
            _logger.Warning("Install script not found, cannot launch");
            return;
        }
        
        try
        {
            System.Diagnostics.ProcessStartInfo startInfo;
            if (isWindows)
            {
                startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-ExecutionPolicy Bypass -NoProfile -File \"{scriptPath}\"",
                    UseShellExecute = true,
                    CreateNoWindow = false
                };
                System.Diagnostics.Process.Start(startInfo);
                _logger.Info("Launched install script in new window");
            }
            else
            {
                // On Linux (likely headless via SSH), run in background with output to log file
                // Use nohup so it continues even if server restarts
                startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "bash",
                    Arguments = $"-c \"nohup bash '{scriptPath}' > '{logPath}' 2>&1 &\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };
                System.Diagnostics.Process.Start(startInfo);
                
                _logger.Info("========================================");
                _logger.Info("Install script started in background!");
                _logger.Info($"Script: {scriptPath}");
                _logger.Info($"Log:    {logPath}");
                _logger.Info("");
                _logger.Info("To watch progress, run:");
                _logger.Info($"  tail -f \"{logPath}\"");
                _logger.Info("========================================");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to launch install script: {ex.Message}");
        }
    }

    #endregion
}

