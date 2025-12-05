using ModGod.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace ModGod.Services;

[Injectable(InjectionType = InjectionType.Singleton)]
public class ModDownloadService
{
    private readonly ConfigService _configService;
    private readonly ISptLogger<ModDownloadService> _logger;
    private readonly HttpClient _httpClient;

    public ModDownloadService(
        ConfigService configService,
        ISptLogger<ModDownloadService> logger)
    {
        _configService = configService;
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ModGod/1.0");
        _httpClient.Timeout = TimeSpan.FromMinutes(30); // Allow 30 min for large files
    }

    public async Task<ModDownloadResult> DownloadAndAnalyzeModAsync(string url)
    {
        var result = new ModDownloadResult { Url = url };

        try
        {
            // Check if already staged
            if (_configService.IsUrlStaged(url))
            {
                var stagedPath = _configService.Staging.UrlToPath[url];
                result.ExtractPath = stagedPath;
                result.Success = true;
                result.FromCache = true;
                AnalyzeModStructure(result);
                return result;
            }

            // Download the archive using streaming (better for large files)
            _logger.Info($"Downloading mod from: {url}");
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            // Log content info
            var contentLength = response.Content.Headers.ContentLength;
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";
            _logger.Info($"Content-Type: {contentType}, Size: {(contentLength.HasValue ? $"{contentLength.Value / 1024 / 1024}MB" : "unknown")}");

            // Warn if content type doesn't look like a zip
            if (!contentType.Contains("zip") && !contentType.Contains("octet-stream") && !contentType.Contains("binary"))
            {
                _logger.Warning($"Unexpected content type: {contentType}. Expected application/zip or application/octet-stream");
            }

            var stagingPath = _configService.GetStagingPathForUrl(url);

            // Clean and create directory
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, true);
            }
            Directory.CreateDirectory(stagingPath);

            // Stream directly to file (handles large files efficiently)
            var archivePath = Path.Combine(stagingPath, "mod.zip");
            await using (var fileStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                await response.Content.CopyToAsync(fileStream);
            }

            var fileSize = new FileInfo(archivePath).Length;
            _logger.Info($"Downloaded {fileSize / 1024 / 1024}MB to {archivePath}");

            // Extract archive (supports .zip, .7z, .rar, .tar.gz, etc.)
            var extractPath = Path.Combine(stagingPath, "extracted");
            Directory.CreateDirectory(extractPath);
            
            _logger.Info("Extracting archive...");
            try
            {
                using (var archive = ArchiveFactory.Open(archivePath))
                {
                    _logger.Info($"Archive type: {archive.Type}");
                    var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                    _logger.Info($"Found {entries.Count} files to extract");
                    
                    foreach (var entry in entries)
                    {
                        entry.WriteToDirectory(extractPath, new ExtractionOptions
                        {
                            ExtractFullPath = true,
                            Overwrite = true
                        });
                    }
                }
                _logger.Info("Extraction complete!");
            }
            catch (Exception ex)
            {
                _logger.Error($"Extraction failed: {ex.GetType().Name}: {ex.Message}");
                throw;
            }

            // Update staging index
            _configService.Staging.UrlToPath[url] = stagingPath;
            await _configService.SaveStagingIndexAsync();

            result.ExtractPath = stagingPath;
            result.Success = true;

            // Analyze structure
            AnalyzeModStructure(result);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to download mod: {ex.Message}");
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    private void AnalyzeModStructure(ModDownloadResult result)
    {
        var extractedPath = Path.Combine(result.ExtractPath!, "extracted");

        if (!Directory.Exists(extractedPath))
        {
            result.IsStandardStructure = false;
            return;
        }

        var topLevelDirs = Directory.GetDirectories(extractedPath)
            .Select(d => Path.GetFileName(d))
            .ToList();

        // Check if structure follows standard pattern (BepInEx and/or SPT at top level)
        var validDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BepInEx", "SPT" };
        var hasOnlyValidDirs = topLevelDirs.All(d => validDirs.Contains(d));
        var hasAtLeastOneValidDir = topLevelDirs.Any(d => validDirs.Contains(d));

        result.TopLevelDirectories = topLevelDirs;
        result.IsStandardStructure = hasOnlyValidDirs && hasAtLeastOneValidDir;

        if (result.IsStandardStructure)
        {
            result.SuggestedInstallPaths = GenerateInstallPaths(extractedPath, topLevelDirs);
        }
    }

    private List<string[]> GenerateInstallPaths(string extractedPath, List<string> topLevelDirs)
    {
        var installPaths = new List<string[]>();

        foreach (var dir in topLevelDirs)
        {
            var sourcePath = dir; // Relative path within the extracted mod
            var targetPath = dir switch
            {
                "BepInEx" => "<SPT_ROOT>/BepInEx",
                "SPT" => "<SPT_ROOT>/SPT",
                _ => $"<SPT_ROOT>/{dir}"
            };

            installPaths.Add(new[] { sourcePath, targetPath });
        }

        return installPaths;
    }

    public List<string> GetExtractedContents(string stagingPath)
    {
        var extractedPath = Path.Combine(stagingPath, "extracted");
        if (!Directory.Exists(extractedPath))
        {
            return new List<string>();
        }

        return GetDirectoryContentsRecursive(extractedPath, "")
            .Take(100) // Limit to first 100 items for UI display
            .ToList();
    }

    private IEnumerable<string> GetDirectoryContentsRecursive(string basePath, string relativePath)
    {
        var currentPath = Path.Combine(basePath, relativePath);

        foreach (var dir in Directory.GetDirectories(currentPath))
        {
            var dirName = Path.GetFileName(dir);
            var newRelative = string.IsNullOrEmpty(relativePath) ? dirName : $"{relativePath}/{dirName}";
            yield return $"[DIR] {newRelative}";

            foreach (var item in GetDirectoryContentsRecursive(basePath, newRelative))
            {
                yield return item;
            }
        }

        foreach (var file in Directory.GetFiles(currentPath))
        {
            var fileName = Path.GetFileName(file);
            var newRelative = string.IsNullOrEmpty(relativePath) ? fileName : $"{relativePath}/{fileName}";
            yield return newRelative;
        }
    }
}

public class ModDownloadResult
{
    public string Url { get; set; } = string.Empty;
    public string? ModName { get; set; }
    public bool Success { get; set; }
    public bool FromCache { get; set; }
    public string? ExtractPath { get; set; }
    public string? Error { get; set; }
    public bool IsStandardStructure { get; set; }
    public List<string> TopLevelDirectories { get; set; } = new();
    public List<string[]> SuggestedInstallPaths { get; set; } = new();
}
