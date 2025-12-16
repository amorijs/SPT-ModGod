namespace ModGod.Web.Models;

/// <summary>
/// Step in the Add Mods dialog workflow
/// </summary>
public enum AddDialogStep
{
    Input,
    Downloading,
    Results
}

/// <summary>
/// State of a node in the sync exclusions tree
/// </summary>
public enum SyncNodeState
{
    Included,
    Excluded,
    Mixed
}

/// <summary>
/// State of a file rule (overwrite vs ignore during install)
/// </summary>
public enum FileRuleState
{
    Overwrite,
    Ignore,
    Mixed
}

/// <summary>
/// State of a node in the uninstall tree
/// </summary>
public enum DeleteNodeState
{
    Delete,
    Keep,
    Mixed
}

/// <summary>
/// Represents a file or directory node in a tree view
/// </summary>
public class FileNode
{
    public string Name { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public List<FileNode> Children { get; set; } = new();
}

/// <summary>
/// Install path mapping (source in archive -> target on disk)
/// </summary>
public class InstallPathItem
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
}

/// <summary>
/// Result from the Edit Mod dialog
/// </summary>
public class EditModResult
{
    public string ModName { get; set; } = "";
    public bool IsOptional { get; set; }
    public List<string[]> InstallPaths { get; set; } = new();
    public List<ModGod.Models.FileCopyRule> FileRules { get; set; } = new();
}

#region Forge UI Models (used by frontend to communicate with ModGod internal API)

/// <summary>
/// Mod information displayed in the UI (transformed from Forge API)
/// </summary>
public class ForgeModInfoViewModel
{
    public int Id { get; set; }
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Teaser { get; set; }
    public string? Thumbnail { get; set; }
    public long Downloads { get; set; }
    public string? Owner { get; set; }
    public string? Category { get; set; }
    public string? CategoryColor { get; set; }
    public string? License { get; set; }
    public string? DetailUrl { get; set; }
    public List<ForgeVersionViewModel> Versions { get; set; } = new();
}

/// <summary>
/// Version information displayed in the UI
/// </summary>
public class ForgeVersionViewModel
{
    public int Id { get; set; }
    public string Version { get; set; } = "";
    public string? SptVersionConstraint { get; set; }
    public long Downloads { get; set; }
    public string? PublishedAt { get; set; }
    public string DownloadUrl { get; set; } = "";
}

/// <summary>
/// Search result item displayed in the UI
/// </summary>
public class ForgeSearchResultViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Thumbnail { get; set; }
    public long Downloads { get; set; }
    public string? Teaser { get; set; }
    public string? DetailUrl { get; set; }
}

/// <summary>
/// Response from the ModGod internal API status endpoint
/// </summary>
public class InternalForgeStatusResponse
{
    public bool HasApiKey { get; set; }
}

/// <summary>
/// Response from the ModGod internal API key validation endpoint
/// </summary>
public class InternalForgeValidateResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Response from the ModGod internal API mod details endpoint
/// </summary>
public class InternalForgeModResponse
{
    public bool Success { get; set; }
    public InternalForgeModData? Mod { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Response from the ModGod internal API search endpoint
/// </summary>
public class InternalForgeSearchResponse
{
    public bool Success { get; set; }
    public List<InternalForgeSearchModData>? Mods { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Mod data from internal API search results
/// </summary>
public class InternalForgeSearchModData
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Slug { get; set; }
    public string? Thumbnail { get; set; }
    public long Downloads { get; set; }
    public string? Teaser { get; set; }
    public string? DetailUrl { get; set; }
}

/// <summary>
/// Detailed mod data from the internal API
/// </summary>
public class InternalForgeModData
{
    public int Id { get; set; }
    public string? Guid { get; set; }
    public string? Name { get; set; }
    public string? Teaser { get; set; }
    public string? Thumbnail { get; set; }
    public long Downloads { get; set; }
    public string? Owner { get; set; }
    public string? Category { get; set; }
    public string? CategoryColor { get; set; }
    public string? License { get; set; }
    public string? DetailUrl { get; set; }
    public List<InternalForgeVersionData>? Versions { get; set; }
}

/// <summary>
/// Version data from the internal API
/// </summary>
public class InternalForgeVersionData
{
    public int Id { get; set; }
    public string? Version { get; set; }
    public string? SptVersionConstraint { get; set; }
    public long Downloads { get; set; }
    public string? PublishedAt { get; set; }
    public string? DownloadUrl { get; set; }
}

#endregion

#region Forge Addon Models

/// <summary>
/// Addon information displayed in the UI
/// </summary>
public class ForgeAddonViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Teaser { get; set; }
    public string? Thumbnail { get; set; }
    public long Downloads { get; set; }
    public string? DetailUrl { get; set; }
    public int ModId { get; set; }
    public string? Owner { get; set; }
    
    // UI state
    public bool IsSelected { get; set; }
    public bool IsLoadingVersions { get; set; }
    public string? VersionsError { get; set; }
    public List<ForgeAddonVersionViewModel> Versions { get; set; } = new();
    public string? SelectedVersion { get; set; }
}

/// <summary>
/// Addon version information displayed in the UI
/// </summary>
public class ForgeAddonVersionViewModel
{
    public int Id { get; set; }
    public string Version { get; set; } = "";
    public string? Description { get; set; }
    public string DownloadUrl { get; set; } = "";
    public long ContentLength { get; set; }
    public string? ModVersionConstraint { get; set; }
    public long Downloads { get; set; }
    public string? PublishedAt { get; set; }
}

/// <summary>
/// Response from the ModGod internal API addons endpoint
/// </summary>
public class InternalForgeAddonsResponse
{
    public bool Success { get; set; }
    public List<InternalForgeAddonData>? Addons { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Addon data from the internal API
/// </summary>
public class InternalForgeAddonData
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Slug { get; set; }
    public string? Teaser { get; set; }
    public string? Thumbnail { get; set; }
    public long Downloads { get; set; }
    public string? DetailUrl { get; set; }
    public int ModId { get; set; }
    public string? Owner { get; set; }
    public string? PublishedAt { get; set; }
}

/// <summary>
/// Response from the ModGod internal API addon versions endpoint
/// </summary>
public class InternalForgeAddonVersionsResponse
{
    public bool Success { get; set; }
    public List<InternalForgeAddonVersionData>? Versions { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Addon version data from the internal API
/// </summary>
public class InternalForgeAddonVersionData
{
    public int Id { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string? DownloadUrl { get; set; }
    public long ContentLength { get; set; }
    public string? ModVersionConstraint { get; set; }
    public long Downloads { get; set; }
    public string? PublishedAt { get; set; }
}

#endregion

