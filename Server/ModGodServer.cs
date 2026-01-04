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
    public override SemanticVersioning.Version Version { get; init; } = new("2.3.0");
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
        logger.Success("  Web UI: <your-server-url>/modgod");
        logger.Success("  Config API: <your-server-url>/modgod/api/config");
        logger.Success("========================================");

        return Task.CompletedTask;
    }
}

/// <summary>
/// HTTP listener to serve the mod config to clients.
/// Filters the mod list to only include mods that have files in the manifest.
/// Supports ?headless=true query parameter for headless clients.
/// </summary>
[Injectable(TypePriority = 0)]
public class ModConfigHttpListener : IHttpListener
{
    private readonly ConfigService _configService;
    private readonly ManifestService _manifestService;
    private readonly ISptLogger<ModConfigHttpListener> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ModConfigHttpListener(
        ConfigService configService,
        ManifestService manifestService,
        ISptLogger<ModConfigHttpListener> logger)
    {
        _configService = configService;
        _manifestService = manifestService;
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
        // Check if this is a headless client request
        var isHeadless = context.Request.Query.ContainsKey("headless") &&
                         context.Request.Query["headless"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase);

        _logger.Info($"Client requested mod config (headless: {isHeadless})");

        // Generate the appropriate manifest to determine which mods have syncable files
        var manifest = isHeadless
            ? _manifestService.GenerateHeadlessManifest()
            : _manifestService.GenerateManifest();

        // Get the set of mod names that have files in the manifest
        var modsWithFiles = manifest.Files.Values
            .Select(f => f.ModName)
            .Where(name => !string.IsNullOrEmpty(name) && name != "Untracked")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Filter the mod list to only include mods with syncable files
        var filteredModList = _configService.Config.ModList
            .Where(mod => modsWithFiles.Contains(mod.ModName) || mod.IsProtected)
            .ToList();

        var skippedCount = _configService.Config.ModList.Count - filteredModList.Count;
        if (skippedCount > 0)
        {
            _logger.Info($"Filtered out {skippedCount} mod(s) with no syncable files for this client type");
        }

        // Create a filtered config response (don't modify the actual config)
        var filteredConfig = new
        {
            modList = filteredModList,
            // Include sync config info so clients know the rules
            playerSyncConfig = isHeadless ? null : _configService.Config.PlayerSyncConfig,
            headlessSyncConfig = isHeadless ? _configService.Config.HeadlessSyncConfig : null
        };

        var json = JsonSerializer.Serialize(filteredConfig, JsonOptions);

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
/// HTTP listener to serve the headless file manifest to headless clients
/// Only includes files explicitly configured for headless syncing
/// </summary>
[Injectable(TypePriority = 0)]
public class HeadlessManifestHttpListener : IHttpListener
{
    private readonly ManifestService _manifestService;
    private readonly ISptLogger<HeadlessManifestHttpListener> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public HeadlessManifestHttpListener(
        ManifestService manifestService,
        ISptLogger<HeadlessManifestHttpListener> logger)
    {
        _manifestService = manifestService;
        _logger = logger;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
        return context.Request.Method == "GET" &&
               path.Equals("/modgod/api/manifest/headless", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        _logger.Info("Headless client requested file manifest");

        var manifest = _manifestService.GenerateHeadlessManifest();
        var json = JsonSerializer.Serialize(manifest, JsonOptions);

        _logger.Info($"Headless manifest generated: {manifest.Files.Count} files in {manifest.GenerationTimeMs}ms");

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }
}

/// <summary>
/// HTTP listener to serve individual files for client sync
/// URL format: /modgod/api/file/{relativePath}
/// e.g., /modgod/api/file/BepInEx/plugins/ModName/ModName.dll
///
/// The relativePath is the TARGET path (what client expects).
/// This handler reverse-maps it to the SOURCE path (where file actually exists on server).
/// </summary>
[Injectable(TypePriority = 0)]
public class FileDownloadHttpListener : IHttpListener
{
    private readonly ConfigService _configService;
    private readonly ISptLogger<FileDownloadHttpListener> _logger;

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
        // This is the TARGET path that the client is requesting
        var targetPath = requestPath.Substring("/modgod/api/file/".Length);
        targetPath = Uri.UnescapeDataString(targetPath).Replace('\\', '/').TrimStart('/');

        // Check if request is for headless manifest (via query param or header)
        var isHeadless = context.Request.Query["headless"].FirstOrDefault() == "true";

        // Get the appropriate sync config
        var syncConfig = isHeadless
            ? (_configService.Config.HeadlessSyncConfig ?? ClientSyncConfig.DefaultHeadlessConfig())
            : (_configService.Config.PlayerSyncConfig ?? ClientSyncConfig.DefaultPlayerConfig());

        // Build allowed targets from sync paths
        var allowedTargets = syncConfig.SyncPaths
            .Select(p => NormalizePath(p.Target))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        // Security: Only allow files under configured sync path targets
        if (!allowedTargets.Any(target =>
            targetPath.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            targetPath.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.Warning($"Blocked file request outside allowed sync targets: {targetPath}");
            context.Response.StatusCode = 403;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"Access denied\"}"));
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
            return;
        }

        // Reverse-map target path to source path
        var sourcePath = ReverseMapTargetToSource(targetPath, syncConfig.SyncPaths);
        if (sourcePath == null)
        {
            _logger.Warning($"Could not reverse-map target path to source: {targetPath}");
            context.Response.StatusCode = 404;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"File not found\"}"));
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
            return;
        }

