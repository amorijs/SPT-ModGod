using System.Text.Json.Serialization;

namespace ModGod3.Models;

/// <summary>
/// Manifest of all files that should exist for synced source items.
/// Generated from filesystem state and filtered by sync rules.
/// </summary>
public class FileManifest
{
    /// <summary>
    /// ModGod server version - clients should match this version
    /// </summary>
    [JsonPropertyName("modGodVersion")]
    public string ModGodVersion { get; set; } = string.Empty;

    /// <summary>
    /// When the manifest was generated
    /// </summary>
    [JsonPropertyName("generatedAt")]
    public string GeneratedAt { get; set; } = DateTime.UtcNow.ToString("o");

    /// <summary>
    /// Time taken to generate the manifest (for profiling)
    /// </summary>
    [JsonPropertyName("generationTimeMs")]
    public long GenerationTimeMs { get; set; }

    /// <summary>
    /// Dictionary of relative file paths to their metadata.
    /// Key is the target path relative to SPT_ROOT (e.g., "BepInEx/plugins/ModName/ModName.dll")
    /// </summary>
    [JsonPropertyName("files")]
    public Dictionary<string, FileEntry> Files { get; set; } = new();

    /// <summary>
    /// Exclusion patterns applied to this manifest.
    /// Provided so clients can suppress warnings for excluded files.
    /// </summary>
    [JsonPropertyName("exclusions")]
    public List<string> Exclusions { get; set; } = new();

    /// <summary>
    /// Sync roots that clients should scan for extra files.
    /// </summary>
    [JsonPropertyName("syncRoots")]
    public List<string> SyncRoots { get; set; } = new();

    /// <summary>
    /// Source items included in this manifest (for client display)
    /// </summary>
    [JsonPropertyName("sourceItems")]
    public List<ManifestSourceItem> SourceItems { get; set; } = new();
}

/// <summary>
/// Metadata for a single file in the manifest
/// </summary>
public class FileEntry
{
    /// <summary>
    /// SHA256 hash of the file contents (hex string, lowercase)
    /// </summary>
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// File size in bytes
    /// </summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>
    /// Source item this file belongs to (relative path)
    /// </summary>
    [JsonPropertyName("sourceItem")]
    public string SourceItem { get; set; } = string.Empty;

    /// <summary>
    /// Whether this file is from a required (non-optional) source item
    /// </summary>
    [JsonPropertyName("required")]
    public bool Required { get; set; }
}

/// <summary>
/// Source item info included in manifest for client display
/// </summary>
public class ManifestSourceItem
{
    /// <summary>
    /// Relative path of the source item
    /// </summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Display name
    /// </summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Whether this source item is optional
    /// </summary>
    [JsonPropertyName("optional")]
    public bool Optional { get; set; }
}
