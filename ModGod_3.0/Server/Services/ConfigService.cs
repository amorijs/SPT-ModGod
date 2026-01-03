using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModGod3.Models;
using ModGod3.Models.Legacy;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace ModGod3.Services;

/// <summary>
/// Core configuration service for ModGod 3.0.
/// Manages the modgod.json config file and provides filesystem scanning.
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton, TypePriority = OnLoadOrder.PreSptModLoader)]
public class ConfigService : IOnLoad
{
    private readonly ModHelper _modHelper;
    private readonly ISptLogger<ConfigService> _logger;

    private string _modPath = string.Empty;
    private string _dataPath = string.Empty;
    private string _sptRoot = string.Empty;

    /// <summary>
    /// The live/active configuration. This represents the current saved state.
    /// Only modified when changes are applied.
    /// </summary>
    public ModGodConfig Config { get; private set; } = new();

    /// <summary>
    /// The staged configuration with pending edits. UI changes write here.
    /// When Apply is clicked, this replaces Config.
    /// </summary>
    public ModGodConfig StagedConfig { get; private set; } = new();

    /// <summary>
    /// True if a legacy v2.x config exists and needs migration
    /// </summary>
    public bool NeedsMigration { get; private set; }

    /// <summary>
    /// True if this is a fresh install (no existing config)
    /// </summary>
    public bool IsFreshInstall { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ConfigService(
        ModHelper modHelper,
        ISptLogger<ConfigService> logger)
    {
        _modHelper = modHelper;
        _logger = logger;
    }

    public string ModPath => _modPath;
    public string DataPath => _dataPath;
    public string SptRoot => _sptRoot;
    public string ConfigPath => Path.Combine(_dataPath, "modgod.json");
    public string StagedConfigPath => Path.Combine(_dataPath, "modgod.staged.json");
    public string LegacyConfigPath => Path.Combine(_dataPath, "serverConfig.json");

    // Standard sync roots
    public string BepInExPluginsPath => Path.Combine(_sptRoot, "BepInEx", "plugins");
    public string SptUserModsPath => Path.Combine(_sptRoot, "SPT", "user", "mods");

    public async Task OnLoad()
    {
        _modPath = _modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

        // SPT root is 4 levels up from mod folder: <SPT_ROOT>/SPT/user/mods/ModGodServer
        _sptRoot = Path.GetFullPath(Path.Combine(_modPath, "..", "..", "..", ".."));

        // Data folder is at SPT root level
        _dataPath = Path.Combine(_sptRoot, "ModGodData");

        // Ensure data directory exists
        Directory.CreateDirectory(_dataPath);

        // Check for migration scenario
        await CheckMigrationStatusAsync();

        // Load or create config
        await LoadConfigAsync();

        // Load staged config (if exists, user has pending changes)
        await LoadStagedConfigAsync();

        _logger.Success($"ModGod 3.0 ConfigService loaded!");
        _logger.Info($"  SPT Root: {_sptRoot}");
        _logger.Info($"  Data Path: {_dataPath}");
        _logger.Info($"  Needs Migration: {NeedsMigration}");
        _logger.Info($"  Fresh Install: {IsFreshInstall}");
    }

    /// <summary>
    /// Check if we need to migrate from v2.x
    /// </summary>
    private Task CheckMigrationStatusAsync()
    {
        var hasV3Config = File.Exists(ConfigPath);
        var hasLegacyConfig = File.Exists(LegacyConfigPath);

        if (hasV3Config)
        {
            // Already migrated or fresh v3.0 install
            NeedsMigration = false;
            IsFreshInstall = false;
        }
        else if (hasLegacyConfig)
        {
            // Legacy config exists, needs migration
            NeedsMigration = true;
            IsFreshInstall = false;
            _logger.Warning("Detected legacy v2.x configuration. Migration required.");
        }
        else
        {
            // No config at all - fresh install
            NeedsMigration = false;
            IsFreshInstall = true;
            _logger.Info("Fresh install detected - will create default configuration");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Load the configuration file
    /// </summary>
    public async Task LoadConfigAsync()
    {
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(ConfigPath);
                Config = JsonSerializer.Deserialize<ModGodConfig>(json, JsonOptions) ?? new ModGodConfig();
                _logger.Info($"Loaded config with {Config.Sources.Count} source items");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to load config: {ex.Message}. Using defaults.");
                Config = new ModGodConfig();
            }
        }
        else if (!NeedsMigration)
        {
            // Create default config
            Config = new ModGodConfig
            {
                SyncRules = new SyncRulesConfig
                {
                    UseDefaultExclusions = true,
                    Exclusions = new List<string>(),
                    AdditionalRoots = new List<string>()
                }
            };
            await SaveConfigAsync();
            _logger.Info("Created default configuration");
        }
        // If NeedsMigration is true, we don't create a config yet - wait for user to trigger migration
    }

    /// <summary>
    /// Save the configuration file
    /// </summary>
    public async Task SaveConfigAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(Config, JsonOptions);
            await File.WriteAllTextAsync(ConfigPath, json);
            _logger.Debug("Configuration saved");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to save config: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Scan the filesystem and return all source items grouped by sync root.
    /// This is a pure function: UI = render(filesOnDisk, sourcesMetadata)
    /// </summary>
    public List<SourceGroup> ScanSourceItems()
    {
        var groups = new List<SourceGroup>();
        // Use staged config to show pending sync root changes
        var syncRoots = DefaultSyncRoots.GetEffective(StagedConfig.SyncRules);

        foreach (var root in syncRoots)
        {
            var fullPath = Path.Combine(_sptRoot, root.Replace('/', Path.DirectorySeparatorChar));

            if (!Directory.Exists(fullPath))
            {
                _logger.Debug($"Sync root does not exist: {root}");
                continue;
            }

            var group = new SourceGroup
            {
                SyncRoot = root,
                DisplayName = GetSyncRootDisplayName(root),
                Items = new List<SourceItem>()
            };

            // Get top-level items (directories and files) in this sync root
            try
            {
                // Directories
                foreach (var dir in Directory.GetDirectories(fullPath))
                {
                    var dirName = Path.GetFileName(dir);
                    var relativePath = $"{root}/{dirName}";

                    var item = CreateSourceItem(relativePath, root, isDirectory: true, fullPath: dir);
                    group.Items.Add(item);
                }

                // Files (less common but possible)
                foreach (var file in Directory.GetFiles(fullPath))
                {
                    var fileName = Path.GetFileName(file);
                    var relativePath = $"{root}/{fileName}";

                    var item = CreateSourceItem(relativePath, root, isDirectory: false, fullPath: file);
                    group.Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Error scanning sync root {root}: {ex.Message}");
            }

            // Sort items: directories first, then alphabetically
            group.Items = group.Items
                .OrderByDescending(i => i.IsDirectory)
                .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            groups.Add(group);
        }

        return groups;
    }

    /// <summary>
    /// Create a SourceItem from filesystem + metadata
    /// </summary>
    private SourceItem CreateSourceItem(string relativePath, string syncRoot, bool isDirectory, string fullPath)
    {
        var name = Path.GetFileName(relativePath.TrimEnd('/'));

        // Check if we have metadata for this item (use staged config to show pending changes)
        StagedConfig.Sources.TryGetValue(relativePath, out var metadata);

        var item = new SourceItem
        {
            Path = relativePath,
            SyncRoot = syncRoot,
            IsDirectory = isDirectory,
            DisplayName = metadata?.DisplayName ?? name,
            Optional = metadata?.Optional ?? false,
            LinkedTo = metadata?.LinkedTo ?? new List<string>(),
            Exclusions = metadata?.Exclusions ?? new List<string>(),
            ExistsOnDisk = true
        };

        // Calculate file count and size
        try
        {
            if (isDirectory)
            {
                var files = Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories);
                item.FileCount = files.Length;
                item.TotalSize = files.Sum(f => new FileInfo(f).Length);
            }
            else
            {
                item.FileCount = 1;
                item.TotalSize = new FileInfo(fullPath).Length;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"Error calculating size for {relativePath}: {ex.Message}");
        }

        return item;
    }

    /// <summary>
    /// Get a friendly display name for a sync root
    /// </summary>
    private static string GetSyncRootDisplayName(string syncRoot)
    {
        return syncRoot switch
        {
            "BepInEx/plugins" => "Client Mods (BepInEx/plugins)",
            "SPT/user/mods" => "Server Mods (SPT/user/mods)",
            _ => syncRoot
        };
    }

    /// <summary>
    /// Update metadata for a source item
    /// </summary>
    public async Task UpdateSourceMetadataAsync(string path, SourceItemMetadata metadata)
    {
        Config.Sources[path] = metadata;
        await SaveConfigAsync();
    }

    /// <summary>
    /// Remove metadata for a source item
    /// </summary>
    public async Task RemoveSourceMetadataAsync(string path)
    {
        if (Config.Sources.Remove(path))
        {
            await SaveConfigAsync();
        }
    }

    /// <summary>
    /// Toggle the optional flag for a source item
    /// </summary>
    public async Task ToggleOptionalAsync(string path)
    {
        if (!Config.Sources.TryGetValue(path, out var metadata))
        {
            metadata = new SourceItemMetadata();
        }

        metadata.Optional = !metadata.Optional;
        Config.Sources[path] = metadata;
        await SaveConfigAsync();
    }

    /// <summary>
    /// Create a link between two source items
    /// </summary>
    public async Task LinkSourceItemsAsync(string path1, string path2)
    {
        // Get or create metadata for both items
        if (!Config.Sources.TryGetValue(path1, out var meta1))
        {
            meta1 = new SourceItemMetadata();
        }

        if (!Config.Sources.TryGetValue(path2, out var meta2))
        {
            meta2 = new SourceItemMetadata();
        }

        // Add bidirectional links
        if (!meta1.LinkedTo.Contains(path2, StringComparer.OrdinalIgnoreCase))
        {
            meta1.LinkedTo.Add(path2);
        }

        if (!meta2.LinkedTo.Contains(path1, StringComparer.OrdinalIgnoreCase))
        {
            meta2.LinkedTo.Add(path1);
        }

        Config.Sources[path1] = meta1;
        Config.Sources[path2] = meta2;
        await SaveConfigAsync();
    }

    /// <summary>
    /// Remove a link between two source items
    /// </summary>
    public async Task UnlinkSourceItemsAsync(string path1, string path2)
    {
        if (Config.Sources.TryGetValue(path1, out var meta1))
        {
            meta1.LinkedTo.RemoveAll(p => p.Equals(path2, StringComparison.OrdinalIgnoreCase));
        }

        if (Config.Sources.TryGetValue(path2, out var meta2))
        {
            meta2.LinkedTo.RemoveAll(p => p.Equals(path1, StringComparison.OrdinalIgnoreCase));
        }

        await SaveConfigAsync();
    }

    /// <summary>
    /// Delete a source item from disk and remove its metadata
    /// </summary>
    public async Task DeleteSourceItemAsync(string path, bool deleteLinked = false)
    {
        var fullPath = Path.Combine(_sptRoot, path.Replace('/', Path.DirectorySeparatorChar));

        // Get linked items before deleting
        var linkedItems = new List<string>();
        if (Config.Sources.TryGetValue(path, out var metadata))
        {
            linkedItems = metadata.LinkedTo.ToList();
        }

        // Delete from disk
        try
        {
            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
                _logger.Info($"Deleted directory: {path}");
            }
            else if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                _logger.Info($"Deleted file: {path}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to delete {path}: {ex.Message}");
            throw;
        }

        // Remove metadata
        Config.Sources.Remove(path);

        // Handle linked items
        if (deleteLinked)
        {
            foreach (var linkedPath in linkedItems)
            {
                try
                {
                    var linkedFullPath = Path.Combine(_sptRoot, linkedPath.Replace('/', Path.DirectorySeparatorChar));

                    if (Directory.Exists(linkedFullPath))
                    {
                        Directory.Delete(linkedFullPath, recursive: true);
                        _logger.Info($"Deleted linked directory: {linkedPath}");
                    }
                    else if (File.Exists(linkedFullPath))
                    {
                        File.Delete(linkedFullPath);
                        _logger.Info($"Deleted linked file: {linkedPath}");
                    }

                    Config.Sources.Remove(linkedPath);
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to delete linked item {linkedPath}: {ex.Message}");
                }
            }
        }
        else
        {
            // Just remove the link references from linked items
            foreach (var linkedPath in linkedItems)
            {
                if (Config.Sources.TryGetValue(linkedPath, out var linkedMeta))
                {
                    linkedMeta.LinkedTo.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        await SaveConfigAsync();
    }

    /// <summary>
    /// Clean up orphaned metadata (entries for items that no longer exist on disk)
    /// </summary>
    public async Task CleanupOrphanedMetadataAsync()
    {
        var orphaned = new List<string>();

        foreach (var path in Config.Sources.Keys)
        {
            var fullPath = Path.Combine(_sptRoot, path.Replace('/', Path.DirectorySeparatorChar));

            if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
            {
                orphaned.Add(path);
            }
        }

        if (orphaned.Count > 0)
        {
            foreach (var path in orphaned)
            {
                Config.Sources.Remove(path);
                _logger.Info($"Removed orphaned metadata: {path}");
            }

            await SaveConfigAsync();
        }
    }

    /// <summary>
    /// Get all effective exclusion patterns
    /// </summary>
    public List<string> GetEffectiveExclusions()
    {
        return DefaultExclusions.GetEffective(Config.SyncRules);
    }

    /// <summary>
    /// Get all effective sync roots
    /// </summary>
    public List<string> GetEffectiveSyncRoots()
    {
        return DefaultSyncRoots.GetEffective(Config.SyncRules);
    }

    #region Staged Config Management

    /// <summary>
    /// Load the staged configuration (working copy for UI edits).
    /// If no staged config exists, use live config as the working copy.
    /// </summary>
    public async Task LoadStagedConfigAsync()
    {
        if (File.Exists(StagedConfigPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(StagedConfigPath);
                StagedConfig = JsonSerializer.Deserialize<ModGodConfig>(json, JsonOptions) ?? new ModGodConfig();
                _logger.Info("Loaded staged config (unsaved changes exist)");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to load staged config: {ex.Message}. Using live config.");
                StagedConfig = CloneConfig(Config);
            }
        }
        else
        {
            // No staged file - clone from live config
            StagedConfig = CloneConfig(Config);
        }
    }

    /// <summary>
    /// Save the staged configuration (called on every UI edit).
    /// Creates modgod.staged.json if it doesn't exist.
    /// </summary>
    public async Task SaveStagedConfigAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(StagedConfig, JsonOptions);
            await File.WriteAllTextAsync(StagedConfigPath, json);
            _logger.Debug("Staged config saved");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to save staged config: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Called after migration is complete to sync all in-memory state.
    /// Resets NeedsMigration flag and syncs StagedConfig with the new Config.
    /// </summary>
    public async Task OnMigrationCompleteAsync()
    {
        NeedsMigration = false;

        // Delete any stale staged config file
        if (File.Exists(StagedConfigPath))
        {
            File.Delete(StagedConfigPath);
        }

        // Sync staged config with the new live config
        StagedConfig = CloneConfig(Config);

        _logger.Info("Migration complete - synced staged config with new config");
        await Task.CompletedTask;
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
        StagedConfig = CloneConfig(Config);
        _logger.Info("Staged config reset to match live config");

        await Task.CompletedTask;
    }

    /// <summary>
    /// Check if there are pending changes (staged file exists).
    /// </summary>
    public bool HasPendingChanges()
    {
        return File.Exists(StagedConfigPath);
    }

    /// <summary>
    /// Detailed comparison between staged and live config.
    /// Returns true if there are actual differences.
    /// </summary>
    public bool HasActualChanges()
    {
        // Compare sources
        if (Config.Sources.Count != StagedConfig.Sources.Count)
            return true;

        foreach (var (path, stagedMeta) in StagedConfig.Sources)
        {
            if (!Config.Sources.TryGetValue(path, out var liveMeta))
                return true;

            if (liveMeta.DisplayName != stagedMeta.DisplayName ||
                liveMeta.Optional != stagedMeta.Optional ||
                !liveMeta.LinkedTo.SequenceEqual(stagedMeta.LinkedTo) ||
                !liveMeta.Exclusions.SequenceEqual(stagedMeta.Exclusions))
                return true;
        }

        // Compare sync rules
        if (Config.SyncRules.UseDefaultExclusions != StagedConfig.SyncRules.UseDefaultExclusions)
            return true;

        if (!Config.SyncRules.Exclusions.SequenceEqual(StagedConfig.SyncRules.Exclusions))
            return true;

        if (!Config.SyncRules.AdditionalRoots.SequenceEqual(StagedConfig.SyncRules.AdditionalRoots))
            return true;

        // Compare Forge API key
        if (Config.ForgeApiKey != StagedConfig.ForgeApiKey)
            return true;

        return false;
    }

    /// <summary>
    /// Apply staged config to live config. Called when user clicks "Apply Changes".
    /// Deletes the staged file after successful apply.
    /// </summary>
    public async Task ApplyChangesAsync()
    {
        // Replace live config with staged config
        Config = CloneConfig(StagedConfig);

        // Save the new live config
        await SaveConfigAsync();

        // Delete the staged config file (no more pending changes)
        if (File.Exists(StagedConfigPath))
        {
            File.Delete(StagedConfigPath);
            _logger.Info("Deleted staged config file after apply");
        }

        _logger.Success("Applied staged changes to live config");
    }

    /// <summary>
    /// Deep clone a config object
    /// </summary>
    private ModGodConfig CloneConfig(ModGodConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        return JsonSerializer.Deserialize<ModGodConfig>(json, JsonOptions) ?? new ModGodConfig();
    }

    #endregion

    #region Staged Operations (UI writes to staged config)

    /// <summary>
    /// Update metadata for a source item (staged)
    /// </summary>
    public async Task UpdateSourceMetadataStagedAsync(string path, SourceItemMetadata metadata)
    {
        StagedConfig.Sources[path] = metadata;
        await SaveStagedConfigAsync();
    }

    /// <summary>
    /// Remove metadata for a source item (staged)
    /// </summary>
    public async Task RemoveSourceMetadataStagedAsync(string path)
    {
        if (StagedConfig.Sources.Remove(path))
        {
            await SaveStagedConfigAsync();
        }
    }

    /// <summary>
    /// Toggle the optional flag for a source item (staged)
    /// </summary>
    public async Task ToggleOptionalStagedAsync(string path)
    {
        if (!StagedConfig.Sources.TryGetValue(path, out var metadata))
        {
            metadata = new SourceItemMetadata();
        }

        metadata.Optional = !metadata.Optional;
        StagedConfig.Sources[path] = metadata;
        await SaveStagedConfigAsync();
    }

    /// <summary>
    /// Create a link between two source items (staged)
    /// </summary>
    public async Task LinkSourceItemsStagedAsync(string path1, string path2)
    {
        if (!StagedConfig.Sources.TryGetValue(path1, out var meta1))
        {
            meta1 = new SourceItemMetadata();
        }

        if (!StagedConfig.Sources.TryGetValue(path2, out var meta2))
        {
            meta2 = new SourceItemMetadata();
        }

        // Add bidirectional links
        if (!meta1.LinkedTo.Contains(path2, StringComparer.OrdinalIgnoreCase))
        {
            meta1.LinkedTo.Add(path2);
        }

        if (!meta2.LinkedTo.Contains(path1, StringComparer.OrdinalIgnoreCase))
        {
            meta2.LinkedTo.Add(path1);
        }

        StagedConfig.Sources[path1] = meta1;
        StagedConfig.Sources[path2] = meta2;
        await SaveStagedConfigAsync();
    }

    /// <summary>
    /// Remove a link between two source items (staged)
    /// </summary>
    public async Task UnlinkSourceItemsStagedAsync(string path1, string path2)
    {
        if (StagedConfig.Sources.TryGetValue(path1, out var meta1))
        {
            meta1.LinkedTo.RemoveAll(p => p.Equals(path2, StringComparison.OrdinalIgnoreCase));
        }

        if (StagedConfig.Sources.TryGetValue(path2, out var meta2))
        {
            meta2.LinkedTo.RemoveAll(p => p.Equals(path1, StringComparison.OrdinalIgnoreCase));
        }

        await SaveStagedConfigAsync();
    }

    /// <summary>
    /// Add a sync root (staged)
    /// </summary>
    public async Task AddSyncRootStagedAsync(string root)
    {
        StagedConfig.SyncRules ??= new SyncRulesConfig();
        StagedConfig.SyncRules.AdditionalRoots ??= new List<string>();

        if (!StagedConfig.SyncRules.AdditionalRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
        {
            StagedConfig.SyncRules.AdditionalRoots.Add(root);
            await SaveStagedConfigAsync();
        }
    }

    /// <summary>
    /// Remove a sync root (staged)
    /// </summary>
    public async Task RemoveSyncRootStagedAsync(string root)
    {
        if (StagedConfig.SyncRules?.AdditionalRoots != null)
        {
            StagedConfig.SyncRules.AdditionalRoots.Remove(root);
            await SaveStagedConfigAsync();
        }
    }

    /// <summary>
    /// Toggle default exclusions setting (staged)
    /// </summary>
    public async Task SetDefaultExclusionsStagedAsync(bool useDefaults)
    {
        StagedConfig.SyncRules ??= new SyncRulesConfig();
        StagedConfig.SyncRules.UseDefaultExclusions = useDefaults;
        await SaveStagedConfigAsync();
    }

    /// <summary>
    /// Add an exclusion pattern (staged)
    /// </summary>
    public async Task AddExclusionStagedAsync(string pattern)
    {
        StagedConfig.SyncRules ??= new SyncRulesConfig();
        StagedConfig.SyncRules.Exclusions ??= new List<string>();

        if (!StagedConfig.SyncRules.Exclusions.Contains(pattern, StringComparer.OrdinalIgnoreCase))
        {
            StagedConfig.SyncRules.Exclusions.Add(pattern);
            await SaveStagedConfigAsync();
        }
    }

    /// <summary>
    /// Remove an exclusion pattern (staged)
    /// </summary>
    public async Task RemoveExclusionStagedAsync(string pattern)
    {
        if (StagedConfig.SyncRules?.Exclusions != null)
        {
            StagedConfig.SyncRules.Exclusions.Remove(pattern);
            await SaveStagedConfigAsync();
        }
    }

    /// <summary>
    /// Get effective sync roots from staged config
    /// </summary>
    public List<string> GetEffectiveSyncRootsStaged()
    {
        return DefaultSyncRoots.GetEffective(StagedConfig.SyncRules);
    }

    /// <summary>
    /// Update per-item exclusions for a source item (staged)
    /// </summary>
    public async Task UpdateSourceExclusionsStagedAsync(string path, List<string> exclusions)
    {
        if (!StagedConfig.Sources.TryGetValue(path, out var metadata))
        {
            metadata = new SourceItemMetadata();
        }

        metadata.Exclusions = exclusions.ToList();
        StagedConfig.Sources[path] = metadata;
        await SaveStagedConfigAsync();
    }

    /// <summary>
    /// Add an exclusion to a source item (staged)
    /// </summary>
    public async Task AddSourceExclusionStagedAsync(string path, string exclusion)
    {
        if (!StagedConfig.Sources.TryGetValue(path, out var metadata))
        {
            metadata = new SourceItemMetadata();
        }

        var normalized = exclusion.Replace("\\", "/").TrimStart('/');
        if (!metadata.Exclusions.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            metadata.Exclusions.Add(normalized);
            StagedConfig.Sources[path] = metadata;
            await SaveStagedConfigAsync();
        }
    }

    /// <summary>
    /// Remove an exclusion from a source item (staged)
    /// </summary>
    public async Task RemoveSourceExclusionStagedAsync(string path, string exclusion)
    {
        if (StagedConfig.Sources.TryGetValue(path, out var metadata))
        {
            var normalized = exclusion.Replace("\\", "/").TrimStart('/');
            metadata.Exclusions.RemoveAll(e => e.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            await SaveStagedConfigAsync();
        }
    }

    #endregion
}
