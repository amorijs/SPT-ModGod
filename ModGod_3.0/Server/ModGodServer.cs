using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModGod3.Models;
using ModGod3.Services;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers.Http;
using SPTarkov.Server.Web;

namespace ModGod3;

/// <summary>
/// Mod metadata - required for all SPT server mods.
/// Implements IModWebMetadata to enable SPT's built-in Blazor Server support.
/// </summary>
public record ModMetadata : AbstractModMetadata, IModWebMetadata
{
    public override string ModGuid { get; init; } = "com.modgod3.server";
    public override string Name { get; init; } = "ModGod 3.0";
    public override string Author { get; init; } = "Bewa";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new("3.0.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; }
    public override string License { get; init; } = "MIT";
}

/// <summary>
/// Main server mod entry point - Logs startup message
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton, TypePriority = OnLoadOrder.PostSptModLoader)]
public class ModGodServer(
    ISptLogger<ModGodServer> logger,
    ModHelper modHelper)
    : IOnLoad
{
    public string ModPath = string.Empty;

    private static readonly string ServerVersion =
        typeof(ModGodServer).Assembly.GetName().Version?.ToString(3) ?? "3.0.0";

    public Task OnLoad()
    {
        ModPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

        logger.Success("========================================");
        logger.Success($"  ModGod v{ServerVersion} loaded!");
        logger.Success("  Web UI: <your-server-url>/modgod");
        logger.Success("  API: <your-server-url>/modgod/api/");
        logger.Success("========================================");

        return Task.CompletedTask;
    }
}

/// <summary>
/// HTTP listener for status endpoint
/// GET /modgod/api/status
/// </summary>
[Injectable(TypePriority = 0)]
public class StatusHttpListener : IHttpListener
{
    private readonly MigrationService _migrationService;

    private static readonly string ServerVersion =
        typeof(ModGodServer).Assembly.GetName().Version?.ToString(3) ?? "3.0.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public StatusHttpListener(MigrationService migrationService)
    {
        _migrationService = migrationService;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
        return context.Request.Method == "GET" &&
               path.Equals("/modgod/api/status", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        var response = new
        {
            version = ServerVersion,
            status = "ok",
            needsMigration = _migrationService.NeedsMigration
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }
}

/// <summary>
/// HTTP listener for configuration endpoint
/// GET /modgod/api/config
/// Returns source items grouped by sync root
/// </summary>
[Injectable(TypePriority = 0)]
public class ConfigHttpListener : IHttpListener
{
    private readonly ConfigService _configService;
    private readonly MigrationService _migrationService;
    private readonly ISptLogger<ConfigHttpListener> _logger;

    private static readonly string ServerVersion =
        typeof(ModGodServer).Assembly.GetName().Version?.ToString(3) ?? "3.0.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ConfigHttpListener(
        ConfigService configService,
        MigrationService migrationService,
        ISptLogger<ConfigHttpListener> logger)
    {
        _configService = configService;
        _migrationService = migrationService;
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
        try
        {
            _logger.Info("Client requested config");
            var sourceGroups = _configService.ScanSourceItems();

            var response = new
            {
                version = ServerVersion,
                needsMigration = _migrationService.NeedsMigration,
                sourceGroups = sourceGroups,
                syncRoots = _configService.GetEffectiveSyncRoots()
            };

            var json = JsonSerializer.Serialize(response, JsonOptions);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        }
        catch (Exception ex)
        {
            _logger.Error($"Error getting config: {ex.Message}");
            context.Response.StatusCode = 500;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message}\"}}"));
        }

        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }
}

/// <summary>
/// HTTP listener for manifest endpoint
/// GET /modgod/api/manifest?optedIn=path1,path2
/// Returns file manifest for client synchronization
/// </summary>
[Injectable(TypePriority = 0)]
public class ManifestHttpListener : IHttpListener
{
    private readonly ManifestService _manifestService;
    private readonly ISptLogger<ManifestHttpListener> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
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
        try
        {
            // Parse opted-in items from query string
            var optedInParam = context.Request.Query["optedIn"].FirstOrDefault();
            IEnumerable<string>? optedInItems = null;

            if (!string.IsNullOrEmpty(optedInParam))
            {
                optedInItems = optedInParam.Split(',', StringSplitOptions.RemoveEmptyEntries);
                _logger.Info($"Client opted into: {optedInParam}");
            }

            var manifest = _manifestService.GenerateManifest(optedInItems);
            _logger.Info($"Generated manifest: {manifest.Files.Count} files in {manifest.GenerationTimeMs}ms");

            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        }
        catch (Exception ex)
        {
            _logger.Error($"Error generating manifest: {ex.Message}");
            context.Response.StatusCode = 500;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message}\"}}"));
        }

        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }
}

/// <summary>
/// HTTP listener for file downloads
/// GET /modgod/api/file/{relativePath}
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
        var relativePath = requestPath.Substring("/modgod/api/file/".Length);
        relativePath = Uri.UnescapeDataString(relativePath).Replace('/', Path.DirectorySeparatorChar);

        var fullPath = Path.Combine(_configService.SptRoot, relativePath);

        // Security: prevent path traversal
        var normalizedPath = Path.GetFullPath(fullPath);
        if (!normalizedPath.StartsWith(_configService.SptRoot))
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
            _logger.Error($"Error serving file {relativePath}: {ex.Message}");
            context.Response.StatusCode = 500;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"Internal error\"}"));
            await context.Response.StartAsync();
            await context.Response.CompleteAsync();
        }
    }
}

