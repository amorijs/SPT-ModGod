using System.Text.Json.Serialization;

namespace ModGod.Models;

/// <summary>
/// Represents a sync path with source (where files are on server) and target (where they go on client)
/// </summary>
public class SyncPathEntry
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    /// <summary>
    /// Creates a SyncPathEntry where source equals target (standard case)
    /// </summary>
    public static SyncPathEntry Standard(string path) => new() { Source = path, Target = path };
}

/// <summary>
/// Configuration for syncing files to a specific client type (player or headless)
/// </summary>
public class ClientSyncConfig
{
    /// <summary>
    /// Directories to sync. Each entry maps a source path (on server) to a target path (on client).
    /// For standard paths, source equals target.
    /// </summary>
    [JsonPropertyName("syncPaths")]
    public List<SyncPathEntry> SyncPaths { get; set; } = new();

    /// <summary>
    /// Specific files/folders to exclude from syncing (within sync paths).
    /// These are explicit paths, not patterns.
    /// </summary>
    [JsonPropertyName("excludedPaths")]
    public List<string> ExcludedPaths { get; set; } = new();

    /// <summary>
    /// Whether to apply the built-in default exclusion patterns (logs, cache, dev files, etc.)
    /// </summary>
    [JsonPropertyName("useDefaultExclusions")]
    public bool UseDefaultExclusions { get; set; } = true;

    /// <summary>
    /// Custom exclusion patterns (supports globs: *, **, ?).
    /// If null, uses built-in defaults from DefaultSyncExclusions.Patterns.
    /// If user edits patterns, this stores their customized list.
    /// </summary>
    [JsonPropertyName("exclusionPatterns")]
    public List<string>? ExclusionPatterns { get; set; }

    /// <summary>
    /// Creates default config for player clients (sync BepInEx/plugins and SPT/user/mods)
    /// </summary>
    public static ClientSyncConfig DefaultPlayerConfig() => new()
    {
        SyncPaths = new List<SyncPathEntry>
        {
            SyncPathEntry.Standard("BepInEx/plugins"),
            SyncPathEntry.Standard("SPT/user/mods")
        },
        UseDefaultExclusions = true
    };

    /// <summary>
    /// Creates default config for headless clients (empty by default - user configures as needed)
    /// </summary>
    public static ClientSyncConfig DefaultHeadlessConfig() => new()
    {
        SyncPaths = new List<SyncPathEntry>(),
        UseDefaultExclusions = true
    };
}

/// <summary>
/// Represents a default install path mapping for auto-generating mod install paths.
/// When a mod's archive contains a matching source directory, it will be mapped to the target.
/// </summary>
public class DefaultInstallPathMapping
{
    /// <summary>
    /// Directory name/path in the mod archive to match (e.g., "BepInEx", "BepInEx/plugins")
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Target path on the server where the source should be installed (e.g., "BepInEx", "BepInEx/plugins_custom")
    /// </summary>
    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;
}

public class ServerConfig
{
    [JsonPropertyName("modList")]
    public List<ModEntry> ModList { get; set; } = new();

    /// <summary>
    /// User-chosen paths to delete when uninstalling a mod (keyed by download URL).
    /// Paths can be relative (e.g., "BepInEx/plugins/MyMod") or use the legacy &lt;SPT_ROOT&gt; prefix.
    /// Forward slashes are preferred.
    /// </summary>
    [JsonPropertyName("removalSelections")]
    public Dictionary<string, List<string>> RemovalSelections { get; set; } = new();

    /// <summary>
    /// Default install path mappings for auto-generating mod install paths.
    /// When null, uses the built-in defaults (BepInEx -> BepInEx, SPT -> SPT).
    /// </summary>
    [JsonPropertyName("defaultInstallPaths")]
    public List<DefaultInstallPathMapping>? DefaultInstallPaths { get; set; }

    /// <summary>
    /// Sync configuration for player (game) clients
    /// </summary>
    [JsonPropertyName("playerSyncConfig")]
    public ClientSyncConfig? PlayerSyncConfig { get; set; }

    /// <summary>
    /// Sync configuration for headless (dedicated raid-hosting) clients
    /// </summary>
    [JsonPropertyName("headlessSyncConfig")]
    public ClientSyncConfig? HeadlessSyncConfig { get; set; }

    #region Legacy Properties (for migration - will be removed in future version)

    /// <summary>
    /// [LEGACY] Custom paths/patterns to exclude from client sync/manifest.
    /// Migrated to PlayerSyncConfig.ExcludedPaths
    /// </summary>
    [JsonPropertyName("syncExclusions")]
    public List<string>? SyncExclusions { get; set; }

    /// <summary>
    /// [LEGACY] Whether to apply the built-in default exclusions.
    /// Migrated to PlayerSyncConfig.UseDefaultExclusions
    /// </summary>
    [JsonPropertyName("useDefaultExclusions")]
    public bool? UseDefaultExclusions { get; set; }

    /// <summary>
    /// [LEGACY] Custom default exclusion patterns.
    /// Migrated to PlayerSyncConfig.ExclusionPatterns
    /// </summary>
    [JsonPropertyName("customDefaultExclusions")]
    public List<string>? CustomDefaultExclusions { get; set; }

