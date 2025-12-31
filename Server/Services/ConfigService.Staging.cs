using System.Text.Json;
using ModGod.Models;

namespace ModGod.Services;

/// <summary>
/// Partial class for staged config and staging index management.
/// </summary>
public partial class ConfigService
{
    #region Staged Config Management
    
    /// <summary>
    /// Load the staged configuration (working copy for UI edits).
    /// If no staged config exists, use live config as the working copy (no file created).
    /// </summary>
    public async Task LoadStagedConfigAsync()
    {
        if (File.Exists(StagedConfigPath))
        {
            // Staged file exists - user has unsaved changes
            var json = await File.ReadAllTextAsync(StagedConfigPath);
            StagedConfig = JsonSerializer.Deserialize<ServerConfig>(json, JsonOptions) ?? new ServerConfig();
            _logger.Info("Loaded staged config (unsaved changes exist)");
        }
        else
        {
            // No staged file - use live config as working copy (no changes)
            // Deep clone from live config, but don't create a file
            var json = JsonSerializer.Serialize(Config, JsonOptions);
            StagedConfig = JsonSerializer.Deserialize<ServerConfig>(json, JsonOptions) ?? new ServerConfig();
        }
        
        // Safety: ensure new properties are initialized
        StagedConfig.RemovalSelections ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        StagedConfig.PlayerSyncConfig ??= ClientSyncConfig.DefaultPlayerConfig();
        StagedConfig.HeadlessSyncConfig ??= ClientSyncConfig.DefaultHeadlessConfig();
    }
    
    /// <summary>
    /// Save the staged configuration (called on every UI edit).
    /// This creates serverConfig.staged.json if it doesn't exist.
    /// </summary>
    public async Task SaveStagedConfigAsync()
    {
        var json = JsonSerializer.Serialize(StagedConfig, JsonOptions);
        await File.WriteAllTextAsync(StagedConfigPath, json);
    }
    
    /// <summary>
    /// Reset staged config to match live config (discard all changes).
    /// Deletes the staged file and resets in-memory state.
    /// </summary>
    public async Task ResetStagedConfigAsync()
    {
        // Delete the staged config file
        if (File.Exists(StagedConfigPath))
        {
            File.Delete(StagedConfigPath);
            _logger.Info("Deleted staged config file");
        }
        
        // Reset in-memory staged config to match live config
        var json = JsonSerializer.Serialize(Config, JsonOptions);
        StagedConfig = JsonSerializer.Deserialize<ServerConfig>(json, JsonOptions) ?? new ServerConfig();
        StagedConfig.RemovalSelections ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        StagedConfig.PlayerSyncConfig ??= ClientSyncConfig.DefaultPlayerConfig();
        StagedConfig.HeadlessSyncConfig ??= ClientSyncConfig.DefaultHeadlessConfig();
        _logger.Info("Staged config reset to match live config");
        
        await Task.CompletedTask; // Keep async signature for consistency
    }
    
    /// <summary>
    /// Reload staged config from disk (useful if file was edited externally).
    /// </summary>
    public async Task ReloadStagedConfigFromDiskAsync()
    {
        await LoadStagedConfigAsync();
        _logger.Info("Reloaded staged config from disk");
    }
    
    /// <summary>
    /// Reload live config from disk (useful if file was edited externally).
    /// </summary>
    public async Task ReloadConfigFromDiskAsync()
    {
        await LoadConfigAsync();
        // Also reload staged to stay in sync
        await LoadStagedConfigAsync();
        _logger.Info("Reloaded configs from disk");
    }
    
    /// <summary>
    /// Check if there are unsaved changes (staged file exists).
    /// </summary>
    public bool HasStagedChanges()
    {
        // Simple check: if staged file exists, there are unsaved changes
        return File.Exists(StagedConfigPath);
    }
    
/// <summary>
    /// Detailed check of what changes exist between staged and live config.
    /// Use this for the Apply button to see actual differences.
    /// </summary>
    public bool HasActualStagedChanges()
    {
        // Check for pending reinstalls
        if (StagedConfig.PendingReinstallUrls.Count > 0)
            return true;
        
        // Compare mod lists
        var liveUrls = Config.ModList.Select(m => m.DownloadUrl).ToHashSet();
        var stagedUrls = StagedConfig.ModList.Select(m => m.DownloadUrl).ToHashSet();
        
        // Check for added/removed mods
        if (!liveUrls.SetEquals(stagedUrls))
            return true;
        
        // Check for modified mods (compare each mod's properties)
        foreach (var stagedMod in StagedConfig.ModList)
        {
            var liveMod = Config.ModList.Find(m => m.DownloadUrl == stagedMod.DownloadUrl);
            if (liveMod == null)
                return true; // New mod
            
            // Compare key properties
            if (liveMod.ModName != stagedMod.ModName ||
                liveMod.Optional != stagedMod.Optional ||
                !InstallPathsEqual(liveMod.InstallPaths, stagedMod.InstallPaths) ||
                !FileRulesEqual(liveMod.FileRules, stagedMod.FileRules))
            {
                return true;
            }
        }
        
        // Check player sync config changes
        if (!SyncConfigsEqual(Config.PlayerSyncConfig, StagedConfig.PlayerSyncConfig))
            return true;
        
        // Check headless sync config changes
        if (!SyncConfigsEqual(Config.HeadlessSyncConfig, StagedConfig.HeadlessSyncConfig))
            return true;
        
        // Check default install paths changes
        if (!DefaultInstallPathsEqual(Config.DefaultInstallPaths, StagedConfig.DefaultInstallPaths))
            return true;
        
        return false;
    }
    
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
    
