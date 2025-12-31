using System.Text.Json.Serialization;

namespace ModGod3.Models.Legacy;

/// <summary>
/// Legacy v2.x ServerConfig for migration purposes.
/// This is a read-only model used during migration from v2.x to v3.0.
/// </summary>
public class LegacyServerConfig
{
    [JsonPropertyName("modList")]
    public List<LegacyModEntry> ModList { get; set; } = new();

    [JsonPropertyName("playerSyncConfig")]
    public LegacyClientSyncConfig? PlayerSyncConfig { get; set; }

    [JsonPropertyName("headlessSyncConfig")]
    public LegacyClientSyncConfig? HeadlessSyncConfig { get; set; }

    [JsonPropertyName("defaultInstallPaths")]
    public List<LegacyInstallPathMapping>? DefaultInstallPaths { get; set; }

    // Legacy properties (pre-2.0)
    [JsonPropertyName("syncExclusions")]
    public List<string>? SyncExclusions { get; set; }

    [JsonPropertyName("useDefaultExclusions")]
    public bool? UseDefaultExclusions { get; set; }

    [JsonPropertyName("customDefaultExclusions")]
    public List<string>? CustomDefaultExclusions { get; set; }
}

public class LegacyModEntry
{
    [JsonPropertyName("modName")]
    public string ModName { get; set; } = string.Empty;

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("optional")]
    public bool Optional { get; set; } = false;

    [JsonPropertyName("lastUpdated")]
    public string LastUpdated { get; set; } = string.Empty;

    [JsonPropertyName("installPaths")]
    public List<string[]> InstallPaths { get; set; } = new();

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LegacyModStatus Status { get; set; } = LegacyModStatus.Installed;

    [JsonPropertyName("isProtected")]
    public bool IsProtected { get; set; } = false;

    [JsonPropertyName("installedFiles")]
    public List<string> InstalledFiles { get; set; } = new();
}

public enum LegacyModStatus
{
    Pending,
    Installed,
    PendingRemoval
}

public class LegacyClientSyncConfig
{
    [JsonPropertyName("syncPaths")]
    public List<LegacySyncPathEntry> SyncPaths { get; set; } = new();

    [JsonPropertyName("excludedPaths")]
    public List<string> ExcludedPaths { get; set; } = new();

    [JsonPropertyName("useDefaultExclusions")]
    public bool UseDefaultExclusions { get; set; } = true;

    [JsonPropertyName("exclusionPatterns")]
    public List<string>? ExclusionPatterns { get; set; }
}

public class LegacySyncPathEntry
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;
}

public class LegacyInstallPathMapping
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;
}
