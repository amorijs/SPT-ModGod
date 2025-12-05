using System.Collections.Generic;

namespace BewasModSync.ClientEnforcer.Models
{
    public class DownloadedMod
    {
        public string ModName { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public bool OptIn { get; set; } = false;
        public string LastUpdated { get; set; } = string.Empty;
    }

    public class ServerConfig
    {
        public List<ModEntry> ModList { get; set; } = new List<ModEntry>();
        public List<string> SyncExclusions { get; set; } = new List<string>();
    }

    public class ModEntry
    {
        public string ModName { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public bool Optional { get; set; } = false;
        public string LastUpdated { get; set; } = string.Empty;
        public List<string[]> InstallPaths { get; set; } = new List<string[]>();
        public string Status { get; set; } = "Pending"; // "Pending", "Installed", "PendingRemoval"
    }

    public class ClientConfig
    {
        public string ServerUrl { get; set; } = string.Empty;
    }

    /// <summary>
    /// Manifest of all files that should exist for synced mods
    /// </summary>
    public class FileManifest
    {
        public string GeneratedAt { get; set; } = string.Empty;
        public long GenerationTimeMs { get; set; }
        public Dictionary<string, FileEntry> Files { get; set; } = new Dictionary<string, FileEntry>();
        public List<string> SyncExclusions { get; set; } = new List<string>();
    }

    /// <summary>
    /// Metadata for a single file in the manifest
    /// </summary>
    public class FileEntry
    {
        public string Hash { get; set; } = string.Empty;
        public long Size { get; set; }
        public string ModName { get; set; } = string.Empty;
        public bool Required { get; set; }
    }

    /// <summary>
    /// Categories of file verification issues
    /// </summary>
    public enum FileIssueType
    {
        Missing,      // File should exist but doesn't
        HashMismatch, // File exists but hash doesn't match
        ExtraFile     // File exists but isn't in manifest (unknown mod file)
    }

    /// <summary>
    /// A file verification issue
    /// </summary>
    public class FileIssue
    {
        public FileIssueType Type { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string ModName { get; set; } = string.Empty;
        public bool Required { get; set; }
        public string Details { get; set; } = string.Empty;
    }
}
