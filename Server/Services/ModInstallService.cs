using ModGod.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace ModGod.Services;

/// <summary>
/// Service for installing and removing mods from the actual SPT installation
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton)]
public class ModInstallService
{
    private readonly ConfigService _configService;
    private readonly ISptLogger<ModInstallService> _logger;

    public ModInstallService(
        ConfigService configService,
        ISptLogger<ModInstallService> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// Apply all pending changes from staged config (installs, removals, and config changes like sync exclusions)
    /// </summary>
    /// <param name="serverUrl">The server URL to use for the install script (e.g., from NavigationManager.BaseUri)</param>
    /// <returns>Result with details about what was done</returns>
    public async Task<ApplyChangesResult> ApplyPendingChangesAsync(string? serverUrl = null)
    {
        var result = new ApplyChangesResult();
        var applyStartTime = DateTime.UtcNow;

        _logger.Info("===============================================================");
        _logger.Info("[Apply] Starting to apply staged config changes...");
        _logger.Info("===============================================================");

        // Calculate what mod changes need to be made
        _logger.Info("[Apply] Calculating staged changes...");
        var stagedChanges = _configService.CalculateStagedChanges();
        
        // Check if there's actually a staged file (could be config-only changes like sync exclusions)
        var hasStagedFile = _configService.HasPendingChanges();
        
        if (!stagedChanges.HasChanges && !hasStagedFile)
        {
            _logger.Info("[Apply] No changes to apply - nothing staged");
            result.Success = true;
            return result;
        }
        
        _logger.Info($"[Apply] Changes summary:");
        _logger.Info($"[Apply]   - Mods to install: {stagedChanges.ModsToInstall.Count}");
        _logger.Info($"[Apply]   - Mods to remove: {stagedChanges.ModsToRemove.Count}");
        _logger.Info($"[Apply]   - Mods to update: {stagedChanges.ModsToUpdate.Count}");
        if (hasStagedFile && !stagedChanges.HasChanges)
        {
            _logger.Info($"[Apply]   - Config-only changes detected");
        }

        // First, handle removals
        // On Linux: Delete files immediately (Linux doesn't lock files in use)
        // On Windows: Queue for deletion on shutdown (files are locked while server runs)
        if (stagedChanges.ModsToRemove.Count > 0)
        {
            _logger.Info($"[Apply] -------- Processing {stagedChanges.ModsToRemove.Count} removal(s) --------");
        }
        var removalIndex = 0;
        foreach (var mod in stagedChanges.ModsToRemove)
        {
            removalIndex++;
            _logger.Info($"[Apply] Removal {removalIndex}/{stagedChanges.ModsToRemove.Count}: {mod.ModName}");
            
            // Skip protected mods
            if (mod.IsProtected)
            {
                _logger.Warning($"[Apply] Skipped - protected mod: {mod.ModName}");
                continue;
            }
            
            var removalResult = await RemoveModAsync(mod);
            if (removalResult.Success)
            {
                if (removalResult.WasImmediate)
                {
                    result.RemovedMods.Add(mod.ModName);
                    _logger.Info($"[Apply] Removed immediately: {mod.ModName} ({removalResult.DeletedCount} files)");
                    
                    // Collect any files that couldn't be deleted
                    if (removalResult.FailedPaths.Count > 0)
                    {
                        result.FailedDeletions.AddRange(removalResult.FailedPaths);
                    }
                }
                else
                {
                    result.QueuedForRemoval.Add(mod.ModName);
                    _logger.Info($"[Apply] Queued for removal on shutdown: {mod.ModName}");
                }
            }
            else
            {
                _logger.Error($"[Apply] Removal failed: {mod.ModName}: {removalResult.Error}");
                result.Errors.Add($"Failed to remove {mod.ModName}: {removalResult.Error}");
            }
        }

        // Then, handle new installations
        if (stagedChanges.ModsToInstall.Count > 0)
        {
            _logger.Info($"[Apply] -------- Processing {stagedChanges.ModsToInstall.Count} installation(s) --------");
        }
        var installIndex = 0;
        foreach (var mod in stagedChanges.ModsToInstall)
        {
            installIndex++;
            _logger.Info($"[Apply] Installation {installIndex}/{stagedChanges.ModsToInstall.Count}: {mod.ModName}");
            
            var installResult = await InstallModAsync(mod);
            
            if (installResult.Success)
            {
                result.InstalledMods.Add(mod.ModName);
                mod.LastUpdated = DateTime.UtcNow.ToString("o");
                
                // Clear staging after successful install
                _logger.Info($"[Apply] Clearing staging for: {mod.ModName}");
                _configService.ClearStagingForUrl(mod.DownloadUrl);
            }
            else if (installResult.NeedsRestart)
            {
                // Files are locked - queue for restart
                result.QueuedForInstall.Add(mod.ModName);
                _logger.Info($"[Apply] Queued for install on restart: {mod.ModName}");
            }
            else
            {
                _logger.Error($"[Apply] Installation failed: {mod.ModName}: {installResult.Error}");
                result.Errors.Add($"Failed to install {mod.ModName}: {installResult.Error}");
            }
        }
        
        // Handle updates (reinstalls with different config)
        if (stagedChanges.ModsToUpdate.Count > 0)
        {
            _logger.Info($"[Apply] -------- Processing {stagedChanges.ModsToUpdate.Count} update(s) --------");
        }
        var updateIndex = 0;
        foreach (var mod in stagedChanges.ModsToUpdate)
        {
            updateIndex++;
            _logger.Info($"[Apply] Update {updateIndex}/{stagedChanges.ModsToUpdate.Count}: {mod.ModName}");
            
            var installResult = await InstallModAsync(mod);
            
            if (installResult.Success)
            {
                result.InstalledMods.Add($"{mod.ModName} (updated)");
                mod.LastUpdated = DateTime.UtcNow.ToString("o");
                _logger.Info($"[Apply] Update complete: {mod.ModName}");
            }
            else if (installResult.NeedsRestart)
            {
                result.QueuedForInstall.Add($"{mod.ModName} (update)");
                _logger.Info($"[Apply] Update queued for restart: {mod.ModName}");
            }
            else
            {
                _logger.Error($"[Apply] Update failed: {mod.ModName}: {installResult.Error}");
                result.Errors.Add($"Failed to update {mod.ModName}: {installResult.Error}");
            }
        }

        // Track config changes (player sync config, headless sync config)
        result.PlayerSyncConfigChanged = !SyncConfigsEqual(
            _configService.Config.PlayerSyncConfig, 
            _configService.StagedConfig.PlayerSyncConfig);
        result.HeadlessSyncConfigChanged = !SyncConfigsEqual(
            _configService.Config.HeadlessSyncConfig, 
            _configService.StagedConfig.HeadlessSyncConfig);
        
        // Count sync paths and exclusions for reporting
        result.PlayerSyncPathCount = _configService.StagedConfig.PlayerSyncConfig?.SyncPaths?.Count ?? 0;
        result.PlayerExclusionCount = _configService.StagedConfig.PlayerSyncConfig?.ExcludedPaths?.Count ?? 0;
        result.HeadlessSyncPathCount = _configService.StagedConfig.HeadlessSyncConfig?.SyncPaths?.Count ?? 0;
        result.HeadlessExclusionCount = _configService.StagedConfig.HeadlessSyncConfig?.ExcludedPaths?.Count ?? 0;
        
        // Track settings changes (default install paths)
        result.DefaultInstallPathsChanged = !DefaultInstallPathsEqual(
            _configService.Config.DefaultInstallPaths,
            _configService.StagedConfig.DefaultInstallPaths);
        result.DefaultInstallPathCount = DefaultInstallPaths.GetEffectiveMappings(_configService.StagedConfig).Count;

        // Apply staged config to live config
        await _configService.ApplyStagedToLiveAsync();
        await _configService.SaveStagingIndexAsync();

        // Generate and launch install script if there are queued operations
        if (result.QueuedForInstall.Count > 0 || result.QueuedForRemoval.Count > 0)
        {
            result.InstallScriptPath = await _configService.GenerateInstallScriptAsync(serverUrl ?? "https://127.0.0.1:6969");
            
            if (!string.IsNullOrEmpty(result.InstallScriptPath))
            {
                // Launch the auto-install script in a new window
                _configService.LaunchInstallScript();
                result.AutoInstallerLaunched = true;
            }
        }

        result.RequiresRestart = result.QueuedForRemoval.Count > 0 || result.QueuedForInstall.Count > 0 || result.RemovedMods.Count > 0;
        result.Success = result.Errors.Count == 0;

        var totalElapsed = DateTime.UtcNow - applyStartTime;
        _logger.Info("===============================================================");
        _logger.Info($"[Apply] COMPLETE - Total time: {totalElapsed.TotalSeconds:F1}s");
        _logger.Info($"[Apply] Results:");
        _logger.Info($"[Apply]   - Installed: {result.InstalledMods.Count}");
        _logger.Info($"[Apply]   - Removed: {result.RemovedMods.Count}");
        _logger.Info($"[Apply]   - Queued for install (restart): {result.QueuedForInstall.Count}");
        _logger.Info($"[Apply]   - Queued for removal (restart): {result.QueuedForRemoval.Count}");
        _logger.Info($"[Apply]   - Errors: {result.Errors.Count}");
        _logger.Info($"[Apply]   - Requires restart: {result.RequiresRestart}");
        _logger.Info("===============================================================");

        return result;
    }

    /// <summary>
    /// Install a mod from staging to actual install paths
    /// </summary>
    private Task<ModOperationResult> InstallModAsync(ModEntry mod)
    {
        var result = new ModOperationResult { ModName = mod.ModName };
        var lockedFiles = new List<string>();
        var installedFiles = new List<string>(); // Track all installed files
        var installStartTime = DateTime.UtcNow;

        try
        {
            _logger.Info($"[Install] ========== Starting installation: {mod.ModName} ==========");
            
            // Check if mod is staged
            _logger.Info($"[Install] Checking staging status for URL: {mod.DownloadUrl[..Math.Min(80, mod.DownloadUrl.Length)]}...");
            if (!_configService.IsUrlStaged(mod.DownloadUrl))
            {
                _logger.Error("[Install] FAILED: Mod is not staged");
                result.Error = "Mod is not staged. Please download it first.";
                return Task.FromResult(result);
            }
            _logger.Info("[Install] Staging verified - mod is staged");

            var stagingPath = _configService.Staging.UrlToPath[mod.DownloadUrl];
            var extractedPath = Path.Combine(stagingPath, "extracted");
            _logger.Info($"[Install] Staging path: {stagingPath}");
            _logger.Info($"[Install] Extracted path: {extractedPath}");

            if (!Directory.Exists(extractedPath))
            {
                _logger.Error($"[Install] FAILED: Extracted path does not exist: {extractedPath}");
                result.Error = "Staging extraction path not found.";
                return Task.FromResult(result);
            }

            // Prepare ignore rules (relative paths from extracted root)
            var ignoreRules = mod.FileRules
                .Where(r => r.State == FileCopyRuleState.Ignore)
                .Select(r => NormalizeRelativePath(r.Path))
                .ToList();
            _logger.Info($"[Install] Ignore rules: {ignoreRules.Count} path(s) will be skipped");
            if (ignoreRules.Count > 0 && ignoreRules.Count <= 10)
            {
                foreach (var rule in ignoreRules)
                {
                    _logger.Info($"[Install]   - {rule}");
                }
            }

            _logger.Info($"[Install] Install paths to process: {mod.InstallPaths.Count}");

            // Copy files for each install path
            var pathIndex = 0;
            foreach (var installPath in mod.InstallPaths)
            {
                pathIndex++;
                var sourcePath = installPath[0]; // e.g., "BepInEx"
                var targetPath = installPath[1]; // e.g., "BepInEx" (relative to SPT root)

                var fullSourcePath = Path.Combine(extractedPath, sourcePath);
                // Handle both old format (<SPT_ROOT>/path) and new format (path) for backwards compatibility
                var targetRel = targetPath.Replace("<SPT_ROOT>", "").TrimStart('/', '\\');
                var fullTargetPath = Path.Combine(_configService.SptRoot, targetRel);

                _logger.Info($"[Install] Processing path {pathIndex}/{mod.InstallPaths.Count}: {sourcePath} -> {targetPath}");

                if (Directory.Exists(fullSourcePath))
                {
                    var fileCount = Directory.GetFiles(fullSourcePath, "*", SearchOption.AllDirectories).Length;
                    _logger.Info($"[Install] Source is directory with {fileCount} file(s)");
                    CopyWithRules(extractedPath, fullSourcePath, fullTargetPath, ignoreRules, lockedFiles, installedFiles);
                }
                else if (File.Exists(fullSourcePath))
                {
                    var fileSize = new FileInfo(fullSourcePath).Length;
                    _logger.Info($"[Install] Source is single file ({fileSize / 1024.0:F1}KB)");
                    
                    // When source is a file, append the filename to the target directory
                    var fileName = Path.GetFileName(fullSourcePath);
                    var actualTargetPath = Path.Combine(fullTargetPath, fileName);
                    _logger.Info($"[Install] Target file path: {actualTargetPath}");
                    
                    CopyFileWithRules(extractedPath, fullSourcePath, actualTargetPath, ignoreRules, lockedFiles, installedFiles);
                }
                else
                {
                    _logger.Warning($"[Install] Source path not found: {fullSourcePath}");
                }
            }

            // Store the list of installed files in the mod entry
            mod.InstalledFiles = installedFiles;
            var elapsed = DateTime.UtcNow - installStartTime;
            _logger.Info($"[Install] File tracking complete: {installedFiles.Count} file(s) tracked");

            // Check if any files were locked
            if (lockedFiles.Count > 0)
            {
                _logger.Warning($"[Install] {lockedFiles.Count} file(s) are locked and will be installed on restart");
                if (lockedFiles.Count <= 10)
                {
                    foreach (var locked in lockedFiles)
                    {
                        _logger.Warning($"[Install]   Locked: {locked}");
                    }
                }
                result.NeedsRestart = true;
                result.LockedFiles = lockedFiles;
                return Task.FromResult(result);
            }

            result.Success = true;
            _logger.Success($"[Install] ========== Completed: {mod.ModName} ({installedFiles.Count} files, {elapsed.TotalSeconds:F1}s) ==========");
        }
        catch (IOException ex) when (IsFileLockedException(ex))
        {
            _logger.Warning($"[Install] Files locked for {mod.ModName}, will install on restart: {ex.Message}");
            result.NeedsRestart = true;
            result.Error = "Files are in use - will be installed on server restart";
        }
        catch (Exception ex)
        {
            _logger.Error($"[Install] FAILED: {mod.ModName}: {ex.GetType().Name}: {ex.Message}");
            result.Error = ex.Message;
        }

        return Task.FromResult(result);
    }

    private void CopyWithRules(string extractedRoot, string fullSourcePath, string fullTargetPath, List<string> ignoreRules, List<string> lockedFiles, List<string> installedFiles)
    {
        Directory.CreateDirectory(fullTargetPath);

        var files = Directory.GetFiles(fullSourcePath, "*", SearchOption.AllDirectories);
        var totalFiles = files.Length;
        var copiedCount = 0;
        var skippedCount = 0;
        long totalBytesCopied = 0;
        var startTime = DateTime.UtcNow;
        
        // Calculate log interval: every 100 files or 10%, whichever is smaller (min 1)
        var logInterval = Math.Max(1, Math.Min(100, totalFiles / 10));
        
        _logger.Info($"[Copy] Starting copy: {totalFiles} file(s) to process");
        
        foreach (var file in files)
        {
            var relativeFromSource = Path.GetRelativePath(fullSourcePath, file);
            var targetFile = Path.Combine(fullTargetPath, relativeFromSource);
            
            // Track ALL files that would be installed (relative path from SPT root using forward slashes)
            // This includes ignored files so we know what files the mod "owns" for uninstall purposes
            var relativePath = Path.GetRelativePath(_configService.SptRoot, targetFile).Replace('\\', '/');
            installedFiles.Add(relativePath);
            
            // Check if this file should be skipped (user chose not to overwrite)
            var relative = Path.GetRelativePath(extractedRoot, file);
            if (IsIgnored(relative, ignoreRules))
            {
                skippedCount++;
                copiedCount++; // Still count for progress
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);

            try
            {
                var fileInfo = new FileInfo(file);
                File.Copy(file, targetFile, true);
                totalBytesCopied += fileInfo.Length;
            }
            catch (IOException ex) when (IsFileLockedException(ex))
            {
                lockedFiles.Add(targetFile);
            }
            
            copiedCount++;
            
            // Log progress at intervals
            if (copiedCount % logInterval == 0 || copiedCount == totalFiles)
            {
                var percent = (double)copiedCount / totalFiles * 100;
                var elapsed = DateTime.UtcNow - startTime;
                _logger.Info($"[Copy] Progress: {copiedCount}/{totalFiles} files ({percent:F0}%) - {totalBytesCopied / 1024.0 / 1024.0:F1}MB copied - elapsed: {elapsed.TotalSeconds:F1}s");
            }
        }
        
        var totalElapsed = DateTime.UtcNow - startTime;
        _logger.Info($"[Copy] Complete: {copiedCount - skippedCount} copied, {skippedCount} skipped, {lockedFiles.Count} locked - {totalBytesCopied / 1024.0 / 1024.0:F1}MB in {totalElapsed.TotalSeconds:F1}s");
    }

    private void CopyFileWithRules(string extractedRoot, string fullSourcePath, string fullTargetPath, List<string> ignoreRules, List<string> lockedFiles, List<string> installedFiles)
    {
        // Track ALL files that would be installed (relative path from SPT root using forward slashes)
        // This includes ignored files so we know what files the mod "owns" for uninstall purposes
        var relativePath = Path.GetRelativePath(_configService.SptRoot, fullTargetPath).Replace('\\', '/');
        installedFiles.Add(relativePath);
        
        // Check if this file should be skipped (user chose not to overwrite)
        var relative = Path.GetRelativePath(extractedRoot, fullSourcePath);
        if (IsIgnored(relative, ignoreRules))
        {
            _logger.Info($"[Copy] Skipped (ignored): {Path.GetFileName(fullSourcePath)}");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullTargetPath)!);
        try
        {
            var fileInfo = new FileInfo(fullSourcePath);
            _logger.Info($"[Copy] Copying single file: {Path.GetFileName(fullSourcePath)} ({fileInfo.Length / 1024.0:F1}KB)");
            File.Copy(fullSourcePath, fullTargetPath, true);
            _logger.Info($"[Copy] Single file copy complete");
        }
        catch (IOException ex) when (IsFileLockedException(ex))
        {
            _logger.Warning($"[Copy] File locked, queued for restart: {fullTargetPath}");
            lockedFiles.Add(fullTargetPath);
        }
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace("\\", "/").TrimStart('/');
    }

