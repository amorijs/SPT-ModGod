using System.Text.Json.Serialization;

namespace BewasModSync.Models;

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

    [JsonPropertyName("syncPaths")]
    public List<string[]> SyncPaths { get; set; } = new();
}

