using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModGod.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace ModGod.Services;

[Injectable(InjectionType = InjectionType.Singleton, TypePriority = OnLoadOrder.PreSptModLoader)]
public partial class ConfigService : IOnLoad
{
    private readonly ModHelper _modHelper;
    private readonly JsonUtil _jsonUtil;
    private readonly FileUtil _fileUtil;
    private readonly ISptLogger<ConfigService> _logger;

    private string _modPath = string.Empty;
    private string _dataPath = string.Empty;
    private string _sptRoot = string.Empty;

    /// <summary>
    /// The live/active configuration. This is what clients see and what represents
    /// the current installed state. Only modified when changes are applied.
    /// </summary>
    public ServerConfig Config { get; private set; } = new();
    
    /// <summary>
    /// The staged configuration with pending edits. UI changes write here.
    /// When Apply is clicked, this replaces Config.
    /// </summary>
    public ServerConfig StagedConfig { get; private set; } = new();
    
    public StagingIndex Staging { get; private set; } = new();
    public PendingOperations PendingOps { get; private set; } = new();
    
    /// <summary>
    /// True if the config file uses the legacy format and needs migration.
    /// UI should show a migration dialog when this is true.
    /// </summary>
    public bool IsLegacyConfig { get; private set; }

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
    public string StagedConfigPath => Path.Combine(_dataPath, "serverConfig.staged.json");
    public string StagingIndexPath => Path.Combine(_dataPath, "stagingIndex.json");
    public string PendingOpsPath => Path.Combine(_dataPath, "pendingOperations.json");

    // Actual SPT installation paths
    public string BepInExPluginsPath => Path.Combine(_sptRoot, "BepInEx", "plugins");
    public string SptUserModsPath => Path.Combine(_sptRoot, "SPT", "user", "mods");

    public async Task OnLoad()
    {
        _modPath = _modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        
        // SPT root is 4 levels up from mod folder: <SPT_ROOT>/SPT/user/mods/ModGodServer
        // Example: C:\SPT\SPT\user\mods\ModGodServer -> C:\SPT
        _sptRoot = Path.GetFullPath(Path.Combine(_modPath, "..", "..", "..", ".."));
        
        // IMPORTANT: Data folder must be OUTSIDE of SPT/user/mods/ to prevent SPT from
        // scanning extracted DLLs in staging as server mods!
        _dataPath = Path.Combine(_sptRoot, "ModGodData");

        // Ensure data directory exists
        Directory.CreateDirectory(_dataPath);
        Directory.CreateDirectory(StagingPath);

        await LoadConfigAsync();
        await LoadStagedConfigAsync();
        await LoadStagingIndexAsync();
        await LoadPendingOpsAsync();
        
        // Ensure ModGod is in both live and staged config as a protected entry
        await EnsureModGodEntryAsync();
        
        // Apply any pending operations from previous session
        await ApplyPendingOperationsOnStartupAsync();

        _logger.Success($"ModGod ConfigService loaded!");
        _logger.Info($"  SPT Root: {_sptRoot}");
        _logger.Info($"  Data Path: {_dataPath}");
    }

    #region ModGod Self-Registration

    /// <summary>
    /// Ensure ModGod itself is in both live and staged config as a protected entry.
    /// This allows clients to download ModGod from the server.
    /// Note: Only saves to live config file. Staged config is updated in-memory only
    /// to avoid creating a staged file just for ModGod initialization.
    /// </summary>
    private async Task EnsureModGodEntryAsync()
    {
        const string modGodUrl = "{SERVER_URL}/modgod/api/self-download";
        
            var modGodEntry = new ModEntry
            {
                ModName = "ModGod",
                DownloadUrl = modGodUrl,
                Optional = false,
                IsProtected = true,
            Status = ModStatus.Installed,
                LastUpdated = DateTime.UtcNow.ToString("o"),
                InstallPaths = new List<string[]>
                {
                // Note: ModGodUpdater.exe is NOT included here because:
                // 1. It's synced via the self-download mechanism (/modgod/api/self-download)
                // 2. It's at SPT root, not under BepInEx/plugins or SPT/user/mods
                // 3. It doesn't exist on Linux servers
                    new[] { "BepInEx/plugins/ModGodClientEnforcer", "<SPT_ROOT>/BepInEx/plugins/ModGodClientEnforcer" },
                    new[] { "SPT/user/mods/ModGodServer", "<SPT_ROOT>/SPT/user/mods/ModGodServer" }
                }
            };
            
        bool liveNeedsSave = EnsureModGodInConfig(Config, modGodEntry, modGodUrl);
        bool stagedNeedsUpdate = EnsureModGodInConfig(StagedConfig, modGodEntry, modGodUrl);
        
        if (liveNeedsSave)
        {
            await SaveConfigAsync();
            _logger.Info("Added/updated ModGod in live config");
        }
        
        // Note: We update staged config in-memory but DON'T save to file
        // This avoids creating serverConfig.staged.json just for ModGod initialization
        if (stagedNeedsUpdate)
        {
            _logger.Info("Updated ModGod in staged config (in-memory only)");
        }
    }
    
