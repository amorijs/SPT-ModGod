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
        Config.SyncExclusions ??= new List<string>();
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
                ClearStagingForUrl(downloadUrl);
                await SaveStagingIndexAsync();
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