    private static bool InstallPathsEqual(List<string[]> a, List<string[]> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Length != b[i].Length) return false;
            for (int j = 0; j < a[i].Length; j++)
            {
                if (a[i][j] != b[i][j]) return false;
            }
        }
        return true;
    }
    
    private static bool FileRulesEqual(List<FileCopyRule> a, List<FileCopyRule> b)
    {
        if (a.Count != b.Count) return false;
        var aSet = a.Select(r => $"{r.Path}:{r.State}").ToHashSet();
        var bSet = b.Select(r => $"{r.Path}:{r.State}").ToHashSet();
        return aSet.SetEquals(bSet);
    }
    
    /// <summary>
    /// Calculate what changes need to be applied (mods to add/remove/update).
    /// </summary>
    public StagedChanges CalculateStagedChanges()
    {
        var changes = new StagedChanges();
        
        var liveModsByUrl = Config.ModList.ToDictionary(m => m.DownloadUrl);
        var stagedModsByUrl = StagedConfig.ModList.ToDictionary(m => m.DownloadUrl);
        
        // Find mods to add (in staged but not in live)
        foreach (var stagedMod in StagedConfig.ModList)
        {
            if (!liveModsByUrl.ContainsKey(stagedMod.DownloadUrl))
            {
                changes.ModsToInstall.Add(stagedMod);
            }
            else
            {
                // Check if mod needs update (properties changed)
                var liveMod = liveModsByUrl[stagedMod.DownloadUrl];
                if (!InstallPathsEqual(liveMod.InstallPaths, stagedMod.InstallPaths) ||
                    !FileRulesEqual(liveMod.FileRules, stagedMod.FileRules))
                {
                    changes.ModsToUpdate.Add(stagedMod);
                }
            }
        }
        
        // Find mods to remove (in live but not in staged)
        foreach (var liveMod in Config.ModList)
        {
            if (!stagedModsByUrl.ContainsKey(liveMod.DownloadUrl))
            {
                changes.ModsToRemove.Add(liveMod);
            }
        }
        
        return changes;
    }
    
    /// <summary>
    /// Apply staged config to live config. Called when user clicks "Apply Changes".
    /// Deletes the staged file after successful apply.
    /// Returns the changes that were applied.
    /// </summary>
    public async Task<StagedChanges> ApplyStagedToLiveAsync()
    {
        var changes = CalculateStagedChanges();
        
        // Clear pending reinstall tracking - these are now applied
        StagedConfig.PendingReinstallUrls.Clear();
        
        // Replace live config with staged config
        var json = JsonSerializer.Serialize(StagedConfig, JsonOptions);
        Config = JsonSerializer.Deserialize<ServerConfig>(json, JsonOptions) ?? new ServerConfig();
        
        // Save the new live config
        await SaveConfigAsync();
        
        // Delete the staged config file (no more pending changes)
        if (File.Exists(StagedConfigPath))
        {
            File.Delete(StagedConfigPath);
            _logger.Info("Deleted staged config file after apply");
        }
        
        // Clear all staging data (downloaded mod archives) - they're no longer needed
        await ClearAllStagingAsync();
        
        _logger.Info($"Applied staged config: {changes.ModsToInstall.Count} to install, " +
                    $"{changes.ModsToRemove.Count} to remove, {changes.ModsToUpdate.Count} to update");
        
        return changes;
    }

    #endregion

    #region Staging Index Management

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

    /// <summary>
    /// Clear all staging data (all downloaded mod archives)
    /// </summary>
    public async Task ClearAllStagingAsync()
    {
        var clearedCount = 0;
        
        // Delete all staging folders
        if (Directory.Exists(StagingPath))
        {
            foreach (var dir in Directory.GetDirectories(StagingPath))
            {
                try
                {
                    Directory.Delete(dir, true);
                    clearedCount++;
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to delete staging folder {dir}: {ex.Message}");
                }
            }
        }
        
        // Clear the staging index
        Staging.UrlToPath.Clear();
        await SaveStagingIndexAsync();
        
        if (clearedCount > 0)
        {
            _logger.Info($"Cleared {clearedCount} staging folders");
        }
    }

    #endregion
}

