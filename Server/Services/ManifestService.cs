using System.Diagnostics;
using System.Security.Cryptography;
using BewasModSync.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace BewasModSync.Services;

/// <summary>
/// Service for generating file manifests from the mod cache
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton)]
public class ManifestService
{
    private readonly ConfigService _configService;
    private readonly ISptLogger<ManifestService> _logger;

    public ManifestService(
        ConfigService configService,
        ISptLogger<ManifestService> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// Generate a file manifest for all mods in the config
    /// Maps files from mod cache to their target paths and computes hashes
    /// </summary>
    public FileManifest GenerateManifest()
    {
        var stopwatch = Stopwatch.StartNew();
        var manifest = new FileManifest();

        _logger.Info("Generating file manifest...");

        foreach (var mod in _configService.Config.ModList)
        {
            try
            {
                AddModToManifest(manifest, mod);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to process mod '{mod.ModName}' for manifest: {ex.Message}");
            }
        }

        stopwatch.Stop();
        manifest.GenerationTimeMs = stopwatch.ElapsedMilliseconds;
        manifest.GeneratedAt = DateTime.UtcNow.ToString("o");

        _logger.Success($"Manifest generated in {manifest.GenerationTimeMs}ms with {manifest.Files.Count} files");

        return manifest;
    }

    private void AddModToManifest(FileManifest manifest, ModEntry mod)
    {
        // Get the cached extraction path for this mod
        if (!_configService.IsUrlCached(mod.DownloadUrl))
        {
            _logger.Warning($"Mod '{mod.ModName}' is not cached, skipping manifest generation");
            return;
        }

        var cachePath = _configService.ModCache.UrlToPath[mod.DownloadUrl];
        var extractedPath = Path.Combine(cachePath, "extracted");

        if (!Directory.Exists(extractedPath))
        {
            _logger.Warning($"Extracted path not found for mod '{mod.ModName}': {extractedPath}");
            return;
        }

        // Process each sync path
        foreach (var syncPath in mod.SyncPaths)
        {
            var sourcePath = syncPath[0]; // e.g., "BepInEx" or "SPT"
            var targetPath = syncPath[1]; // e.g., "<SPT_ROOT>/BepInEx"

            // Full source path in the cache
            var fullSourcePath = Path.Combine(extractedPath, sourcePath);
            
            if (!Directory.Exists(fullSourcePath))
            {
                // Might be a file, not a directory
                if (File.Exists(fullSourcePath))
                {
                    AddFileToManifest(manifest, fullSourcePath, targetPath, mod);
                }
                continue;
            }

            // Recursively add all files from this source directory
            AddDirectoryToManifest(manifest, fullSourcePath, targetPath, mod);
        }
    }

    private void AddDirectoryToManifest(FileManifest manifest, string sourceDir, string targetBase, ModEntry mod)
    {
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            // Calculate relative path from source directory
            var relativePath = Path.GetRelativePath(sourceDir, file);
            
            // Build target path (replace <SPT_ROOT> with empty to get relative path)
            var targetPath = targetBase.Replace("<SPT_ROOT>", "").TrimStart('/', '\\');
            var fullTargetPath = Path.Combine(targetPath, relativePath).Replace('\\', '/');

            AddFileToManifest(manifest, file, fullTargetPath, mod);
        }
    }

    private void AddFileToManifest(FileManifest manifest, string sourceFile, string targetPath, ModEntry mod)
    {
        // Normalize path separators
        targetPath = targetPath.Replace('\\', '/').TrimStart('/');

        // Skip if already in manifest (shouldn't happen, but safety check)
        if (manifest.Files.ContainsKey(targetPath))
        {
            _logger.Debug($"File already in manifest, skipping: {targetPath}");
            return;
        }

        try
        {
            var fileInfo = new FileInfo(sourceFile);
            var hash = ComputeFileHash(sourceFile);

            manifest.Files[targetPath] = new FileEntry
            {
                Hash = hash,
                Size = fileInfo.Length,
                ModName = mod.ModName,
                Required = !mod.Optional
            };
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to hash file '{sourceFile}': {ex.Message}");
        }
    }

    /// <summary>
    /// Compute SHA256 hash of a file
    /// </summary>
    private static string ComputeFileHash(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hashBytes = sha256.ComputeHash(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}

