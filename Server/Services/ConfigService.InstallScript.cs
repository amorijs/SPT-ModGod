using System.Runtime.InteropServices;
using System.Text.Json;
using ModGod.Models;

namespace ModGod.Services;

/// <summary>
/// Partial class for pending operations and install script generation.
/// </summary>
public partial class ConfigService
{
    /// <summary>
    /// Set to true if a previous install script was interrupted (pending marker exists but no completion marker)
    /// </summary>
    public bool InterruptedScriptDetected { get; private set; }

    /// <summary>
    /// Returns true if there are pending changes waiting for server shutdown to complete
    /// </summary>
    public bool HasPendingShutdown => File.Exists(Path.Combine(_dataPath, "pending-script.json"));

    /// <summary>
    /// Returns true if files were changed and server needs restart to load them
    /// </summary>
    public bool HasPendingReload => File.Exists(Path.Combine(_dataPath, "pending-reload.json"));

    /// <summary>
    /// Returns true if server needs restart for any reason (pending script or reload)
    /// </summary>
    public bool NeedsRestart => HasPendingShutdown || HasPendingReload;

    /// <summary>
    /// Mark that files have changed and server needs restart to load them
    /// </summary>
    public void MarkPendingReload()
    {
        var pendingReloadPath = Path.Combine(_dataPath, "pending-reload.json");
        try
        {
            File.WriteAllText(pendingReloadPath, "{}");
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to write pending reload marker: {ex.Message}");
        }
    }

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
        var pendingScriptPath = Path.Combine(_dataPath, "pending-script.json");
        var completedPath = Path.Combine(_dataPath, "completed-installs.json");
        var pendingReloadPath = Path.Combine(_dataPath, "pending-reload.json");

        // Check for interrupted install script (pending marker exists but no completion marker)
        // Important: Check BEFORE processing completed-installs.json
        if (File.Exists(pendingScriptPath) && !File.Exists(completedPath))
        {
            _logger.Warning("========================================");
            _logger.Warning("ModGod: Previous update script was interrupted!");
            _logger.Warning("Open the ModGod web UI to retry the update.");
            _logger.Warning("========================================");
            InterruptedScriptDetected = true;
            // Don't clear pending-reload - the changes weren't actually applied!
        }
        else
        {
            // Only clear pending-reload marker if script completed successfully (or no script was pending)
            // This means mods were actually loaded on this restart
            if (File.Exists(pendingReloadPath))
            {
                try
                {
                    File.Delete(pendingReloadPath);
                    _logger.Info("ModGod: Cleared pending reload marker (mods are now loaded)");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to clear pending reload marker: {ex.Message}");
                }
            }
        }

        // FIRST: Check if the auto-installer script already handled operations
        // This clears PendingOps.PathsToDelete for any removals the script completed
        await CheckAndMarkInstalledModsAsync();

        // SECOND: Apply any remaining queued deletions as a fallback
        // This handles cases where the script didn't run (e.g., user closed the window)
        // BUT: Don't apply if script was interrupted - let user choose to retry or dismiss
        if (PendingOps.PathsToDelete.Count > 0 && !InterruptedScriptDetected)
        {
            _logger.Info("========================================");
            _logger.Info("ModGod: Processing remaining pending deletions...");
            _logger.Info("========================================");
            await ApplyPendingDeletionsAsync();
        }

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
                    await ClearStagingForUrlAsync(url);
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
                        await ClearStagingForUrlAsync(url);
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
                    // Handle both old format (<SPT_ROOT>/path) and new format (path)
                    var fullPath = path.Contains("<SPT_ROOT>")
                        ? path.Replace("<SPT_ROOT>", _sptRoot)
                        : Path.Combine(_sptRoot, path.TrimStart('/', '\\'));
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
                    // Handle both old format (<SPT_ROOT>/path) and new format (path)
                    var fullPath = path.Contains("<SPT_ROOT>")
                        ? path.Replace("<SPT_ROOT>", _sptRoot)
                        : Path.Combine(_sptRoot, path.TrimStart('/', '\\'));
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
    /// Generate install script (PowerShell for Windows only) to auto-apply installs/removals on server shutdown.
    /// On Linux, files are handled immediately without needing a script.
    /// Uses staged changes to determine what needs to be installed/removed.
    /// </summary>
    public async Task<string?> GenerateInstallScriptAsync(string serverUrl = "https://127.0.0.1:6969")
    {
        var stagedChanges = CalculateStagedChanges();
        return await GenerateInstallScriptAsync(stagedChanges, serverUrl);
    }
    