    private static bool IsIgnored(string relativePath, List<string> ignoreRules)
    {
        var normalized = NormalizeRelativePath(relativePath);
        foreach (var rule in ignoreRules)
        {
            if (normalized.StartsWith(rule, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Check if an IOException is due to file being locked
    /// </summary>
    private static bool IsFileLockedException(IOException ex)
    {
        var message = ex.Message.ToLowerInvariant();
        return message.Contains("being used by another process") ||
               message.Contains("cannot access") ||
               message.Contains("locked");
    }

    /// <summary>
    /// Remove a mod's files. On Linux, deletes immediately. On Windows, queues for removal on shutdown.
    /// </summary>
    private async Task<ModRemovalResult> RemoveModAsync(ModEntry mod)
    {
        var result = new ModRemovalResult { ModName = mod.ModName };
        var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows);

        try
        {
            _logger.Info($"Removing mod: {mod.ModName} (OS: {(isWindows ? "Windows" : "Linux")})");

            var hasUserSelection = _configService.StagedConfig.RemovalSelections?.ContainsKey(mod.DownloadUrl) == true;
            var selectedPaths = hasUserSelection
                ? _configService.GetRemovalSelection(mod.DownloadUrl)
                : CalculateDeletionPaths(mod);

            // Normalize and de-dupe
            var pathsToDelete = selectedPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Replace("\\", "/"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (pathsToDelete.Count > 0)
            {
                if (isWindows)
                {
                    // Windows: Queue for deletion on shutdown (files are locked)
                    await _configService.QueueDeletionsAsync(pathsToDelete);
                    result.Success = true;
                    result.WasQueued = true;
                    _logger.Info($"Queued {pathsToDelete.Count} path(s) for deletion on shutdown");
                }
                else
                {
                    // Linux: Delete immediately (files can be deleted while in use)
                    var deletedCount = 0;
                    var failedPaths = new List<string>();
                    
                    // First pass: delete files
                    foreach (var path in pathsToDelete)
                    {
                        try
                        {
                            var fullPath = path.Replace("<SPT_ROOT>", _configService.SptRoot);
                            if (File.Exists(fullPath))
                            {
                                File.Delete(fullPath);
                                deletedCount++;
                                _logger.Info($"  Deleted file: {fullPath}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Warning($"  Failed to delete {path}: {ex.Message}");
                            failedPaths.Add(path);
                        }
                    }
                    
                    // Second pass: try to delete empty directories
                    foreach (var path in pathsToDelete)
                    {
                        try
                        {
                            var fullPath = path.Replace("<SPT_ROOT>", _configService.SptRoot);
                            if (Directory.Exists(fullPath) && !Directory.EnumerateFileSystemEntries(fullPath).Any())
                            {
                                Directory.Delete(fullPath);
                                _logger.Info($"  Deleted empty directory: {fullPath}");
                            }
                        }
                        catch
                        {
                            // Ignore directory deletion failures
                        }
                    }
                    
                    // Store any failed paths so user can be notified to manually delete them
                    result.FailedPaths = failedPaths;
                    result.Success = true;
                    result.WasImmediate = true;
                    result.DeletedCount = deletedCount;
                    
                    if (failedPaths.Count > 0)
                    {
                        _logger.Warning($"  {failedPaths.Count} file(s) could not be deleted - user should manually remove them");
                    }
                    _logger.Success($"Deleted {deletedCount} file(s) for {mod.ModName}");
                }
            }
            else
            {
                // No specific paths found - just remove from config
                result.Success = true;
                result.WasImmediate = true;
                _logger.Warning($"No specific paths selected for {mod.ModName}, will just remove from config");
            }

            if (hasUserSelection)
            {
                _configService.ClearRemovalSelection(mod.DownloadUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to remove {mod.ModName}: {ex.Message}");
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Provide suggested deletion paths for a mod (used by UI).
    /// </summary>
    public Task<List<string>> GetSuggestedDeletionPathsAsync(ModEntry mod)
    {
        var paths = CalculateDeletionPaths(mod);
        return Task.FromResult(paths);
    }

    private List<string> CalculateDeletionPaths(ModEntry mod)
    {
        var pathsToDelete = new List<string>();

        // Use InstalledFiles if available (new system - accurate per-file tracking)
        if (mod.InstalledFiles.Count > 0)
        {
            _logger.Info($"  Using tracked InstalledFiles list ({mod.InstalledFiles.Count} files)");

            // Convert relative paths to <SPT_ROOT> paths for deletion
            foreach (var relativePath in mod.InstalledFiles)
            {
                pathsToDelete.Add($"<SPT_ROOT>/{relativePath}");
            }

            // Also find and queue empty parent directories for cleanup
            var directories = mod.InstalledFiles
                .Select(f => Path.GetDirectoryName(f)?.Replace('\\', '/'))
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct()
                .OrderByDescending(d => d!.Length) // Deepest first
                .ToList();

            foreach (var dir in directories)
            {
                if (!string.IsNullOrEmpty(dir))
                {
                    pathsToDelete.Add($"<SPT_ROOT>/{dir}");
                }
            }
        }
        else
        {
            // Fallback: Try to determine paths from install paths (legacy mods without InstalledFiles)
            _logger.Info($"  No InstalledFiles tracked, falling back to folder detection");

            foreach (var installPath in mod.InstallPaths)
            {
                if (installPath.Length < 2) continue;

                var targetPath = installPath[1]; // e.g., "<SPT_ROOT>/BepInEx"
                var fullTargetPath = targetPath.Replace("<SPT_ROOT>", _configService.SptRoot);

                // Try to determine the mod-specific folder
                var modSubfolder = DetermineModSubfolder(mod, fullTargetPath);
                if (modSubfolder != null && Directory.Exists(modSubfolder))
                {
                    pathsToDelete.Add(modSubfolder.Replace(_configService.SptRoot, "<SPT_ROOT>"));
                    _logger.Info($"  Will delete: {modSubfolder}");
                }
            }
        }

        return pathsToDelete
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Replace("\\", "/"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Try to determine the mod-specific subfolder based on mod name and install paths
    /// </summary>
    private string? DetermineModSubfolder(ModEntry mod, string installBasePath)
    {
        // Common patterns:
        // BepInEx/plugins/ModName/
        // SPT/user/mods/ModName/
        
        if (installBasePath.Contains("BepInEx", StringComparison.OrdinalIgnoreCase))
        {
            // Look for a subfolder matching the mod name in plugins
            var pluginsPath = Path.Combine(_configService.SptRoot, "BepInEx", "plugins");
            if (Directory.Exists(pluginsPath))
            {
                // Try exact match first
                var exactMatch = Path.Combine(pluginsPath, mod.ModName);
                if (Directory.Exists(exactMatch))
                    return exactMatch;

                // Try partial match
                var dirs = Directory.GetDirectories(pluginsPath);
                var partial = dirs.FirstOrDefault(d => 
                    Path.GetFileName(d).Contains(mod.ModName, StringComparison.OrdinalIgnoreCase));
                if (partial != null)
                    return partial;
            }
        }
        else if (installBasePath.Contains("SPT", StringComparison.OrdinalIgnoreCase))
        {
            var modsPath = Path.Combine(_configService.SptRoot, "SPT", "user", "mods");
            if (Directory.Exists(modsPath))
            {
                var exactMatch = Path.Combine(modsPath, mod.ModName);
                if (Directory.Exists(exactMatch))
                    return exactMatch;

                var dirs = Directory.GetDirectories(modsPath);
                var partial = dirs.FirstOrDefault(d => 
                    Path.GetFileName(d).Contains(mod.ModName, StringComparison.OrdinalIgnoreCase));
                if (partial != null)
                    return partial;
            }
        }

        return null;
    }

    /// <summary>
    /// Recursively copy a directory, tracking locked files
    /// </summary>
    private void CopyDirectory(string sourceDir, string targetDir, List<string> lockedFiles)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var targetFile = Path.Combine(targetDir, Path.GetFileName(file));
            try
            {
                File.Copy(file, targetFile, true);
            }
            catch (IOException ex) when (IsFileLockedException(ex))
            {
                lockedFiles.Add(targetFile);
                _logger.Warning($"    File locked, will install on restart: {targetFile}");
            }
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var targetSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, targetSubDir, lockedFiles);
        }
    }

    /// <summary>
    /// Compare two DefaultInstallPaths lists for equality.
    /// </summary>
    private static bool DefaultInstallPathsEqual(List<DefaultInstallPathMapping>? a, List<DefaultInstallPathMapping>? b)
    {
        // Get effective mappings (handles null = defaults case)
        var aEffective = a ?? DefaultInstallPaths.Mappings;
        var bEffective = b ?? DefaultInstallPaths.Mappings;
        
        if (aEffective.Count != bEffective.Count)
            return false;
        
        for (int i = 0; i < aEffective.Count; i++)
        {
            if (!aEffective[i].Source.Equals(bEffective[i].Source, StringComparison.OrdinalIgnoreCase) ||
                !aEffective[i].Target.Equals(bEffective[i].Target, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        
        return true;
    }

    /// <summary>
    /// Compare two ClientSyncConfig instances for equality.
    /// </summary>
    private static bool SyncConfigsEqual(ClientSyncConfig? a, ClientSyncConfig? b)
    {
        // Handle nulls
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        
        // Compare sync paths
        if (a.SyncPaths.Count != b.SyncPaths.Count) return false;
        for (int i = 0; i < a.SyncPaths.Count; i++)
        {
            if (a.SyncPaths[i].Source != b.SyncPaths[i].Source ||
                a.SyncPaths[i].Target != b.SyncPaths[i].Target)
                return false;
        }
        
        // Compare excluded paths
        if (!a.ExcludedPaths.SequenceEqual(b.ExcludedPaths))
            return false;
        
        // Compare useDefaultExclusions
        if (a.UseDefaultExclusions != b.UseDefaultExclusions)
            return false;
        
        // Compare exclusion patterns
        var aPat = a.ExclusionPatterns ?? new List<string>();
        var bPat = b.ExclusionPatterns ?? new List<string>();
        if (!aPat.SequenceEqual(bPat))
            return false;
        
        return true;
    }
}

public class ApplyChangesResult
{
    public bool Success { get; set; }
    public bool RequiresRestart { get; set; }
    public bool AutoInstallerLaunched { get; set; }
    public List<string> InstalledMods { get; set; } = new();
    public List<string> QueuedForInstall { get; set; } = new();
    public List<string> QueuedForRemoval { get; set; } = new();
    public List<string> RemovedMods { get; set; } = new(); // Mods removed immediately (Linux)
    public List<string> FailedDeletions { get; set; } = new(); // Files that couldn't be deleted (user should manually remove)
    public List<string> Errors { get; set; } = new();
    public string? InstallScriptPath { get; set; }
    public bool IsWindows { get; set; } = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
    
    // Config changes (player sync config, headless sync config)
    public bool PlayerSyncConfigChanged { get; set; }
    public bool HeadlessSyncConfigChanged { get; set; }
    public int PlayerSyncPathCount { get; set; }
    public int PlayerExclusionCount { get; set; }
    public int HeadlessSyncPathCount { get; set; }
    public int HeadlessExclusionCount { get; set; }
    
    // Settings changes (default install paths)
    public bool DefaultInstallPathsChanged { get; set; }
    public int DefaultInstallPathCount { get; set; }
    
    /// <summary>
    /// Returns true if any sync config (player or headless) changed.
    /// </summary>
    public bool AnySyncConfigChanged => PlayerSyncConfigChanged || HeadlessSyncConfigChanged;
    
    /// <summary>
    /// Returns true if any settings changed.
    /// </summary>
    public bool AnySettingsChanged => DefaultInstallPathsChanged;
}

public class ModOperationResult
{
    public string ModName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public bool NeedsRestart { get; set; }
    public string? Error { get; set; }
    public List<string> LockedFiles { get; set; } = new();
}

public class ModRemovalResult
{
    public string ModName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public bool WasImmediate { get; set; } // True if files were deleted immediately (Linux)
    public bool WasQueued { get; set; } // True if files are queued for shutdown (Windows)
    public int DeletedCount { get; set; }
    public List<string> FailedPaths { get; set; } = new(); // Files that couldn't be deleted (for user to manually remove)
    public string? Error { get; set; }
}
