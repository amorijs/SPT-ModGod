using System.Diagnostics;
using System.Security.Cryptography;
using ModGod.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace ModGod.Services;

/// <summary>
/// Service for generating file manifests from the actual installed mods
/// Reads directly from the SPT installation folders, not staging/cache
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton)]
public class ManifestService(
    ConfigService configService,
    ISptLogger<ManifestService> logger)
{
    private static readonly string[] ManifestAllowedRoots =
    [
        "BepInEx/plugins",
        "SPT/user/mods"
    ];

    /// <summary>
    /// Generate a file manifest for all installed mods
    /// Reads directly from actual install paths on the server
    /// This means any config file changes are automatically reflected
    /// </summary>
    public FileManifest GenerateManifest()
    {
        var stopwatch = Stopwatch.StartNew();
        var manifest = new FileManifest();

        // Combine default exclusions + custom exclusions
        var allExclusions = new List<string>();

        // Add default exclusions if enabled
        allExclusions.AddRange(DefaultSyncExclusions.GetEffectiveDefaults(configService.Config));

        // Add custom exclusions
        allExclusions.AddRange(
            configService.Config.SyncExclusions
                .Select(NormalizePath)
                .Where(p => !string.IsNullOrWhiteSpace(p)));

        // Deduplicate
        var exclusions = allExclusions
            .Select(NormalizePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        logger.Info($"Generating file manifest with {exclusions.Count} exclusion patterns...");

        // Only include installed mods (not pending or pending removal)
        var installedMods = configService.Config.ModList
            .Where(m => m.Status == ModStatus.Installed)
            .ToList();

        logger.Info($"Processing {installedMods.Count} installed mods...");

        foreach (var mod in installedMods)
        {
            try
            {
                AddModToManifest(manifest, mod, exclusions);
            }
            catch (Exception ex)
            {
                logger.Warning($"Failed to process mod '{mod.ModName}' for manifest: {ex.Message}");
            }
        }

        manifest.SyncExclusions = exclusions;

        stopwatch.Stop();
        manifest.GenerationTimeMs = stopwatch.ElapsedMilliseconds;
        manifest.GeneratedAt = DateTime.UtcNow.ToString("o");

        logger.Success(
            $"Manifest generated in {manifest.GenerationTimeMs}ms with {manifest.Files.Count} files from {installedMods.Count} mods");

        return manifest;
    }

    /// <summary>
    /// Generate a file manifest for headless clients.
    /// Only includes files that are explicitly in the HeadlessSyncPaths list.
    /// </summary>
    public FileManifest GenerateHeadlessManifest()
    {
        var stopwatch = Stopwatch.StartNew();
        var manifest = new FileManifest();

        var headlessPaths = configService.Config.HeadlessSyncPaths
            .Select(NormalizePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        if (headlessPaths.Count == 0)
        {
            logger.Info("No headless sync paths configured - returning empty manifest");
            manifest.GenerationTimeMs = stopwatch.ElapsedMilliseconds;
            manifest.GeneratedAt = DateTime.UtcNow.ToString("o");
            return manifest;
        }

        logger.Info($"Generating headless manifest with {headlessPaths.Count} inclusion paths...");

        // Get all installed mods
        var installedMods = configService.Config.ModList
            .Where(m => m.Status == ModStatus.Installed)
            .ToList();

        // Build the full manifest first, then filter to only headless paths
        foreach (var mod in installedMods)
        {
            try
            {
                AddModToHeadlessManifest(manifest, mod, headlessPaths);
            }
            catch (Exception ex)
            {
                logger.Warning($"Failed to process mod '{mod.ModName}' for headless manifest: {ex.Message}");
            }
        }

        // Store the headless paths in the manifest for client reference
        manifest.SyncExclusions = headlessPaths; // Reusing this field to indicate included paths for headless

        stopwatch.Stop();
        manifest.GenerationTimeMs = stopwatch.ElapsedMilliseconds;
        manifest.GeneratedAt = DateTime.UtcNow.ToString("o");

        logger.Success(
            $"Headless manifest generated in {manifest.GenerationTimeMs}ms with {manifest.Files.Count} files");

        return manifest;
    }

    private void AddModToHeadlessManifest(FileManifest manifest, ModEntry mod, List<string> headlessPaths)
    {
        // Use InstalledFiles if available
        if (mod.InstalledFiles.Count > 0)
        {
            foreach (var relativePath in mod.InstalledFiles)
            {
                // Only include if path matches headless inclusion patterns
                if (!IsIncludedForHeadless(relativePath, headlessPaths))
                    continue;

                var fullPath = Path.Combine(configService.SptRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(fullPath))
                {
                    AddFileToHeadlessManifest(manifest, fullPath, relativePath, mod);
                }
            }

            return;
        }

        // Fallback for legacy mods without InstalledFiles tracking
        var legacyMod = new ModEntry
        {
            ModName = "Unknown (Legacy)",
            Optional = mod.Optional
        };

        foreach (var installPath in mod.InstallPaths)
        {
            var targetPath = installPath[1];
            var actualInstalledPath = targetPath.Replace("<SPT_ROOT>", configService.SptRoot);

            if (Directory.Exists(actualInstalledPath))
            {
                AddDirectoryToHeadlessManifest(manifest, actualInstalledPath, targetPath, legacyMod, headlessPaths);
            }
            else if (File.Exists(actualInstalledPath))
            {
                var relPath = targetPath.Replace("<SPT_ROOT>", "").TrimStart('/', '\\');
                if (IsIncludedForHeadless(relPath, headlessPaths))
                {
                    AddFileToHeadlessManifest(manifest, actualInstalledPath, relPath, legacyMod);
                }
            }
        }
    }

    private void AddDirectoryToHeadlessManifest(FileManifest manifest, string installedDir, string targetBase,
        ModEntry mod, List<string> headlessPaths)
    {
        try
        {
            foreach (var file in Directory.GetFiles(installedDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(installedDir, file);
                var targetPathBase = targetBase.Replace("<SPT_ROOT>", "").TrimStart('/', '\\');
                var fullTargetPath = Path.Combine(targetPathBase, relativePath).Replace('\\', '/');

                if (IsIncludedForHeadless(fullTargetPath, headlessPaths))
                {
                    AddFileToHeadlessManifest(manifest, file, fullTargetPath, mod);
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"Error scanning directory '{installedDir}' for headless manifest: {ex.Message}");
        }
    }

    private void AddFileToHeadlessManifest(FileManifest manifest, string sourceFile, string targetPath, ModEntry mod)
    {
        targetPath = targetPath.Replace('\\', '/').TrimStart('/');

        if (!IsAllowedPath(targetPath))
            return;

        if (manifest.Files.ContainsKey(targetPath))
            return;

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
            logger.Warning($"Failed to hash file '{sourceFile}' for headless manifest: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if a path should be included for headless syncing
    /// </summary>
    private static bool IsIncludedForHeadless(string relativePath, IEnumerable<string> headlessPaths)
    {
        var norm = NormalizePath(relativePath);

        foreach (var pattern in headlessPaths)
        {
            var normPattern = NormalizePath(pattern);

            // Check if it's a glob pattern
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                if (GlobMatcher.IsMatch(norm, pattern))
                    return true;
            }
            else
            {
                // Exact match or the file is under this directory
                if (norm.Equals(normPattern, StringComparison.OrdinalIgnoreCase) ||
                    norm.StartsWith(normPattern + "/", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private void AddModToManifest(FileManifest manifest, ModEntry mod, List<string> exclusions)
    {
        // PREFER InstalledFiles if available - this tracks the exact files installed by this mod
        // This prevents the bug where shared directories (e.g., BepInEx/plugins) cause files
        // from other mods to be attributed to the wrong mod.
        if (mod.InstalledFiles.Count > 0)
        {
            logger.Debug($"Using InstalledFiles for mod '{mod.ModName}' ({mod.InstalledFiles.Count} files)");

            foreach (var relativePath in mod.InstalledFiles)
            {
                var fullPath = Path.Combine(configService.SptRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(fullPath))
                {
                    AddFileToManifest(manifest, fullPath, relativePath, mod, exclusions);
                }
                else
                {
                    logger.Debug($"InstalledFile not found (may have been removed): {relativePath}");
                }
            }

            return;
        }

        // FALLBACK: Scan InstallPaths directories for legacy mods without InstalledFiles tracking
        // Use a generic mod name since we can't reliably attribute files to specific mods
        logger.Warning($"Mod '{mod.ModName}' has no InstalledFiles tracked - using legacy fallback");

        // Create a temporary mod entry with a generic name for attribution
        var legacyMod = new ModEntry
        {
            ModName = "Unknown (Installed on old ModGod version, or manually installed)",
            Optional = mod.Optional
        };

        foreach (var installPath in mod.InstallPaths)
        {
            var targetPath = installPath[1]; // e.g., "<SPT_ROOT>/BepInEx" (where it was installed)

            // The actual installed path on the server
            var actualInstalledPath = targetPath.Replace("<SPT_ROOT>", configService.SptRoot);

            if (!Directory.Exists(actualInstalledPath))
            {
                // Might be a file, not a directory
                if (File.Exists(actualInstalledPath))
                {
                    AddFileToManifest(manifest, actualInstalledPath, targetPath, legacyMod, exclusions);
                }
                else
                {
                    logger.Warning($"Install path not found for mod '{mod.ModName}': {actualInstalledPath}");
                }

                continue;
            }

            // Recursively add all files from this installed directory
            AddDirectoryToManifest(manifest, actualInstalledPath, targetPath, legacyMod, exclusions);
        }
    }

    private void AddDirectoryToManifest(FileManifest manifest, string installedDir, string targetBase, ModEntry mod,
        List<string> exclusions)
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

                AddFileToManifest(manifest, file, fullTargetPath, mod, exclusions);
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"Error scanning directory '{installedDir}': {ex.Message}");
        }
    }

    private void AddFileToManifest(FileManifest manifest, string sourceFile, string targetPath, ModEntry mod,
        List<string> exclusions)
    {
        // Normalize path separators
        targetPath = targetPath.Replace('\\', '/').TrimStart('/');

        if (!IsAllowedPath(targetPath))
        {
            logger.Debug($"Skipping non-sync path: {targetPath}");
            return;
        }

        if (IsExcluded(targetPath, exclusions))
        {
            logger.Debug($"Excluded from manifest: {targetPath}");
            return;
        }

        // Skip if already in manifest (another mod may have the same file)
        if (manifest.Files.ContainsKey(targetPath))
        {
            logger.Debug($"File already in manifest from another mod, skipping: {targetPath}");
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
            logger.Warning($"Failed to hash file '{sourceFile}': {ex.Message}");
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

    private static string NormalizePath(string path)
    {
        return path.Replace("\\", "/").TrimStart('/');
    }

    private static bool IsExcluded(string relativePath, IEnumerable<string> exclusions)
    {
        var norm = NormalizePath(relativePath);

        foreach (var pattern in exclusions)
        {
            // Check if it's a glob pattern (contains *, ?, or **)
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                if (GlobMatcher.IsMatch(norm, pattern))
                    return true;
            }
            else
            {
                // Exact match or prefix match for non-glob patterns
                if (norm.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                    norm.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static bool IsAllowedPath(string relativePath)
    {
        return ManifestAllowedRoots.Any(root =>
            relativePath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }
}
