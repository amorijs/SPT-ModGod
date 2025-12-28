namespace ModGod.Updater.Models;

public class ClientConfig
{
    public string ServerUrl { get; set; } = string.Empty;
    
    /// <summary>
    /// Whether this client is a headless raid-hosting instance.
    /// Headless clients only sync files explicitly configured for headless syncing.
    /// </summary>
    public bool Headless { get; set; } = false;
}

public class DownloadedMod
{
    public string ModName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public bool OptIn { get; set; } = false;
    public string LastUpdated { get; set; } = string.Empty;
}

public class ServerConfig
{
    public List<ModEntry> ModList { get; set; } = new();
}

public class ModEntry
{
    public string ModName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public bool Optional { get; set; } = false;
    public string LastUpdated { get; set; } = string.Empty;
    public List<string[]> InstallPaths { get; set; } = new();
    public string Status { get; set; } = "Pending";
    public bool IsProtected { get; set; } = false;
}

// File manifest models for file sync
public class FileManifest
{
    public string GeneratedAt { get; set; } = string.Empty;
    public long GenerationTimeMs { get; set; }
    public Dictionary<string, FileEntry> Files { get; set; } = new();
    public List<string> SyncExclusions { get; set; } = new();
    
    /// <summary>
    /// Target directories that clients should scan for extra files.
    /// If null/empty, falls back to default paths (BepInEx/plugins, SPT/user/mods).
    /// </summary>
    public List<string>? SyncRoots { get; set; }
}

public class FileEntry
{
    public string Hash { get; set; } = string.Empty;
    public long Size { get; set; }
    public string ModName { get; set; } = string.Empty;
    public bool Required { get; set; }
}

public enum FileSyncAction
{
    Download,    // File missing locally
    Update,      // File hash mismatch
    Delete       // Extra file not in manifest
}

public class FileSyncIssue
{
    public FileSyncAction Action { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string ModName { get; set; } = string.Empty;
    public bool Required { get; set; }
    public long? ServerSize { get; set; }
}

