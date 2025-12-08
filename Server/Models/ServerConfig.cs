using System.Text.Json.Serialization;

namespace ModGod.Models;

public class ServerConfig
{
    [JsonPropertyName("modList")]
    public List<ModEntry> ModList { get; set; } = new();

    /// <summary>
    /// Custom paths/patterns to exclude from client sync/manifest.
    /// Supports glob patterns: *, **, ?
    /// Example: "SPT/user/mods/MyMod/logs/**" or "**/*.log"
    /// </summary>
    [JsonPropertyName("syncExclusions")]
    public List<string> SyncExclusions { get; set; } = new();
    
    /// <summary>
    /// Whether to apply the built-in default exclusions (logs, cache, dev files, etc.)
    /// Default: true
    /// </summary>
    [JsonPropertyName("useDefaultExclusions")]
    public bool UseDefaultExclusions { get; set; } = true;
    
    /// <summary>
    /// Custom default exclusion patterns (can be modified by user).
    /// If null, uses the built-in defaults from DefaultSyncExclusions.Patterns.
    /// </summary>
    [JsonPropertyName("customDefaultExclusions")]
    public List<string>? CustomDefaultExclusions { get; set; }
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
    /// Get all effective exclusions (custom defaults if set, otherwise built-in)
    /// </summary>
    public static List<string> GetEffectiveDefaults(ServerConfig config)
    {
        if (!config.UseDefaultExclusions)
            return new List<string>();
            
        return config.CustomDefaultExclusions ?? Patterns;
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