/// <summary>
/// HTTP listener for migration with transfer
/// POST /modgod/api/migrate/transfer
/// </summary>
[Injectable(TypePriority = 0)]
public class MigrateTransferHttpListener : IHttpListener
{
    private readonly MigrationService _migrationService;
    private readonly ISptLogger<MigrateTransferHttpListener> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MigrateTransferHttpListener(
        MigrationService migrationService,
        ISptLogger<MigrateTransferHttpListener> logger)
    {
        _migrationService = migrationService;
        _logger = logger;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
        return context.Request.Method == "POST" &&
               path.Equals("/modgod/api/migrate/transfer", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        try
        {
            _logger.Info("Starting migration with transfer...");
            var result = await _migrationService.MigrateWithTransferAsync();

            var json = JsonSerializer.Serialize(result, JsonOptions);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        }
        catch (Exception ex)
        {
            _logger.Error($"Migration failed: {ex.Message}");
            context.Response.StatusCode = 500;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message}\"}}"));
        }

        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }
}

/// <summary>
/// HTTP listener for starting fresh
/// POST /modgod/api/migrate/fresh
/// </summary>
[Injectable(TypePriority = 0)]
public class MigrateFreshHttpListener : IHttpListener
{
    private readonly MigrationService _migrationService;
    private readonly ISptLogger<MigrateFreshHttpListener> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MigrateFreshHttpListener(
        MigrationService migrationService,
        ISptLogger<MigrateFreshHttpListener> logger)
    {
        _migrationService = migrationService;
        _logger = logger;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
        return context.Request.Method == "POST" &&
               path.Equals("/modgod/api/migrate/fresh", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        try
        {
            _logger.Info("Starting fresh...");
            var result = await _migrationService.StartFreshAsync();

            var json = JsonSerializer.Serialize(result, JsonOptions);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        }
        catch (Exception ex)
        {
            _logger.Error($"Start fresh failed: {ex.Message}");
            context.Response.StatusCode = 500;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message}\"}}"));
        }

        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }
}

/// <summary>
/// HTTP listener for toggling optional flag
/// POST /modgod/api/sources/{path}/toggle-optional
/// </summary>
[Injectable(TypePriority = 0)]
public class ToggleOptionalHttpListener : IHttpListener
{
    private readonly ConfigService _configService;
    private readonly ISptLogger<ToggleOptionalHttpListener> _logger;

    public ToggleOptionalHttpListener(
        ConfigService configService,
        ISptLogger<ToggleOptionalHttpListener> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        return context.Request.Method == "POST" &&
               path.Contains("/modgod/api/sources/") &&
               path.EndsWith("/toggle-optional", StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        try
        {
            var requestPath = context.Request.Path.Value ?? "";
            var startIndex = "/modgod/api/sources/".Length;
            var endIndex = requestPath.LastIndexOf("/toggle-optional", StringComparison.OrdinalIgnoreCase);
            var sourcePath = Uri.UnescapeDataString(requestPath.Substring(startIndex, endIndex - startIndex));

            _logger.Info($"Toggling optional for: {sourcePath}");
            await _configService.ToggleOptionalAsync(sourcePath);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"success\":true}"));
        }
        catch (Exception ex)
        {
            _logger.Error($"Error toggling optional: {ex.Message}");
            context.Response.StatusCode = 500;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message}\"}}"));
        }

        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }
}

/// <summary>
/// HTTP listener for deleting source items
/// DELETE /modgod/api/sources/{path}?deleteLinked=true/false
/// </summary>
[Injectable(TypePriority = 0)]
public class DeleteSourceItemHttpListener : IHttpListener
{
    private readonly ConfigService _configService;
    private readonly ISptLogger<DeleteSourceItemHttpListener> _logger;

    public DeleteSourceItemHttpListener(
        ConfigService configService,
        ISptLogger<DeleteSourceItemHttpListener> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        return context.Request.Method == "DELETE" &&
               path.StartsWith("/modgod/api/sources/", StringComparison.OrdinalIgnoreCase) &&
               !path.Contains("/toggle-optional");
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        try
        {
            var requestPath = context.Request.Path.Value ?? "";
            var sourcePath = Uri.UnescapeDataString(requestPath.Substring("/modgod/api/sources/".Length));
            var deleteLinked = context.Request.Query["deleteLinked"].FirstOrDefault() == "true";

            _logger.Info($"Deleting source item: {sourcePath} (deleteLinked: {deleteLinked})");
            await _configService.DeleteSourceItemAsync(sourcePath, deleteLinked);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"success\":true}"));
        }
        catch (Exception ex)
        {
            _logger.Error($"Error deleting source item: {ex.Message}");
            context.Response.StatusCode = 500;
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message}\"}}"));
        }

        await context.Response.StartAsync();
        await context.Response.CompleteAsync();
    }
}

// Note: Blazor UI is served automatically by SPT via IModWebMetadata
// No custom HTTP listener needed - SPT handles /modgod/* routes for the web UI
