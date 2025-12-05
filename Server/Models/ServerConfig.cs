using System.Text.Json.Serialization;

namespace ModGod.Models;

public class ServerConfig
{
    [JsonPropertyName("modList")]
    public List<ModEntry> ModList { get; set; } = new();

    /// <summary>
    /// Paths (relative to SPT root) to exclude from client sync/manifest.
    /// Example: "SPT/user/logs" or "BepInEx/plugins/SomeMod/cache.json"
    /// </summary>
    [JsonPropertyName("syncExclusions")]
    public List<string> SyncExclusions { get; set; } = new();

    /// <summary>
    /// API key for SP-Tarkov Forge integration.
    /// Get yours at: https://forge.sp-tarkov.com/user/api-tokens
    /// </summary>
    [JsonPropertyName("forgeApiKey")]
    public string? ForgeApiKey { get; set; }
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
