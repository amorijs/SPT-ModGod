using System.Text.Json.Serialization;

namespace BewasModSync.SyncClient.Models;

public class DownloadedMod
{
    [JsonPropertyName("modName")]
    public string ModName { get; set; } = string.Empty;

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("optIn")]
    public bool OptIn { get; set; } = false;

    [JsonPropertyName("lastUpdated")]
    public string LastUpdated { get; set; } = string.Empty;
}

public class ServerConfig
{
    [JsonPropertyName("modList")]
    public List<ModEntry> ModList { get; set; } = new();
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

    [JsonPropertyName("syncPaths")]
    public List<string[]> SyncPaths { get; set; } = new();
}

