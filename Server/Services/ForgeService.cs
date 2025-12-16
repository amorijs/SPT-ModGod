using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Utils;

namespace ModGod.Services;

/// <summary>
/// Credentials stored separately from mod configuration (secrets shouldn't be in shareable config)
/// </summary>
public class ForgeCredentials
{
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }
}

/// <summary>
/// Service for interacting with the SP-Tarkov Forge API
/// https://forge.sp-tarkov.com/docs/index.html
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton)]
public class ForgeService : IOnLoad
{
    private readonly ConfigService _configService;
    private readonly ISptLogger<ForgeService> _logger;
    private readonly HttpClient _httpClient;
    
    private ForgeCredentials _credentials = new();
    private string CredentialsPath => Path.Combine(_configService.DataPath, "credentials.json");

    private const string ForgeBaseUrl = "https://forge.sp-tarkov.com";
    private const string ApiBaseUrl = "https://forge.sp-tarkov.com/api/v0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    
    private static readonly JsonSerializerOptions CredentialsJsonOptions = new()
    {
        WriteIndented = true
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
    
    public async Task OnLoad()
    {
        if (File.Exists(CredentialsPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(CredentialsPath);
                _credentials = JsonSerializer.Deserialize<ForgeCredentials>(json, CredentialsJsonOptions) ?? new ForgeCredentials();
                _logger.Info("Loaded Forge credentials");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to load credentials: {ex.Message}");
                _credentials = new ForgeCredentials();
            }
        }
    }

    /// <summary>
    /// Check if a Forge API key is configured
    /// </summary>
    public bool HasApiKey => !string.IsNullOrWhiteSpace(_credentials.ApiKey);

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
    /// Save the Forge API key to credentials file (pass null to remove)
    /// </summary>
    public async Task SaveApiKeyAsync(string? apiKey)
    {
        _credentials.ApiKey = apiKey;
        
        if (string.IsNullOrEmpty(apiKey))
        {
            // Remove credentials file if key is cleared
            if (File.Exists(CredentialsPath))
            {
                File.Delete(CredentialsPath);
                _logger.Info("Forge credentials file removed");
            }
        }
        else
        {
            var json = JsonSerializer.Serialize(_credentials, CredentialsJsonOptions);
            await File.WriteAllTextAsync(CredentialsPath, json);
            _logger.Info("Forge API key saved to credentials file");
        }
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
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credentials.ApiKey);

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
    /// Get mod details from Forge by GUID, trying multiple variations to handle naming inconsistencies
    /// </summary>
    public async Task<ForgeModResponse?> GetModByGuidAsync(string guid, string? sptVersion = null)
    {
        if (!HasApiKey)
        {
            _logger.Warning("Cannot fetch mod by GUID - no Forge API key configured");
            return null;
        }

        if (string.IsNullOrWhiteSpace(guid))
        {
            return null;
        }

        // Generate GUID variations to try (handles inconsistent naming between DLLs and Forge)
        var guidVariations = GenerateGuidVariations(guid);
        
        foreach (var variation in guidVariations)
        {
            var result = await TryLookupGuidAsync(variation);
            if (result?.Success == true && result.Mod != null)
            {
                if (variation != guid)
                {
                    _logger.Info($"Found mod using normalized GUID: '{guid}' -> '{variation}'");
                }
                return result;
            }
        }

        // No variation found the mod
        return new ForgeModResponse
        {
            Success = false,
            Error = $"GUID not registered on Forge (tried: {string.Join(", ", guidVariations)})"
        };
    }

    /// <summary>
    /// Generate variations of a GUID to try for lookup (handles naming inconsistencies)
    /// </summary>
    private static List<string> GenerateGuidVariations(string guid)
    {
        var variations = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal); // Case-sensitive to allow both cases
        
        void AddVariation(string v)
        {
            if (!string.IsNullOrWhiteSpace(v) && seen.Add(v))
                variations.Add(v);
        }
        
        // 1. Lowercase version first (most common convention on Forge)
        var lowercase = guid.ToLowerInvariant();
        AddVariation(lowercase);
        
        // 2. Original as-is (in case it's already correct)
        AddVariation(guid);
        
        // 3. If doesn't start with "com.", add com. prefix (common convention)
        if (!lowercase.StartsWith("com."))
        {
            // Convert "Author.ModName" -> "com.author.modname"
            AddVariation($"com.{lowercase}");
        }
        
        // 4. If it's a simple "Author.ModName" pattern, also try extracting just the mod name part
        // e.g., "Tyfon.UIFixes" -> try "com.tyfon.uifixes"
        var parts = guid.Split('.');
        if (parts.Length == 2 && !guid.StartsWith("com.", StringComparison.OrdinalIgnoreCase))
        {
            // "Author.ModName" -> "com.author.modname"
            var author = parts[0].ToLowerInvariant();
            var modName = parts[1].ToLowerInvariant();
            AddVariation($"com.{author}.{modName}");
        }

        return variations;
    }

