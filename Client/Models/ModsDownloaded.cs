using System.Collections.Generic;

namespace BewasModSync.Models
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
    }

    public class ModEntry
    {
        public string ModName { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public bool Optional { get; set; } = false;
        public string LastUpdated { get; set; } = string.Empty;
        public List<string[]> SyncPaths { get; set; } = new List<string[]>();
    }

    public class ClientConfig
    {
        public string ServerUrl { get; set; } = string.Empty;
    }
}