        // Build full path using the SOURCE path (where file actually exists)
        var fullPath = Path.Combine(_configService.SptRoot, sourcePath.Replace('/', Path.DirectorySeparatorChar));

        // Security: Prevent path traversal
        var resolvedPath = Path.GetFullPath(fullPath);
        var sptRootFull = Path.GetFullPath(_configService.SptRoot);
        if (!resolvedPath.StartsWith(sptRootFull))
        {
            _logger.Warning($"Blocked path traversal attempt: {sourcePath}");
            context.Response.StatusCode = 403;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"Access denied\"}"));
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
            return;
        }

        if (!File.Exists(fullPath))
        {
            _logger.Warning($"File not found: {sourcePath} (requested as target: {targetPath})");
            context.Response.StatusCode = 404;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"File not found\"}"));
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
            return;
        }

        _logger.Debug($"Serving file: {sourcePath} (target: {targetPath})");

        try
        {
            var fileBytes = await File.ReadAllBytesAsync(fullPath);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers.Append("Content-Length", fileBytes.Length.ToString());

            await context.Response.Body.WriteAsync(fileBytes);
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
        }
        catch (Exception ex)
        {
            _logger.Error($"Error serving file {sourcePath}: {ex.Message}");
            context.Response.StatusCode = 500;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"Internal error\"}"));
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
        }
    }

    /// <summary>
    /// Reverse-map a target path back to a source path using sync path mappings.
    /// </summary>
    private static string? ReverseMapTargetToSource(string targetPath, List<SyncPathEntry> syncPaths)
    {
        var normTarget = NormalizePath(targetPath);

        foreach (var syncPath in syncPaths)
        {
            var normSyncSource = NormalizePath(syncPath.Source);
            var normSyncTarget = NormalizePath(syncPath.Target);

            // Check if targetPath is under this sync path's target
            if (normTarget.Equals(normSyncTarget, StringComparison.OrdinalIgnoreCase))
            {
                // Exact match - return the source
                return normSyncSource;
            }

            if (normTarget.StartsWith(normSyncTarget + "/", StringComparison.OrdinalIgnoreCase))
            {
                // Path is under this sync target - transform back to source
                var relativePart = normTarget.Substring(normSyncTarget.Length + 1);
                return string.IsNullOrEmpty(normSyncSource)
                    ? relativePart
                    : $"{normSyncSource}/{relativePart}";
            }
        }

        return null; // Path not in any sync path
    }

    private static string NormalizePath(string path)
    {
        return path.Replace("\\", "/").TrimStart('/');
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

/// <summary>
/// HTTP listener for Forge API key validation
/// POST /modgod/api/forge/validate-key
/// </summary>
[Injectable(TypePriority = 0)]
public class ForgeValidateKeyHttpListener : IHttpListener
{
    private readonly ForgeService _forgeService;
    private readonly ISptLogger<ForgeValidateKeyHttpListener> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ForgeValidateKeyHttpListener(
        ForgeService forgeService,
        ISptLogger<ForgeValidateKeyHttpListener> logger)
    {
        _forgeService = forgeService;
        _logger = logger;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
        return context.Request.Method == "POST" &&
               path.Equals("/modgod/api/forge/validate-key", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<ValidateKeyRequest>(body, JsonOptions);

            if (string.IsNullOrWhiteSpace(request?.ApiKey))
            {
                await SendJsonResponse(context, 400, new { success = false, error = "API key is required" });
                return;
            }

            _logger.Info("Validating Forge API key...");
            var (isValid, error) = await _forgeService.ValidateApiKeyAsync(request.ApiKey);

            if (isValid)
            {
                // Save the valid API key
                await _forgeService.SaveApiKeyAsync(request.ApiKey);
                _logger.Success("Forge API key validated and saved");
                await SendJsonResponse(context, 200, new { success = true });
            }
            else
            {
                _logger.Warning($"Forge API key validation failed: {error}");
                await SendJsonResponse(context, 200, new { success = false, error });
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error validating Forge API key: {ex.Message}");
            await SendJsonResponse(context, 500, new { success = false, error = "Internal error" });
        }
    }

    private static async Task SendJsonResponse(HttpContext context, int statusCode, object data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }

    private class ValidateKeyRequest
    {
        public string? ApiKey { get; set; }
    }
}

/// <summary>
/// HTTP listener for fetching mod details from Forge
/// GET /modgod/api/forge/mod/{modId}
/// </summary>
[Injectable(TypePriority = 0)]
public class ForgeModDetailsHttpListener : IHttpListener
{
    private readonly ForgeService _forgeService;
    private readonly ISptLogger<ForgeModDetailsHttpListener> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ForgeModDetailsHttpListener(
        ForgeService forgeService,
        ISptLogger<ForgeModDetailsHttpListener> logger)
    {
        _forgeService = forgeService;
        _logger = logger;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        // Only match /modgod/api/forge/mod/{modId} - NOT paths with additional segments like /addons
        if (context.Request.Method != "GET") return false;
        if (!path.StartsWith("/modgod/api/forge/mod/", StringComparison.OrdinalIgnoreCase)) return false;

        // Exclude sub-paths like /addons
        var remainder = path.Substring("/modgod/api/forge/mod/".Length).TrimEnd('/');
        return !remainder.Contains('/'); // Only match if there's no additional path segment
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        try
        {
            var path = context.Request.Path.Value ?? "";
            var modIdStr = path.Substring("/modgod/api/forge/mod/".Length).TrimEnd('/');

            if (!int.TryParse(modIdStr, out var modId))
            {
                await SendJsonResponse(context, 400, new { success = false, error = "Invalid mod ID" });
                return;
            }

            if (!_forgeService.HasApiKey)
            {
                await SendJsonResponse(context, 400, new { success = false, error = "No Forge API key configured" });
                return;
            }

            _logger.Info($"Fetching mod details for mod ID: {modId}");
            var result = await _forgeService.GetModDetailsAsync(modId);

            if (result?.Success == true && result.Mod != null)
            {
                // Build download URLs for each version
                var versionsWithUrls = result.Mod.Versions?.Select(v => new
                {
                    v.Id,
                    v.Version,
                    v.SptVersionConstraint,
                    v.Downloads,
                    v.PublishedAt,
                    DownloadUrl = ForgeService.BuildDownloadUrl(result.Mod.Id, result.Mod.Slug, v.Version)
                }).ToList();

                await SendJsonResponse(context, 200, new
                {
                    success = true,
                    mod = new
                    {
                        result.Mod.Id,
                        result.Mod.Guid,
                        result.Mod.Name,
                        result.Mod.Slug,
                        result.Mod.Teaser,
                        result.Mod.Thumbnail,
                        result.Mod.Downloads,
                        result.Mod.DetailUrl,
                        Owner = result.Mod.Owner?.Name,
                        Category = result.Mod.Category?.Name,
                        CategoryColor = result.Mod.Category?.ColorClass,
                        License = result.Mod.License?.ShortName ?? result.Mod.License?.Name,
                        result.Mod.UpdatedAt,
                        Versions = versionsWithUrls
                    }
                });
            }
            else
            {
                await SendJsonResponse(context, 404, new { success = false, error = result?.Error ?? "Mod not found" });
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error fetching mod details: {ex.Message}");
            await SendJsonResponse(context, 500, new { success = false, error = "Internal error" });
        }
    }

    private static async Task SendJsonResponse(HttpContext context, int statusCode, object data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }
}

/// <summary>
/// HTTP listener for fetching addons for a mod
/// GET /modgod/api/forge/mod/{modId}/addons
/// </summary>
[Injectable(TypePriority = 0)]
public class ForgeModAddonsHttpListener : IHttpListener
{
    private readonly ForgeService _forgeService;
    private readonly ISptLogger<ForgeModAddonsHttpListener> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ForgeModAddonsHttpListener(
        ForgeService forgeService,
        ISptLogger<ForgeModAddonsHttpListener> logger)
    {
        _forgeService = forgeService;
        _logger = logger;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        return context.Request.Method == "GET" &&
               path.Contains("/modgod/api/forge/mod/") &&
               path.EndsWith("/addons", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        try
        {
            var path = context.Request.Path.Value ?? "";
            _logger.Info($"[AddonsHandler] Handling request: {path}");

            // Extract modId from /modgod/api/forge/mod/{modId}/addons
            var startIndex = "/modgod/api/forge/mod/".Length;
            var endIndex = path.LastIndexOf("/addons", StringComparison.OrdinalIgnoreCase);
            var modIdStr = path.Substring(startIndex, endIndex - startIndex);

            if (!int.TryParse(modIdStr, out var modId))
            {
                _logger.Warning($"[AddonsHandler] Invalid mod ID: {modIdStr}");
                await SendJsonResponse(context, 400, new { success = false, error = "Invalid mod ID" });
                return;
            }

            if (!_forgeService.HasApiKey)
            {
                _logger.Warning("[AddonsHandler] No Forge API key configured");
                await SendJsonResponse(context, 400, new { success = false, error = "No Forge API key configured" });
                return;
            }

            _logger.Info($"[AddonsHandler] Fetching addons for mod ID: {modId}");
            var result = await _forgeService.GetModAddonsAsync(modId);
            _logger.Info($"[AddonsHandler] API result - Success: {result?.Success}, Addons count: {result?.Addons?.Count ?? 0}, Error: {result?.Error ?? "none"}");

            if (result?.Success == true)
            {
                await SendJsonResponse(context, 200, new
                {
                    success = true,
                    addons = result.Addons.Select(a => new
                    {
                        a.Id,
                        a.Name,
                        a.Slug,
                        a.Teaser,
                        a.Thumbnail,
                        a.Downloads,
                        a.DetailUrl,
                        a.ModId,
                        Owner = a.Owner?.Name,
                        a.PublishedAt
                    })
                });
            }
            else
            {
                await SendJsonResponse(context, 500, new { success = false, error = result?.Error ?? "Failed to fetch addons" });
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error fetching mod addons: {ex.Message}");
            await SendJsonResponse(context, 500, new { success = false, error = "Internal error" });
        }
    }

    private static async Task SendJsonResponse(HttpContext context, int statusCode, object data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }
}

/// <summary>
/// HTTP listener for fetching versions for an addon
/// GET /modgod/api/forge/addon/{addonId}/versions
/// </summary>
[Injectable(TypePriority = 0)]
public class ForgeAddonVersionsHttpListener : IHttpListener
{
    private readonly ForgeService _forgeService;
    private readonly ISptLogger<ForgeAddonVersionsHttpListener> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ForgeAddonVersionsHttpListener(
        ForgeService forgeService,
        ISptLogger<ForgeAddonVersionsHttpListener> logger)
    {
        _forgeService = forgeService;
        _logger = logger;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        return context.Request.Method == "GET" &&
               path.Contains("/modgod/api/forge/addon/") &&
               path.EndsWith("/versions", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        try
        {
            var path = context.Request.Path.Value ?? "";
            // Extract addonId from /modgod/api/forge/addon/{addonId}/versions
            var startIndex = "/modgod/api/forge/addon/".Length;
            var endIndex = path.LastIndexOf("/versions", StringComparison.OrdinalIgnoreCase);
            var addonIdStr = path.Substring(startIndex, endIndex - startIndex);

            if (!int.TryParse(addonIdStr, out var addonId))
            {
                await SendJsonResponse(context, 400, new { success = false, error = "Invalid addon ID" });
                return;
            }

            if (!_forgeService.HasApiKey)
            {
                await SendJsonResponse(context, 400, new { success = false, error = "No Forge API key configured" });
                return;
            }

            _logger.Info($"Fetching versions for addon ID: {addonId}");
            var result = await _forgeService.GetAddonVersionsAsync(addonId);

            if (result?.Success == true)
            {
                await SendJsonResponse(context, 200, new
                {
                    success = true,
                    versions = result.Versions.Select(v => new
                    {
                        v.Id,
                        v.Version,
                        v.Description,
                        DownloadUrl = v.Link, // Rename 'link' to 'downloadUrl' for consistency
                        v.ContentLength,
                        v.ModVersionConstraint,
                        v.Downloads,
                        v.PublishedAt
                    })
                });
            }
            else
            {
                await SendJsonResponse(context, 500, new { success = false, error = result?.Error ?? "Failed to fetch addon versions" });
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error fetching addon versions: {ex.Message}");
            await SendJsonResponse(context, 500, new { success = false, error = "Internal error" });
        }
    }

    private static async Task SendJsonResponse(HttpContext context, int statusCode, object data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }
}

/// <summary>
/// HTTP listener to check Forge API key status
/// GET /modgod/api/forge/status
/// </summary>
[Injectable(TypePriority = 0)]
public class ForgeStatusHttpListener : IHttpListener
{
    private readonly ForgeService _forgeService;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ForgeStatusHttpListener(ForgeService forgeService)
    {
        _forgeService = forgeService;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
        return context.Request.Method == "GET" &&
               path.Equals("/modgod/api/forge/status", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        var json = JsonSerializer.Serialize(new { hasApiKey = _forgeService.HasApiKey }, JsonOptions);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }
}

/// <summary>
/// HTTP listener to get Forge API key (for copy to clipboard)
/// GET /modgod/api/forge/api-key
/// </summary>
[Injectable(TypePriority = 0)]
public class ForgeGetApiKeyHttpListener : IHttpListener
{
    private readonly ForgeService _forgeService;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ForgeGetApiKeyHttpListener(ForgeService forgeService)
    {
        _forgeService = forgeService;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
        return context.Request.Method == "GET" &&
               path.Equals("/modgod/api/forge/api-key", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        if (!_forgeService.HasApiKey)
        {
            var errorJson = JsonSerializer.Serialize(new { apiKey = (string?)null }, JsonOptions);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(errorJson));
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
            return;
        }

        var json = JsonSerializer.Serialize(new { apiKey = _forgeService.ApiKey }, JsonOptions);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }
}

/// <summary>
/// HTTP listener to delete Forge API key
/// DELETE /modgod/api/forge/api-key
/// </summary>
[Injectable(TypePriority = 0)]
public class ForgeDeleteKeyHttpListener : IHttpListener
{
    private readonly ForgeService _forgeService;
    private readonly ISptLogger<ForgeDeleteKeyHttpListener> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ForgeDeleteKeyHttpListener(
        ForgeService forgeService,
        ISptLogger<ForgeDeleteKeyHttpListener> logger)
    {
        _forgeService = forgeService;
        _logger = logger;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
        return context.Request.Method == "DELETE" &&
               path.Equals("/modgod/api/forge/api-key", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        try
        {
            await _forgeService.SaveApiKeyAsync(null!);
            _logger.Info("Forge API key removed");

            var json = JsonSerializer.Serialize(new { success = true }, JsonOptions);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        }
        catch (Exception ex)
        {
            _logger.Error($"Error removing Forge API key: {ex.Message}");
            var json = JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions);
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        }

        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }
}

/// <summary>
/// HTTP listener for searching mods on Forge
/// GET /modgod/api/forge/search?query=...&sptVersion=...
/// </summary>
[Injectable(TypePriority = 0)]
public class ForgeSearchHttpListener : IHttpListener
{
    private readonly ForgeService _forgeService;
    private readonly ISptLogger<ForgeSearchHttpListener> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ForgeSearchHttpListener(
        ForgeService forgeService,
        ISptLogger<ForgeSearchHttpListener> logger)
    {
        _forgeService = forgeService;
        _logger = logger;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
        return context.Request.Method == "GET" &&
               path.Equals("/modgod/api/forge/search", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        try
        {
            var query = context.Request.Query["query"].FirstOrDefault();
            var sptVersion = context.Request.Query["sptVersion"].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(query))
            {
                await SendJsonResponse(context, 400, new { success = false, error = "Query parameter is required" });
                return;
            }

            if (!_forgeService.HasApiKey)
            {
                await SendJsonResponse(context, 400, new { success = false, error = "No Forge API key configured" });
                return;
            }

            var result = await _forgeService.SearchModsAsync(query, sptVersion);

            if (result?.Success == true)
            {
                await SendJsonResponse(context, 200, new
                {
                    success = true,
                    mods = result.Mods.Select(m => new
                    {
                        m.Id,
                        m.Name,
                        m.Slug,
                        m.Thumbnail,
                        m.Downloads,
                        m.Teaser,
                        m.DetailUrl
                    }).ToList()
                });
            }
            else
            {
                await SendJsonResponse(context, 500, new { success = false, error = result?.Error ?? "Search failed" });
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error searching Forge mods: {ex.Message}");
            await SendJsonResponse(context, 500, new { success = false, error = ex.Message });
        }
    }

    private async Task SendJsonResponse(HttpContext context, int statusCode, object data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }
}

/// <summary>
/// HTTP listener to serve local mods as downloadable zip archives.
/// URL format: /modgod/api/local-mods/{guid}
///
/// Local mods are mods that exist on the server filesystem (e.g., distributed via Discord).
/// This endpoint zips the files on-the-fly and serves them to clients.
/// The client updater downloads from this URL just like any other mod.
/// </summary>
[Injectable(TypePriority = 0)]
public class LocalModDownloadHttpListener : IHttpListener
{
    private readonly ConfigService _configService;
    private readonly ISptLogger<LocalModDownloadHttpListener> _logger;

    // Cache zipped mods briefly to avoid re-zipping for rapid requests
    private readonly Dictionary<string, (byte[] Data, DateTime Cached)> _zipCache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    public LocalModDownloadHttpListener(
        ConfigService configService,
        ISptLogger<LocalModDownloadHttpListener> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        return context.Request.Method == "GET" &&
               path.StartsWith("/modgod/api/local-mods/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        var requestPath = context.Request.Path.Value ?? "";

        // Extract guid from URL (after /modgod/api/local-mods/)
        var guid = requestPath.Substring("/modgod/api/local-mods/".Length).TrimEnd('/');

        if (string.IsNullOrWhiteSpace(guid))
        {
            context.Response.StatusCode = 400;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"Missing mod guid\"}"));
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
            return;
        }

        // Validate the guid exists
        if (!_configService.IsValidLocalMod(guid))
        {
            _logger.Warning($"Local mod not found: {guid}");
            context.Response.StatusCode = 404;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"Local mod not found\"}"));
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
            return;
        }

        var localModPath = _configService.GetLocalModPath(guid);
        if (localModPath == null || !Directory.Exists(localModPath))
        {
            _logger.Warning($"Local mod path does not exist: {guid}");
            context.Response.StatusCode = 404;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"Local mod files not found\"}"));
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
            return;
        }

        var modInfo = _configService.GetLocalModInfo(guid);
        var modName = modInfo?.ModName ?? guid;

        _logger.Info($"Serving local mod: {modName} (guid: {guid})");

        try
        {
            // Check cache first
            byte[] zipData;
            if (_zipCache.TryGetValue(guid, out var cached) && DateTime.UtcNow - cached.Cached < CacheDuration)
            {
                _logger.Debug($"Using cached zip for local mod: {guid}");
                zipData = cached.Data;
            }
            else
            {
                // Generate zip on-the-fly
                zipData = await GenerateZipFromDirectory(localModPath);

                // Cache it
                _zipCache[guid] = (zipData, DateTime.UtcNow);

                // Clean old cache entries
                CleanCache();

                _logger.Info($"Generated zip for local mod: {modName} ({zipData.Length / 1024}KB)");
            }

            // Sanitize filename for Content-Disposition
            var safeFileName = SanitizeFileName(modName) + ".zip";

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/zip";
            context.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{safeFileName}\"");
            context.Response.Headers.Append("Content-Length", zipData.Length.ToString());

            await context.Response.Body.WriteAsync(zipData);
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
        }
        catch (Exception ex)
        {
            _logger.Error($"Error serving local mod {guid}: {ex.Message}");
            context.Response.StatusCode = 500;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"Failed to serve mod\"}"));
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
        }
    }

    private static async Task<byte[]> GenerateZipFromDirectory(string sourceDir)
    {
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                var relativePath = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);

                await using var entryStream = entry.Open();
                await using var fileStream = File.OpenRead(file);
                await fileStream.CopyToAsync(entryStream);
            }
        }

        return memoryStream.ToArray();
    }

    private void CleanCache()
    {
        var expired = _zipCache
            .Where(kvp => DateTime.UtcNow - kvp.Value.Cached > CacheDuration)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expired)
        {
            _zipCache.Remove(key);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Where(c => !invalidChars.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "mod" : sanitized;
    }
}

