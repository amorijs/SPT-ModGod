namespace ModGod.Models;

/// <summary>
/// Overall status of a mod's health
/// </summary>
public enum ModHealthStatus
{
    /// <summary>Verified on Forge and up to date</summary>
    UpToDate,
    
    /// <summary>Verified on Forge but update available</summary>
    UpdateAvailable,
    
    /// <summary>Verified but installed version is newer than Forge (dev/beta)</summary>
    NewerThanForge,
    
    /// <summary>Could not find mod on Forge</summary>
    NotOnForge,
    
    /// <summary>Incompatible with current SPT version</summary>
    Incompatible,
    
    /// <summary>Error checking this mod</summary>
    Error
}

/// <summary>
/// Information about a scanned mod (from DLL)
/// </summary>
public class ScannedMod
{
    /// <summary>GUID extracted from the DLL</summary>
    public required string Guid { get; init; }
    
    /// <summary>Version extracted from the DLL</summary>
    public string? Version { get; set; }
    
    /// <summary>Path to the DLL file</summary>
    public required string DllPath { get; init; }
    
    /// <summary>Whether this is a server mod (SPT/user/mods) or client mod (BepInEx/plugins)</summary>
    public bool IsServerMod { get; init; }
    
    /// <summary>Display name (from DLL or derived from path)</summary>
    public string? Name { get; set; }
    
    /// <summary>Author if available</summary>
    public string? Author { get; set; }
}

/// <summary>
/// Health information for a single mod
/// </summary>
public class ModHealthInfo
{
    /// <summary>The mod entry from our config (null if untracked)</summary>
    public ModEntry? TrackedMod { get; set; }
    
    /// <summary>The scanned mod data from DLL</summary>
    public required ScannedMod ScannedMod { get; init; }
    
    /// <summary>Whether this mod is tracked by ModGod</summary>
    public bool IsTracked => TrackedMod != null;
    
    /// <summary>Display name for the mod</summary>
    public string DisplayName => TrackedMod?.ModName ?? ScannedMod.Name ?? Path.GetFileNameWithoutExtension(ScannedMod.DllPath);
    
    /// <summary>Overall health status</summary>
    public ModHealthStatus Status { get; set; } = ModHealthStatus.NotOnForge;
    
    /// <summary>Forge mod ID</summary>
    public int? ForgeModId { get; set; }
    
    /// <summary>Mod slug on Forge</summary>
    public string? ForgeSlug { get; set; }
    
    /// <summary>Currently installed version</summary>
    public string? InstalledVersion { get; set; }
    
    /// <summary>Latest version available on Forge</summary>
    public string? LatestVersion { get; set; }
    
    /// <summary>SPT version constraint for the latest version</summary>
    public string? LatestSptConstraint { get; set; }
    
    /// <summary>Download URL for the latest version</summary>
    public string? LatestDownloadUrl { get; set; }
    
    /// <summary>Link to mod page on Forge</summary>
    public string? ForgeUrl { get; set; }
    
    /// <summary>Error message if status is Error</summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>Whether an update is available</summary>
    public bool HasUpdate => Status == ModHealthStatus.UpdateAvailable;
    
    /// <summary>Dependencies required by this mod</summary>
    public List<DependencyInfo> Dependencies { get; set; } = [];
    
    /// <summary>Whether all dependencies are satisfied</summary>
    public bool HasDependencyIssues => Dependencies.Any(d => !d.IsSatisfied);
    
    /// <summary>Count of missing dependencies</summary>
    public int MissingDependencyCount => Dependencies.Count(d => d.Status == DependencyStatus.Missing);
    
    /// <summary>Count of version mismatch dependencies</summary>
    public int VersionMismatchCount => Dependencies.Count(d => d.Status == DependencyStatus.VersionMismatch);
    
    /// <summary>Available addons for this mod</summary>
    public List<AddonInfo> AvailableAddons { get; set; } = [];
    
    /// <summary>Whether this mod has addons available</summary>
    public bool HasAddons => AvailableAddons.Count > 0;
    
    /// <summary>Count of addons not yet installed</summary>
    public int UninstalledAddonsCount => AvailableAddons.Count(a => !a.IsInstalled);
}

/// <summary>
/// Status of a dependency
/// </summary>
public enum DependencyStatus
{
    /// <summary>Dependency is installed and version is compatible</summary>
    Satisfied,
    
    /// <summary>Dependency is not installed</summary>
    Missing,
    
    /// <summary>Dependency is installed but version doesn't match constraint</summary>
    VersionMismatch,
    
    /// <summary>Unknown - couldn't determine status</summary>
    Unknown
}

/// <summary>
/// Information about an available addon for a mod
/// </summary>
public class AddonInfo
{
    /// <summary>Forge addon ID</summary>
    public int AddonId { get; set; }
    
    /// <summary>Name of the addon</summary>
    public required string Name { get; set; }
    
    /// <summary>Slug for URL construction</summary>
    public string? Slug { get; set; }
    
    /// <summary>Short description/teaser</summary>
    public string? Teaser { get; set; }
    
    /// <summary>Author/owner name</summary>
    public string? Author { get; set; }
    
    /// <summary>Download count</summary>
    public long Downloads { get; set; }
    