    /// <summary>
    /// Try to lookup a single GUID on Forge
    /// </summary>
    private async Task<ForgeModResponse?> TryLookupGuidAsync(string guid)
    {
        try
        {
            var url = $"{ApiBaseUrl}/mods?filter[guid]={Uri.EscapeDataString(guid)}&include=versions";
            
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credentials.ApiKey);

            var response = await _httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                return null; // Try next variation
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ForgeSearchApiResponse>(json, JsonOptions);
            
            if (result?.Success != true || result.Data == null || result.Data.Count == 0)
            {
                return null; // Try next variation
            }

            // Found a match!
            var searchResult = result.Data[0];
            return new ForgeModResponse
            {
                Success = true,
                Mod = new ForgeModData
                {
                    Id = searchResult.Id,
                    Name = searchResult.Name,
                    Slug = searchResult.Slug,
                    Thumbnail = searchResult.Thumbnail,
                    Downloads = searchResult.Downloads,
                    Teaser = searchResult.Teaser,
                    DetailUrl = searchResult.DetailUrl,
                    Versions = searchResult.Versions
                }
            };
        }
        catch (Exception ex)
        {
            _logger.Debug($"Error trying GUID variation '{guid}': {ex.Message}");
            return null; // Try next variation
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
    /// Search for mods on Forge
    /// </summary>
    public async Task<ForgeSearchResponse?> SearchModsAsync(string query, string? sptVersion = null)
    {
        if (!HasApiKey)
        {
            _logger.Warning("Cannot search mods - no Forge API key configured");
            return null;
        }

        try
        {
            // Build the query URL with all the specified parameters
            var queryEncoded = Uri.EscapeDataString(query);
            var fields = "id,name,slug,thumbnail,downloads,teaser,detail_url";
            var url = $"{ApiBaseUrl}/mods?query={queryEncoded}&sort=-downloads&fields={fields}";
            
            // Add SPT version filter if provided
            if (!string.IsNullOrWhiteSpace(sptVersion))
            {
                var versionEncoded = Uri.EscapeDataString($"^{sptVersion}");
                url += $"&filter[spt_version]={versionEncoded}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credentials.ApiKey);

            var response = await _httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning($"Forge search API returned {response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ForgeSearchApiResponse>(json, JsonOptions);
            
            if (result?.Success != true)
            {
                _logger.Warning("Forge search API returned unsuccessful response");
                return null;
            }

            return new ForgeSearchResponse
            {
                Success = true,
                Mods = result.Data ?? new List<ForgeSearchModData>()
            };
        }
        catch (Exception ex)
        {
            _logger.Error($"Error searching mods on Forge: {ex.Message}");
            return new ForgeSearchResponse
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Construct a download URL for a specific mod version
    /// </summary>
    public static string BuildDownloadUrl(int modId, string slug, string version)
    {
        return $"{ForgeBaseUrl}/mod/download/{modId}/{slug}/{version}";
    }

    /// <summary>
    /// Get addons for a specific mod
    /// </summary>
    public async Task<ForgeAddonsResponse?> GetModAddonsAsync(int modId)
    {
        if (!HasApiKey)
        {
            _logger.Warning("Cannot fetch addons - no Forge API key configured");
            return null;
        }

        try
        {
            var url = $"{ApiBaseUrl}/addons?filter[mod_id]={modId}";
            _logger.Info($"[ForgeService] Fetching addons from: {url}");
            
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credentials.ApiKey);

            var response = await _httpClient.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            
            _logger.Info($"[ForgeService] Addons API response status: {response.StatusCode}");
            _logger.Info($"[ForgeService] Addons API response (first 500 chars): {json.Substring(0, Math.Min(500, json.Length))}");
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning($"Forge API returned {response.StatusCode} for mod {modId} addons");
                return new ForgeAddonsResponse { Success = false, Error = $"API error: {response.StatusCode}" };
            }

            var result = JsonSerializer.Deserialize<ForgeApiResponse<List<ForgeAddonData>>>(json, JsonOptions);
            _logger.Info($"[ForgeService] Deserialized result - Success: {result?.Success}, Data count: {result?.Data?.Count ?? 0}");
            
            if (result?.Data == null)
            {
                return new ForgeAddonsResponse { Success = true, Addons = new List<ForgeAddonData>() };
            }

            return new ForgeAddonsResponse
            {
                Success = true,
                Addons = result.Data
            };
        }
        catch (Exception ex)
        {
            _logger.Error($"Error fetching addons for mod {modId}: {ex.Message}");
            return new ForgeAddonsResponse { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Get versions for a specific addon
    /// </summary>
    public async Task<ForgeAddonVersionsResponse?> GetAddonVersionsAsync(int addonId)
    {
        if (!HasApiKey)
        {
            _logger.Warning("Cannot fetch addon versions - no Forge API key configured");
            return null;
        }

        try
        {
            var url = $"{ApiBaseUrl}/addon/{addonId}/versions";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credentials.ApiKey);

            var response = await _httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning($"Forge API returned {response.StatusCode} for addon {addonId} versions");
                return new ForgeAddonVersionsResponse { Success = false, Error = $"API error: {response.StatusCode}" };
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ForgeApiResponse<List<ForgeAddonVersionData>>>(json, JsonOptions);
            
            if (result?.Data == null)
            {
                return new ForgeAddonVersionsResponse { Success = true, Versions = new List<ForgeAddonVersionData>() };
            }

            return new ForgeAddonVersionsResponse
            {
                Success = true,
                Versions = result.Data
            };
        }
        catch (Exception ex)
        {
            _logger.Error($"Error fetching versions for addon {addonId}: {ex.Message}");
            return new ForgeAddonVersionsResponse { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Get all available SPT versions from Forge
    /// </summary>
    public async Task<ForgeSptVersionsResponse?> GetSptVersionsAsync()
    {
        if (!HasApiKey)
        {
            _logger.Warning("Cannot fetch SPT versions - no Forge API key configured");
            return null;
        }

        try
        {
            var url = $"{ApiBaseUrl}/spt/versions?sort=-version&per_page=10";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credentials.ApiKey);

            var response = await _httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning($"Forge SPT versions API returned {response.StatusCode}");
                return new ForgeSptVersionsResponse { Success = false, Error = $"API error: {response.StatusCode}" };
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ForgeApiResponse<List<ForgeSptVersionData>>>(json, JsonOptions);
            
            return new ForgeSptVersionsResponse
            {
                Success = true,
                Versions = result?.Data ?? []
            };
        }
        catch (Exception ex)
        {
            _logger.Error($"Error fetching SPT versions: {ex.Message}");
            return new ForgeSptVersionsResponse { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Batch check for mod updates
    /// </summary>
    /// <param name="modUpdates">List of (modId, currentVersion) pairs</param>
    /// <param name="sptVersion">Current SPT version</param>
    public async Task<ForgeModUpdatesResponse?> GetModUpdatesAsync(
        IEnumerable<(int ModId, string CurrentVersion)> modUpdates, 
        string sptVersion)
    {
        if (!HasApiKey)
        {
            _logger.Warning("Cannot check mod updates - no Forge API key configured");
            return null;
        }

        var modList = modUpdates.ToList();
        if (modList.Count == 0)
        {
            return new ForgeModUpdatesResponse { Success = true };
        }

        try
        {
            // Build mods query parameter as comma-separated "id:version" pairs
            var modsParam = string.Join(",", 
                modList.Select(m => $"{m.ModId}:{Uri.EscapeDataString(m.CurrentVersion)}"));
            
            var url = $"{ApiBaseUrl}/mods/updates?mods={modsParam}&spt_version={Uri.EscapeDataString(sptVersion)}";
            
            _logger.Debug($"Checking mod updates: {url}");
            
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credentials.ApiKey);

            var response = await _httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning($"Forge mod updates API returned {response.StatusCode}");
                return new ForgeModUpdatesResponse { Success = false, Error = $"API error: {response.StatusCode}" };
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ForgeApiResponse<ForgeModUpdatesData>>(json, JsonOptions);
            
            if (result?.Success != true || result.Data == null)
            {
                return new ForgeModUpdatesResponse { Success = false, Error = "Invalid API response" };
            }

            return new ForgeModUpdatesResponse
            {
                Success = true,
                Data = result.Data
            };
        }
        catch (Exception ex)
        {
            _logger.Error($"Error checking mod updates: {ex.Message}");
            return new ForgeModUpdatesResponse { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Get dependencies for a list of mods
    /// </summary>
    /// <param name="modVersions">List of (modId, version) pairs to check</param>
    public async Task<ForgeDependenciesResponse?> GetModDependenciesAsync(
        IEnumerable<(int ModId, string Version)> modVersions)
    {
        if (!HasApiKey)
        {
            _logger.Warning("Cannot check mod dependencies - no Forge API key configured");
            return null;
        }

        var modList = modVersions.ToList();
        if (modList.Count == 0)
        {
            return new ForgeDependenciesResponse { Success = true };
        }

        try
        {
            // Build mods query parameter as comma-separated "id:version" pairs
            // API expects: mods=791:4.3.0,902:1.4.0
            // Note: Reference project escapes both identifier and version
            var modsParam = string.Join(",", 
                modList.Select(m => $"{m.ModId}:{Uri.EscapeDataString(m.Version)}"));
            
            var url = $"{ApiBaseUrl}/mods/dependencies?mods={modsParam}";
            
            _logger.Debug($"Dependency API URL: {url}");
            
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credentials.ApiKey);

            var response = await _httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning($"Forge dependencies API returned {response.StatusCode}");
                return new ForgeDependenciesResponse { Success = false, Error = $"API error: {response.StatusCode}" };
            }

            var json = await response.Content.ReadAsStringAsync();
            
            var result = JsonSerializer.Deserialize<ForgeApiResponse<List<ForgeModDependencyData>>>(json, JsonOptions);
            
            if (result?.Success != true)
            {
                _logger.Warning($"Dependency API returned success=false or null. Response: {json[..Math.Min(300, json.Length)]}");
                return new ForgeDependenciesResponse { Success = false, Error = "Invalid API response" };
            }

            // Log if any mods have dependencies
            var modsWithDeps = result.Data?.Where(m => m.Dependencies?.Count > 0).ToList() ?? [];
            if (modsWithDeps.Count > 0)
            {
                foreach (var m in modsWithDeps)
                {
                    _logger.Info($"API found: {m.Name} (ID:{m.Id}) has {m.Dependencies!.Count} deps: {string.Join(", ", m.Dependencies.Select(d => d.Name))}");
                }
            }
            return new ForgeDependenciesResponse
            {
                Success = true,
                Mods = result.Data ?? []
            };
        }
        catch (Exception ex)
        {
            _logger.Error($"Error checking mod dependencies: {ex.Message}");
            return new ForgeDependenciesResponse { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Extract mod ID, slug, and version from a Forge download URL
    /// Format: https://forge.sp-tarkov.com/mod/download/{modId}/{slug}/{version}
    /// </summary>
    public static (int? ModId, string? Slug, string? Version) ParseDownloadUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return (null, null, null);

        try
        {
            var uri = new Uri(url);
            if (!uri.Host.Contains("forge.sp-tarkov.com"))
                return (null, null, null);

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            
            // Look for "mod/download/{id}/{slug}/{version}" pattern
            for (int i = 0; i < segments.Length - 3; i++)
            {
                if (segments[i].Equals("mod", StringComparison.OrdinalIgnoreCase) &&
                    segments[i + 1].Equals("download", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(segments[i + 2], out var modId))
                    {
                        var slug = segments[i + 3];
                        var version = segments.Length > i + 4 ? segments[i + 4] : null;
                        return (modId, slug, version);
                    }
                }
            }
        }
        catch
        {
            // Invalid URL
        }

        return (null, null, null);
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

public class ForgeSearchApiResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("data")]
    public List<ForgeSearchModData>? Data { get; set; }
}

public class ForgeSearchResponse
{
    public bool Success { get; set; }
    public List<ForgeSearchModData> Mods { get; set; } = new();
    public string? Error { get; set; }
}

public class ForgeSearchModData
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("thumbnail")]
    public string? Thumbnail { get; set; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("teaser")]
    public string? Teaser { get; set; }

    [JsonPropertyName("detail_url")]
    public string? DetailUrl { get; set; }

    [JsonPropertyName("versions")]
    public List<ForgeModVersion>? Versions { get; set; }
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

// Addon Models

public class ForgeAddonsResponse
{
    public bool Success { get; set; }
    public List<ForgeAddonData> Addons { get; set; } = new();
    public string? Error { get; set; }
}

public class ForgeAddonVersionsResponse
{
    public bool Success { get; set; }
    public List<ForgeAddonVersionData> Versions { get; set; } = new();
    public string? Error { get; set; }
}

public class ForgeAddonData
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("teaser")]
    public string? Teaser { get; set; }

    [JsonPropertyName("thumbnail")]
    public string? Thumbnail { get; set; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("detail_url")]
    public string? DetailUrl { get; set; }

    [JsonPropertyName("mod_id")]
    public int ModId { get; set; }

    [JsonPropertyName("owner")]
    public ForgeUser? Owner { get; set; }

    [JsonPropertyName("published_at")]
    public string? PublishedAt { get; set; }
}

public class ForgeAddonVersionData
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("link")]
    public string? Link { get; set; }

    [JsonPropertyName("content_length")]
    public long? ContentLength { get; set; }

    [JsonPropertyName("mod_version_constraint")]
    public string? ModVersionConstraint { get; set; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("published_at")]
    public string? PublishedAt { get; set; }
}

// SPT Version Models

public class ForgeSptVersionsResponse
{
    public bool Success { get; set; }
    public List<ForgeSptVersionData> Versions { get; set; } = [];
    public string? Error { get; set; }
}

public class ForgeSptVersionData
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("color_class")]
    public string? ColorClass { get; set; }

    [JsonPropertyName("mod_count")]
    public int ModCount { get; set; }

    [JsonPropertyName("link")]
    public string? Link { get; set; }
}

// Mod Updates Models (Batch Update Check)

public class ForgeModUpdatesResponse
{
    public bool Success { get; set; }
    public ForgeModUpdatesData? Data { get; set; }
    public string? Error { get; set; }
}

public class ForgeModUpdatesData
{
    [JsonPropertyName("updates")]
    public List<ForgeModUpdateInfo>? SafeToUpdate { get; set; }

    [JsonPropertyName("blocked_updates")]
    public List<ForgeBlockedUpdate>? Blocked { get; set; }

    [JsonPropertyName("up_to_date")]
    public List<ForgeUpToDateMod>? UpToDate { get; set; }

    [JsonPropertyName("incompatible_with_spt")]
    public List<ForgeIncompatibleMod>? Incompatible { get; set; }
}

public class ForgeModUpdateInfo
{
    [JsonPropertyName("current_version")]
    public ForgeVersionInfo? CurrentVersion { get; set; }

    [JsonPropertyName("recommended_version")]
    public ForgeVersionInfo? RecommendedVersion { get; set; }

    [JsonPropertyName("update_reason")]
    public string? UpdateReason { get; set; }
}

public class ForgeBlockedUpdate
{
    [JsonPropertyName("current_version")]
    public ForgeVersionInfo? CurrentVersion { get; set; }

    [JsonPropertyName("latest_version")]
    public ForgeVersionInfo? LatestVersion { get; set; }

    [JsonPropertyName("block_reason")]
    public string? BlockReason { get; set; }

    [JsonPropertyName("blocking_mods")]
    public List<ForgeBlockingMod>? BlockingMods { get; set; }
}

public class ForgeBlockingMod
{
    [JsonPropertyName("mod_id")]
    public int ModId { get; set; }

    [JsonPropertyName("mod_guid")]
    public string? ModGuid { get; set; }

    [JsonPropertyName("mod_name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("current_version")]
    public string? CurrentVersion { get; set; }

    [JsonPropertyName("constraint")]
    public string Constraint { get; set; } = string.Empty;
}

public class ForgeUpToDateMod
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [JsonPropertyName("mod_id")]
    public int ModId { get; set; }

    [JsonPropertyName("guid")]
    public string? Guid { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("spt_versions")]
    public List<string>? SptVersions { get; set; }
}

public class ForgeIncompatibleMod
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [JsonPropertyName("mod_id")]
    public int ModId { get; set; }

    [JsonPropertyName("guid")]
    public string? Guid { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("latest_compatible_version")]
    public ForgeVersionInfo? LatestCompatibleVersion { get; set; }
}

public class ForgeVersionInfo
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [JsonPropertyName("mod_id")]
    public int ModId { get; set; }

    [JsonPropertyName("guid")]
    public string? Guid { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("link")]
    public string? Link { get; set; }

    [JsonPropertyName("spt_versions")]
    public List<string>? SptVersions { get; set; }
}

// Dependency Models

public class ForgeDependenciesResponse
{
    public bool Success { get; set; }
    public List<ForgeModDependencyData> Mods { get; set; } = [];
    public string? Error { get; set; }
}

/// <summary>
/// Response item from /mods/dependencies API
/// Each item represents a mod and its dependencies
/// </summary>
public class ForgeModDependencyData
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("guid")]
    public string? Guid { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("latest_compatible_version")]
    public ForgeCompatibleVersion? LatestCompatibleVersion { get; set; }

    [JsonPropertyName("dependencies")]
    public List<ForgeDependency>? Dependencies { get; set; }

    [JsonPropertyName("conflict")]
    public bool Conflict { get; set; }
}

/// <summary>
/// Version info from dependencies API
/// </summary>
public class ForgeCompatibleVersion
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("link")]
    public string? Link { get; set; }

    [JsonPropertyName("content_length")]
    public long? ContentLength { get; set; }

    [JsonPropertyName("spt_version_constraint")]
    public string? SptVersionConstraint { get; set; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("fika_compatibility")]
    public string? FikaCompatibility { get; set; }

    [JsonPropertyName("published_at")]
    public string? PublishedAt { get; set; }
}

/// <summary>
/// A dependency entry - this is a mod that the parent mod depends on
/// </summary>
public class ForgeDependency
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("guid")]
    public string? Guid { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("version_constraint")]
    public string? VersionConstraint { get; set; }

    [JsonPropertyName("latest_compatible_version")]
    public ForgeCompatibleVersion? LatestCompatibleVersion { get; set; }
}

#endregion

