using System.IO.Compression;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModGod.Models;
using ModGod.Services;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers.Http;
using SPTarkov.Server.Web;

namespace ModGod;

/// <summary>
/// Mod metadata - required for all SPT server mods
/// </summary>
public record ModMetadata : AbstractModMetadata, IModWebMetadata
{
    public override string ModGuid { get; init; } = "com.modgod.server";
    public override string Name { get; init; } = "ModGod";
    public override string Author { get; init; } = "Bewa";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; }
    public override string License { get; init; } = "MIT";
}

/// <summary>
/// Main server mod entry point
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton, TypePriority = OnLoadOrder.PostSptModLoader)]
public class ModGodServer(
    ISptLogger<ModGodServer> logger,
    ModHelper modHelper)
    : IOnLoad
{
    public string ModPath = string.Empty;

    public Task OnLoad()
    {
        ModPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        
        logger.Success("========================================");
        logger.Success("  ModGod Server loaded!");
        logger.Success($"  Web UI: http://localhost:6969/modgod");
        logger.Success($"  Config API: http://localhost:6969/modgod/api/config");
        logger.Success("========================================");

        return Task.CompletedTask;
    }
}

/// <summary>
/// HTTP listener to serve the mod config to clients
/// </summary>
[Injectable(TypePriority = 0)]
public class ModConfigHttpListener : IHttpListener
{
    private readonly ConfigService _configService;
    private readonly ISptLogger<ModConfigHttpListener> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ModConfigHttpListener(
        ConfigService configService,
        ISptLogger<ModConfigHttpListener> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
        return context.Request.Method == "GET" && 
               path.Equals("/modgod/api/config", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        _logger.Info("Client requested mod config");

        var json = JsonSerializer.Serialize(_configService.Config, JsonOptions);

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }
}

/// <summary>
/// HTTP listener to serve the file manifest to clients
/// </summary>
[Injectable(TypePriority = 0)]
public class ManifestHttpListener : IHttpListener
{
    private readonly ManifestService _manifestService;
    private readonly ISptLogger<ManifestHttpListener> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ManifestHttpListener(
        ManifestService manifestService,
        ISptLogger<ManifestHttpListener> logger)
    {
        _manifestService = manifestService;
        _logger = logger;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
        return context.Request.Method == "GET" && 
               path.Equals("/modgod/api/manifest", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        _logger.Info("Client requested file manifest");

        var manifest = _manifestService.GenerateManifest();
        var json = JsonSerializer.Serialize(manifest, JsonOptions);

        _logger.Info($"Manifest generated: {manifest.Files.Count} files in {manifest.GenerationTimeMs}ms");

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }
}

/// <summary>
/// HTTP listener for status checks (used by install script to detect server shutdown)
/// </summary>
[Injectable(TypePriority = 0)]
public class StatusHttpListener : IHttpListener
{
    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
        return context.Request.Method == "GET" && 
               path.Equals("/modgod/api/status", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        // Simple OK response to indicate server is running
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"status\":\"ok\"}"));
        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }
}

/// <summary>
/// HTTP listener to serve individual files for client sync
/// URL format: /modgod/api/file/{relativePath}
/// e.g., /modgod/api/file/BepInEx/plugins/ModName/ModName.dll
/// </summary>
[Injectable(TypePriority = 0)]
public class FileDownloadHttpListener : IHttpListener
{
    private readonly ConfigService _configService;
    private readonly ISptLogger<FileDownloadHttpListener> _logger;

    // Only serve files from these directories for security
    private static readonly string[] AllowedRoots = { "BepInEx/plugins", "SPT/user/mods" };

