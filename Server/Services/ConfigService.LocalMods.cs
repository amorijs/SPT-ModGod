using System.Text.Json;
using ModGod.Models;

namespace ModGod.Services;

/// <summary>
/// Partial class for local mod storage management.
/// Local mods are mods that exist on the server filesystem (e.g., distributed via Discord)
/// rather than from a download URL. They are served via a self-hosted download endpoint.
/// </summary>
public partial class ConfigService
{
    /// <summary>
    /// Index of local mods: guid -> metadata
    /// </summary>
    public LocalModsIndex LocalMods { get; private set; } = new();
    
    public string LocalModsIndexPath => Path.Combine(_dataPath, "localModsIndex.json");

    /// <summary>
    /// Ensure the local-mods directory exists. Called during OnLoad.
    /// </summary>
    public void EnsureLocalModsDirectory()
    {
        Directory.CreateDirectory(LocalModsPath);
    }

    /// <summary>
    /// Load the local mods index from disk.
    /// </summary>
    public async Task LoadLocalModsIndexAsync()
    {
        if (File.Exists(LocalModsIndexPath))
        {
            var json = await File.ReadAllTextAsync(LocalModsIndexPath);
            LocalMods = JsonSerializer.Deserialize<LocalModsIndex>(json, JsonOptions) ?? new LocalModsIndex();
        }
        else
        {
            LocalMods = new LocalModsIndex();
        }
        
        // Clean up orphaned entries (folders that don't exist anymore)
        var orphaned = LocalMods.Mods.Keys
            .Where(guid => !Directory.Exists(Path.Combine(LocalModsPath, guid)))
            .ToList();
        
        foreach (var guid in orphaned)
        {
            LocalMods.Mods.Remove(guid);
            _logger.Warning($"Removed orphaned local mod entry: {guid}");
        }
        
        if (orphaned.Count > 0)
        {
            await SaveLocalModsIndexAsync();
        }
    }

    /// <summary>
    /// Save the local mods index to disk.
    /// </summary>
    public async Task SaveLocalModsIndexAsync()
    {
        var json = JsonSerializer.Serialize(LocalMods, JsonOptions);
        await File.WriteAllTextAsync(LocalModsIndexPath, json);
    }

    /// <summary>
    /// Stage a local mod by copying files from source path to local-mods storage.
    /// Returns the guid that identifies this local mod.
    /// </summary>
    /// <param name="sourcePath">Path to the folder containing mod files</param>
    /// <param name="modName">Display name for the mod</param>
    /// <returns>Guid identifying the staged local mod</returns>
    public async Task<string> StageLocalModAsync(string sourcePath, string modName)
    {
        if (!Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException($"Source path does not exist: {sourcePath}");
        }

        var guid = Guid.NewGuid().ToString("N")[..16]; // 16-char hex string
        var destPath = Path.Combine(LocalModsPath, guid);

        _logger.Info($"Staging local mod '{modName}' from {sourcePath} to {destPath}");

        // Copy all files recursively
        await Task.Run(() => CopyDirectory(sourcePath, destPath));

        // Add to index
        LocalMods.Mods[guid] = new LocalModInfo
        {
            Guid = guid,
            ModName = modName,
            SourcePath = sourcePath,
            StagedAt = DateTime.UtcNow.ToString("o")
        };

        await SaveLocalModsIndexAsync();
        
        _logger.Info($"Local mod '{modName}' staged with guid: {guid}");
        return guid;
    }

    /// <summary>
    /// Get the storage path for a local mod by guid.
    /// </summary>
    public string? GetLocalModPath(string guid)
    {
        var path = Path.Combine(LocalModsPath, guid);
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>
    /// Get the download URL for a local mod.
    /// </summary>
    public string GetLocalModDownloadUrl(string guid, string serverBaseUrl)
    {
        return $"{serverBaseUrl.TrimEnd('/')}/modgod/api/local-mods/{guid}";
    }

    /// <summary>
    /// Delete a local mod from storage.
    /// </summary>
    public async Task DeleteLocalModAsync(string guid)
    {
        var path = Path.Combine(LocalModsPath, guid);
        
        if (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, true);
                _logger.Info($"Deleted local mod storage: {guid}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to delete local mod storage: {ex.Message}");
            }
        }

        LocalMods.Mods.Remove(guid);
        await SaveLocalModsIndexAsync();
    }

    /// <summary>
    /// Check if a guid corresponds to a valid local mod.
    /// </summary>
    public bool IsValidLocalMod(string guid)
    {
        return LocalMods.Mods.ContainsKey(guid) && 
               Directory.Exists(Path.Combine(LocalModsPath, guid));
    }

    /// <summary>
    /// Get info about a local mod by guid.
    /// </summary>
    public LocalModInfo? GetLocalModInfo(string guid)
    {
        return LocalMods.Mods.TryGetValue(guid, out var info) ? info : null;
    }

    /// <summary>
    /// Helper to copy a directory recursively.
    /// </summary>
    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectory(dir, destSubDir);
        }
    }
}

/// <summary>
/// Index of local mods stored on the server.
/// </summary>
public class LocalModsIndex
{
    public Dictionary<string, LocalModInfo> Mods { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Metadata for a locally stored mod.
/// </summary>
public class LocalModInfo
{
    public string Guid { get; set; } = "";
    public string ModName { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string StagedAt { get; set; } = "";
}
