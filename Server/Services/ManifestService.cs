using System.Diagnostics;
using System.Security.Cryptography;
using BewasModSync.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace BewasModSync.Services;

/// <summary>
/// Service for generating file manifests from the actual installed mods
/// Reads directly from the SPT installation folders, not staging/cache
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
    /// Generate a file manifest for all installed mods
    /// Reads directly from actual install paths on the server
    /// This means any config file changes are automatically reflected
    /// </summary>
    public FileManifest GenerateManifest()
    {
        var stopwatch = Stopwatch.StartNew();
        var manifest = new FileManifest();

        _logger.Info("Generating file manifest from installed files...");

        // Only include installed mods (not pending or pending removal)
        var installedMods = _configService.Config.ModList
            .Where(m => m.Status == ModStatus.Installed)
            .ToList();

        _logger.Info($"Processing {installedMods.Count} installed mods...");

        foreach (var mod in installedMods)
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

        _logger.Success($"Manifest generated in {manifest.GenerationTimeMs}ms with {manifest.Files.Count} files from {installedMods.Count} mods");

        return manifest;
    }

    private void AddModToManifest(FileManifest manifest, ModEntry mod)
    {
        // Process each install path - read from ACTUAL installed location
        foreach (var installPath in mod.InstallPaths)
        {
            var sourcePath = installPath[0]; // e.g., "BepInEx" (relative path in original archive)
            var targetPath = installPath[1]; // e.g., "<SPT_ROOT>/BepInEx" (where it was installed)

            // The actual installed path on the server
            var actualInstalledPath = targetPath.Replace("<SPT_ROOT>", _configService.SptRoot);
            
            if (!Directory.Exists(actualInstalledPath))
            {
                // Might be a file, not a directory
                if (File.Exists(actualInstalledPath))
                {
                    AddFileToManifest(manifest, actualInstalledPath, targetPath, mod);
                }
                else
                {
                    _logger.Warning($"Install path not found for mod '{mod.ModName}': {actualInstalledPath}");
                }
                continue;
            }

            // Recursively add all files from this installed directory
            AddDirectoryToManifest(manifest, actualInstalledPath, targetPath, mod);
        }
    }

    private void AddDirectoryToManifest(FileManifest manifest, string installedDir, string targetBase, ModEntry mod)
    {
        try
        {
            foreach (var file in Directory.GetFiles(installedDir, "*", SearchOption.AllDirectories))
            {
                // Calculate relative path from the installed directory
                var relativePath = Path.GetRelativePath(installedDir, file);
                
                // Build target path (replace <SPT_ROOT> with empty to get relative path for manifest)
                var targetPathBase = targetBase.Replace("<SPT_ROOT>", "").TrimStart('/', '\\');
                var fullTargetPath = Path.Combine(targetPathBase, relativePath).Replace('\\', '/');

                AddFileToManifest(manifest, file, fullTargetPath, mod);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Error scanning directory '{installedDir}': {ex.Message}");
        }
    }

    private void AddFileToManifest(FileManifest manifest, string sourceFile, string targetPath, ModEntry mod)
    {
        // Normalize path separators
        targetPath = targetPath.Replace('\\', '/').TrimStart('/');

        // Skip if already in manifest (another mod may have the same file)
        if (manifest.Files.ContainsKey(targetPath))
        {
            _logger.Debug($"File already in manifest from another mod, skipping: {targetPath}");
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
