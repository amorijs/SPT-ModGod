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
    /// <returns>Result with details about what was done</returns>
    public async Task<ApplyChangesResult> ApplyPendingChangesAsync()
    {
        var result = new ApplyChangesResult();

        _logger.Info("Applying staged config changes...");

        // Calculate what mod changes need to be made
        var stagedChanges = _configService.CalculateStagedChanges();
        
        // Check if there's actually a staged file (could be config-only changes like sync exclusions)
        var hasStagedFile = _configService.HasPendingChanges();
        
        if (!stagedChanges.HasChanges && !hasStagedFile)
        {
            _logger.Info("No changes to apply");
            result.Success = true;
            return result;
        }
        
        _logger.Info($"Changes to apply: {stagedChanges.ModsToInstall.Count} installs, " +
                    $"{stagedChanges.ModsToRemove.Count} removals, {stagedChanges.ModsToUpdate.Count} updates" +
                    (hasStagedFile && !stagedChanges.HasChanges ? " (config-only changes)" : ""));

        // First, handle removals (queue them for next startup since DLLs may be locked)
        foreach (var mod in stagedChanges.ModsToRemove)
        {
            // Skip protected mods
            if (mod.IsProtected)
            {
                _logger.Warning($"Cannot remove protected mod: {mod.ModName}");
                continue;
            }
            
            var removalResult = await QueueModForRemovalAsync(mod);
            if (removalResult.Success)
            {
                result.QueuedForRemoval.Add(mod.ModName);
            }
            else
            {
                result.Errors.Add($"Failed to queue removal for {mod.ModName}: {removalResult.Error}");
            }
        }

        // Then, handle new installations
        foreach (var mod in stagedChanges.ModsToInstall)
        {
            var installResult = await InstallModAsync(mod);
            
            if (installResult.Success)
            {
                result.InstalledMods.Add(mod.ModName);
                mod.LastUpdated = DateTime.UtcNow.ToString("o");
                
                // Clear staging after successful install
                _configService.ClearStagingForUrl(mod.DownloadUrl);
            }
            else if (installResult.NeedsRestart)
            {
                // Files are locked - queue for restart
                result.QueuedForInstall.Add(mod.ModName);
            }
            else
            {
                result.Errors.Add($"Failed to install {mod.ModName}: {installResult.Error}");
            }
        }
        
        // Handle updates (reinstalls with different config)
        foreach (var mod in stagedChanges.ModsToUpdate)
        {
            var installResult = await InstallModAsync(mod);
            
            if (installResult.Success)
            {
                result.InstalledMods.Add($"{mod.ModName} (updated)");
                mod.LastUpdated = DateTime.UtcNow.ToString("o");
            }
            else if (installResult.NeedsRestart)
            {
                result.QueuedForInstall.Add($"{mod.ModName} (update)");
            }
            else
            {
                result.Errors.Add($"Failed to update {mod.ModName}: {installResult.Error}");
            }
        }

        // Apply staged config to live config
        await _configService.ApplyStagedToLiveAsync();
        await _configService.SaveStagingIndexAsync();

        // Generate and launch install script if there are queued operations
        if (result.QueuedForInstall.Count > 0 || result.QueuedForRemoval.Count > 0)
        {
            result.InstallScriptPath = await _configService.GenerateInstallScriptAsync();
            
            if (!string.IsNullOrEmpty(result.InstallScriptPath))
            {
                // Launch the auto-install script in a new window
                _configService.LaunchInstallScript();
                result.AutoInstallerLaunched = true;
            }
        }

        result.RequiresRestart = result.QueuedForRemoval.Count > 0 || result.QueuedForInstall.Count > 0;
        result.Success = result.Errors.Count == 0;

        _logger.Info($"Apply complete. Installed: {result.InstalledMods.Count}, " +
                     $"Queued for install: {result.QueuedForInstall.Count}, " +
                     $"Queued for removal: {result.QueuedForRemoval.Count}, " +
                     $"Errors: {result.Errors.Count}");

        return result;
    }

    /// <summary>
    /// Install a mod from staging to actual install paths
    /// </summary>
    private Task<ModOperationResult> InstallModAsync(ModEntry mod)
    {
        var result = new ModOperationResult { ModName = mod.ModName };
        var lockedFiles = new List<string>();

        try
        {
            // Check if mod is staged
            if (!_configService.IsUrlStaged(mod.DownloadUrl))
            {
                result.Error = "Mod is not staged. Please download it first.";
                return Task.FromResult(result);
            }

            var stagingPath = _configService.Staging.UrlToPath[mod.DownloadUrl];
            var extractedPath = Path.Combine(stagingPath, "extracted");

            if (!Directory.Exists(extractedPath))
            {
                result.Error = "Staging extraction path not found.";
                return Task.FromResult(result);
            }

            _logger.Info($"Installing mod: {mod.ModName}");

            // Prepare ignore rules (relative paths from extracted root)
            var ignoreRules = mod.FileRules
                .Where(r => r.State == FileCopyRuleState.Ignore)
                .Select(r => NormalizeRelativePath(r.Path))
                .ToList();

            // Copy files for each install path
            foreach (var installPath in mod.InstallPaths)
            {
                var sourcePath = installPath[0]; // e.g., "BepInEx"
                var targetPath = installPath[1]; // e.g., "<SPT_ROOT>/BepInEx"

                var fullSourcePath = Path.Combine(extractedPath, sourcePath);
                var fullTargetPath = targetPath.Replace("<SPT_ROOT>", _configService.SptRoot);

                if (Directory.Exists(fullSourcePath))
                {
                    _logger.Info($"  Copying {sourcePath} -> {fullTargetPath} (with rules)");
                    CopyWithRules(extractedPath, fullSourcePath, fullTargetPath, ignoreRules, lockedFiles);
                }
                else if (File.Exists(fullSourcePath))
                {
                    _logger.Info($"  Copying file {sourcePath} -> {fullTargetPath} (with rules)");
                    CopyFileWithRules(extractedPath, fullSourcePath, fullTargetPath, ignoreRules, lockedFiles);
                }
                else
                {
                    _logger.Warning($"  Source path not found: {fullSourcePath}");
                }
            }

            // Check if any files were locked
            if (lockedFiles.Count > 0)
            {
                _logger.Warning($"  {lockedFiles.Count} file(s) are locked and will be installed on restart");
                result.NeedsRestart = true;
                result.LockedFiles = lockedFiles;
                return Task.FromResult(result);
            }

            result.Success = true;
            _logger.Success($"Installed mod: {mod.ModName}");
        }
        catch (IOException ex) when (IsFileLockedException(ex))
        {
            _logger.Warning($"Files locked for {mod.ModName}, will install on restart");
            result.NeedsRestart = true;
            result.Error = "Files are in use - will be installed on server restart";
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to install {mod.ModName}: {ex.Message}");
            result.Error = ex.Message;
        }

        return Task.FromResult(result);
    }

    private void CopyWithRules(string extractedRoot, string fullSourcePath, string fullTargetPath, List<string> ignoreRules, List<string> lockedFiles)
    {
        Directory.CreateDirectory(fullTargetPath);

        var files = Directory.GetFiles(fullSourcePath, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(extractedRoot, file);
            if (IsIgnored(relative, ignoreRules))
                continue;

            var relativeFromSource = Path.GetRelativePath(fullSourcePath, file);
            var targetFile = Path.Combine(fullTargetPath, relativeFromSource);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);

            try
            {
                File.Copy(file, targetFile, true);
            }
            catch (IOException ex) when (IsFileLockedException(ex))
            {
                lockedFiles.Add(targetFile);
            }
        }
    }

    private void CopyFileWithRules(string extractedRoot, string fullSourcePath, string fullTargetPath, List<string> ignoreRules, List<string> lockedFiles)
    {
        var relative = Path.GetRelativePath(extractedRoot, fullSourcePath);
        if (IsIgnored(relative, ignoreRules))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(fullTargetPath)!);
        try
        {
            File.Copy(fullSourcePath, fullTargetPath, true);
        }
        catch (IOException ex) when (IsFileLockedException(ex))
        {
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
    /// Queue a mod's files for removal on next startup
    /// </summary>
    private async Task<ModOperationResult> QueueModForRemovalAsync(ModEntry mod)
    {
        var result = new ModOperationResult { ModName = mod.ModName };

        try
        {
            _logger.Info($"Queuing mod for removal: {mod.ModName}");

            var pathsToDelete = new List<string>();

            // Build list of paths to delete based on install paths
            foreach (var installPath in mod.InstallPaths)
            {
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

            if (pathsToDelete.Count > 0)
            {
                await _configService.QueueDeletionsAsync(pathsToDelete);
                result.Success = true;
                _logger.Info($"Queued {pathsToDelete.Count} paths for deletion");
            }
            else
            {
                // No specific paths found - just remove from config
                result.Success = true;
                _logger.Warning($"No specific paths found for {mod.ModName}, will just remove from config");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to queue removal for {mod.ModName}: {ex.Message}");
            result.Error = ex.Message;
        }

        return result;
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
}

public class ApplyChangesResult
{
    public bool Success { get; set; }
    public bool RequiresRestart { get; set; }
    public bool AutoInstallerLaunched { get; set; }
    public List<string> InstalledMods { get; set; } = new();
    public List<string> QueuedForInstall { get; set; } = new();
    public List<string> QueuedForRemoval { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public string? InstallScriptPath { get; set; }
}

public class ModOperationResult
{
    public string ModName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public bool NeedsRestart { get; set; }
    public string? Error { get; set; }
    public List<string> LockedFiles { get; set; } = new();
}
