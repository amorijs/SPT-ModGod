using System.Text.Json.Serialization;

namespace ModGod3.Models;

/// <summary>
/// Main configuration file: ModGodData/modgod.json
/// Contains source item metadata and sync rules.
/// </summary>
public class ModGodConfig
{
    /// <summary>
    /// Metadata for source items, keyed by relative path from SPT root.
    /// Example key: "SPT/user/mods/SAIN" or "BepInEx/plugins/SAIN"
    /// </summary>
    [JsonPropertyName("sources")]
    public Dictionary<string, SourceItemMetadata> Sources { get; set; } = new();

    /// <summary>
    /// Sync rules configuration
    /// </summary>
    [JsonPropertyName("syncRules")]
    public SyncRulesConfig SyncRules { get; set; } = new();

    /// <summary>
    /// Forge API key (stored here for convenience)
    /// </summary>
    [JsonPropertyName("forgeApiKey")]
    public string? ForgeApiKey { get; set; }

    /// <summary>
    /// Default install path mappings for auto-detecting where to install mods
    /// </summary>
    [JsonPropertyName("defaultInstallPaths")]
    public List<InstallPathMapping>? DefaultInstallPaths { get; set; }
}

/// <summary>
/// Metadata for a single source item (file or directory).
/// Keys are the relative path from SPT root (e.g., "SPT/user/mods/SAIN").
/// </summary>
public class SourceItemMetadata
{
    /// <summary>
    /// User-friendly display name. Defaults to the directory/file name if not set.
    /// </summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Whether clients can opt out of this source item.
    /// Default: false (required)
    /// </summary>
    [JsonPropertyName("optional")]
    public bool Optional { get; set; } = false;

    /// <summary>
    /// Related items in other sources. Used for mods with both server and client components.
    /// Example: ["BepInEx/plugins/SAIN"] for a mod at "SPT/user/mods/SAIN"
    /// </summary>
    [JsonPropertyName("linkedTo")]
    public List<string> LinkedTo { get; set; } = new();
}

/// <summary>
/// Sync rules configuration
/// </summary>
public class SyncRulesConfig
{
    /// <summary>
    /// Glob patterns for files/directories to exclude from syncing.
    /// Example: ["*.log", "cache/**", "*/node_modules/**"]
    /// </summary>
    [JsonPropertyName("exclusions")]
    public List<string> Exclusions { get; set; } = new();

    /// <summary>
    /// Additional sync roots beyond the defaults (BepInEx/plugins, SPT/user/mods)
    /// </summary>
    [JsonPropertyName("additionalRoots")]
    public List<string> AdditionalRoots { get; set; } = new();

    /// <summary>
    /// Whether to apply the built-in default exclusion patterns
    /// </summary>
    [JsonPropertyName("useDefaultExclusions")]
    public bool UseDefaultExclusions { get; set; } = true;
}

/// <summary>
/// Mapping for auto-detecting install paths from archive contents
/// </summary>
public class InstallPathMapping
{
    /// <summary>
    /// Directory path in the archive to match (e.g., "BepInEx", "BepInEx/plugins")
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Target path on the server where the source should be installed
    /// </summary>
    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;
}

/// <summary>
/// Built-in default exclusion patterns
/// </summary>
public static class DefaultExclusions
{
    public static readonly List<string> Patterns = new()
    {
        // SPT Core (never sync - clients have their own)
        "BepInEx/plugins/spt/**",
        "BepInEx/patchers/spt-prepatch.dll",

        // Log files
        "**/*.log",
        "**/logs/**",
        "**/log/**",

        // Cache and temporary files
        "**/cache/**",
        "**/temp/**",
        "**/*.tmp",
        "**/*.cache",

        // Development files
        "SPT/user/mods/**/.git/**",
        "SPT/user/mods/**/node_modules/**",
        "SPT/user/mods/**/*.js",
        "SPT/user/mods/**/*.js.map",
        "SPT/user/mods/**/*.ts",
        "SPT/user/mods/**/src/**/*.ts",

        // Admin/Dev marker files
        "**/*.nosync",
        "**/*.nosync.txt",

        // Common mod-specific exclusions
        "BepInEx/plugins/Fika.Headless.dll",
        "SPT/user/mods/fika-server/types/**",
        "SPT/user/mods/fika-server/cache/**",
        "SPT/user/mods/SPT-Realism/ProfileBackups/**",
        "BepInEx/plugins/kmyuhkyuk-EFTApi/cache/**",
        "BepInEx/plugins/DanW-SPTQuestingBots/log/**",
        "SPT/user/mods/*LiveFleaPrices*/config/**",
        "SPT/user/mods/**/output/**",
        "SPT/user/mods/**/*backup*/**",
    };

    /// <summary>
    /// Get effective exclusion patterns based on config
    /// </summary>
    public static List<string> GetEffective(SyncRulesConfig? config)
    {
        if (config == null)
            return Patterns.ToList();

        var result = new List<string>();

        if (config.UseDefaultExclusions)
            result.AddRange(Patterns);

        result.AddRange(config.Exclusions);

        return result;
    }
}

/// <summary>
/// Default install path mappings
/// </summary>
public static class DefaultInstallPaths
{
    public static readonly List<InstallPathMapping> Mappings = new()
    {
        new() { Source = "BepInEx", Target = "BepInEx" },
        new() { Source = "SPT", Target = "SPT" }
    };

    public static List<InstallPathMapping> GetEffective(ModGodConfig? config)
    {
        return config?.DefaultInstallPaths ?? Mappings;
    }
}

/// <summary>
/// Default sync roots (directories that get synced to clients)
/// </summary>
public static class DefaultSyncRoots
{
    public static readonly List<string> Roots = new()
    {
        "BepInEx/plugins",
        "SPT/user/mods"
    };

    public static List<string> GetEffective(SyncRulesConfig? config)
    {
        var result = new List<string>(Roots);

        if (config?.AdditionalRoots != null)
            result.AddRange(config.AdditionalRoots);

        return result.Distinct().ToList();
    }
}
