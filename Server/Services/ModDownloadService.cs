using System.Diagnostics;
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

    /// <summary>
    /// Downloads a mod, analyzes it, and adds it to staged mods in one operation.
    /// This is the preferred method for automated downloads (health check, dependency installs).
    /// </summary>
    public async Task<(bool Success, string? Error)> DownloadAndStageAsync(
        string url, string modName, bool optional = false)
    {
        var result = await DownloadAndAnalyzeModAsync(url);

        if (!result.Success)
        {
            return (false, result.Error);
        }

        await StageDownloadedModAsync(result, url, modName, optional);
        return (true, null);
    }

    /// <summary>
    /// Takes an already-downloaded mod result and stages it.
    /// Use this when you need to track individual download results (e.g., batch downloads with progress).
    /// </summary>
    public async Task StageDownloadedModAsync(ModDownloadResult result, string url, string modName, bool optional = false)
    {
        if (!result.Success)
        {
            throw new InvalidOperationException($"Cannot stage failed download: {result.Error}");
        }

        var installPaths = result.SuggestedInstallPaths.Count > 0
            ? result.SuggestedInstallPaths
            : result.TopLevelDirectories.Count > 0
                ? result.TopLevelDirectories.Select(dir => new[] { dir, dir }).ToList()
                : new List<string[]> { new[] { "", "" } };

        var mod = new ModEntry
        {
            ModName = modName,
            DownloadUrl = url,
            Optional = optional,
            LastUpdated = DateTime.UtcNow.ToString("o"),
            InstallPaths = installPaths,
            Status = ModStatus.Installed
        };

        await _configService.AddModToStagedAsync(mod);
        _logger.Info($"Mod '{modName}' staged successfully");
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
                _logger.Info($"[Cache] Using cached staging for URL (already downloaded)");
                _logger.Info($"[Cache] Staged path: {stagedPath}");
                result.ExtractPath = stagedPath;
                result.Success = true;
                result.FromCache = true;
                AnalyzeModStructure(result);
                _logger.Info($"[Cache] Structure analysis complete - Standard: {result.IsStandardStructure}, TopLevelDirs: [{string.Join(", ", result.TopLevelDirectories)}]");
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

            // Stream directly to file with progress logging (handles large files efficiently)
            var archivePath = Path.Combine(stagingPath, "mod.zip");
            var expectedSize = contentLength ?? 0;
            
            _logger.Info($"[Download] Starting download to: {archivePath}");
            if (expectedSize > 0)
            {
                _logger.Info($"[Download] Expected size: {expectedSize / 1024.0 / 1024.0:F1}MB");
            }
            
            await using (var fileStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await using (var contentStream = await response.Content.ReadAsStreamAsync())
            {
                var buffer = new byte[81920]; // 80KB buffer
                long totalBytesRead = 0;
                int lastLoggedPercent = 0;
                var lastLogTime = DateTime.UtcNow;
                var startTime = DateTime.UtcNow;
                int bytesRead;
                
                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    totalBytesRead += bytesRead;
                    
                    // Log progress every 10% or every 30 seconds (whichever comes first)
                    var timeSinceLastLog = DateTime.UtcNow - lastLogTime;
                    
                    if (expectedSize > 0)
                    {
                        var currentPercent = (int)((double)totalBytesRead / expectedSize * 100);
                        var percentThreshold = (currentPercent / 10) * 10; // Round down to nearest 10%
                        
                        if (percentThreshold > lastLoggedPercent || timeSinceLastLog.TotalSeconds >= 30)
                        {
                            var elapsed = DateTime.UtcNow - startTime;
                            var speedMBps = totalBytesRead / 1024.0 / 1024.0 / elapsed.TotalSeconds;
                            _logger.Info($"[Download] Progress: {currentPercent}% ({totalBytesRead / 1024.0 / 1024.0:F0}MB / {expectedSize / 1024.0 / 1024.0:F0}MB) - {speedMBps:F1} MB/s");
                            lastLoggedPercent = percentThreshold;
                            lastLogTime = DateTime.UtcNow;
                        }
                    }
                    else if (timeSinceLastLog.TotalSeconds >= 30)
                    {
                        // Unknown size - log every 30 seconds
                        _logger.Info($"[Download] Progress: {totalBytesRead / 1024.0 / 1024.0:F1}MB downloaded");
                        lastLogTime = DateTime.UtcNow;
                    }
                }
            }

            var fileSize = new FileInfo(archivePath).Length;
            _logger.Info($"[Download] Complete: {fileSize / 1024.0 / 1024.0:F1}MB saved to {Path.GetFileName(archivePath)}");

            // Extract archive (supports .zip, .7z, .rar, .tar.gz, etc.)
            var extractPath = Path.Combine(stagingPath, "extracted");
            Directory.CreateDirectory(extractPath);
            
            _logger.Info("[Extract] Starting archive extraction...");
            
            // Detect archive type first
            ArchiveType? archiveType = null;
            try
            {
                using (var archive = ArchiveFactory.Open(archivePath))
                {
                    archiveType = archive.Type;
                    _logger.Info($"[Extract] Archive type: {archiveType}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[Extract] Failed to detect archive type: {ex.Message}");
                throw;
            }
            
            // For 7z archives, try to use native 7z.exe (much faster)
            if (archiveType == ArchiveType.SevenZip)
            {
                var sevenZipPath = Find7ZipExecutable();
                if (sevenZipPath != null)
                {
                    _logger.Info("[Extract] Using native 7-Zip for faster extraction (7z archives are slow with SharpCompress)");
                    try
                    {
                        if (await ExtractWith7ZipAsync(archivePath, extractPath, sevenZipPath))
                        {
                            // Success with native 7z - skip SharpCompress
                            goto ExtractionComplete;
                        }
                        else
                        {
                            _logger.Warning("[Extract] Native 7-Zip extraction failed, falling back to SharpCompress...");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"[Extract] Native 7-Zip error: {ex.Message}, falling back to SharpCompress...");
                    }
                }
                else
                {
                    _logger.Warning("[Extract] 7-Zip not found - using SharpCompress (this will be SLOW for large 7z files)");
                    _logger.Warning("[Extract] Install 7-Zip from https://www.7-zip.org for much faster extraction");
                }
            }
            
            // SharpCompress extraction (for zip, or 7z fallback)
            try
            {
                using (var archive = ArchiveFactory.Open(archivePath))
                {
                    var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                    var totalFiles = entries.Count;
                    _logger.Info($"[Extract] Total files to extract: {totalFiles}");
                    
                    // Calculate log interval: every 100 files or 10%, whichever is smaller (min 1)
                    var logInterval = Math.Max(1, Math.Min(100, totalFiles / 10));
                    var extractedCount = 0;
                    long totalBytesExtracted = 0;
                    var startTime = DateTime.UtcNow;
                    var lastLogTime = DateTime.UtcNow;
                    
                    foreach (var entry in entries)
                    {
                        // Get the entry key (path within archive)
                        var entryPath = entry.Key?.Replace('\\', '/') ?? "";
                        
                        if (!string.IsNullOrEmpty(entryPath))
                        {
                            // Create directory structure manually to handle edge cases
                            var targetPath = Path.Combine(extractPath, entryPath.Replace('/', Path.DirectorySeparatorChar));
                            var targetDir = Path.GetDirectoryName(targetPath);
                            
                            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                            {
                                Directory.CreateDirectory(targetDir);
                            }
                            
                            // Extract the file
                            entry.WriteToFile(targetPath, new ExtractionOptions { Overwrite = true });
                            totalBytesExtracted += entry.Size;
                        }
                        else
                        {
                            // Fallback to original method
                            entry.WriteToDirectory(extractPath, new ExtractionOptions
                            {
                                ExtractFullPath = true,
                                Overwrite = true
                            });
                            totalBytesExtracted += entry.Size;
                        }
                        
                        extractedCount++;
                        
                        // Log progress at intervals, or every 30 seconds for slow archives
                        var timeSinceLastLog = DateTime.UtcNow - lastLogTime;
                        if (extractedCount % logInterval == 0 || extractedCount == totalFiles || timeSinceLastLog.TotalSeconds >= 30)
                        {
                            var percent = (double)extractedCount / totalFiles * 100;
                            var elapsed = DateTime.UtcNow - startTime;
                            _logger.Info($"[Extract] Progress: {extractedCount}/{totalFiles} files ({percent:F0}%) - {totalBytesExtracted / 1024.0 / 1024.0:F1}MB extracted - elapsed: {elapsed.TotalSeconds:F1}s");
                            lastLogTime = DateTime.UtcNow;
                        }
                    }
                }
                var extractedSize = GetDirectorySize(extractPath);
                _logger.Info($"[Extract] Complete: {extractedSize / 1024.0 / 1024.0:F1}MB extracted to staging");
            }
            catch (Exception ex)
            {
                _logger.Error($"Extraction failed: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
            
            ExtractionComplete:

            // Update staging index
            _logger.Info("[Staging] Updating staging index...");
            _configService.Staging.UrlToPath[url] = stagingPath;
            await _configService.SaveStagingIndexAsync();
            _logger.Info($"[Staging] Staging index saved - path: {stagingPath}");

            result.ExtractPath = stagingPath;
            result.Success = true;

            // Analyze structure
            _logger.Info("[Analyze] Analyzing mod structure...");
            AnalyzeModStructure(result);
            _logger.Info($"[Analyze] Structure analysis complete:");
            _logger.Info($"[Analyze]   Standard structure: {result.IsStandardStructure}");
            _logger.Info($"[Analyze]   Top-level dirs: [{string.Join(", ", result.TopLevelDirectories)}]");
            if (result.SuggestedInstallPaths.Count > 0)
            {
                _logger.Info($"[Analyze]   Suggested install paths: {result.SuggestedInstallPaths.Count}");
                foreach (var path in result.SuggestedInstallPaths)
                {
                    _logger.Info($"[Analyze]     {path[0]} -> {path[1]}");
                }
            }
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
        
        // Get effective install path mappings from STAGED config (so changes are reflected immediately)
        var mappings = DefaultInstallPaths.GetEffectiveMappings(_configService.StagedConfig);

        // For each mapping, check if the source path exists in the archive
        // This allows more specific mappings (like BepInEx/plugins) to generate separate install paths
        foreach (var mapping in mappings)
        {
            var sourcePath = Path.Combine(extractedPath, mapping.Source.Replace('/', Path.DirectorySeparatorChar));
            
            // Check if this path exists in the archive (as file or directory)
            if (Directory.Exists(sourcePath) || File.Exists(sourcePath))
            {
                // Target must include <SPT_ROOT> prefix for ModInstallService to resolve the path
                var target = mapping.Target.StartsWith("<SPT_ROOT>", StringComparison.OrdinalIgnoreCase)
                    ? mapping.Target
                    : $"<SPT_ROOT>/{mapping.Target}";
                installPaths.Add(new[] { mapping.Source, target });
            }
        }

        // If no mappings matched, fall back to 1:1 mapping for top-level directories
        if (installPaths.Count == 0)
        {
            foreach (var dir in topLevelDirs)
            {
                installPaths.Add(new[] { dir, $"<SPT_ROOT>/{dir}" });
            }
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

    /// <summary>
    /// Get total size of a directory in bytes
    /// </summary>
    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
            return 0;
        
        return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
    }

    /// <summary>
    /// Find 7z executable - checks ModGodData/tools first, then system installations
    /// </summary>
    private string? Find7ZipExecutable()
    {
        var isWindows = OperatingSystem.IsWindows();
        
        // First, check for bundled 7z in ModGodData/tools (shared location for server and updater)
        var modGodDataTools = Path.Combine(_configService.SptRoot, "ModGodData", "tools");
        
        if (isWindows)
        {
            var bundledPath = Path.Combine(modGodDataTools, "7z.exe");
            if (File.Exists(bundledPath))
            {
                _logger.Info($"[7z] Using bundled 7z.exe: {bundledPath}");
                return bundledPath;
            }

            // Fall back to system 7-Zip installations
            var windowsPaths = new[]
            {
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "7-Zip", "7z.exe"),
            };

            foreach (var path in windowsPaths)
            {
                if (File.Exists(path))
                {
                    _logger.Info($"[7z] Found system 7-Zip at: {path}");
                    return path;
                }
            }
            
            _logger.Warning("[7z] 7-Zip not found - large .7z archives will extract VERY slowly");
            _logger.Warning("[7z] To install 7-Zip on Windows:");
            _logger.Warning("[7z]   Option 1: Download from https://www.7-zip.org/download.html");
            _logger.Warning("[7z]   Option 2: winget install 7zip.7zip");
            _logger.Warning("[7z]   Option 3: choco install 7zip");
        }
        else
        {
            // Linux/macOS: Check for bundled 7zz in ModGodData/tools
            var bundledPath = Path.Combine(modGodDataTools, "7zz");
            if (File.Exists(bundledPath))
            {
                _logger.Info($"[7z] Using bundled 7zz: {bundledPath}");
                return bundledPath;
            }
            
            // Check for 7z in PATH (installed via package manager)
            var linuxCommands = new[] { "7zz", "7z", "7za", "7zr" };
            
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
                            _logger.Info($"[7z] Found 7-Zip in PATH: {cmd}");
                            return cmd;
                        }
                    }
                }
                catch
                {
                    // Command not found, try next
                }
            }
            
            _logger.Warning("[7z] 7-Zip not found - large .7z archives will extract VERY slowly");
            if (OperatingSystem.IsMacOS())
            {
                _logger.Warning("[7z] To install 7-Zip on macOS:");
                _logger.Warning("[7z]   brew install p7zip");
            }
            else
            {
                _logger.Warning("[7z] To install 7-Zip on Linux/Docker:");
                _logger.Warning("[7z]   Option 1: Install in container: apt update && apt install -y p7zip-full");
                _logger.Warning("[7z]   Option 2: Add to Dockerfile: RUN apt-get update && apt-get install -y p7zip-full");
            }
        }

        return null;
    }

    /// <summary>
    /// Extract using native 7z.exe (much faster for 7z/LZMA archives)
    /// </summary>
    private async Task<bool> ExtractWith7ZipAsync(string archivePath, string extractPath, string sevenZipPath)
    {
        _logger.Info($"[7z] Extracting with native 7-Zip: {Path.GetFileName(archivePath)}");
        
        var startInfo = new ProcessStartInfo
        {
            FileName = sevenZipPath,
            // x = extract with full paths, -o = output directory, -y = yes to all prompts
            // -bsp1 = progress to stdout, -bse1 = errors to stdout (for unified parsing)
            Arguments = $"x \"{archivePath}\" -o\"{extractPath}\" -y -bsp1 -bse1",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var startTime = DateTime.UtcNow;
        int lastLoggedPercent = 0;
        var lastProgressLog = DateTime.UtcNow;
        
        // Helper to parse progress from a line (works for both stdout and stderr)
        void TryParseProgress(string? data)
        {
            if (string.IsNullOrEmpty(data)) return;
            
            var line = data.Trim();
            if (line.Length == 0) return;
            
            // 7z outputs progress like "  45% 123 - filename" or just "45%"
            // Find percentage pattern anywhere in the line
            var percentIndex = line.IndexOf('%');
            if (percentIndex > 0)
            {
                // Work backwards from % to find the number
                var numStart = percentIndex - 1;
                while (numStart >= 0 && (char.IsDigit(line[numStart]) || line[numStart] == ' '))
                {
                    numStart--;
                }
                numStart++;
                
                var numStr = line[numStart..percentIndex].Trim();
                if (int.TryParse(numStr, out var percent) && percent >= 0 && percent <= 100)
                {
                    // Log every 10% or every 30 seconds
                    var percentThreshold = (percent / 10) * 10;
                    var timeSinceLastLog = DateTime.UtcNow - lastProgressLog;
                    
                    if (percentThreshold > lastLoggedPercent || timeSinceLastLog.TotalSeconds >= 30)
                    {
                        var elapsed = DateTime.UtcNow - startTime;
                        _logger.Info($"[7z] Progress: {percent}% - elapsed: {elapsed.TotalSeconds:F1}s");
                        lastLoggedPercent = percentThreshold;
                        lastProgressLog = DateTime.UtcNow;
                    }
                }
            }
        }
        
        using var process = new Process { StartInfo = startInfo };
        
        process.OutputDataReceived += (sender, e) => TryParseProgress(e.Data);
        process.ErrorDataReceived += (sender, e) => TryParseProgress(e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        
        // Wait with timeout, but log heartbeat every 30 seconds if no progress received
        while (!process.HasExited)
        {
            // Check every second
            await Task.Delay(1000);
            
            if (process.HasExited) break;
            
            // Heartbeat: if no progress logged recently, show we're still alive
            var timeSinceLastLog = DateTime.UtcNow - lastProgressLog;
            var elapsed = DateTime.UtcNow - startTime;
            
            if (timeSinceLastLog.TotalSeconds >= 30)
            {
                _logger.Info($"[7z] Extracting... (elapsed: {elapsed.TotalSeconds:F0}s)");
                lastProgressLog = DateTime.UtcNow;
            }
            
            // Overall timeout: 30 minutes
            if (elapsed.TotalMinutes >= 30)
            {
                _logger.Error("[7z] Extraction timed out after 30 minutes");
                try { process.Kill(); } catch { }
                return false;
            }
        }
        
        // Ensure process has fully exited and async output handlers complete
        await Task.Run(() => process.WaitForExit());

        var totalElapsed = DateTime.UtcNow - startTime;
        
        if (process.ExitCode == 0)
        {
            var extractedSize = GetDirectorySize(extractPath);
            _logger.Info($"[7z] Complete: {extractedSize / 1024.0 / 1024.0:F1}MB extracted in {totalElapsed.TotalSeconds:F1}s");
            return true;
        }
        else
        {
            _logger.Error($"[7z] Failed with exit code {process.ExitCode}");
            return false;
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
