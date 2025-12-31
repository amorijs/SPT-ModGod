using System.Diagnostics;
using SharpCompress.Archives;
using SharpCompress.Common;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace ModGod3.Services;

/// <summary>
/// Service for downloading and extracting mod archives
/// </summary>
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
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ModGod/3.0");
        _httpClient.Timeout = TimeSpan.FromMinutes(30);
    }

    /// <summary>
    /// Downloads and extracts an archive from a URL to a temporary location.
    /// Returns the path to extracted contents.
    /// </summary>
    public async Task<DownloadResult> DownloadAndExtractAsync(string url, IProgress<DownloadProgress>? progress = null)
    {
        var result = new DownloadResult { Url = url };

        try
        {
            // Create temp directory for this download
            var tempDir = Path.Combine(_configService.DataPath, "temp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            // Download the archive
            _logger.Info($"Downloading from: {url}");
            progress?.Report(new DownloadProgress { Stage = "Downloading", Progress = 0 });

            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength ?? 0;
            var archivePath = Path.Combine(tempDir, "archive.zip");

            await using (var fileStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await using (var contentStream = await response.Content.ReadAsStreamAsync())
            {
                var buffer = new byte[81920];
                long totalBytesRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    totalBytesRead += bytesRead;

                    if (contentLength > 0)
                    {
                        var percent = (int)((double)totalBytesRead / contentLength * 100);
                        progress?.Report(new DownloadProgress
                        {
                            Stage = "Downloading",
                            Progress = percent,
                            BytesDownloaded = totalBytesRead,
                            TotalBytes = contentLength
                        });
                    }
                }
            }

            var fileSize = new FileInfo(archivePath).Length;
            _logger.Info($"Downloaded: {fileSize / 1024.0 / 1024.0:F1}MB");

            // Extract archive
            var extractPath = Path.Combine(tempDir, "extracted");
            Directory.CreateDirectory(extractPath);

            progress?.Report(new DownloadProgress { Stage = "Extracting", Progress = 0 });
            _logger.Info("Extracting archive...");

            await ExtractArchiveAsync(archivePath, extractPath, progress);

            result.ExtractPath = extractPath;
            result.TempDir = tempDir;
            result.Success = true;

            // Analyze structure
            AnalyzeStructure(result);
            _logger.Info($"Extraction complete: {result.TopLevelDirectories.Count} top-level directories");
        }
        catch (Exception ex)
        {
            _logger.Error($"Download failed: {ex.Message}");
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Extract archive with support for multiple formats
    /// </summary>
    private async Task ExtractArchiveAsync(string archivePath, string extractPath, IProgress<DownloadProgress>? progress)
    {
        // Detect archive type
        ArchiveType? archiveType = null;
        try
        {
            using (var archive = ArchiveFactory.Open(archivePath))
            {
                archiveType = archive.Type;
                _logger.Info($"Archive type: {archiveType}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to detect archive type: {ex.Message}");
            throw;
        }

        // For 7z archives, try native 7z.exe first (much faster)
        if (archiveType == ArchiveType.SevenZip)
        {
            var sevenZipPath = Find7ZipExecutable();
            if (sevenZipPath != null)
            {
                _logger.Info("Using native 7-Zip for faster extraction");
                try
                {
                    if (await ExtractWith7ZipAsync(archivePath, extractPath, sevenZipPath, progress))
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Native 7-Zip failed: {ex.Message}, falling back to SharpCompress");
                }
            }
        }

        // SharpCompress extraction
        using (var archive = ArchiveFactory.Open(archivePath))
        {
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            var totalFiles = entries.Count;
            var extractedCount = 0;

            foreach (var entry in entries)
            {
                var entryPath = entry.Key?.Replace('\\', '/') ?? "";

                if (!string.IsNullOrEmpty(entryPath))
                {
                    var targetPath = Path.Combine(extractPath, entryPath.Replace('/', Path.DirectorySeparatorChar));
                    var targetDir = Path.GetDirectoryName(targetPath);

                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    entry.WriteToFile(targetPath, new ExtractionOptions { Overwrite = true });
                }

                extractedCount++;
                var percent = (int)((double)extractedCount / totalFiles * 100);
                progress?.Report(new DownloadProgress
                {
                    Stage = "Extracting",
                    Progress = percent,
                    FilesExtracted = extractedCount,
                    TotalFiles = totalFiles
                });
            }
        }
    }

    /// <summary>
    /// Analyze the structure of extracted files
    /// </summary>
    private void AnalyzeStructure(DownloadResult result)
    {
        if (string.IsNullOrEmpty(result.ExtractPath) || !Directory.Exists(result.ExtractPath))
        {
            result.IsStandardStructure = false;
            return;
        }

        var topLevelDirs = Directory.GetDirectories(result.ExtractPath)
            .Select(d => Path.GetFileName(d))
            .ToList();

        var topLevelFiles = Directory.GetFiles(result.ExtractPath)
            .Select(f => Path.GetFileName(f))
            .ToList();

        // Check if structure follows standard pattern (BepInEx and/or SPT at top level)
        var validDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BepInEx", "SPT" };
        var hasOnlyValidDirs = topLevelDirs.All(d => validDirs.Contains(d)) && topLevelFiles.Count == 0;
        var hasAtLeastOneValidDir = topLevelDirs.Any(d => validDirs.Contains(d));

        result.TopLevelDirectories = topLevelDirs;
        result.TopLevelFiles = topLevelFiles;
        result.IsStandardStructure = hasOnlyValidDirs && hasAtLeastOneValidDir;

        if (result.IsStandardStructure)
        {
            result.SuggestedInstallPaths = GenerateInstallPaths(result.ExtractPath, topLevelDirs);
        }
    }

    /// <summary>
    /// Generate suggested install paths based on archive structure
    /// </summary>
    private List<ArchiveInstallMapping> GenerateInstallPaths(string extractedPath, List<string> topLevelDirs)
    {
        var installPaths = new List<ArchiveInstallMapping>();

        // Default mappings
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BepInEx"] = "BepInEx",
            ["BepInEx/plugins"] = "BepInEx/plugins",
            ["SPT"] = "SPT",
            ["SPT/user/mods"] = "SPT/user/mods"
        };

        foreach (var mapping in mappings)
        {
            var sourcePath = Path.Combine(extractedPath, mapping.Key.Replace('/', Path.DirectorySeparatorChar));

            if (Directory.Exists(sourcePath) || File.Exists(sourcePath))
            {
                installPaths.Add(new ArchiveInstallMapping
                {
                    Source = mapping.Key,
                    Target = mapping.Value
                });
            }
        }

        // Fallback: 1:1 mapping for top-level directories
        if (installPaths.Count == 0)
        {
            foreach (var dir in topLevelDirs)
            {
                installPaths.Add(new ArchiveInstallMapping
                {
                    Source = dir,
                    Target = dir
                });
            }
        }

        return installPaths;
    }

    /// <summary>
    /// Install extracted files to the SPT installation
    /// </summary>
    public async Task<InstallResult> InstallExtractedAsync(
        string extractPath,
        List<ArchiveInstallMapping>? customMappings = null)
    {
        var result = new InstallResult();

        try
        {
            // Determine install paths
            var topLevelDirs = Directory.GetDirectories(extractPath)
                .Select(d => Path.GetFileName(d))
                .ToList();

            var mappings = customMappings ?? GenerateInstallPaths(extractPath, topLevelDirs);

            foreach (var mapping in mappings)
            {
                var sourcePath = Path.Combine(extractPath, mapping.Source.Replace('/', Path.DirectorySeparatorChar));
                var targetPath = Path.Combine(_configService.SptRoot, mapping.Target.Replace('/', Path.DirectorySeparatorChar));

                if (Directory.Exists(sourcePath))
                {
                    CopyDirectory(sourcePath, targetPath, result);
                }
                else if (File.Exists(sourcePath))
                {
                    var targetDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    File.Copy(sourcePath, targetPath, true);
                    result.CopiedFiles++;
                    result.InstalledPaths.Add(mapping.Target);
                }
            }

            result.Success = true;
            _logger.Info($"Installed {result.CopiedFiles} files");
        }
        catch (Exception ex)
        {
            _logger.Error($"Installation failed: {ex.Message}");
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Copy directory recursively
    /// </summary>
    private void CopyDirectory(string sourceDir, string targetDir, InstallResult result)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var targetFile = Path.Combine(targetDir, Path.GetFileName(file));
            try
            {
                File.Copy(file, targetFile, true);
                result.CopiedFiles++;

                // Track relative path from SPT root
                var relativePath = Path.GetRelativePath(_configService.SptRoot, targetFile).Replace('\\', '/');
                result.InstalledPaths.Add(relativePath);
            }
            catch (IOException ex)
            {
                _logger.Warning($"Could not copy {file}: {ex.Message}");
                result.LockedFiles.Add(targetFile);
            }
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var targetSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, targetSubDir, result);
        }
    }

    /// <summary>
    /// Clean up temporary download directory
    /// </summary>
    public void CleanupTempDir(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
                _logger.Debug($"Cleaned up temp directory: {tempDir}");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to clean up temp directory: {ex.Message}");
        }
    }

    /// <summary>
    /// Find 7z executable
    /// </summary>
    private string? Find7ZipExecutable()
    {
        var isWindows = OperatingSystem.IsWindows();

        // Check bundled 7z in ModGodData/tools
        var modGodDataTools = Path.Combine(_configService.SptRoot, "ModGodData", "tools");

        if (isWindows)
        {
            var bundledPath = Path.Combine(modGodDataTools, "7z.exe");
            if (File.Exists(bundledPath))
            {
                return bundledPath;
            }

            var windowsPaths = new[]
            {
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe",
            };

            foreach (var path in windowsPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }
        else
        {
            var bundledPath = Path.Combine(modGodDataTools, "7zz");
            if (File.Exists(bundledPath))
            {
                return bundledPath;
            }

            var linuxCommands = new[] { "7zz", "7z", "7za" };
            foreach (var cmd in linuxCommands)
            {
                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = cmd,
                        Arguments = "--help",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    using var process = Process.Start(startInfo);
                    if (process != null)
                    {
                        process.WaitForExit(2000);
                        if (process.ExitCode == 0)
                        {
                            return cmd;
                        }
                    }
                }
                catch
                {
                    // Command not found
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Extract using native 7z.exe
    /// </summary>
    private async Task<bool> ExtractWith7ZipAsync(
        string archivePath,
        string extractPath,
        string sevenZipPath,
        IProgress<DownloadProgress>? progress)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = sevenZipPath,
            Arguments = $"x \"{archivePath}\" -o\"{extractPath}\" -y -bsp1",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };

        process.OutputDataReceived += (sender, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            var line = e.Data.Trim();
            var percentIndex = line.IndexOf('%');
            if (percentIndex > 0)
            {
                var numStart = percentIndex - 1;
                while (numStart >= 0 && (char.IsDigit(line[numStart]) || line[numStart] == ' '))
                {
                    numStart--;
                }
                numStart++;
                var numStr = line[numStart..percentIndex].Trim();
                if (int.TryParse(numStr, out var percent))
                {
                    progress?.Report(new DownloadProgress { Stage = "Extracting", Progress = percent });
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        return process.ExitCode == 0;
    }
}

/// <summary>
/// Result of a download operation
/// </summary>
public class DownloadResult
{
    public string Url { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ExtractPath { get; set; }
    public string? TempDir { get; set; }
    public bool IsStandardStructure { get; set; }
    public List<string> TopLevelDirectories { get; set; } = new();
    public List<string> TopLevelFiles { get; set; } = new();
    public List<ArchiveInstallMapping> SuggestedInstallPaths { get; set; } = new();
}

/// <summary>
/// Progress information for download operations
/// </summary>
public class DownloadProgress
{
    public string Stage { get; set; } = string.Empty;
    public int Progress { get; set; }
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public int FilesExtracted { get; set; }
    public int TotalFiles { get; set; }
}

/// <summary>
/// Mapping from archive path to install path for download operations
/// </summary>
public class ArchiveInstallMapping
{
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
}

/// <summary>
/// Result of an install operation
/// </summary>
public class InstallResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int CopiedFiles { get; set; }
    public List<string> InstalledPaths { get; set; } = new();
    public List<string> LockedFiles { get; set; } = new();
}
