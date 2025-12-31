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
    /// The main configuration (modgod.json)
    /// </summary>
    public ModGodConfig Config { get; private set; } = new();

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
        var syncRoots = DefaultSyncRoots.GetEffective(Config.SyncRules);

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

        // Check if we have metadata for this item
        Config.Sources.TryGetValue(relativePath, out var metadata);

        var item = new SourceItem
        {
            Path = relativePath,
            SyncRoot = syncRoot,
            IsDirectory = isDirectory,
            DisplayName = metadata?.DisplayName ?? name,
            Optional = metadata?.Optional ?? false,
            LinkedTo = metadata?.LinkedTo ?? new List<string>(),
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
}
