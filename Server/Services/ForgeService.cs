using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace ModGod.Services;

/// <summary>
/// Service for interacting with the SP-Tarkov Forge API
/// https://forge.sp-tarkov.com/docs/index.html
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton)]
public class ForgeService
{
    private readonly ConfigService _configService;
    private readonly ISptLogger<ForgeService> _logger;
    private readonly HttpClient _httpClient;

    private const string ForgeBaseUrl = "https://forge.sp-tarkov.com";
    private const string ApiBaseUrl = "https://forge.sp-tarkov.com/api/v0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public ForgeService(
        ConfigService configService,
        ISptLogger<ForgeService> logger)
    {
        _configService = configService;
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ModGod/1.0");
    }

    /// <summary>
    /// Check if a Forge API key is configured
    /// </summary>
    public bool HasApiKey => !string.IsNullOrWhiteSpace(_configService.Config.ForgeApiKey);

    /// <summary>
    /// Validate an API key by making a test request
    /// </summary>
    public async Task<(bool IsValid, string? Error)> ValidateApiKeyAsync(string apiKey)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/auth/user?include=role");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await _httpClient.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                return (true, null);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return (false, "Invalid API key");
            }

            return (false, $"API returned status {response.StatusCode}");
        }
        catch (Exception ex)
        {
            _logger.Warning($"Error validating Forge API key: {ex.Message}");
            return (false, $"Connection error: {ex.Message}");
        }
    }

    /// <summary>
    /// Save the Forge API key to config (pass null to remove)
    /// </summary>
    public async Task SaveApiKeyAsync(string? apiKey)
    {
        _configService.Config.ForgeApiKey = apiKey;
        await _configService.SaveConfigAsync();
        
        if (string.IsNullOrEmpty(apiKey))
            _logger.Info("Forge API key removed from config");
        else
            _logger.Info("Forge API key saved to config");
    }

    /// <summary>
    /// Get mod details from Forge by mod ID
    /// </summary>
    public async Task<ForgeModResponse?> GetModDetailsAsync(int modId)
    {
        if (!HasApiKey)
        {
            _logger.Warning("Cannot fetch mod details - no Forge API key configured");
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, 
                $"{ApiBaseUrl}/mod/{modId}?include=versions,license,category");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _configService.Config.ForgeApiKey);

            var response = await _httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning($"Forge API returned {response.StatusCode} for mod {modId}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ForgeApiResponse<ForgeModData>>(json, JsonOptions);
            
            if (result?.Success != true || result.Data == null)
            {
                _logger.Warning($"Forge API returned unsuccessful response for mod {modId}");
                return null;
            }

            return new ForgeModResponse
            {
                Success = true,
                Mod = result.Data
            };
        }
        catch (Exception ex)
        {
            _logger.Error($"Error fetching mod {modId} from Forge: {ex.Message}");
            return new ForgeModResponse
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Extract mod ID from a Forge URL
    /// Supports this format: https://forge.sp-tarkov.com/mod/861/morecheckmarks
    /// </summary>
    public static int? ExtractModIdFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        // Pattern: https://forge.sp-tarkov.com/mod/{modId}/...
        // or: https://forge.sp-tarkov.com/mods/{modId}/...
        try
        {
            var uri = new Uri(url);
            if (!uri.Host.Contains("forge.sp-tarkov.com"))
                return null;

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            
            // Look for "mod" or "mods" segment followed by a number
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (segments[i].Equals("mod", StringComparison.OrdinalIgnoreCase) ||
                    segments[i].Equals("mods", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(segments[i + 1], out var modId))
                    {
                        return modId;
                    }
                }
            }
        }
        catch
        {
            // Invalid URL
        }

        return null;
    }

    /// <summary>
    /// Construct a download URL for a specific mod version
    /// </summary>
    public static string BuildDownloadUrl(int modId, string slug, string version)
    {
        return $"{ForgeBaseUrl}/mod/download/{modId}/{slug}/{version}";
    }
}

#region Forge API Models

public class ForgeApiResponse<T>
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public class ForgeModResponse
{
    public bool Success { get; set; }
    public ForgeModData? Mod { get; set; }
    public string? Error { get; set; }
}

public class ForgeModData
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("guid")]
    public string Guid { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("teaser")]
    public string? Teaser { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("thumbnail")]
    public string? Thumbnail { get; set; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("detail_url")]
    public string? DetailUrl { get; set; }

    [JsonPropertyName("featured")]
    public bool Featured { get; set; }

    [JsonPropertyName("owner")]
    public ForgeUser? Owner { get; set; }

    [JsonPropertyName("versions")]
    public List<ForgeModVersion>? Versions { get; set; }

    [JsonPropertyName("license")]
    public ForgeLicense? License { get; set; }

    [JsonPropertyName("category")]
    public ForgeCategory? Category { get; set; }

    [JsonPropertyName("published_at")]
    public string? PublishedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }
}

public class ForgeModVersion
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("spt_version_constraint")]
    public string? SptVersionConstraint { get; set; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("published_at")]
    public string? PublishedAt { get; set; }
}

public class ForgeUser
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("profile_photo_url")]
    public string? ProfilePhotoUrl { get; set; }
}

public class ForgeLicense
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("short_name")]
    public string? ShortName { get; set; }
}

public class ForgeCategory
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("color_class")]
    public string? ColorClass { get; set; }
}

#endregion