    public FileDownloadHttpListener(
        ConfigService configService,
        ISptLogger<FileDownloadHttpListener> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        return context.Request.Method == "GET" && 
               path.StartsWith("/modgod/api/file/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        var requestPath = context.Request.Path.Value ?? "";
        
        // Extract relative file path from URL (after /modgod/api/file/)
        var relativePath = requestPath.Substring("/modgod/api/file/".Length);
        relativePath = Uri.UnescapeDataString(relativePath).Replace('/', Path.DirectorySeparatorChar);

        // Security: Only allow files under approved directories
        var normalizedPath = relativePath.Replace('\\', '/');
        if (!AllowedRoots.Any(root => normalizedPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.Warning($"Blocked file request outside allowed roots: {relativePath}");
            context.Response.StatusCode = 403;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"Access denied\"}"));
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
            return;
        }

        // Build full path
        var fullPath = Path.Combine(_configService.SptRoot, relativePath);

        // Security: Prevent path traversal
        var resolvedPath = Path.GetFullPath(fullPath);
        var sptRootFull = Path.GetFullPath(_configService.SptRoot);
        if (!resolvedPath.StartsWith(sptRootFull))
        {
            _logger.Warning($"Blocked path traversal attempt: {relativePath}");
            context.Response.StatusCode = 403;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"Access denied\"}"));
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
            return;
        }

        if (!File.Exists(fullPath))
        {
            _logger.Warning($"File not found: {relativePath}");
            context.Response.StatusCode = 404;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"File not found\"}"));
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
            return;
        }

        _logger.Info($"Serving file: {relativePath}");

        try
        {
            var fileBytes = await File.ReadAllBytesAsync(fullPath);
            
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{Path.GetFileName(fullPath)}\"");
            context.Response.Headers.Append("Content-Length", fileBytes.Length.ToString());
            
            await context.Response.Body.WriteAsync(fileBytes);
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
        }
        catch (Exception ex)
        {
            _logger.Error($"Error serving file {relativePath}: {ex.Message}");
            context.Response.StatusCode = 500;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"Internal error\"}"));
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
        }
    }
}

/// <summary>
/// HTTP listener to serve ModGod itself as a download
/// This allows clients to download the exact version of ModGod running on the server
/// URL: /modgod/api/self-download
/// </summary>
[Injectable(TypePriority = 0)]
public class SelfDownloadHttpListener : IHttpListener
{
    private readonly ConfigService _configService;
    private readonly ISptLogger<SelfDownloadHttpListener> _logger;

    // Cache the zip in memory to avoid regenerating on every request
    private byte[]? _cachedZip;
    private DateTime _cacheTime;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public SelfDownloadHttpListener(
        ConfigService configService,
        ISptLogger<SelfDownloadHttpListener> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
        return context.Request.Method == "GET" && 
               path.Equals("/modgod/api/self-download", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        _logger.Info("Client requested ModGod self-download");

        try
        {
            // Check cache validity
            if (_cachedZip == null || DateTime.UtcNow - _cacheTime > CacheDuration)
            {
                _cachedZip = GenerateModGodZip();
                _cacheTime = DateTime.UtcNow;
                _logger.Info($"Generated ModGod zip: {_cachedZip.Length / 1024}KB");
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/zip";
            context.Response.Headers.Append("Content-Disposition", "attachment; filename=\"ModGod.zip\"");
            context.Response.Headers.Append("Content-Length", _cachedZip.Length.ToString());
            
            await context.Response.Body.WriteAsync(_cachedZip);
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
        }
        catch (Exception ex)
        {
            _logger.Error($"Error generating ModGod zip: {ex.Message}");
            context.Response.StatusCode = 500;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"Failed to generate download\"}"));
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
        }
    }

    private byte[] GenerateModGodZip()
    {
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            var sptRoot = _configService.SptRoot;

            // Add client plugin (BepInEx/plugins/ModGodClientEnforcer/)
            var clientPluginPath = Path.Combine(sptRoot, "BepInEx", "plugins", "ModGodClientEnforcer");
            if (Directory.Exists(clientPluginPath))
            {
                AddDirectoryToZip(archive, clientPluginPath, "BepInEx/plugins/ModGodClientEnforcer");
            }

            // Add updater (ModGodUpdater.exe at SPT root)
            var updaterPath = Path.Combine(sptRoot, "ModGodUpdater.exe");
            if (File.Exists(updaterPath))
            {
                AddFileToZip(archive, updaterPath, "ModGodUpdater.exe");
            }

            // Add server mod (SPT/user/mods/ModGodServer/)
            // This is the current running mod, get it from ModPath
            var serverModPath = _configService.ModPath;
            if (Directory.Exists(serverModPath))
            {
                var serverModName = Path.GetFileName(serverModPath.TrimEnd(Path.DirectorySeparatorChar));
                AddDirectoryToZip(archive, serverModPath, $"SPT/user/mods/{serverModName}");
            }
        }

        return memoryStream.ToArray();
    }

    private void AddDirectoryToZip(ZipArchive archive, string sourceDir, string archivePath)
    {
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            var entryName = $"{archivePath}/{relativePath}";
            
            try
            {
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var fileStream = File.OpenRead(file);
                fileStream.CopyTo(entryStream);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to add file to zip: {file} - {ex.Message}");
            }
        }
    }

    private void AddFileToZip(ZipArchive archive, string filePath, string entryName)
    {
        try
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var fileStream = File.OpenRead(filePath);
            fileStream.CopyTo(entryStream);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to add file to zip: {filePath} - {ex.Message}");
        }
    }
}
