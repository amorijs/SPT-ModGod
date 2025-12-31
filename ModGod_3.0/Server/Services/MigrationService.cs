using System.Text.Json;
using ModGod3.Models;
using ModGod3.Models.Legacy;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace ModGod3.Services;

/// <summary>
/// Service for migrating from v2.x to v3.0 configuration format.
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton)]
public class MigrationService
{
    private readonly ConfigService _configService;
    private readonly ISptLogger<MigrationService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MigrationService(
        ConfigService configService,
        ISptLogger<MigrationService> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// Check if migration is needed
    /// </summary>
    public bool NeedsMigration => _configService.NeedsMigration;

    /// <summary>
    /// Migrate from v2.x configuration with backup.
    /// Transfers existing settings to new format.
    /// </summary>
    public async Task<MigrationResult> MigrateWithTransferAsync()
    {
        var result = new MigrationResult();

        try
        {
            // Create backup
            await CreateBackupAsync();
            result.BackupCreated = true;

            // Load legacy config
            var legacyConfig = await LoadLegacyConfigAsync();
            if (legacyConfig == null)
            {
                result.Success = false;
                result.Error = "Failed to load legacy configuration";
                return result;
            }

            // Create new config from legacy
            var newConfig = new ModGodConfig
            {
                Sources = new Dictionary<string, SourceItemMetadata>(),
                SyncRules = new SyncRulesConfig
                {
                    UseDefaultExclusions = true,
                    Exclusions = new List<string>(),
                    AdditionalRoots = new List<string>()
                }
            };

            // Migrate mod entries to source metadata
            var migratedSources = 0;
            foreach (var mod in legacyConfig.ModList.Where(m => m.Status == LegacyModStatus.Installed))
            {
                // Extract install paths and create source metadata
                foreach (var installPath in mod.InstallPaths)
                {
                    if (installPath.Length < 2) continue;

                    // Get the target path (remove <SPT_ROOT> prefix)
                    var targetPath = installPath[1]
                        .Replace("<SPT_ROOT>", "")
                        .Replace("\\", "/")
                        .TrimStart('/');

                    if (string.IsNullOrWhiteSpace(targetPath)) continue;

                    // Check if this path exists on disk
                    var fullPath = Path.Combine(_configService.SptRoot, targetPath.Replace('/', Path.DirectorySeparatorChar));
                    if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
                    {
                        _logger.Debug($"Skipping non-existent install path: {targetPath}");
                        continue;
                    }

                    // Create metadata entry
                    if (!newConfig.Sources.ContainsKey(targetPath))
                    {
                        newConfig.Sources[targetPath] = new SourceItemMetadata
                        {
                            DisplayName = mod.ModName,
                            Optional = mod.Optional,
                            LinkedTo = new List<string>()
                        };
                        migratedSources++;
                        _logger.Info($"Migrated source: {targetPath} ({mod.ModName})");
                    }
                }

                // If mod has multiple install paths, create links between them
                if (mod.InstallPaths.Count > 1)
                {
                    var paths = mod.InstallPaths
                        .Where(p => p.Length >= 2)
                        .Select(p => p[1].Replace("<SPT_ROOT>", "").Replace("\\", "/").TrimStart('/'))
                        .Where(p => newConfig.Sources.ContainsKey(p))
                        .ToList();

                    foreach (var path in paths)
                    {
                        var otherPaths = paths.Where(p => p != path).ToList();
                        if (newConfig.Sources.TryGetValue(path, out var meta))
                        {
                            meta.LinkedTo = otherPaths;
                        }
                    }
                }
            }

            result.SourcesMigrated = migratedSources;

            // Migrate sync configuration from PlayerSyncConfig
            if (legacyConfig.PlayerSyncConfig != null)
            {
                var playerConfig = legacyConfig.PlayerSyncConfig;

                newConfig.SyncRules.UseDefaultExclusions = playerConfig.UseDefaultExclusions;

                if (playerConfig.ExcludedPaths.Count > 0)
                {
                    newConfig.SyncRules.Exclusions = playerConfig.ExcludedPaths
                        .Select(p => p.Replace("\\", "/").TrimStart('/'))
                        .ToList();
                    result.ExclusionsMigrated = newConfig.SyncRules.Exclusions.Count;
                }

                // If there were custom sync paths beyond defaults, add them as additional roots
                var defaultRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "BepInEx/plugins",
                    "SPT/user/mods"
                };

                var additionalRoots = playerConfig.SyncPaths
                    .Select(p => p.Target.Replace("\\", "/").TrimStart('/'))
                    .Where(p => !defaultRoots.Contains(p))
                    .Distinct()
                    .ToList();

                if (additionalRoots.Count > 0)
                {
                    newConfig.SyncRules.AdditionalRoots = additionalRoots;
                    _logger.Info($"Migrated {additionalRoots.Count} additional sync roots");
                }
            }

            // Migrate install path mappings if customized
            if (legacyConfig.DefaultInstallPaths?.Count > 0)
            {
                newConfig.DefaultInstallPaths = legacyConfig.DefaultInstallPaths
                    .Select(p => new InstallPathMapping
                    {
                        Source = p.Source,
                        Target = p.Target
                    })
                    .ToList();
                _logger.Info($"Migrated {newConfig.DefaultInstallPaths.Count} install path mappings");
            }

            // Save new config
            _configService.Config.Sources = newConfig.Sources;
            _configService.Config.SyncRules = newConfig.SyncRules;
            _configService.Config.DefaultInstallPaths = newConfig.DefaultInstallPaths;
            await _configService.SaveConfigAsync();

            result.Success = true;
            _logger.Success($"Migration complete! Migrated {migratedSources} source items");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger.Error($"Migration failed: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Start fresh with default configuration, keeping no v2.x settings.
    /// </summary>
    public async Task<MigrationResult> StartFreshAsync()
    {
        var result = new MigrationResult();

        try
        {
            // Create backup
            await CreateBackupAsync();
            result.BackupCreated = true;

            // Create fresh config
            _configService.Config.Sources = new Dictionary<string, SourceItemMetadata>();
            _configService.Config.SyncRules = new SyncRulesConfig
            {
                UseDefaultExclusions = true,
                Exclusions = new List<string>(),
                AdditionalRoots = new List<string>()
            };
            _configService.Config.DefaultInstallPaths = null;
            _configService.Config.ForgeApiKey = null;

            await _configService.SaveConfigAsync();

            result.Success = true;
            _logger.Success("Started fresh with default configuration");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger.Error($"Failed to start fresh: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Create a backup of the legacy config
    /// </summary>
    private async Task CreateBackupAsync()
    {
        if (File.Exists(_configService.LegacyConfigPath))
        {
            var backupPath = _configService.LegacyConfigPath + ".v2backup";
            File.Copy(_configService.LegacyConfigPath, backupPath, overwrite: true);
            _logger.Info($"Created backup at: {backupPath}");
        }
    }

    /// <summary>
    /// Load the legacy v2.x configuration
    /// </summary>
    private async Task<LegacyServerConfig?> LoadLegacyConfigAsync()
    {
        if (!File.Exists(_configService.LegacyConfigPath))
        {
            _logger.Warning("Legacy config file not found");
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_configService.LegacyConfigPath);
            return JsonSerializer.Deserialize<LegacyServerConfig>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to load legacy config: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Result of a migration operation
/// </summary>
public class MigrationResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public bool BackupCreated { get; set; }
    public int SourcesMigrated { get; set; }
    public int ExclusionsMigrated { get; set; }
}