    private bool EnsureModGodInConfig(ServerConfig config, ModEntry template, string expectedUrl)
    {
        var existingModGod = config.ModList.Find(m => m.IsProtected && m.ModName == "ModGod");
        
        if (existingModGod == null)
        {
            // Clone the template for this config
            var entry = new ModEntry
            {
                ModName = template.ModName,
                DownloadUrl = template.DownloadUrl,
                Optional = template.Optional,
                IsProtected = template.IsProtected,
                Status = template.Status,
                LastUpdated = template.LastUpdated,
                InstallPaths = template.InstallPaths.Select(p => new[] { p[0], p[1] }).ToList()
            };
            config.ModList.Insert(0, entry);
            return true;
        }
        else
        {
            bool needsUpdate = false;
            
            if (!existingModGod.IsProtected)
            {
                existingModGod.IsProtected = true;
                needsUpdate = true;
            }
            
            if (existingModGod.Status != ModStatus.Installed)
            {
                existingModGod.Status = ModStatus.Installed;
                needsUpdate = true;
            }
            
            if (existingModGod.DownloadUrl != expectedUrl)
            {
                existingModGod.DownloadUrl = expectedUrl;
                needsUpdate = true;
            }
            
            // Update install paths to match template (removes ModGodUpdater.exe if present)
            var expectedPaths = template.InstallPaths.Select(p => $"{p[0]}|{p[1]}").ToHashSet();
            var currentPaths = existingModGod.InstallPaths.Select(p => $"{p[0]}|{p[1]}").ToHashSet();
            if (!expectedPaths.SetEquals(currentPaths))
            {
                existingModGod.InstallPaths = template.InstallPaths.Select(p => new[] { p[0], p[1] }).ToList();
                needsUpdate = true;
            }
            
            return needsUpdate;
        }
    }

    #endregion

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

        // Safety: ensure new properties are initialized
        Config.RemovalSelections ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        
        // Migrate any legacy Pending/PendingRemoval mods to Installed
        // With staged config system, live config should only have Installed mods
        foreach (var mod in Config.ModList)
        {
            if (mod.Status == ModStatus.Pending || mod.Status == ModStatus.PendingRemoval)
            {
                _logger.Info($"Migrating mod '{mod.ModName}' from {mod.Status} to Installed");
                mod.Status = ModStatus.Installed;
            }
        }
        
        // Check if config uses legacy format (needs user-driven migration)
        IsLegacyConfig = CheckIsLegacyConfig(Config);
        
