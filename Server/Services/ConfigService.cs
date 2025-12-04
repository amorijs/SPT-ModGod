using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using BewasModSync.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace BewasModSync.Services;

[Injectable(InjectionType = InjectionType.Singleton, TypePriority = OnLoadOrder.PreSptModLoader)]
public class ConfigService : IOnLoad
{
    private readonly ModHelper _modHelper;
    private readonly JsonUtil _jsonUtil;
    private readonly FileUtil _fileUtil;
    private readonly ISptLogger<ConfigService> _logger;

    private string _modPath = string.Empty;
    private string _dataPath = string.Empty;

    public ServerConfig Config { get; private set; } = new();
    public ModCache ModCache { get; private set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ConfigService(
        ModHelper modHelper,
        JsonUtil jsonUtil,
        FileUtil fileUtil,
        ISptLogger<ConfigService> logger)
    {
        _modHelper = modHelper;
        _jsonUtil = jsonUtil;
        _fileUtil = fileUtil;
        _logger = logger;
    }

    public string ModPath => _modPath;
    public string DataPath => _dataPath;
    public string ModCachePath => Path.Combine(_dataPath, "modCache");
    public string ConfigPath => Path.Combine(_dataPath, "serverConfig.json");
    public string CacheIndexPath => Path.Combine(_dataPath, "modCacheIndex.json");

    public async Task OnLoad()
    {
        _modPath = _modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        _dataPath = Path.Combine(_modPath, "BewaModSyncData");

        // Ensure data directory exists
        Directory.CreateDirectory(_dataPath);
        Directory.CreateDirectory(ModCachePath);

        await LoadConfigAsync();
        await LoadCacheIndexAsync();

        _logger.Success("BewasModSync ConfigService loaded!");
    }

    public async Task LoadConfigAsync()
    {
        if (File.Exists(ConfigPath))
        {
            var json = await File.ReadAllTextAsync(ConfigPath);
            Config = JsonSerializer.Deserialize<ServerConfig>(json, JsonOptions) ?? new ServerConfig();
        }
        else
        {
            Config = new ServerConfig();
            await SaveConfigAsync();
        }
    }

    public async Task SaveConfigAsync()
    {
        var json = JsonSerializer.Serialize(Config, JsonOptions);
        await File.WriteAllTextAsync(ConfigPath, json);
    }

    public async Task LoadCacheIndexAsync()
    {
        if (File.Exists(CacheIndexPath))
        {
            var json = await File.ReadAllTextAsync(CacheIndexPath);
            ModCache = JsonSerializer.Deserialize<ModCache>(json, JsonOptions) ?? new ModCache();
        }
        else
        {
            ModCache = new ModCache();
        }
    }

    public async Task SaveCacheIndexAsync()
    {
        var json = JsonSerializer.Serialize(ModCache, JsonOptions);
        await File.WriteAllTextAsync(CacheIndexPath, json);
    }

    public string GetCachePathForUrl(string url)
    {
        // Create a hash of the URL for the folder name
        var hash = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(url)))
            .Replace("/", "_")
            .Replace("+", "-")
            .Substring(0, 16);

        return Path.Combine(ModCachePath, hash);
    }

    public bool IsUrlCached(string url)
    {
        return ModCache.UrlToPath.ContainsKey(url) &&
               Directory.Exists(ModCache.UrlToPath[url]);
    }

    public async Task AddModAsync(ModEntry mod)
    {
        // Check if mod already exists by URL
        var existing = Config.ModList.FindIndex(m => m.DownloadUrl == mod.DownloadUrl);
        if (existing >= 0)
        {
            Config.ModList[existing] = mod;
        }
        else
        {
            Config.ModList.Add(mod);
        }

        await SaveConfigAsync();
    }

    public async Task RemoveModAsync(string downloadUrl)
    {
        Config.ModList.RemoveAll(m => m.DownloadUrl == downloadUrl);
        await SaveConfigAsync();
    }

    public async Task UpdateModTimestampAsync(string downloadUrl)
    {
        var mod = Config.ModList.Find(m => m.DownloadUrl == downloadUrl);
        if (mod != null)
        {
            mod.LastUpdated = DateTime.UtcNow.ToString("o");
            await SaveConfigAsync();
        }
    }
}
