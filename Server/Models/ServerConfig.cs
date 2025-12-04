using System.Text.Json.Serialization;

namespace BewasModSync.Models;

public class ServerConfig
{
    [JsonPropertyName("modList")]
    public List<ModEntry> ModList { get; set; } = new();
}

public class ModCache
{
    [JsonPropertyName("urlToPath")]
    public Dictionary<string, string> UrlToPath { get; set; } = new();
}
