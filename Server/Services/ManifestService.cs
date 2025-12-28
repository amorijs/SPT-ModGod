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
    /// <summary>
    /// Generate a file manifest for player (game) clients.
    /// Uses PlayerSyncConfig to determine what files to include.
    /// </summary>
    public FileManifest GenerateManifest()
    {
        var stopwatch = Stopwatch.StartNew();
        var manifest = new FileManifest();

        var playerConfig = configService.Config.PlayerSyncConfig ?? ClientSyncConfig.DefaultPlayerConfig();
        
        // Build list of allowed target paths from sync paths
        var allowedTargets = playerConfig.SyncPaths
            .Select(p => NormalizePath(p.Target))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        
        if (allowedTargets.Count == 0)
        {
            logger.Info("No player sync paths configured - returning empty manifest");
            manifest.GenerationTimeMs = stopwatch.ElapsedMilliseconds;
            manifest.GeneratedAt = DateTime.UtcNow.ToString("o");
            return manifest;
        }

        // Combine default exclusions + custom exclusions
        var exclusions = BuildExclusionList(playerConfig);

        logger.Info($"Generating player manifest with {allowedTargets.Count} sync paths and {exclusions.Count} exclusion patterns...");

        // Only include installed mods (not pending or pending removal)
        var installedMods = configService.Config.ModList
            .Where(m => m.Status == ModStatus.Installed)
            .ToList();

        logger.Info($"Processing {installedMods.Count} installed mods...");

        foreach (var mod in installedMods)
        {
            try
            {
                AddModToManifest(manifest, mod, exclusions, allowedTargets, playerConfig.SyncPaths);
            }
            catch (Exception ex)
            {
                logger.Warning($"Failed to process mod '{mod.ModName}' for manifest: {ex.Message}");
            }
        }

        // Scan sync directories for any untracked files (files not from managed mods)
        var untrackedCount = AddUntrackedFilesToManifest(manifest, playerConfig.SyncPaths, exclusions, allowedTargets);
        if (untrackedCount > 0)
        {
            logger.Info($"Added {untrackedCount} untracked files from sync directories");
        }

        manifest.SyncExclusions = exclusions;
        manifest.SyncRoots = allowedTargets;

        stopwatch.Stop();
        manifest.GenerationTimeMs = stopwatch.ElapsedMilliseconds;
        manifest.GeneratedAt = DateTime.UtcNow.ToString("o");

        logger.Success(
            $"Player manifest generated in {manifest.GenerationTimeMs}ms with {manifest.Files.Count} files ({installedMods.Count} mods + {untrackedCount} untracked)");

        return manifest;
    }
    
    /// <summary>
    /// Build the combined exclusion list from a sync config.
    /// Exclusion paths are transformed through sync path mappings so they match target paths.
    /// </summary>
    private List<string> BuildExclusionList(ClientSyncConfig config)
    {
        var allExclusions = new List<string>();

        // Add default exclusion patterns if enabled
        // Note: Default patterns are glob patterns that work on any path structure
        allExclusions.AddRange(DefaultSyncExclusions.GetEffectiveDefaults(config));

        // Add explicit excluded paths - these need to be transformed to target paths
        // because manifest files use target paths, not source paths
        foreach (var excludedPath in config.ExcludedPaths)
        {
            var normalized = NormalizePath(excludedPath);
            if (string.IsNullOrWhiteSpace(normalized)) continue;
            
            // Transform exclusion through sync path mapping (source -> target)
            var targetPath = TransformPathThroughSyncPaths(normalized, config.SyncPaths);
            if (targetPath != null)
            {
                allExclusions.Add(targetPath);
                logger.Debug($"Exclusion mapped: {normalized} -> {targetPath}");
            }
            else
            {
                // Path doesn't match any sync path - add as-is (might be a glob or direct path)
                allExclusions.Add(normalized);
            }
        }

        // Deduplicate
        return allExclusions
            .Select(NormalizePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Generate a file manifest for headless clients.
    /// Uses HeadlessSyncConfig to determine what files to include.
    /// </summary>
    public FileManifest GenerateHeadlessManifest()
    {
        var stopwatch = Stopwatch.StartNew();
        var manifest = new FileManifest();

        var headlessConfig = configService.Config.HeadlessSyncConfig ?? ClientSyncConfig.DefaultHeadlessConfig();
        
        // Build list of allowed target paths from sync paths
        var allowedTargets = headlessConfig.SyncPaths
            .Select(p => NormalizePath(p.Target))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        if (headlessConfig.SyncPaths.Count == 0)
        {
            logger.Info("No headless sync paths configured - returning empty manifest");
            manifest.GenerationTimeMs = stopwatch.ElapsedMilliseconds;
            manifest.GeneratedAt = DateTime.UtcNow.ToString("o");
            return manifest;
        }

        // Build exclusions
        var exclusions = BuildExclusionList(headlessConfig);

        logger.Info($"Generating headless manifest with {headlessConfig.SyncPaths.Count} sync paths...");

        // Get all installed mods
        var installedMods = configService.Config.ModList
            .Where(m => m.Status == ModStatus.Installed)
            .ToList();

        // Build the manifest using headless sync paths
        foreach (var mod in installedMods)
        {
            try
            {
                AddModToManifest(manifest, mod, exclusions, allowedTargets, headlessConfig.SyncPaths);
            }
            catch (Exception ex)
            {
                logger.Warning($"Failed to process mod '{mod.ModName}' for headless manifest: {ex.Message}");
            }
        }

        // Scan sync directories for any untracked files (files not from managed mods)
        var untrackedCount = AddUntrackedFilesToManifest(manifest, headlessConfig.SyncPaths, exclusions, allowedTargets);
        if (untrackedCount > 0)
        {
            logger.Info($"Added {untrackedCount} untracked files from sync directories");
        }

        // Store exclusions and sync roots in manifest
        manifest.SyncExclusions = exclusions;
        manifest.SyncRoots = allowedTargets;

        stopwatch.Stop();
        manifest.GenerationTimeMs = stopwatch.ElapsedMilliseconds;
        manifest.GeneratedAt = DateTime.UtcNow.ToString("o");

        logger.Success(
            $"Headless manifest generated in {manifest.GenerationTimeMs}ms with {manifest.Files.Count} files ({installedMods.Count} mods + {untrackedCount} untracked)");

        return manifest;
    }

    private void AddModToManifest(FileManifest manifest, ModEntry mod, List<string> exclusions, 
        List<string> allowedTargets, List<SyncPathEntry> syncPaths)
    {
        // PREFER InstalledFiles if available - this tracks the exact files installed by this mod
        // This prevents the bug where shared directories (e.g., BepInEx/plugins) cause files
        // from other mods to be attributed to the wrong mod.
        if (mod.InstalledFiles.Count > 0)
        {
            logger.Debug($"Using InstalledFiles for mod '{mod.ModName}' ({mod.InstalledFiles.Count} files)");

            foreach (var relativePath in mod.InstalledFiles)
            {
                // Transform path through sync path mapping (source -> target)
                var targetPath = TransformPathThroughSyncPaths(relativePath, syncPaths);
                if (targetPath == null)
                {
                    logger.Debug($"File not in any sync path, skipping: {relativePath}");
                    continue;
                }
                
                var fullPath = Path.Combine(configService.SptRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(fullPath))
                {
                    AddFileToManifest(manifest, fullPath, targetPath, mod, exclusions, allowedTargets);
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
            // Handle both old format (<SPT_ROOT>/path) and new format (path) for backwards compatibility
            var targetPathRaw = installPath[1];
            var sourceRelPath = targetPathRaw.Replace("<SPT_ROOT>", "").TrimStart('/', '\\');
            
            // Transform path through sync path mapping
            var targetPath = TransformPathThroughSyncPaths(sourceRelPath, syncPaths);
            if (targetPath == null)
            {
                logger.Debug($"Install path not in any sync path, skipping: {sourceRelPath}");
                continue;
            }

            // The actual installed path on the server - handle both formats
            var actualInstalledPath = targetPathRaw.Contains("<SPT_ROOT>")
                ? targetPathRaw.Replace("<SPT_ROOT>", configService.SptRoot)
                : Path.Combine(configService.SptRoot, sourceRelPath);

            if (!Directory.Exists(actualInstalledPath))
            {
                // Might be a file, not a directory
                if (File.Exists(actualInstalledPath))
                {
                    AddFileToManifest(manifest, actualInstalledPath, targetPath, legacyMod, exclusions, allowedTargets);
                }
                else
                {
                    logger.Warning($"Install path not found for mod '{mod.ModName}': {actualInstalledPath}");
                }

                continue;
            }

            // Recursively add all files from this installed directory
            AddDirectoryToManifest(manifest, actualInstalledPath, installPath[1], legacyMod, exclusions, allowedTargets, syncPaths);
        }
    }
    
    /// <summary>
    /// Transform a source path to a target path using sync path mappings.
    /// Returns null if the path is not within any sync path.
    /// </summary>
    private static string? TransformPathThroughSyncPaths(string sourcePath, List<SyncPathEntry> syncPaths)
    {
        var normSource = NormalizePath(sourcePath);
        
        foreach (var syncPath in syncPaths)
        {
            var normSyncSource = NormalizePath(syncPath.Source);
            var normSyncTarget = NormalizePath(syncPath.Target);
            
            // Check if sourcePath is under this sync path's source
            if (normSource.Equals(normSyncSource, StringComparison.OrdinalIgnoreCase))
            {
                // Exact match - return the target
                return normSyncTarget;
            }
            
            if (normSource.StartsWith(normSyncSource + "/", StringComparison.OrdinalIgnoreCase))
            {
                // Path is under this sync source - transform to target
                var relativePart = normSource.Substring(normSyncSource.Length + 1);
                return string.IsNullOrEmpty(normSyncTarget) 
                    ? relativePart 
                    : $"{normSyncTarget}/{relativePart}";
            }
        }
        
        return null; // Path not in any sync path
    }

    private void AddDirectoryToManifest(FileManifest manifest, string installedDir, string targetBase, ModEntry mod,
        List<string> exclusions, List<string> allowedTargets, List<SyncPathEntry> syncPaths)
    {
        try
        {
            foreach (var file in Directory.GetFiles(installedDir, "*", SearchOption.AllDirectories))
            {
                // Calculate relative path from the installed directory
                var relativePath = Path.GetRelativePath(installedDir, file);

                // Build source path (replace <SPT_ROOT> with empty to get relative path)
                var sourcePathBase = targetBase.Replace("<SPT_ROOT>", "").TrimStart('/', '\\');
                var fullSourcePath = Path.Combine(sourcePathBase, relativePath).Replace('\\', '/');
                
                // Transform through sync path mapping
                var targetPath = TransformPathThroughSyncPaths(fullSourcePath, syncPaths);
                if (targetPath == null)
                {
                    logger.Debug($"File not in any sync path, skipping: {fullSourcePath}");
                    continue;
                }

                AddFileToManifest(manifest, file, targetPath, mod, exclusions, allowedTargets);
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"Error scanning directory '{installedDir}': {ex.Message}");
        }
    }

    private void AddFileToManifest(FileManifest manifest, string sourceFile, string targetPath, ModEntry mod,
        List<string> exclusions, List<string> allowedTargets)
    {
        // Normalize path separators
        targetPath = targetPath.Replace('\\', '/').TrimStart('/');

        if (!IsAllowedPath(targetPath, allowedTargets))
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
    /// Scan sync directories and add any files not already in the manifest.
    /// These are files that exist in sync paths but weren't installed by any tracked mod.
    /// </summary>
    private int AddUntrackedFilesToManifest(FileManifest manifest, List<SyncPathEntry> syncPaths,
        List<string> exclusions, List<string> allowedTargets)
    {
        var count = 0;
        var untrackedMod = new ModEntry
        {
            ModName = "Untracked",
            Optional = false
        };

        foreach (var syncPath in syncPaths)
        {
            var sourceDir = Path.Combine(configService.SptRoot, 
                syncPath.Source.Replace('/', Path.DirectorySeparatorChar));

            if (!Directory.Exists(sourceDir))
            {
                logger.Debug($"Sync source directory does not exist: {sourceDir}");
                continue;
            }

            try
            {
                foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
                {
                    // Calculate the relative path from SPT root
                    var relativeFromRoot = Path.GetRelativePath(configService.SptRoot, file)
                        .Replace('\\', '/');

                    // Transform through sync path mapping to get target path
                    var targetPath = TransformPathThroughSyncPaths(relativeFromRoot, syncPaths);
                    if (targetPath == null)
                    {
                        continue; // Not in any sync path (shouldn't happen but safety check)
                    }

                    // Normalize
                    targetPath = targetPath.Replace('\\', '/').TrimStart('/');

                    // Skip if already in manifest (was added by a tracked mod)
                    if (manifest.Files.ContainsKey(targetPath))
                    {
                        continue;
                    }

                    // Skip if not in allowed targets
                    if (!IsAllowedPath(targetPath, allowedTargets))
                    {
                        continue;
                    }

                    // Skip if excluded
                    if (IsExcluded(targetPath, exclusions))
                    {
                        logger.Debug($"Untracked file excluded: {targetPath}");
                        continue;
                    }

                    // Add to manifest as untracked
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        var hash = ComputeFileHash(file);

                        manifest.Files[targetPath] = new FileEntry
                        {
                            Hash = hash,
                            Size = fileInfo.Length,
                            ModName = untrackedMod.ModName,
                            Required = !untrackedMod.Optional
                        };

                        count++;
                        logger.Debug($"Added untracked file: {targetPath}");
                    }
                    catch (Exception ex)
                    {
                        logger.Warning($"Failed to hash untracked file '{file}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warning($"Error scanning sync directory '{sourceDir}': {ex.Message}");
            }
        }

        return count;
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

    private static bool IsAllowedPath(string relativePath, List<string> allowedTargets)
    {
        var norm = NormalizePath(relativePath);
        return allowedTargets.Any(target =>
            norm.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase));
    }
}
