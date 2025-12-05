namespace BewasModSync.Models;

/// <summary>
/// Manifest of all files that should exist for synced mods
/// </summary>
public class FileManifest
{
    /// <summary>
    /// When the manifest was generated
    /// </summary>
    public string GeneratedAt { get; set; } = DateTime.UtcNow.ToString("o");
    
    /// <summary>
    /// Time taken to generate the manifest (for profiling)
    /// </summary>
    public long GenerationTimeMs { get; set; }
    
    /// <summary>
    /// Dictionary of relative file paths to their metadata
    /// Key is the target path relative to SPT_ROOT (e.g., "BepInEx/plugins/ModName/ModName.dll")
    /// </summary>
    public Dictionary<string, FileEntry> Files { get; set; } = new();

    /// <summary>
    /// Paths (relative to SPT root) that were excluded from this manifest.
    /// Provided so clients can suppress warnings for server-only/generated files.
    /// </summary>
    public List<string> SyncExclusions { get; set; } = new();
}

/// <summary>
/// Metadata for a single file in the manifest
/// </summary>
public class FileEntry
{
    /// <summary>
    /// SHA256 hash of the file contents (hex string)
    /// </summary>
    public string Hash { get; set; } = string.Empty;
    
    /// <summary>
    /// File size in bytes
    /// </summary>
    public long Size { get; set; }
    
    /// <summary>
    /// Name of the mod this file belongs to
    /// </summary>
    public string ModName { get; set; } = string.Empty;
    
    /// <summary>
    /// Whether this file is from a required (non-optional) mod
    /// </summary>
    public bool Required { get; set; }
}

