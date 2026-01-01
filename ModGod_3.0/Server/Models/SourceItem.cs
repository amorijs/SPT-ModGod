namespace ModGod3.Models;

/// <summary>
/// Represents a source item as seen in the UI.
/// This is a runtime object combining filesystem state with metadata.
/// </summary>
public class SourceItem
{
    /// <summary>
    /// Relative path from SPT root (e.g., "SPT/user/mods/SAIN")
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Display name (from metadata or derived from path)
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The sync root this item belongs to (e.g., "SPT/user/mods")
    /// </summary>
    public string SyncRoot { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is a directory or single file
    /// </summary>
    public bool IsDirectory { get; set; }

    /// <summary>
    /// Whether clients can opt out
    /// </summary>
    public bool Optional { get; set; }

    /// <summary>
    /// Linked items in other sources
    /// </summary>
    public List<string> LinkedTo { get; set; } = new();

    /// <summary>
    /// Files/directories within this source item to exclude from syncing.
    /// Paths are relative to the source item root.
    /// </summary>
    public List<string> Exclusions { get; set; } = new();

    /// <summary>
    /// Health check status (loaded async)
    /// </summary>
    public HealthStatus? Health { get; set; }

    /// <summary>
    /// Whether this item exists on disk
    /// </summary>
    public bool ExistsOnDisk { get; set; } = true;

    /// <summary>
    /// File count within this source item
    /// </summary>
    public int FileCount { get; set; }

    /// <summary>
    /// Total size in bytes
    /// </summary>
    public long TotalSize { get; set; }
}

/// <summary>
/// Health check status for a source item
/// </summary>
public class HealthStatus
{
    /// <summary>
    /// Whether health check has been run
    /// </summary>
    public bool Scanned { get; set; }

    /// <summary>
    /// Whether currently scanning
    /// </summary>
    public bool Scanning { get; set; }

    /// <summary>
    /// Detected version (from DLL metadata)
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Forge mod ID if identified
    /// </summary>
    public int? ForgeModId { get; set; }

    /// <summary>
    /// Whether an update is available on Forge
    /// </summary>
    public bool UpdateAvailable { get; set; }

    /// <summary>
    /// Latest version available on Forge
    /// </summary>
    public string? LatestVersion { get; set; }

    /// <summary>
    /// Any warnings or issues found
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Any linked items that are missing
    /// </summary>
    public List<string> MissingLinks { get; set; } = new();
}

/// <summary>
/// Grouped view of source items by sync root
/// </summary>
public class SourceGroup
{
    /// <summary>
    /// The sync root path (e.g., "SPT/user/mods")
    /// </summary>
    public string SyncRoot { get; set; } = string.Empty;

    /// <summary>
    /// Display name for the sync root
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Source items in this group
    /// </summary>
    public List<SourceItem> Items { get; set; } = new();
}
