using System.Text.Json.Serialization;

namespace BewasModSync.Models;

/// <summary>
/// Status of a mod in the configuration
/// </summary>
public enum ModStatus
{
    /// <summary>Downloaded to staging, not yet installed</summary>
    Pending,
    
    /// <summary>Installed on the server</summary>
    Installed,
    
    /// <summary>Marked for removal on next apply/restart</summary>
    PendingRemoval
}

public class ModEntry
{
    [JsonPropertyName("modName")]
    public string ModName { get; set; } = string.Empty;

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("optional")]
    public bool Optional { get; set; } = false;

    [JsonPropertyName("lastUpdated")]
    public string LastUpdated { get; set; } = string.Empty;

    /// <summary>
    /// Paths mapping from archive structure to SPT install locations.
    /// Each entry is [sourcePath, targetPath] where targetPath uses &lt;SPT_ROOT&gt; placeholder.
    /// Example: ["BepInEx", "&lt;SPT_ROOT&gt;/BepInEx"]
    /// </summary>
    [JsonPropertyName("installPaths")]
    public List<string[]> InstallPaths { get; set; } = new();

    /// <summary>
    /// Current status of this mod
    /// </summary>
    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ModStatus Status { get; set; } = ModStatus.Pending;
}