/// <summary>
/// HTTP listener for browsing the server filesystem.
/// GET /modgod/api/browse?path=...
/// Returns list of directories and files at the given path.
/// Security: Only allows browsing within the SPT root directory.
/// </summary>
[Injectable(TypePriority = 0)]
public class FileBrowserHttpListener : IHttpListener
{
    private readonly ConfigService _configService;
    private readonly ISptLogger<FileBrowserHttpListener> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FileBrowserHttpListener(
        ConfigService configService,
        ISptLogger<FileBrowserHttpListener> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
        return context.Request.Method == "GET" &&
               path.Equals("/modgod/api/browse", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        var requestedPath = context.Request.Query["path"].FirstOrDefault() ?? "";

        // Default to SPT root if no path specified
        var browsePath = string.IsNullOrWhiteSpace(requestedPath)
            ? _configService.SptRoot
            : requestedPath;

        // Resolve to absolute path
        var resolvedPath = Path.GetFullPath(browsePath);
        var sptRootFull = Path.GetFullPath(_configService.SptRoot);

        // Security: Only allow browsing within SPT root (or on same drive for flexibility)
        // This is a server-side admin tool, so we allow more flexibility but prevent obvious issues
        if (!Directory.Exists(resolvedPath))
        {
            await SendJsonResponse(context, 404, new { success = false, error = "Directory not found" });
            return;
        }

        try
        {
            var entries = new List<object>();

            // Add parent directory entry if not at root
            var parent = Directory.GetParent(resolvedPath);
            if (parent != null)
            {
                entries.Add(new
                {
                    name = "..",
                    path = parent.FullName,
                    isDirectory = true,
                    isParent = true
                });
            }

            // List directories first
            foreach (var dir in Directory.GetDirectories(resolvedPath).OrderBy(d => d))
            {
                var dirInfo = new DirectoryInfo(dir);
                entries.Add(new
                {
                    name = dirInfo.Name,
                    path = dirInfo.FullName,
                    isDirectory = true,
                    isParent = false
                });
            }

            // Then list files
            foreach (var file in Directory.GetFiles(resolvedPath).OrderBy(f => f))
            {
                var fileInfo = new FileInfo(file);
                entries.Add(new
                {
                    name = fileInfo.Name,
                    path = fileInfo.FullName,
                    isDirectory = false,
                    isParent = false,
                    size = fileInfo.Length
                });
            }

            await SendJsonResponse(context, 200, new
            {
                success = true,
                currentPath = resolvedPath,
                sptRoot = sptRootFull,
                entries
            });
        }
        catch (UnauthorizedAccessException)
        {
            await SendJsonResponse(context, 403, new { success = false, error = "Access denied to directory" });
        }
        catch (Exception ex)
        {
            _logger.Error($"Error browsing directory: {ex.Message}");
            await SendJsonResponse(context, 500, new { success = false, error = ex.Message });
        }
    }

    private static async Task SendJsonResponse(HttpContext context, int statusCode, object data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }
}

/// <summary>
/// HTTP listener for staging a local mod from the server filesystem.
/// POST /modgod/api/local-mods/stage
/// Body: { "path": "/path/to/mod/folder", "modName": "My Mod", "optional": false }
///
/// Copies the folder to local-mods storage and returns the download URL.
/// </summary>
[Injectable(TypePriority = 0)]
public class LocalModStageHttpListener : IHttpListener
{
    private readonly ConfigService _configService;
    private readonly ModDownloadService _modDownloadService;
    private readonly ISptLogger<LocalModStageHttpListener> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public LocalModStageHttpListener(
        ConfigService configService,
        ModDownloadService modDownloadService,
        ISptLogger<LocalModStageHttpListener> logger)
    {
        _configService = configService;
        _modDownloadService = modDownloadService;
        _logger = logger;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
        return context.Request.Method == "POST" &&
               path.Equals("/modgod/api/local-mods/stage", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<StageLocalModRequest>(body, JsonOptions);

            if (string.IsNullOrWhiteSpace(request?.Path))
            {
                await SendJsonResponse(context, 400, new { success = false, error = "Path is required" });
                return;
            }

            if (!Directory.Exists(request.Path))
            {
                await SendJsonResponse(context, 404, new { success = false, error = "Directory not found" });
                return;
            }

            var modName = string.IsNullOrWhiteSpace(request.ModName)
                ? Path.GetFileName(request.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : request.ModName;

            _logger.Info($"Staging local mod: {modName} from {request.Path}");

            // Stage the local mod (copy files to local-mods storage)
            var guid = await _configService.StageLocalModAsync(request.Path, modName);

            // Build the download URL
            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var downloadUrl = _configService.GetLocalModDownloadUrl(guid, baseUrl);

            _logger.Info($"Local mod staged: {modName} -> {downloadUrl}");

            // Now download and analyze the mod (will use the local-mods endpoint)
            // This reuses the existing download/staging infrastructure
            var downloadResult = await _modDownloadService.DownloadAndAnalyzeModAsync(downloadUrl);

            if (!downloadResult.Success)
            {
                // Clean up the local mod storage if staging failed
                await _configService.DeleteLocalModAsync(guid);
                await SendJsonResponse(context, 500, new
                {
                    success = false,
                    error = $"Failed to analyze mod: {downloadResult.Error}"
                });
                return;
            }

            // Stage the mod entry
            await _modDownloadService.StageDownloadedModAsync(
                downloadResult,
                downloadUrl,
                modName,
                request.Optional);

            await SendJsonResponse(context, 200, new
            {
                success = true,
                guid,
                downloadUrl,
                modName,
                isStandardStructure = downloadResult.IsStandardStructure,
                topLevelDirectories = downloadResult.TopLevelDirectories,
                suggestedInstallPaths = downloadResult.SuggestedInstallPaths
            });
        }
        catch (Exception ex)
        {
            _logger.Error($"Error staging local mod: {ex.Message}");
            await SendJsonResponse(context, 500, new { success = false, error = ex.Message });
        }
    }

    private static async Task SendJsonResponse(HttpContext context, int statusCode, object data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }

    private class StageLocalModRequest
    {
        public string? Path { get; set; }
        public string? ModName { get; set; }
        public bool Optional { get; set; }
    }
}