        if (IsLegacyConfig)
        {
            _logger.Warning("Detected legacy config format. User action required for migration.");
        }
        else
        {
            // Ensure new configs are properly initialized for non-legacy configs
            if (Config.PlayerSyncConfig == null || Config.HeadlessSyncConfig == null)
            {
                Config.PlayerSyncConfig ??= ClientSyncConfig.DefaultPlayerConfig();
                Config.HeadlessSyncConfig ??= ClientSyncConfig.DefaultHeadlessConfig();
                await SaveConfigAsync();
            }
        }
    }
    
    /// <summary>
    /// Check if config uses legacy format (has legacy properties but no new config objects)
    /// </summary>
    private bool CheckIsLegacyConfig(ServerConfig config)
    {
        bool hasLegacyProps = config.SyncExclusions?.Count > 0 || 
                              config.UseDefaultExclusions.HasValue || 
                              config.CustomDefaultExclusions != null ||
                              config.HeadlessSyncPaths?.Count > 0;
        bool hasNewConfig = config.PlayerSyncConfig != null || config.HeadlessSyncConfig != null;
        
        return hasLegacyProps && !hasNewConfig;
    }
    
    /// <summary>
    /// Migrate legacy config to new format with backup. Called by UI when user chooses "Transfer Settings".
    /// </summary>
    public async Task MigrateConfigWithBackupAsync()
    {
        if (!IsLegacyConfig) return;
        
        // Create backup
        var backupPath = ConfigPath + ".bak";
        if (File.Exists(ConfigPath))
        {
            File.Copy(ConfigPath, backupPath, overwrite: true);
            _logger.Info($"Created config backup at: {backupPath}");
        }
        
        // Perform migration on live config
        await MigrateSyncConfigAsync(Config);
        await SaveConfigAsync();
        
        // Reset staged config from the newly migrated live config
        // This ensures staged config matches live and has the migrated settings
        await ResetStagedConfigAsync();
        
        IsLegacyConfig = false;
        _logger.Success("Config migration complete!");
    }
    
    /// <summary>
    /// Start fresh with default config. Called by UI when user chooses "Start Fresh".
    /// </summary>
    public async Task StartFreshConfigAsync()
    {
        // Create backup of old config
        var backupPath = ConfigPath + ".bak";
        if (File.Exists(ConfigPath))
        {
            File.Copy(ConfigPath, backupPath, overwrite: true);
            _logger.Info($"Created config backup at: {backupPath}");
        }
        
        // Create new config preserving only the mod list
        var modList = Config.ModList.ToList();
        Config = new ServerConfig
        {
            ModList = modList,
            PlayerSyncConfig = ClientSyncConfig.DefaultPlayerConfig(),
            HeadlessSyncConfig = ClientSyncConfig.DefaultHeadlessConfig()
        };
        await SaveConfigAsync();
        
        // Also reset staged config
        StagedConfig = new ServerConfig
        {
            ModList = modList.Select(m => CloneMod(m)).ToList(),
            PlayerSyncConfig = ClientSyncConfig.DefaultPlayerConfig(),
            HeadlessSyncConfig = ClientSyncConfig.DefaultHeadlessConfig()
        };
        await SaveStagedConfigAsync();
        
        IsLegacyConfig = false;
        _logger.Success("Started fresh with default config!");
    }
    
    private ModEntry CloneMod(ModEntry mod)
    {
        return new ModEntry
        {
            ModName = mod.ModName,
            DownloadUrl = mod.DownloadUrl,
            Optional = mod.Optional,
            LastUpdated = mod.LastUpdated,
            InstallPaths = mod.InstallPaths.Select(p => new[] { p[0], p[1] }).ToList(),
            Status = mod.Status,
            FileRules = mod.FileRules.ToList(),
            IsProtected = mod.IsProtected,
            InstalledFiles = mod.InstalledFiles.ToList()
        };
    }
    
    /// <summary>
    /// Migrate from legacy sync properties to new ClientSyncConfig format.
    /// Returns true if migration occurred and config should be saved.
    /// </summary>
    private Task<bool> MigrateSyncConfigAsync(ServerConfig config)
    {
        bool needsMigration = false;
        
        // Check if we need to migrate (new properties are null but legacy properties exist)
        bool hasLegacyPlayerConfig = config.SyncExclusions?.Count > 0 || 
                                      config.UseDefaultExclusions.HasValue || 
                                      config.CustomDefaultExclusions != null;
        bool hasLegacyHeadlessConfig = config.HeadlessSyncPaths?.Count > 0;
        bool hasNewConfig = config.PlayerSyncConfig != null || config.HeadlessSyncConfig != null;
        
        // If we already have new config, no migration needed
        if (hasNewConfig)
        {
            // Ensure configs are initialized even if they exist
            config.PlayerSyncConfig ??= ClientSyncConfig.DefaultPlayerConfig();
            config.HeadlessSyncConfig ??= ClientSyncConfig.DefaultHeadlessConfig();
            return Task.FromResult(false);
        }
        
        // Create new configs with defaults
        config.PlayerSyncConfig = ClientSyncConfig.DefaultPlayerConfig();
        config.HeadlessSyncConfig = ClientSyncConfig.DefaultHeadlessConfig();
        
        // Migrate player sync config from legacy properties
        if (hasLegacyPlayerConfig)
        {
            _logger.Info("Migrating legacy player sync configuration...");
            
            // Migrate exclusions
            if (config.SyncExclusions?.Count > 0)
            {
                config.PlayerSyncConfig.ExcludedPaths = config.SyncExclusions.ToList();
                _logger.Info($"  Migrated {config.SyncExclusions.Count} exclusion paths");
            }
            
            // Migrate UseDefaultExclusions
            if (config.UseDefaultExclusions.HasValue)
            {
                config.PlayerSyncConfig.UseDefaultExclusions = config.UseDefaultExclusions.Value;
                _logger.Info($"  Migrated UseDefaultExclusions: {config.UseDefaultExclusions.Value}");
            }
            
            // Migrate custom default exclusion patterns
            if (config.CustomDefaultExclusions != null)
            {
                config.PlayerSyncConfig.ExclusionPatterns = config.CustomDefaultExclusions.ToList();
                _logger.Info($"  Migrated {config.CustomDefaultExclusions.Count} custom exclusion patterns");
            }
            
            needsMigration = true;
        }
        
        // Migrate headless sync config from legacy properties
        if (hasLegacyHeadlessConfig)
        {
            _logger.Info("Migrating legacy headless sync configuration...");
            
            // Convert legacy string paths to SyncPathEntry objects
            // Legacy paths were simple strings like "BepInEx/plugins/SomeMod"
            // We need to determine the appropriate target - for files under standard roots,
            // the target is the same as the source. For other paths, we'll use the path as-is.
            foreach (var path in config.HeadlessSyncPaths!)
            {
                var normalizedPath = NormalizePath(path);
                
                // Determine if this is a path within standard roots
                string target;
                if (normalizedPath.StartsWith("BepInEx/plugins", StringComparison.OrdinalIgnoreCase))
                {
                    target = normalizedPath; // Standard BepInEx path
                }
                else if (normalizedPath.StartsWith("SPT/user/mods", StringComparison.OrdinalIgnoreCase))
                {
                    target = normalizedPath; // Standard SPT mods path
                }
                else
                {
                    // For non-standard paths, assume source == target
                    target = normalizedPath;
                }
                
                config.HeadlessSyncConfig.SyncPaths.Add(new SyncPathEntry
                {
                    Source = normalizedPath,
                    Target = target
                });
            }
            
            _logger.Info($"  Migrated {config.HeadlessSyncPaths.Count} headless sync paths");
            needsMigration = true;
        }
        
        if (needsMigration)
        {
            _logger.Success("Sync configuration migration complete!");
            
            // Clear legacy properties after migration (they'll still be serialized as null)
            config.SyncExclusions = null;
            config.UseDefaultExclusions = null;
            config.CustomDefaultExclusions = null;
            config.HeadlessSyncPaths = null;
        }
        else
        {
            _logger.Info("No legacy sync configuration to migrate, using defaults");
        }
        
        return Task.FromResult(needsMigration || !hasNewConfig); // Save if migrated or if we just created defaults
    }
    
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        return path.Replace('\\', '/').TrimStart('/');
    }

    /// <summary>
    /// Save the live configuration. Only called when applying staged changes.
    /// </summary>
    public async Task SaveConfigAsync()
    {
        var json = JsonSerializer.Serialize(Config, JsonOptions);
        await File.WriteAllTextAsync(ConfigPath, json);
    }

    #endregion

    #region SPT Server Mod Manager (Staged Operations)

    /// <summary>
    /// Add or update a mod in the staged config.
    /// All mods added through the UI go to staged config first.
    /// </summary>
    public async Task AddModToStagedAsync(ModEntry mod)
    {
        // Ensure the mod has Installed status (staged mods represent the "desired" state)
        mod.Status = ModStatus.Installed;
        
        // Check if mod already exists by URL in staged config
        var existing = StagedConfig.ModList.FindIndex(m => m.DownloadUrl == mod.DownloadUrl);
        if (existing >= 0)
        {
            StagedConfig.ModList[existing] = mod;
        }
        else
        {
            StagedConfig.ModList.Add(mod);
        }

        await SaveStagedConfigAsync();
    }

    /// <summary>
    /// Remove a mod from the staged config.
    /// This stages the mod for removal - it won't actually be removed until Apply.
    /// </summary>
    public async Task RemoveModFromStagedAsync(string downloadUrl)
    {
        var mod = StagedConfig.ModList.Find(m => m.DownloadUrl == downloadUrl);
        if (mod != null)
        {
            StagedConfig.ModList.Remove(mod);
            
            // If this mod isn't in live config, it was never installed, so clean up staging
            var isInLive = Config.ModList.Any(m => m.DownloadUrl == downloadUrl);
            if (!isInLive)
            {
                await ClearStagingForUrlAsync(downloadUrl);
            }
            
            await SaveStagedConfigAsync();
        }
    }

    /// <summary>
    /// Persist the user-selected paths to delete for a given mod (keyed by URL).
    /// </summary>
    public void SetRemovalSelection(string downloadUrl, List<string> paths)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
            return;

        StagedConfig.RemovalSelections ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        StagedConfig.RemovalSelections[downloadUrl] = paths ?? new List<string>();
    }

    /// <summary>
    /// Get any previously stored deletion selection for a mod.
    /// </summary>
    public List<string> GetRemovalSelection(string downloadUrl)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
            return new List<string>();

        if (StagedConfig.RemovalSelections != null &&
            StagedConfig.RemovalSelections.TryGetValue(downloadUrl, out var paths))
        {
            return paths;
        }

        return new List<string>();
    }

    /// <summary>
    /// Clear the stored deletion selection for a mod (after apply or when cancelled).
    /// </summary>
    public void ClearRemovalSelection(string downloadUrl)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
            return;

        StagedConfig.RemovalSelections?.Remove(downloadUrl);
    }

    /// <summary>
    /// Update a mod's timestamp in staged config.
    /// </summary>
    public async Task UpdateStagedModTimestampAsync(string downloadUrl)
    {
        var mod = StagedConfig.ModList.Find(m => m.DownloadUrl == downloadUrl);
        if (mod != null)
        {
            mod.LastUpdated = DateTime.UtcNow.ToString("o");
            await SaveStagedConfigAsync();
        }
    }

    /// <summary>
    /// Check if there are staged changes to apply.
    /// </summary>
    public bool HasPendingChanges()
    {
        return HasStagedChanges();
    }

    #endregion
    
    #region Legacy Methods (for backwards compatibility)
    
    /// <summary>
    /// Legacy: Add mod directly to live config. Use AddModToStagedAsync instead.
    /// </summary>
    [Obsolete("Use AddModToStagedAsync instead")]
    public async Task AddModAsync(ModEntry mod)
    {
        await AddModToStagedAsync(mod);
    }

    /// <summary>
    /// Legacy: Mark a mod for removal. Use RemoveModFromStagedAsync instead.
    /// </summary>
    [Obsolete("Use RemoveModFromStagedAsync instead")]
    public async Task MarkModForRemovalAsync(string downloadUrl)
    {
        await RemoveModFromStagedAsync(downloadUrl);
    }

    /// <summary>
    /// Legacy: Remove a pending mod. Use RemoveModFromStagedAsync instead.
    /// </summary>
    [Obsolete("Use RemoveModFromStagedAsync instead")]
    public async Task RemovePendingModAsync(string downloadUrl)
    {
        await RemoveModFromStagedAsync(downloadUrl);
    }

    /// <summary>
    /// Legacy: Update mod status. No longer needed with staged config.
    /// </summary>
    [Obsolete("Status is automatically managed with staged config")]
    public async Task UpdateModStatusAsync(string downloadUrl, ModStatus status)
    {
        // With staged config, we don't use status to track pending changes
        // All mods in staged config are "desired state" = Installed
        await Task.CompletedTask;
    }

    /// <summary>
    /// Legacy: Update mod timestamp.
    /// </summary>
    [Obsolete("Use UpdateStagedModTimestampAsync instead")]
    public async Task UpdateModTimestampAsync(string downloadUrl)
    {
        await UpdateStagedModTimestampAsync(downloadUrl);
    }

    #endregion
}

/// <summary>
/// Completion data written by the PowerShell installer script
/// </summary>
public class CompletionData
{
    public List<string> Installed { get; set; } = new();
    public List<string> Removed { get; set; } = new();
}