    /// <summary>
    /// Generate install script with explicitly provided changes.
    /// Only generates PowerShell script on Windows. Linux doesn't need scripts as files aren't locked.
    /// </summary>
    public async Task<string?> GenerateInstallScriptAsync(StagedChanges stagedChanges, string serverUrl = "https://127.0.0.1:6969")
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        
        // On Linux, no script is needed - files are handled immediately
        if (!isWindows)
        {
            return null;
        }
        
        // Filter to only staged mods that have files downloaded
        // Include ModsToInstall, ModsToUpdate, and ModsToReinstall - all need installation
        var pendingInstalls = stagedChanges.ModsToInstall
            .Concat(stagedChanges.ModsToUpdate)
            .Concat(stagedChanges.ModsToReinstall)
            .Where(m => IsUrlStaged(m.DownloadUrl))
            .ToList();

        var pendingRemovals = stagedChanges.ModsToRemove
            .Where(m => !m.IsProtected) // Don't remove protected mods
            .ToList();

        var pathsToDelete = PendingOps.PathsToDelete.ToList();

        var scriptPathPs1 = Path.Combine(_dataPath, "install-pending-mods.ps1");
        var pendingScriptPath = Path.Combine(_dataPath, "pending-script.json");

        // Nothing to do -> delete script and pending marker
        if (pendingInstalls.Count == 0 && pendingRemovals.Count == 0 && pathsToDelete.Count == 0)
        {
            if (File.Exists(scriptPathPs1)) File.Delete(scriptPathPs1);
            if (File.Exists(pendingScriptPath)) File.Delete(pendingScriptPath);
            return null;
        }

        // Write pending marker before generating script
        // This tracks that a script was generated - if it still exists on next startup
        // without a completed-installs.json, we know the script was interrupted
        var pendingMarker = new
        {
            timestamp = DateTime.UtcNow,
            installs = pendingInstalls.Select(m => m.DownloadUrl).ToList(),
            removals = pendingRemovals.Select(m => m.DownloadUrl).ToList()
        };
        await File.WriteAllTextAsync(pendingScriptPath, JsonSerializer.Serialize(pendingMarker, JsonOptions));

        // Generate PowerShell script (Windows only)
        await GeneratePowerShellScriptAsync(scriptPathPs1, pendingInstalls, pendingRemovals, pathsToDelete, serverUrl);

