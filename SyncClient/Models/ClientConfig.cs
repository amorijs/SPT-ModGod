using System.Text.Json.Serialization;

namespace BewasModSync.SyncClient.Models;

public class ClientConfig
{
    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = string.Empty;
}

