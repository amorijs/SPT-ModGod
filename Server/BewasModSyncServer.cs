using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using BewasModSync.Models;
using BewasModSync.Services;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers.Http;
using SPTarkov.Server.Web;

namespace BewasModSync;

/// <summary>
/// Mod metadata - required for all SPT server mods
/// </summary>
public record ModMetadata : AbstractModMetadata, IModWebMetadata
{
    public override string ModGuid { get; init; } = "com.bewas.modsync";
    public override string Name { get; init; } = "BewasModSync";
    public override string Author { get; init; } = "Bewas";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; }
    public override string? License { get; init; } = "MIT";
}

/// <summary>
/// Main server mod entry point
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton, TypePriority = OnLoadOrder.PostSptModLoader)]
public class BewasModSyncServer(
    ISptLogger<BewasModSyncServer> logger,
    ModHelper modHelper)
    : IOnLoad
{
    public string ModPath = string.Empty;

    public Task OnLoad()
    {
        ModPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        
        logger.Success("========================================");
        logger.Success("  BewasModSync Server loaded!");
        logger.Success($"  Web UI: http://localhost:6969/bewasmodsync");
        logger.Success($"  Config API: http://localhost:6969/bewasmodsync/api/config");
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
               path.Equals("/bewasmodsync/api/config", StringComparison.OrdinalIgnoreCase);
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