        return scriptPathPs1;
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
        sb.AppendLine("# Track errors for summary at end");
        sb.AppendLine("$script:InstallErrors = @()");
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
                var safeName = mod.ModName.Replace("'", "''");
                sb.AppendLine($"Write-Host '  + {safeName}' -ForegroundColor Green");
            }
            sb.AppendLine("Write-Host ''");
        }

        if (pendingRemovals.Count > 0)
        {
            sb.AppendLine($"Write-Host 'Pending mods to remove: {pendingRemovals.Count}' -ForegroundColor Yellow");
            foreach (var mod in pendingRemovals)
            {
                var safeName = mod.ModName.Replace("'", "''");
                sb.AppendLine($"Write-Host '  - {safeName}' -ForegroundColor Red");
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
        sb.AppendLine("$spinIndex = 0");
        sb.AppendLine("");
        sb.AppendLine("while ($true) {");
        sb.AppendLine("    try {");
        sb.AppendLine("        $response = Invoke-WebRequest -Uri $StatusEndpoint -TimeoutSec 3 -UseBasicParsing");
        sb.AppendLine("        $serverWasUp = $true");
        sb.AppendLine("        $spinIndex++");
        sb.AppendLine("        Write-Host \"`rServer is running... waiting for shutdown [$spinIndex]    \" -NoNewline -ForegroundColor Gray");
        sb.AppendLine("        Start-Sleep -Seconds $PollIntervalSeconds");
        sb.AppendLine("    }");
        sb.AppendLine("    catch {");
        sb.AppendLine("        if ($serverWasUp) {");
        sb.AppendLine("            Write-Host ''");
        sb.AppendLine("            Write-Host ''");
        sb.AppendLine("            Write-Host 'Server shutdown detected!' -ForegroundColor Green");
        sb.AppendLine("            Write-Host ''");
        sb.AppendLine("            break");
        sb.AppendLine("        }");
        sb.AppendLine("        $spinIndex++");
        sb.AppendLine("        Write-Host \"`rWaiting for server to start... [$spinIndex]              \" -NoNewline -ForegroundColor Yellow");
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
            sb.AppendLine("$script:RemovedParentDirs = @()");
            sb.AppendLine("");

            foreach (var pathToDelete in pathsToDelete)
            {
                // Handle both old format (<SPT_ROOT>/path) and new format (path)
                var fullPath = pathToDelete.Contains("<SPT_ROOT>")
                    ? pathToDelete.Replace("<SPT_ROOT>", "$SptRoot")
                    : "$SptRoot\\" + pathToDelete.Replace("/", "\\").TrimStart('\\');
                var safeFileName = Path.GetFileName(pathToDelete.TrimEnd('/', '\\')).Replace("'", "''");
                var safeFileNameDbl = Path.GetFileName(pathToDelete.TrimEnd('/', '\\')).Replace("\"", "`\"");
                var safePathToDelete = pathToDelete.Replace("'", "''");

                sb.AppendLine($"if (Test-Path \"{fullPath}\") {{");
                sb.AppendLine($"    try {{");
                sb.AppendLine($"        $parentDir = Split-Path -Parent \"{fullPath}\"");
                sb.AppendLine($"        Remove-Item -Path \"{fullPath}\" -Recurse -Force -ErrorAction Stop");
                sb.AppendLine($"        Write-Host '  [OK] Removed {safeFileName}' -ForegroundColor Green");
                sb.AppendLine($"        if ($parentDir -and ($script:RemovedParentDirs -notcontains $parentDir)) {{");
                sb.AppendLine($"            $script:RemovedParentDirs += $parentDir");
                sb.AppendLine($"        }}");
                sb.AppendLine($"    }} catch {{");
                sb.AppendLine($"        Write-Host '  [FAIL] {safePathToDelete}: ' $_.Exception.Message -ForegroundColor Red");
                sb.AppendLine($"        $script:InstallErrors += \"Remove {safeFileNameDbl}: $($_.Exception.Message)\"");
                sb.AppendLine($"    }}");
                sb.AppendLine("}");
                sb.AppendLine("");
            }

            // Add empty directory cleanup logic
            sb.AppendLine("# Find all empty directories recursively from start paths");
            sb.AppendLine("function Get-AllEmptyDirectories {");
            sb.AppendLine("    param([string[]]$StartPaths, [string]$SptRoot)");
            sb.AppendLine("    $allEmpty = @()");
            sb.AppendLine("    $visited = @{}");
            sb.AppendLine("    ");
            sb.AppendLine("    foreach ($startPath in $StartPaths) {");
            sb.AppendLine("        $current = $startPath");
            sb.AppendLine("        while ($current -and $current.Length -gt $SptRoot.Length) {");
            sb.AppendLine("            if ($visited.ContainsKey($current)) { break }");
            sb.AppendLine("            $visited[$current] = $true");
            sb.AppendLine("            ");
            sb.AppendLine("            if (Test-Path $current -PathType Container) {");
            sb.AppendLine("                $items = @(Get-ChildItem -Path $current -Force -ErrorAction SilentlyContinue)");
            sb.AppendLine("                if ($items.Count -eq 0) {");
            sb.AppendLine("                    $allEmpty += $current");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("            $current = Split-Path -Parent $current");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("    return $allEmpty | Sort-Object -Property Length -Descending");
            sb.AppendLine("}");
            sb.AppendLine("");
            sb.AppendLine("$emptyDirs = Get-AllEmptyDirectories -StartPaths $script:RemovedParentDirs -SptRoot $SptRoot");
            sb.AppendLine("");
            sb.AppendLine("if ($emptyDirs.Count -gt 0) {");
            sb.AppendLine("    Write-Host ''");
            sb.AppendLine("    Write-Host \"Found $($emptyDirs.Count) empty director$(if ($emptyDirs.Count -eq 1) { 'y' } else { 'ies' }) after removal:\" -ForegroundColor Yellow");
            sb.AppendLine("    foreach ($dir in $emptyDirs) {");
            sb.AppendLine("        $relPath = $dir.Substring($SptRoot.Length).TrimStart('\\', '/')");
            sb.AppendLine("        Write-Host \"  - $relPath\" -ForegroundColor DarkGray");
            sb.AppendLine("    }");
            sb.AppendLine("    Write-Host ''");
            sb.AppendLine("    Write-Host '1. Remove all empty directories' -ForegroundColor Cyan");
            sb.AppendLine("    Write-Host '2. Review one by one' -ForegroundColor Cyan");
            sb.AppendLine("    Write-Host '3. Skip (keep empty directories)' -ForegroundColor Cyan");
            sb.AppendLine("    Write-Host ''");
            sb.AppendLine("    $choice = Read-Host 'Enter choice (1/2/3)'");
            sb.AppendLine("    Write-Host ''");
            sb.AppendLine("");
            sb.AppendLine("    switch ($choice) {");
            sb.AppendLine("        '1' {");
            sb.AppendLine("            Write-Host 'Removing empty directories...' -ForegroundColor Cyan");
            sb.AppendLine("            # Keep removing until no more empty dirs (handles cascading empties)");
            sb.AppendLine("            $totalRemoved = 0");
            sb.AppendLine("            do {");
            sb.AppendLine("                $removedThisPass = 0");
            sb.AppendLine("                $currentEmpty = Get-AllEmptyDirectories -StartPaths $script:RemovedParentDirs -SptRoot $SptRoot");
            sb.AppendLine("                foreach ($dir in $currentEmpty) {");
            sb.AppendLine("                    try {");
            sb.AppendLine("                        Remove-Item -Path $dir -Force -ErrorAction Stop");
            sb.AppendLine("                        $relPath = $dir.Substring($SptRoot.Length).TrimStart('\\', '/')");
            sb.AppendLine("                        Write-Host \"  [OK] Removed $relPath\" -ForegroundColor Green");
            sb.AppendLine("                        $removedThisPass++");
            sb.AppendLine("                        $totalRemoved++");
            sb.AppendLine("                    } catch {");
            sb.AppendLine("                        Write-Host \"  [FAIL] ${dir}: $($_.Exception.Message)\" -ForegroundColor Red");
            sb.AppendLine("                    }");
            sb.AppendLine("                }");
            sb.AppendLine("            } while ($removedThisPass -gt 0)");
            sb.AppendLine("            Write-Host \"Removed $totalRemoved empty director$(if ($totalRemoved -eq 1) { 'y' } else { 'ies' }).\" -ForegroundColor Green");
            sb.AppendLine("        }");
            sb.AppendLine("        '2' {");
            sb.AppendLine("            Write-Host 'Reviewing directories...' -ForegroundColor Cyan");
            sb.AppendLine("            Write-Host ''");
            sb.AppendLine("            # Keep asking until no more empty dirs");
            sb.AppendLine("            do {");
            sb.AppendLine("                $currentEmpty = Get-AllEmptyDirectories -StartPaths $script:RemovedParentDirs -SptRoot $SptRoot");
            sb.AppendLine("                $removedAny = $false");
            sb.AppendLine("                foreach ($dir in $currentEmpty) {");
            sb.AppendLine("                    $relPath = $dir.Substring($SptRoot.Length).TrimStart('\\', '/')");
            sb.AppendLine("                    $confirm = Read-Host \"Remove '$relPath'? (y/n/q to quit)\"");
            sb.AppendLine("                    if ($confirm -eq 'q' -or $confirm -eq 'Q') { break }");
            sb.AppendLine("                    if ($confirm -eq 'y' -or $confirm -eq 'Y') {");
            sb.AppendLine("                        try {");
            sb.AppendLine("                            Remove-Item -Path $dir -Force -ErrorAction Stop");
            sb.AppendLine("                            Write-Host \"  [OK] Removed\" -ForegroundColor Green");
            sb.AppendLine("                            $removedAny = $true");
            sb.AppendLine("                            break"); // Re-scan after each removal to catch cascading empties
            sb.AppendLine("                        } catch {");
            sb.AppendLine("                            Write-Host \"  [FAIL] $($_.Exception.Message)\" -ForegroundColor Red");
            sb.AppendLine("                        }");
            sb.AppendLine("                    } else {");
            sb.AppendLine("                        Write-Host '  [SKIP] Kept' -ForegroundColor DarkGray");
            sb.AppendLine("                    }");
            sb.AppendLine("                }");
            sb.AppendLine("                if ($confirm -eq 'q' -or $confirm -eq 'Q') { break }");
            sb.AppendLine("            } while ($removedAny)");
            sb.AppendLine("        }");
            sb.AppendLine("        default {");
            sb.AppendLine("            Write-Host 'Skipping empty directory cleanup.' -ForegroundColor DarkGray");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine("");
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

                // Escape mod name for PowerShell strings
                var safeModName = mod.ModName.Replace("'", "''");
                var safeModNameDbl = mod.ModName.Replace("\"", "`\"").Replace("'", "''");

                sb.AppendLine($"# {safeModName}");
                sb.AppendLine($"Write-Host 'Installing: {safeModName}' -ForegroundColor Yellow");

                if (ignoreRules.Count > 0)
                {
                    sb.AppendLine($"Write-Host '  ({ignoreRules.Count} file(s) will be skipped)' -ForegroundColor DarkGray");
                }

                foreach (var installPath in mod.InstallPaths)
                {
                    var sourcePath = installPath[0];
                    var safeSourcePath = sourcePath.Replace("'", "''");
                    // Handle both old format (<SPT_ROOT>/path) and new format (path) for backwards compatibility
                    var rawTarget = installPath[1];
                    var targetPath = rawTarget.Contains("<SPT_ROOT>")
                        ? rawTarget.Replace("<SPT_ROOT>", "$SptRoot")
                        : "$SptRoot\\" + rawTarget.Replace("/", "\\").TrimStart('\\');
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

                    sb.AppendLine($"        Write-Host '  [OK] Copied {safeSourcePath}' -ForegroundColor Green");
                    sb.AppendLine($"    }} catch {{");
                    sb.AppendLine($"        Write-Host '  [FAIL] {safeSourcePath}: ' $_.Exception.Message -ForegroundColor Red");
                    sb.AppendLine($"        $script:InstallErrors += \"{safeModNameDbl} ({safeSourcePath}): $($_.Exception.Message)\"");
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
        // Use compact JSON and Base64 encode to avoid all PowerShell escaping issues
        var compactJsonOptions = new JsonSerializerOptions { WriteIndented = false };
        var urlsJson = JsonSerializer.Serialize(completionData, compactJsonOptions);
        var jsonBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(urlsJson));

        sb.AppendLine("# Write completion marker file");
        sb.AppendLine($"$jsonBase64 = '{jsonBase64}'");
        sb.AppendLine("$completedUrls = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($jsonBase64))");
        sb.AppendLine($"$completedUrls | Out-File -FilePath '{completedPath}' -Encoding UTF8");
        sb.AppendLine("Write-Host 'Wrote completion marker for server.' -ForegroundColor DarkGray");
        sb.AppendLine("");

        // Delete the pending marker now that we've completed successfully
        var pendingScriptPath = Path.Combine(_dataPath, "pending-script.json");
        sb.AppendLine("# Delete pending marker (script completed successfully)");
        sb.AppendLine($"Remove-Item -Path '{pendingScriptPath}' -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("");

        // Completion - check for errors
        sb.AppendLine("# Check for errors and display summary");
        sb.AppendLine("if ($script:InstallErrors.Count -gt 0) {");
        sb.AppendLine("    Write-Host ''");
        sb.AppendLine("    Write-Host '======================================' -ForegroundColor Red");
        sb.AppendLine("    Write-Host '  ERRORS OCCURRED                     ' -ForegroundColor Red");
        sb.AppendLine("    Write-Host '======================================' -ForegroundColor Red");
        sb.AppendLine("    Write-Host ''");
        sb.AppendLine("    foreach ($err in $script:InstallErrors) {");
        sb.AppendLine("        Write-Host \"  - $err\" -ForegroundColor Red");
        sb.AppendLine("    }");
        sb.AppendLine("    Write-Host ''");
        sb.AppendLine("    Write-Host 'Some operations failed. Check the errors above.' -ForegroundColor Yellow");
        sb.AppendLine("    Write-Host 'The completion marker was still written - server will attempt to mark mods as installed.' -ForegroundColor DarkGray");
        sb.AppendLine("} else {");
        sb.AppendLine("    Write-Host ''");
        sb.AppendLine("    Write-Host '======================================' -ForegroundColor Green");
        sb.AppendLine("    Write-Host '  All Operations Complete!            ' -ForegroundColor Green");
        sb.AppendLine("    Write-Host '======================================' -ForegroundColor Green");
        sb.AppendLine("}");
        sb.AppendLine("Write-Host ''");
        sb.AppendLine("Write-Host 'You can now start the SPT server.' -ForegroundColor Cyan");
        sb.AppendLine("Write-Host 'The server will automatically detect the completed changes.' -ForegroundColor DarkGray");
        sb.AppendLine("Write-Host ''");
        sb.AppendLine("Write-Host 'You may close this window.' -ForegroundColor DarkGray");

        await File.WriteAllTextAsync(scriptPath, sb.ToString());
        _logger.Info($"Generated install script: {scriptPath}");
    }

    /// <summary>
    /// Launch the install script (PowerShell on Windows only).
    /// On Linux, file operations are handled immediately without needing a script.
    /// </summary>
    public void LaunchInstallScript()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        
        // On Linux, no script is needed - operations are handled immediately
        if (!isWindows)
        {
            _logger.Info("Linux detected - no install script needed (files handled immediately)");
            return;
        }
        
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
                // -NoExit keeps window open so user can see errors or results
                Arguments = $"-NoExit -ExecutionPolicy Bypass -NoProfile -File \"{scriptPath}\"",
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
    /// Retry a previously interrupted install script.
    /// Regenerates the script from the pending marker and launches it.
    /// </summary>
    public async Task<bool> RetryInterruptedScriptAsync(string serverUrl = "https://127.0.0.1:6969")
    {
        var pendingScriptPath = Path.Combine(_dataPath, "pending-script.json");

        if (!File.Exists(pendingScriptPath))
        {
            _logger.Warning("No pending script marker found, cannot retry");
            return false;
        }

        try
        {
            var json = await File.ReadAllTextAsync(pendingScriptPath);
            var pendingData = JsonSerializer.Deserialize<PendingScriptMarker>(json, JsonOptions);

            if (pendingData == null)
            {
                _logger.Warning("Failed to parse pending script marker");
                return false;
            }

            // Build lists of mods to install/remove based on the URLs in the pending marker
            var pendingInstalls = new List<ModEntry>();
            var pendingRemovals = new List<ModEntry>();

            foreach (var url in pendingData.Installs ?? new List<string>())
            {
                // Look in staged config first, then live config
                var mod = StagedConfig.ModList.FirstOrDefault(m => m.DownloadUrl == url)
                    ?? Config.ModList.FirstOrDefault(m => m.DownloadUrl == url);
                if (mod != null && IsUrlStaged(url))
                {
                    pendingInstalls.Add(mod);
                }
            }

            foreach (var url in pendingData.Removals ?? new List<string>())
            {
                var mod = Config.ModList.FirstOrDefault(m => m.DownloadUrl == url);
                if (mod != null && !mod.IsProtected)
                {
                    pendingRemovals.Add(mod);
                }
            }

            var pathsToDelete = PendingOps.PathsToDelete.ToList();

            if (pendingInstalls.Count == 0 && pendingRemovals.Count == 0 && pathsToDelete.Count == 0)
            {
                _logger.Info("No pending operations found to retry - clearing marker");
                ClearInterruptedScriptFlag();
                return false;
            }

            _logger.Info($"Retrying interrupted script: {pendingInstalls.Count} installs, {pendingRemovals.Count} removals");

            // Regenerate and launch the script
            var scriptPath = Path.Combine(_dataPath, "install-pending-mods.ps1");
            await GeneratePowerShellScriptAsync(scriptPath, pendingInstalls, pendingRemovals, pathsToDelete, serverUrl);
            LaunchInstallScript();

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to retry interrupted script: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Clear the interrupted script flag and delete the pending markers
    /// </summary>
    public async Task ClearInterruptedScriptFlagAsync()
    {
        InterruptedScriptDetected = false;

        // Delete pending script marker
        var pendingScriptPath = Path.Combine(_dataPath, "pending-script.json");
        if (File.Exists(pendingScriptPath))
        {
            try
            {
                File.Delete(pendingScriptPath);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to delete pending script marker: {ex.Message}");
            }
        }

        // Also clear pending reload marker since user is dismissing/handling the interrupted state
        var pendingReloadPath = Path.Combine(_dataPath, "pending-reload.json");
        if (File.Exists(pendingReloadPath))
        {
            try
            {
                File.Delete(pendingReloadPath);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to delete pending reload marker: {ex.Message}");
            }
        }

        // Clear pending file deletions since user dismissed the retry
        if (PendingOps.PathsToDelete.Count > 0)
        {
            PendingOps.PathsToDelete.Clear();
            await SavePendingOpsAsync();
            _logger.Info("Cleared pending file deletions (user dismissed retry)");
        }
    }

    // Keep sync version for backwards compatibility
    public void ClearInterruptedScriptFlag() => ClearInterruptedScriptFlagAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Model for deserializing pending-script.json
    /// </summary>
    private class PendingScriptMarker
    {
        public DateTime Timestamp { get; set; }
        public List<string>? Installs { get; set; }
        public List<string>? Removals { get; set; }
    }

    #endregion
}

