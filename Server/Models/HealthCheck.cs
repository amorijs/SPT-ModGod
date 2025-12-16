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
    
    /// <summary>Overall error message if the check failed</summary>
    public string? Error { get; set; }
    
    /// <summary>Whether the check completed successfully</summary>
    public bool Success => string.IsNullOrEmpty(Error);
}
