using System.Collections.Generic;

namespace ModGod3.ClientEnforcer.Models
{
    /// <summary>
    /// Client configuration stored in ModGodData/ModGodClient.json
    /// </summary>
    public class ClientConfig
    {
        public string ServerUrl { get; set; } = string.Empty;
        public List<string> OptedInItems { get; set; } = new List<string>();
        public bool Headless { get; set; } = false;
    }

    /// <summary>
    /// File manifest from server
    /// </summary>
    public class FileManifest
    {
        public string ModGodVersion { get; set; } = string.Empty;
        public string GeneratedAt { get; set; } = string.Empty;
        public long GenerationTimeMs { get; set; }
        public Dictionary<string, FileEntry> Files { get; set; } = new Dictionary<string, FileEntry>();
        public List<string> Exclusions { get; set; } = new List<string>();
        public List<string> SyncRoots { get; set; } = new List<string>();
        public List<ManifestSourceItem> SourceItems { get; set; } = new List<ManifestSourceItem>();
    }

    public class FileEntry
    {
        public string Hash { get; set; } = string.Empty;
        public long Size { get; set; }
        public string SourceItem { get; set; } = string.Empty;
        public bool Required { get; set; }
    }

    public class ManifestSourceItem
    {
        public string Path { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool Optional { get; set; }
    }

    /// <summary>
    /// Issue found during file verification
    /// </summary>
    public class FileIssue
    {
        public FileIssueType Type { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string SourceItem { get; set; } = string.Empty;
        public bool Required { get; set; }
        public string Details { get; set; } = string.Empty;
    }

    public enum FileIssueType
    {
        Missing,
        HashMismatch,
        ExtraFile,
        VersionMismatch
    }
}
