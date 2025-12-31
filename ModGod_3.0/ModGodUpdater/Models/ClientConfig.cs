using System.Text.Json.Serialization;

namespace ModGod3.Updater.Models;

/// <summary>
/// Client configuration stored in ModGodData/ModGodClient.json
/// </summary>
public class ClientConfig
{
    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional source items the client has opted into (by path)
    /// </summary>
    [JsonPropertyName("optedInItems")]
    public List<string> OptedInItems { get; set; } = new();

    /// <summary>
    /// Whether this is a headless client
    /// </summary>
    [JsonPropertyName("headless")]
    public bool Headless { get; set; } = false;
}

/// <summary>
/// File manifest from server
/// </summary>
public class FileManifest
{
    [JsonPropertyName("modGodVersion")]
    public string ModGodVersion { get; set; } = string.Empty;

    [JsonPropertyName("generatedAt")]
    public string GeneratedAt { get; set; } = string.Empty;

    [JsonPropertyName("generationTimeMs")]
    public long GenerationTimeMs { get; set; }

    [JsonPropertyName("files")]
    public Dictionary<string, FileEntry> Files { get; set; } = new();

    [JsonPropertyName("exclusions")]
    public List<string> Exclusions { get; set; } = new();

    [JsonPropertyName("syncRoots")]
    public List<string> SyncRoots { get; set; } = new();

    [JsonPropertyName("sourceItems")]
    public List<ManifestSourceItem> SourceItems { get; set; } = new();
}

public class FileEntry
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sourceItem")]
    public string SourceItem { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; set; }
}

public class ManifestSourceItem
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("optional")]
    public bool Optional { get; set; }
}

/// <summary>
/// Issue found during file sync
/// </summary>
public class FileSyncIssue
{
    public FileSyncAction Action { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string SourceItem { get; set; } = string.Empty;
    public bool Required { get; set; }
    public long? ServerSize { get; set; }
}

public enum FileSyncAction
{
    Download,
    Update,
    Delete
}
