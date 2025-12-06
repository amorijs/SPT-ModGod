using System.Text.Json.Serialization;

namespace ModGod.Models;

/// <summary>
/// Status of a mod in the configuration.
/// With the staged config system, mods in serverConfig.json are always "Installed".
/// The Pending status is kept for backwards compatibility during migration.
/// </summary>
public enum ModStatus
{
    /// <summary>Legacy: Downloaded to staging, not yet installed. Migrated to Installed on load.</summary>
    Pending,
    
    /// <summary>Installed on the server</summary>
    Installed,
    
    /// <summary>Legacy: Marked for removal. With staged config, removal is handled by comparing staged vs live.</summary>
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

    /// <summary>
    /// Per-file copy rules (overwrite/ignore). Path is relative to the extracted archive root.
    /// </summary>
    [JsonPropertyName("fileRules")]
    public List<FileCopyRule> FileRules { get; set; } = new();

    /// <summary>
    /// If true, this mod cannot be edited or removed (e.g., ModGod itself)
    /// </summary>
    [JsonPropertyName("isProtected")]
    public bool IsProtected { get; set; } = false;
}

/// <summary>
/// How a file should be handled during install
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FileCopyRuleState
{
    Overwrite,
    Ignore
}

public class FileCopyRule
{
    /// <summary>Relative path from extracted root (using forward slashes)</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FileCopyRuleState State { get; set; } = FileCopyRuleState.Overwrite;
}