    /// <summary>
    /// [LEGACY] Paths to sync to headless clients.
    /// Migrated to HeadlessSyncConfig.SyncPaths
    /// </summary>
    [JsonPropertyName("headlessSyncPaths")]
    public List<string>? HeadlessSyncPaths { get; set; }

    #endregion
}

/// <summary>
/// Built-in default exclusion patterns for common files that shouldn't be synced.
/// </summary>
public static class DefaultSyncExclusions
{
    /// <summary>
    /// Default patterns that are always excluded unless disabled.
    /// Supports glob patterns: * (any non-slash), ** (any including slashes), ? (single char)
    /// </summary>
    public static readonly List<string> Patterns = new()
    {
        // === SPT Core (never sync - clients have their own) ===
        "BepInEx/plugins/spt/**",
        "BepInEx/patchers/spt-prepatch.dll",

        // === Log files ===
        "**/*.log",
        "**/logs/**",
        "**/log/**",

        // === Cache and temporary files ===
        "**/cache/**",
        "**/temp/**",
        "**/*.tmp",
        "**/*.cache",

        // === Development files ===
        "SPT/user/mods/**/.git/**",
        "SPT/user/mods/**/node_modules/**",
        "SPT/user/mods/**/*.js",
        "SPT/user/mods/**/*.js.map",
        "SPT/user/mods/**/*.ts",
        "SPT/user/mods/**/src/**/*.ts",

        // === Admin/Dev marker files ===
        "**/*.nosync",
        "**/*.nosync.txt",

        // === Common mod-specific exclusions ===
        // Fika
        "BepInEx/plugins/Fika.Headless.dll",
        "SPT/user/mods/fika-server/types/**",
        "SPT/user/mods/fika-server/cache/**",

        // SPT Realism
        "SPT/user/mods/SPT-Realism/ProfileBackups/**",

        // EFT API
        "BepInEx/plugins/kmyuhkyuk-EFTApi/cache/**",

        // Questing Bots
        "BepInEx/plugins/DanW-SPTQuestingBots/log/**",

        // Live Flea Prices
        "SPT/user/mods/*LiveFleaPrices*/config/**",

        // Other common patterns
        "SPT/user/mods/**/output/**",
        "SPT/user/mods/**/*backup*/**",
    };

    /// <summary>
    /// Get all effective exclusions for a ClientSyncConfig
    /// </summary>
    public static List<string> GetEffectiveDefaults(ClientSyncConfig? syncConfig)
    {
        if (syncConfig == null || !syncConfig.UseDefaultExclusions)
            return new List<string>();

        return syncConfig.ExclusionPatterns ?? Patterns;
    }

    /// <summary>
    /// [LEGACY] Get all effective exclusions from legacy ServerConfig properties.
    /// Use GetEffectiveDefaults(ClientSyncConfig) instead.
    /// </summary>
    public static List<string> GetEffectiveDefaults(ServerConfig config)
    {
        // If new config exists, use it
        if (config.PlayerSyncConfig != null)
            return GetEffectiveDefaults(config.PlayerSyncConfig);

        // Fall back to legacy properties
        if (config.UseDefaultExclusions == false)
            return new List<string>();

        return config.CustomDefaultExclusions ?? Patterns;
    }
}

/// <summary>
/// Built-in default install path mappings for mods.
/// </summary>
public static class DefaultInstallPaths
{
    /// <summary>
    /// Default mappings: standard directories map to themselves.
    /// </summary>
    public static readonly List<DefaultInstallPathMapping> Mappings = new()
    {
        new() { Source = "BepInEx", Target = "BepInEx" },
        new() { Source = "SPT", Target = "SPT" }
    };

    /// <summary>
    /// Get effective install path mappings from config, or defaults if not configured.
    /// </summary>
    public static List<DefaultInstallPathMapping> GetEffectiveMappings(ServerConfig? config)
    {
        return config?.DefaultInstallPaths ?? Mappings;
    }
}

/// <summary>
/// Index of staged (downloaded but not yet installed) mods
/// </summary>
public class StagingIndex
{
    /// <summary>
    /// Maps download URL to staging folder path
    /// </summary>
    [JsonPropertyName("urlToPath")]
    public Dictionary<string, string> UrlToPath { get; set; } = new();
}

/// <summary>
/// Pending operations to be applied on next startup
/// </summary>
public class PendingOperations
{
    /// <summary>
    /// Paths to delete on next startup (for mod removal)
    /// </summary>
    [JsonPropertyName("pathsToDelete")]
    public List<string> PathsToDelete { get; set; } = new();
}

/// <summary>
/// Represents changes between staged and live config
/// </summary>
public class StagedChanges
{
    /// <summary>
    /// Mods that are in staged config but not in live (need to be installed)
    /// </summary>
    public List<ModEntry> ModsToInstall { get; set; } = new();

    /// <summary>
    /// Mods that are in live config but not in staged (need to be removed)
    /// </summary>
    public List<ModEntry> ModsToRemove { get; set; } = new();

    /// <summary>
    /// Mods that exist in both but have different install paths/rules (may need reinstall)
    /// </summary>
    public List<ModEntry> ModsToUpdate { get; set; } = new();

    /// <summary>
    /// Total count of changes
    /// </summary>
    public int TotalChanges => ModsToInstall.Count + ModsToRemove.Count + ModsToUpdate.Count;

    /// <summary>
    /// Whether there are any changes
    /// </summary>
    public bool HasChanges => TotalChanges > 0;
}
