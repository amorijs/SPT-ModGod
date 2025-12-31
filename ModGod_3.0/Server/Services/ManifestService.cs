using System.Diagnostics;
using System.Security.Cryptography;
using ModGod3.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace ModGod3.Services;

/// <summary>
/// Service for generating file manifests from the filesystem.
/// In v3.0, the filesystem is authoritative - we scan what exists and build the manifest.
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton)]
public class ManifestService
{
    private readonly ConfigService _configService;
    private readonly ISptLogger<ManifestService> _logger;

    private static readonly string ServerVersion =
        typeof(ManifestService).Assembly.GetName().Version?.ToString(3) ?? "3.0.0";

    public ManifestService(
        ConfigService configService,
        ISptLogger<ManifestService> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// Generate a file manifest for clients.
    /// Optionally filter by opted-in source items for optional mods.
    /// </summary>
    /// <param name="optedInItems">List of optional source item paths the client has opted into. Null = include all required only.</param>
    public FileManifest GenerateManifest(IEnumerable<string>? optedInItems = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var manifest = new FileManifest { ModGodVersion = ServerVersion };

        var syncRoots = _configService.GetEffectiveSyncRoots();
        var exclusions = _configService.GetEffectiveExclusions();

        // Build set of opted-in paths (lowercase for case-insensitive comparison)
        var optedInSet = optedInItems?
            .Select(p => NormalizePath(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();

        _logger.Info($"Generating manifest with {syncRoots.Count} sync roots, {exclusions.Count} exclusions");
        if (optedInItems != null)
        {
            _logger.Info($"Client opted into {optedInSet.Count} optional items");
        }

        // Scan each sync root
        foreach (var syncRoot in syncRoots)
        {
            var fullPath = Path.Combine(_configService.SptRoot, syncRoot.Replace('/', Path.DirectorySeparatorChar));

            if (!Directory.Exists(fullPath))
            {
                _logger.Debug($"Sync root does not exist: {syncRoot}");
                continue;
            }

            ScanSyncRoot(manifest, fullPath, syncRoot, exclusions, optedInSet);
        }

        // Add source items info to manifest
        manifest.SourceItems = BuildSourceItemsList(optedInSet);
        manifest.SyncRoots = syncRoots;
        manifest.Exclusions = exclusions;

        stopwatch.Stop();
        manifest.GenerationTimeMs = stopwatch.ElapsedMilliseconds;
        manifest.GeneratedAt = DateTime.UtcNow.ToString("o");

        _logger.Success($"Manifest generated in {manifest.GenerationTimeMs}ms with {manifest.Files.Count} files");

        return manifest;
    }

    /// <summary>
    /// Scan a sync root directory and add files to the manifest
    /// </summary>
    private void ScanSyncRoot(
        FileManifest manifest,
        string syncRootPath,
        string syncRootRelative,
        List<string> exclusions,
        HashSet<string> optedInSet)
    {
        try
        {
            // Get all top-level items in the sync root
            var topLevelItems = new List<(string Path, bool IsDirectory)>();

            foreach (var dir in Directory.GetDirectories(syncRootPath))
            {
                topLevelItems.Add((dir, true));
            }

            foreach (var file in Directory.GetFiles(syncRootPath))
            {
                topLevelItems.Add((file, false));
            }

            // Process each top-level item (source item)
            foreach (var (itemPath, isDirectory) in topLevelItems)
            {
                var itemName = Path.GetFileName(itemPath);
                var sourceItemPath = $"{syncRootRelative}/{itemName}";

                // Check if this source item is optional
                var isOptional = _configService.Config.Sources.TryGetValue(sourceItemPath, out var metadata)
                                 && metadata.Optional;

                // If optional and not opted in, skip
                if (isOptional && !optedInSet.Contains(sourceItemPath))
                {
                    _logger.Debug($"Skipping optional item not opted in: {sourceItemPath}");
                    continue;
                }

                // Get display name for source item
                var displayName = metadata?.DisplayName ?? itemName;

                // Scan and add files
                if (isDirectory)
                {
                    ScanDirectory(manifest, itemPath, syncRootRelative, sourceItemPath, displayName, !isOptional, exclusions);
                }
                else
                {
                    AddFileToManifest(manifest, itemPath, sourceItemPath, sourceItemPath, displayName, !isOptional, exclusions);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Error scanning sync root {syncRootRelative}: {ex.Message}");
        }
    }

    /// <summary>
    /// Recursively scan a directory and add all files to the manifest
    /// </summary>
    private void ScanDirectory(
        FileManifest manifest,
        string directoryPath,
        string syncRoot,
        string sourceItemPath,
        string sourceItemDisplayName,
        bool required,
        List<string> exclusions)
    {
        try
        {
            foreach (var file in Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                // Calculate relative path from SPT root
                var relativePath = Path.GetRelativePath(_configService.SptRoot, file)
                    .Replace('\\', '/');

                AddFileToManifest(manifest, file, relativePath, sourceItemPath, sourceItemDisplayName, required, exclusions);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Error scanning directory {directoryPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Add a single file to the manifest
    /// </summary>
    private void AddFileToManifest(
        FileManifest manifest,
        string fullPath,
        string relativePath,
        string sourceItemPath,
        string sourceItemDisplayName,
        bool required,
        List<string> exclusions)
    {
        var normalizedPath = NormalizePath(relativePath);

        // Check if excluded
        if (IsExcluded(normalizedPath, exclusions))
        {
            _logger.Debug($"Excluded from manifest: {normalizedPath}");
            return;
        }

        // Skip if already in manifest
        if (manifest.Files.ContainsKey(normalizedPath))
        {
            return;
        }

        try
        {
            var fileInfo = new FileInfo(fullPath);
            var hash = ComputeFileHash(fullPath);

            manifest.Files[normalizedPath] = new FileEntry
            {
                Hash = hash,
                Size = fileInfo.Length,
                SourceItem = sourceItemPath,
                Required = required
            };
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to process file {relativePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Build list of source items for manifest
    /// </summary>
    private List<ManifestSourceItem> BuildSourceItemsList(HashSet<string> optedInSet)
    {
        var items = new List<ManifestSourceItem>();
        var sourceGroups = _configService.ScanSourceItems();

        foreach (var group in sourceGroups)
        {
            foreach (var item in group.Items)
            {
                // Skip optional items not opted in (if optedInSet is populated)
                if (item.Optional && optedInSet.Count > 0 && !optedInSet.Contains(item.Path))
                {
                    continue;
                }

                items.Add(new ManifestSourceItem
                {
                    Path = item.Path,
                    DisplayName = item.DisplayName,
                    Optional = item.Optional
                });
            }
        }

        return items;
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

    /// <summary>
    /// Check if a path matches any exclusion pattern
    /// </summary>
    private static bool IsExcluded(string path, List<string> exclusions)
    {
        var normalized = NormalizePath(path);

        foreach (var pattern in exclusions)
        {
            // Check if it's a glob pattern
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                if (GlobMatcher.IsMatch(normalized, pattern))
                    return true;
            }
            else
            {
                // Exact match or prefix match
                if (normalized.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }
}