    /// <summary>Parent mod ID this addon is for</summary>
    public int ParentModId { get; set; }
    
    /// <summary>Latest version compatible with current SPT (null if none)</summary>
    public string? LatestCompatibleVersion { get; set; }
    
    /// <summary>Download URL for the latest compatible version</summary>
    public string? DownloadUrl { get; set; }
    
    /// <summary>SPT version constraint for the latest compatible version</summary>
    public string? SptConstraint { get; set; }
    
    /// <summary>Whether this addon is already installed</summary>
    public bool IsInstalled { get; set; }
    
    /// <summary>URL to addon page on Forge</summary>
    public string ForgeUrl => $"https://forge.sp-tarkov.com/addon/{AddonId}/{Slug}";
}

/// <summary>
/// Information about a mod dependency
/// </summary>
public class DependencyInfo
{
    /// <summary>Forge mod ID of the dependency</summary>
    public int ModId { get; set; }
    
    /// <summary>GUID of the dependency</summary>
    public string? Guid { get; set; }
    
    /// <summary>Name of the dependency</summary>
    public required string Name { get; set; }
    
    /// <summary>Slug for the mod page URL</summary>
    public string? Slug { get; set; }
    
    /// <summary>Version constraint (e.g., "~1.4.0", ">=2.0.0")</summary>
    public string? VersionConstraint { get; set; }
    
    /// <summary>Installed version (null if not installed)</summary>
    public string? InstalledVersion { get; set; }
    
    /// <summary>Latest compatible version available</summary>
    public string? LatestVersion { get; set; }
    
    /// <summary>Download link for the latest compatible version</summary>
    public string? DownloadLink { get; set; }
    
    /// <summary>Status of this dependency</summary>
    public DependencyStatus Status { get; set; } = DependencyStatus.Unknown;
    
    /// <summary>Whether this dependency is satisfied</summary>
    public bool IsSatisfied => Status == DependencyStatus.Satisfied;
    
    /// <summary>URL to the mod page on Forge</summary>
    public string? ForgeUrl => !string.IsNullOrEmpty(Slug) ? $"https://forge.sp-tarkov.com/mod/{ModId}/{Slug}" : null;
}

/// <summary>
/// Result of a full health check operation
/// </summary>
public class HealthCheckResult
{
    /// <summary>When the check was performed</summary>
    public DateTime CheckedAt { get; init; } = DateTime.UtcNow;
    
    /// <summary>Current SPT version on the server</summary>
    public string? SptVersion { get; set; }
    
    /// <summary>Latest SPT version available</summary>
    public string? LatestSptVersion { get; set; }
    
    /// <summary>Whether SPT itself has an update</summary>
    public bool SptUpdateAvailable => 
        !string.IsNullOrEmpty(SptVersion) && 
        !string.IsNullOrEmpty(LatestSptVersion) && 
        SptVersion != LatestSptVersion;
    
    /// <summary>All mods found (both tracked and untracked)</summary>
    public List<ModHealthInfo> Mods { get; init; } = [];
    
    /// <summary>Total mods found</summary>
    public int TotalMods => Mods.Count;
    
    /// <summary>Mods tracked by ModGod</summary>
    public int TrackedCount => Mods.Count(m => m.IsTracked);
    
    /// <summary>Mods not tracked by ModGod</summary>
    public int UntrackedCount => Mods.Count(m => !m.IsTracked);
    
    /// <summary>Mods that are up to date</summary>
    public int UpToDateCount => Mods.Count(m => m.Status == ModHealthStatus.UpToDate);
    
    /// <summary>Mods with updates available</summary>
    public int UpdatesAvailableCount => Mods.Count(m => m.Status == ModHealthStatus.UpdateAvailable);
    
    /// <summary>Mods not found on Forge</summary>
    public int NotOnForgeCount => Mods.Count(m => m.Status == ModHealthStatus.NotOnForge);
    
    /// <summary>Mods with errors</summary>
    public int ErrorCount => Mods.Count(m => m.Status == ModHealthStatus.Error);
    
    /// <summary>Mods incompatible with current SPT</summary>
    public int IncompatibleCount => Mods.Count(m => m.Status == ModHealthStatus.Incompatible);
    
    /// <summary>Mods with dependency issues</summary>
    public int DependencyIssuesCount => Mods.Count(m => m.HasDependencyIssues);
    
    /// <summary>Total missing dependencies across all mods</summary>
    public int TotalMissingDependencies => Mods.Sum(m => m.MissingDependencyCount);
    
    /// <summary>Mods that have addons available</summary>
    public int ModsWithAddonsCount => Mods.Count(m => m.HasAddons);
    
    /// <summary>Total available addons across all mods</summary>
    public int TotalAddonsCount => Mods.Sum(m => m.AvailableAddons.Count);
    
    /// <summary>Total uninstalled addons across all mods</summary>
    public int UninstalledAddonsCount => Mods.Sum(m => m.UninstalledAddonsCount);
    
    /// <summary>Overall error message if the check failed</summary>
    public string? Error { get; set; }
    
    /// <summary>Whether the check completed successfully</summary>
    public bool Success => string.IsNullOrEmpty(Error);
}
