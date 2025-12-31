using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using ModGod3.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace ModGod3.Services;

/// <summary>
/// Service for checking the health and update status of source items.
/// Scans DLLs within source items to extract version information and checks against Forge.
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton)]
public class ModHealthService(
    ConfigService configService,
    ForgeService forgeService,
    ISptLogger<ModHealthService> logger)
{
    private HealthCheckResult? _cachedResult;
    private DateTime _cacheTime;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets the last health check result (if any), without running a new check
    /// </summary>
    public HealthCheckResult? GetCachedResult() => _cachedResult;

    /// <summary>
    /// Run a health check on all source items
    /// </summary>
    public async Task<HealthCheckResult> RunHealthCheckAsync(bool forceRefresh = false)
    {
        // Return cached result if valid
        if (!forceRefresh && _cachedResult != null && DateTime.UtcNow - _cacheTime < _cacheDuration)
        {
            return _cachedResult;
        }

        var result = new HealthCheckResult();

        // Check for API key
        if (!forgeService.HasApiKey)
        {
            result.Error = "No Forge API key configured. Please add your API key in settings.";
            return result;
        }

        // Get SPT version
        result.SptVersion = GetSptVersion();
        if (string.IsNullOrEmpty(result.SptVersion))
        {
            result.Error = "Could not determine SPT version";
            return result;
        }

        // Get latest SPT version from Forge
        try
        {
            var sptVersions = await forgeService.GetSptVersionsAsync();
            result.LatestSptVersion = sptVersions?.Versions?.FirstOrDefault()?.Version;
        }
        catch (Exception ex)
        {
            logger.Warning($"Could not fetch SPT versions: {ex.Message}");
        }

        logger.Info($"Running health check for SPT {result.SptVersion}...");

        // Step 1: Scan ALL DLLs in mod directories
        var scannedMods = ScanAllMods();
        logger.Info($"Scanned {scannedMods.Count} mods ({scannedMods.Count(m => m.IsServerMod)} server, {scannedMods.Count(m => !m.IsServerMod)} client)");

        // Step 2: Check each scanned mod against Forge
        logger.Info($"Checking {scannedMods.Count} mods against Forge...");
        foreach (var scannedMod in scannedMods)
        {
            var healthInfo = new ModHealthInfo
            {
                ScannedMod = scannedMod,
                InstalledVersion = scannedMod.Version
            };
            
            await CheckModHealthAsync(healthInfo, result.SptVersion);
            result.Mods.Add(healthInfo);
        }

        // Step 3: Check dependencies for mods that were found on Forge
        logger.Info("Checking mod dependencies...");
        await CheckAllDependenciesAsync(result.Mods, scannedMods, result.SptVersion ?? "4.0.0");

        // Sort by name
        result.Mods.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        var depIssues = result.DependencyIssuesCount;
        var depMessage = depIssues > 0 ? $", {depIssues} with dependency issues" : "";
        logger.Success($"Health check complete: {result.UpToDateCount} up-to-date, {result.UpdatesAvailableCount} updates, {result.NotOnForgeCount} not on Forge{depMessage}");

        _cachedResult = result;
        _cacheTime = DateTime.UtcNow;
        return result;
    }

    /// <summary>
    /// Run health check for a specific source item
    /// </summary>
    public async Task<HealthStatus> CheckSourceItemHealthAsync(SourceItem sourceItem)
    {
        var health = new HealthStatus { Scanning = true };
        sourceItem.Health = health;

        try
        {
            var fullPath = Path.Combine(configService.SptRoot, sourceItem.Path);
            
            if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
            {
                health.Scanning = false;
                health.Scanned = true;
                health.Warnings.Add("Item does not exist on disk");
                return health;
            }

            // Find DLLs in this source item
            var dlls = sourceItem.IsDirectory
                ? Directory.GetFiles(fullPath, "*.dll", SearchOption.AllDirectories)
                : (fullPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? new[] { fullPath } : []);

            if (dlls.Length == 0)
            {
                health.Scanning = false;
                health.Scanned = true;
                return health; // No DLLs to scan
            }

            // Determine if this is a server or client mod
            var isServerMod = sourceItem.Path.Contains("SPT/user/mods", StringComparison.OrdinalIgnoreCase);
            
            ScannedMod? scannedMod = null;
            foreach (var dll in dlls)
            {
                scannedMod = isServerMod ? ScanServerModDll(dll) : ScanClientModDll(dll);
                if (scannedMod != null)
                    break;
            }

            if (scannedMod == null)
            {
                health.Scanning = false;
                health.Scanned = true;
                return health; // No recognizable mod metadata
            }

            health.Version = scannedMod.Version;

            // Check against Forge if we have an API key
            if (forgeService.HasApiKey)
            {
                var sptVersion = GetSptVersion() ?? "4.0.0";
                var forgeResponse = await forgeService.GetModByGuidAsync(scannedMod.Guid, sptVersion);

                if (forgeResponse?.Success == true && forgeResponse.Mod != null)
                {
                    health.ForgeModId = forgeResponse.Mod.Id;

                    // Find latest compatible version
                    var versions = forgeResponse.Mod.Versions?.OrderByDescending(v => v.PublishedAt).ToList() ?? [];
                    ForgeModVersion? latestCompatible = null;

                    foreach (var version in versions)
                    {
                        if (IsVersionCompatible(version.SptVersionConstraint, sptVersion))
                        {
                            latestCompatible = version;
                            break;
                        }
                    }

                    if (latestCompatible != null)
                    {
                        health.LatestVersion = latestCompatible.Version;
                        health.UpdateAvailable = IsNewer(latestCompatible.Version, scannedMod.Version);
                    }
                    else if (versions.Count > 0)
                    {
                        health.LatestVersion = versions[0].Version;
                        health.Warnings.Add("No version compatible with current SPT");
                    }
                }
            }

            health.Scanning = false;
            health.Scanned = true;
        }
        catch (Exception ex)
        {
            logger.Warning($"Error checking health for {sourceItem.Path}: {ex.Message}");
            health.Scanning = false;
            health.Scanned = true;
            health.Warnings.Add($"Error: {ex.Message}");
        }

        return health;
    }

    /// <summary>
    /// Get the SPT version from the server core DLL
    /// </summary>
    private string? GetSptVersion()
    {
        var coreDllPath = Path.Combine(configService.SptRoot, "SPT", "SPTarkov.Server.Core.dll");
        if (!File.Exists(coreDllPath))
        {
            logger.Warning($"SPT core DLL not found at {coreDllPath}");
            return null;
        }

        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(coreDllPath);
            var version = versionInfo.FileVersion;
            if (!string.IsNullOrEmpty(version))
            {
                // Clean up version (remove +hash suffix, take first 3 parts)
                var plusIndex = version.IndexOf('+');
                if (plusIndex > 0) version = version[..plusIndex];
                
                var parts = version.Split('.');
                if (parts.Length > 3)
                    version = string.Join(".", parts.Take(3));
                    
                return version;
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"Could not read SPT version: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Scan all mod directories for DLLs and extract metadata
    /// </summary>
    private List<ScannedMod> ScanAllMods()
    {
        var mods = new List<ScannedMod>();
        var seenGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Scan server mods (SPT/user/mods)
        var serverModsDir = Path.Combine(configService.SptRoot, "SPT", "user", "mods");
        if (Directory.Exists(serverModsDir))
        {
            foreach (var modDir in Directory.GetDirectories(serverModsDir))
            {
                // Skip ModGodServer itself
                if (Path.GetFileName(modDir).Equals("ModGodServer", StringComparison.OrdinalIgnoreCase))
                    continue;

                var dlls = Directory.GetFiles(modDir, "*.dll", SearchOption.TopDirectoryOnly);
                foreach (var dll in dlls)
                {
                    var scanned = ScanServerModDll(dll);
                    if (scanned != null && !seenGuids.Contains(scanned.Guid))
                    {
                        seenGuids.Add(scanned.Guid);
                        mods.Add(scanned);
                        logger.Debug($"[Server] {scanned.Name ?? scanned.Guid}: {scanned.Version}");
                    }
                }
            }
        }

        // Scan client mods (BepInEx/plugins)
        var clientModsDir = Path.Combine(configService.SptRoot, "BepInEx", "plugins");
        if (Directory.Exists(clientModsDir))
        {
            // Skip the spt subfolder (core SPT plugins)
            var sptPluginsDir = Path.Combine(clientModsDir, "spt");
            
            var dlls = Directory.GetFiles(clientModsDir, "*.dll", SearchOption.AllDirectories)
                .Where(f => !f.StartsWith(sptPluginsDir, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var dll in dlls)
            {
                var scanned = ScanClientModDll(dll);
                if (scanned != null && !seenGuids.Contains(scanned.Guid))
                {
                    seenGuids.Add(scanned.Guid);
                    mods.Add(scanned);
                    logger.Debug($"[Client] {scanned.Name ?? scanned.Guid}: {scanned.Version}");
                }
            }
        }

        return mods;
    }

    /// <summary>
    /// Scan a server mod DLL for metadata
    /// </summary>
    private ScannedMod? ScanServerModDll(string dllPath)
    {
        var loadContext = new CollectibleAssemblyLoadContext(configService.SptRoot);
        
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(dllPath);
            
            // Find type that extends AbstractModMetadata
            foreach (var type in GetLoadableTypes(assembly))
            {
                if (type.BaseType?.Name == "AbstractModMetadata" && !type.IsAbstract)
                {
                    var instance = Activator.CreateInstance(type);
                    if (instance == null) continue;

                    var guid = type.GetProperty("ModGuid")?.GetValue(instance)?.ToString();
                    var name = type.GetProperty("Name")?.GetValue(instance)?.ToString();
                    var author = type.GetProperty("Author")?.GetValue(instance)?.ToString();
                    var version = type.GetProperty("Version")?.GetValue(instance)?.ToString();

                    if (!string.IsNullOrEmpty(guid))
                    {
                        return new ScannedMod
                        {
                            Guid = guid,
                            Version = version,
                            DllPath = dllPath,
                            IsServerMod = true,
                            Name = name,
                            Author = author
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.Debug($"Could not scan server mod {Path.GetFileName(dllPath)}: {ex.Message}");
        }
        finally
        {
            loadContext.Unload();
        }

        return null;
    }

    /// <summary>
    /// Scan a client mod DLL for BepInPlugin attribute
    /// </summary>
    private ScannedMod? ScanClientModDll(string dllPath)
    {
        try
        {
            var resolver = new GracefulAssemblyResolver(dllPath, configService.SptRoot);
            using var mlc = new MetadataLoadContext(resolver);
            
            var assembly = mlc.LoadFromAssemblyPath(dllPath);
            
            foreach (var type in assembly.GetTypes())
            {
                try
                {
                    var bepInPlugin = type.GetCustomAttributesData()
                        .FirstOrDefault(a => a.AttributeType.Name is "BepInPlugin" or "BepInPluginAttribute"
                                          || (a.AttributeType.FullName?.Contains("BepInPlugin") ?? false));
                    
                    if (bepInPlugin != null && bepInPlugin.ConstructorArguments.Count >= 3)
                    {
                        var guid = bepInPlugin.ConstructorArguments[0].Value?.ToString();
                        var name = bepInPlugin.ConstructorArguments[1].Value?.ToString();
                        var version = bepInPlugin.ConstructorArguments[2].Value?.ToString();
                        
                        if (!string.IsNullOrEmpty(guid))
                        {
                            return new ScannedMod
                            {
                                Guid = guid,
                                Version = version,
                                DllPath = dllPath,
                                IsServerMod = false,
                                Name = name
                            };
                        }
                    }
                }
                catch
                {
                    // Skip types that can't be inspected
                }
            }
        }
        catch (Exception ex)
        {
            logger.Debug($"Could not scan client mod {Path.GetFileName(dllPath)}: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Check a single mod's health against Forge
    /// </summary>
    private async Task CheckModHealthAsync(ModHealthInfo healthInfo, string sptVersion)
    {
        var guid = healthInfo.ScannedMod.Guid;
        var displayName = healthInfo.DisplayName;
        
        try
        {
            // Look up mod by GUID
            var forgeResponse = await forgeService.GetModByGuidAsync(guid, sptVersion);
            
            if (forgeResponse == null)
            {
                logger.Warning($"[{displayName}] Forge returned null response for GUID: {guid}");
                healthInfo.Status = ModHealthStatus.NotOnForge;
                healthInfo.ErrorMessage = "Forge API returned no response";
                return;
            }
            
            if (!forgeResponse.Success)
            {
                logger.Warning($"[{displayName}] Not on Forge - {forgeResponse.Error ?? "Unknown error"} (GUID: {guid})");
                healthInfo.Status = ModHealthStatus.NotOnForge;
                healthInfo.ErrorMessage = forgeResponse.Error;
                return;
            }
            
            if (forgeResponse.Mod == null)
            {
                logger.Warning($"[{displayName}] Not on Forge - No mod found for GUID: {guid}");
                healthInfo.Status = ModHealthStatus.NotOnForge;
                healthInfo.ErrorMessage = $"No mod found for GUID: {guid}";
                return;
            }

            var forgeMod = forgeResponse.Mod;
            healthInfo.ForgeModId = forgeMod.Id;
            healthInfo.ForgeSlug = forgeMod.Slug;
            healthInfo.ForgeUrl = forgeMod.DetailUrl ?? $"https://forge.sp-tarkov.com/mod/{forgeMod.Id}/{forgeMod.Slug}";

            // Find the latest compatible version
            var versions = forgeMod.Versions?.OrderByDescending(v => v.PublishedAt).ToList() ?? [];
            
            ForgeModVersion? latestCompatible = null;
            ForgeModVersion? latestOverall = versions.FirstOrDefault();
            
            foreach (var version in versions)
            {
                if (IsVersionCompatible(version.SptVersionConstraint, sptVersion))
                {
                    latestCompatible = version;
                    break;
                }
            }

            if (latestCompatible != null)
            {
                healthInfo.LatestVersion = latestCompatible.Version;
                healthInfo.LatestSptConstraint = latestCompatible.SptVersionConstraint;
                healthInfo.LatestDownloadUrl = GetValidDownloadLink(
                    latestCompatible.Link, forgeMod.Id, forgeMod.Slug, latestCompatible.Version);
            }
            else if (latestOverall != null)
            {
                // No compatible version found, show latest anyway
                healthInfo.LatestVersion = latestOverall.Version;
                healthInfo.LatestSptConstraint = latestOverall.SptVersionConstraint;
                healthInfo.LatestDownloadUrl = GetValidDownloadLink(
                    latestOverall.Link, forgeMod.Id, forgeMod.Slug, latestOverall.Version);
            }

            // Determine status
            healthInfo.Status = DetermineStatus(healthInfo.InstalledVersion, healthInfo.LatestVersion, latestCompatible != null);
        }
        catch (Exception ex)
        {
            healthInfo.Status = ModHealthStatus.Error;
            healthInfo.ErrorMessage = ex.Message;
            logger.Debug($"Error checking {healthInfo.DisplayName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if a version constraint is compatible with the current SPT version
    /// </summary>
    private static bool IsVersionCompatible(string? constraint, string sptVersion)
    {
        if (string.IsNullOrEmpty(constraint)) return true;
        
        try
        {
            var range = new SemanticVersioning.Range(constraint);
            var version = new SemanticVersioning.Version(sptVersion);
            return range.IsSatisfied(version);
        }
        catch
        {
            return true; // Can't parse, assume compatible
        }
    }

    /// <summary>
    /// Check if latestVersion is newer than installedVersion
    /// </summary>
    private static bool IsNewer(string? latest, string? installed)
    {
        if (string.IsNullOrEmpty(latest) || string.IsNullOrEmpty(installed))
            return false;

        try
        {
            var latestVer = new SemanticVersioning.Version(CleanVersion(latest));
            var installedVer = new SemanticVersioning.Version(CleanVersion(installed));
            return latestVer > installedVer;
        }
        catch
        {
            return latest != installed;
        }
    }

    /// <summary>
    /// Determine the health status by comparing versions
    /// </summary>
    private static ModHealthStatus DetermineStatus(string? installed, string? latest, bool hasCompatibleVersion)
    {
        if (string.IsNullOrEmpty(latest))
            return ModHealthStatus.NotOnForge;

        if (string.IsNullOrEmpty(installed))
            return hasCompatibleVersion ? ModHealthStatus.UpdateAvailable : ModHealthStatus.Incompatible;

        try
        {
            var installedVer = new SemanticVersioning.Version(CleanVersion(installed));
            var latestVer = new SemanticVersioning.Version(CleanVersion(latest));

            if (installedVer >= latestVer)
                return installedVer > latestVer ? ModHealthStatus.NewerThanForge : ModHealthStatus.UpToDate;

            return hasCompatibleVersion ? ModHealthStatus.UpdateAvailable : ModHealthStatus.Incompatible;
        }
        catch
        {
            // Can't parse versions, do string comparison
            if (installed == latest)
                return ModHealthStatus.UpToDate;
            return hasCompatibleVersion ? ModHealthStatus.UpdateAvailable : ModHealthStatus.Incompatible;
        }
    }

    /// <summary>
    /// Clean a version string for parsing
    /// </summary>
    private static string CleanVersion(string version)
    {
        var plusIndex = version.IndexOf('+');
        if (plusIndex > 0)
            version = version[..plusIndex];
        
        var parts = version.Split('.');
        if (parts.Length > 3)
            version = string.Join(".", parts.Take(3));
        
        return version.Trim();
    }

    /// <summary>
    /// Gets a valid download link - prefers external links (GitHub), uses Forge constructed URL otherwise
    /// </summary>
    private string? GetValidDownloadLink(string? apiLink, int modId, string? slug, string? version)
    {
        // Invalid patterns that should be rejected
        var invalidPatterns = new[]
        {
            "dev.sp-tarkov.com/attachments",
            "sp-tarkov.com/attachments"
        };

        // Check if API link is a valid external link (GitHub, GitLab, etc.)
        if (!string.IsNullOrEmpty(apiLink))
        {
            var isInvalid = invalidPatterns.Any(p => apiLink.Contains(p, StringComparison.OrdinalIgnoreCase));
            
            // If it's a valid external link (not Forge internal, not attachment), use it
            if (!isInvalid && !apiLink.Contains("forge.sp-tarkov.com/mod/download"))
            {
                logger.Debug($"Using external API link: {apiLink}");
                return apiLink;
            }
        }

        // Construct Forge download URL
        if (modId > 0 && !string.IsNullOrEmpty(slug) && !string.IsNullOrEmpty(version))
        {
            var constructedUrl = $"https://forge.sp-tarkov.com/mod/download/{modId}/{slug}/{version}";
            logger.Debug($"Using constructed Forge download URL: {constructedUrl}");
            return constructedUrl;
        }

        // Last resort: use API link even if it looks suspicious
        if (!string.IsNullOrEmpty(apiLink))
        {
            logger.Warning($"Using potentially invalid API link as last resort: {apiLink}");
            return apiLink;
        }

        return null;
    }

    /// <summary>
    /// Check dependencies for all mods that have a Forge mod ID.
    /// </summary>
    private async Task CheckAllDependenciesAsync(List<ModHealthInfo> mods, List<ScannedMod> scannedMods, string sptVersion)
    {
        // Build a map of GUIDs to installed versions for quick lookup
        var installedGuids = scannedMods
            .Where(m => !string.IsNullOrEmpty(m.Guid))
            .ToDictionary(
                m => m.Guid.ToLowerInvariant(),
                m => m.Version,
                StringComparer.OrdinalIgnoreCase
            );

        // Get mods that are on Forge and have a version
        var modsToCheck = mods
            .Where(m => m.ForgeModId.HasValue && !string.IsNullOrEmpty(m.InstalledVersion))
            .ToList();

        if (modsToCheck.Count == 0)
        {
            logger.Info("No mods to check for dependencies (none have Forge IDs)");
            return;
        }

        logger.Info($"Checking dependencies for {modsToCheck.Count} mods...");

        var modsWithDeps = 0;
        var totalDeps = 0;

        // Check each mod individually
        foreach (var healthInfo in modsToCheck)
        {
            var modId = healthInfo.ForgeModId!.Value;
            var version = healthInfo.InstalledVersion!;

            var response = await forgeService.GetModDependenciesAsync([(modId, version)]);
            
            if (response == null || !response.Success)
            {
                continue;
            }
            
            // Filter out the queried mod itself if it appears in the response
            var dependencies = response.Mods.Where(m => m.Id != modId).ToList();
            
            if (dependencies.Count == 0)
            {
                continue;
            }

            modsWithDeps++;
            totalDeps += dependencies.Count;
            logger.Info($"[{healthInfo.DisplayName}] Has {dependencies.Count} dependencies: {string.Join(", ", dependencies.Select(d => d.Name))}");

            foreach (var dep in dependencies)
            {
                // Get actual mod details to find real latest compatible version
                var (latestVersion, downloadLink) = await GetLatestVersionForDependency(
                    dep.Id, dep.Slug, dep.LatestCompatibleVersion, sptVersion);
                
                var depInfo = new DependencyInfo
                {
                    ModId = dep.Id,
                    Guid = dep.Guid,
                    Name = dep.Name,
                    Slug = dep.Slug,
                    LatestVersion = latestVersion,
                    DownloadLink = downloadLink
                };

                // Check if this dependency is installed
                var depGuidLower = dep.Guid?.ToLowerInvariant();
                
                if (!string.IsNullOrEmpty(depGuidLower) && installedGuids.TryGetValue(depGuidLower, out var installedVersion))
                {
                    depInfo.InstalledVersion = installedVersion;
                    depInfo.Status = DependencyStatus.Satisfied;
                }
                else
                {
                    depInfo.Status = DependencyStatus.Missing;
                    logger.Warning($"[{healthInfo.DisplayName}] MISSING dependency: {dep.Name} (GUID: {dep.Guid})");
                }

                healthInfo.Dependencies.Add(depInfo);
            }
        }

        logger.Info($"Dependency check complete: {modsWithDeps} mods have dependencies ({totalDeps} total deps)");
    }

    /// <summary>
    /// Fetches the actual latest compatible version for a dependency from the mod details API
    /// </summary>
    private async Task<(string? Version, string? DownloadLink)> GetLatestVersionForDependency(
        int modId, string? slug, ForgeCompatibleVersion? fallbackVersion, string sptVersion)
    {
        // Try to get actual mod details with all versions
        try
        {
            var modResponse = await forgeService.GetModDetailsAsync(modId);
            if (modResponse?.Success == true && modResponse.Mod?.Versions != null)
            {
                // Find latest version compatible with current SPT
                var versions = modResponse.Mod.Versions.OrderByDescending(v => v.PublishedAt).ToList();
                
                foreach (var version in versions)
                {
                    if (IsVersionCompatible(version.SptVersionConstraint, sptVersion))
                    {
                        var link = !string.IsNullOrEmpty(version.Link)
                            ? version.Link
                            : $"https://forge.sp-tarkov.com/mod/download/{modId}/{modResponse.Mod.Slug}/{version.Version}";
                        
                        logger.Debug($"Found latest compatible version for {modResponse.Mod.Name}: {version.Version}");
                        return (version.Version, link);
                    }
                }
                
                // No compatible version found, use latest anyway
                var latest = versions.FirstOrDefault();
                if (latest != null)
                {
                    var link = !string.IsNullOrEmpty(latest.Link)
                        ? latest.Link
                        : $"https://forge.sp-tarkov.com/mod/download/{modId}/{modResponse.Mod.Slug}/{latest.Version}";
                    return (latest.Version, link);
                }
            }
        }
        catch (Exception ex)
        {
            logger.Debug($"Failed to fetch mod details for dependency {modId}: {ex.Message}");
        }

        // Fallback to dependency API response
        if (fallbackVersion != null)
        {
            var link = GetValidDownloadLink(fallbackVersion.Link, modId, slug, fallbackVersion.Version);
            return (fallbackVersion.Version, link);
        }

        return (null, null);
    }

    /// <summary>
    /// Gets all types from an assembly, handling types that fail to load
    /// </summary>
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }

    /// <summary>
    /// Custom AssemblyLoadContext that can be unloaded and resolves SPT dependencies
    /// </summary>
    private sealed class CollectibleAssemblyLoadContext(string sptRoot) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var sptPath = Path.Combine(sptRoot, "SPT", $"{assemblyName.Name}.dll");
            if (File.Exists(sptPath))
                return LoadFromAssemblyPath(sptPath);
            return null;
        }
    }

    /// <summary>
    /// Custom assembly resolver for MetadataLoadContext that gracefully handles missing assemblies
    /// </summary>
    private sealed class GracefulAssemblyResolver : MetadataAssemblyResolver
    {
        private readonly PathAssemblyResolver _pathResolver;

        public GracefulAssemblyResolver(string dllPath, string sptRoot)
        {
            var assemblyPaths = new List<string> { dllPath };

            var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            if (Directory.Exists(runtimeDir))
                assemblyPaths.AddRange(Directory.GetFiles(runtimeDir, "*.dll"));

            var bepInExCoreDir = Path.Combine(sptRoot, "BepInEx", "core");
            if (Directory.Exists(bepInExCoreDir))
                assemblyPaths.AddRange(Directory.GetFiles(bepInExCoreDir, "*.dll"));

            _pathResolver = new PathAssemblyResolver(assemblyPaths.Distinct());
        }

        public override Assembly? Resolve(MetadataLoadContext context, AssemblyName assemblyName)
        {
            try
            {
                return _pathResolver.Resolve(context, assemblyName);
            }
            catch
            {
                return null; // Return null for missing assemblies
            }
        }
    }
}

#region Health Check Models

/// <summary>
/// Overall status of a mod's health
/// </summary>
public enum ModHealthStatus
{
    /// <summary>Verified on Forge and up to date</summary>
    UpToDate,
    
    /// <summary>Verified on Forge but update available</summary>
    UpdateAvailable,
    
    /// <summary>Verified but installed version is newer than Forge (dev/beta)</summary>
    NewerThanForge,
    
    /// <summary>Could not find mod on Forge</summary>
    NotOnForge,
    
    /// <summary>Incompatible with current SPT version</summary>
    Incompatible,
    
    /// <summary>Error checking this mod</summary>
    Error
}

/// <summary>
/// Information about a scanned mod (from DLL)
/// </summary>
public class ScannedMod
{
    /// <summary>GUID extracted from the DLL</summary>
    public required string Guid { get; init; }
    
    /// <summary>Version extracted from the DLL</summary>
    public string? Version { get; set; }
    
    /// <summary>Path to the DLL file</summary>
    public required string DllPath { get; init; }
    
    /// <summary>Whether this is a server mod (SPT/user/mods) or client mod (BepInEx/plugins)</summary>
    public bool IsServerMod { get; init; }
    
    /// <summary>Display name (from DLL or derived from path)</summary>
    public string? Name { get; set; }
    
    /// <summary>Author if available</summary>
    public string? Author { get; set; }
}

/// <summary>
/// Health information for a single mod
/// </summary>
public class ModHealthInfo
{
    /// <summary>The scanned mod data from DLL</summary>
    public required ScannedMod ScannedMod { get; init; }
    
    /// <summary>Display name for the mod</summary>
    public string DisplayName => ScannedMod.Name ?? Path.GetFileNameWithoutExtension(ScannedMod.DllPath);
    
    /// <summary>Overall health status</summary>
    public ModHealthStatus Status { get; set; } = ModHealthStatus.NotOnForge;
    
    /// <summary>Forge mod ID</summary>
    public int? ForgeModId { get; set; }
    
    /// <summary>Mod slug on Forge</summary>
    public string? ForgeSlug { get; set; }
    
    /// <summary>Currently installed version</summary>
    public string? InstalledVersion { get; set; }
    
    /// <summary>Latest version available on Forge</summary>
    public string? LatestVersion { get; set; }
    
    /// <summary>SPT version constraint for the latest version</summary>
    public string? LatestSptConstraint { get; set; }
    
    /// <summary>Download URL for the latest version</summary>
    public string? LatestDownloadUrl { get; set; }
    
    /// <summary>Link to mod page on Forge</summary>
    public string? ForgeUrl { get; set; }
    
    /// <summary>Error message if status is Error</summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>Whether an update is available</summary>
    public bool HasUpdate => Status == ModHealthStatus.UpdateAvailable;
    
    /// <summary>Dependencies required by this mod</summary>
    public List<DependencyInfo> Dependencies { get; set; } = [];
    
    /// <summary>Whether all dependencies are satisfied</summary>
    public bool HasDependencyIssues => Dependencies.Any(d => !d.IsSatisfied);
    
    /// <summary>Count of missing dependencies</summary>
    public int MissingDependencyCount => Dependencies.Count(d => d.Status == DependencyStatus.Missing);
}

/// <summary>
/// Status of a dependency
/// </summary>
public enum DependencyStatus
{
    /// <summary>Dependency is installed and version is compatible</summary>
    Satisfied,
    
    /// <summary>Dependency is not installed</summary>
    Missing,
    
    /// <summary>Dependency is installed but version doesn't match constraint</summary>
    VersionMismatch,
    
    /// <summary>Unknown - couldn't determine status</summary>
    Unknown
}

/// <summary>
/// Information about a mod dependency
/// </summary>
public class DependencyInfo
{
    /// <summary>Forge mod ID of the dependency</summary>
    public int ModId { get; set; }
    
    /// <summary>GUID of the dependency</summary>
    public string? Guid { get; set; }
    
    /// <summary>Name of the dependency</summary>
    public required string Name { get; set; }
    
    /// <summary>Slug for the mod page URL</summary>
    public string? Slug { get; set; }
    
    /// <summary>Installed version (null if not installed)</summary>
    public string? InstalledVersion { get; set; }
    
    /// <summary>Latest compatible version available</summary>
    public string? LatestVersion { get; set; }
    
    /// <summary>Download link for the latest compatible version</summary>
    public string? DownloadLink { get; set; }
    
    /// <summary>Status of this dependency</summary>
    public DependencyStatus Status { get; set; } = DependencyStatus.Unknown;
    
    /// <summary>Whether this dependency is satisfied</summary>
    public bool IsSatisfied => Status == DependencyStatus.Satisfied;
    
    /// <summary>URL to the mod page on Forge</summary>
    public string? ForgeUrl => !string.IsNullOrEmpty(Slug) ? $"https://forge.sp-tarkov.com/mod/{ModId}/{Slug}" : null;
}

/// <summary>
/// Result of a full health check operation
/// </summary>
public class HealthCheckResult
{
    /// <summary>When the check was performed</summary>
    public DateTime CheckedAt { get; init; } = DateTime.UtcNow;
    
    /// <summary>Current SPT version on the server</summary>
    public string? SptVersion { get; set; }
    
    /// <summary>Latest SPT version available</summary>
    public string? LatestSptVersion { get; set; }
    
    /// <summary>Whether SPT itself has an update</summary>
    public bool SptUpdateAvailable => 
        !string.IsNullOrEmpty(SptVersion) && 
        !string.IsNullOrEmpty(LatestSptVersion) && 
        SptVersion != LatestSptVersion;
    
    /// <summary>All mods found</summary>
    public List<ModHealthInfo> Mods { get; init; } = [];
    
    /// <summary>Total mods found</summary>
    public int TotalMods => Mods.Count;
    
    /// <summary>Mods that are up to date</summary>
    public int UpToDateCount => Mods.Count(m => m.Status == ModHealthStatus.UpToDate);
    
    /// <summary>Mods with updates available</summary>
    public int UpdatesAvailableCount => Mods.Count(m => m.Status == ModHealthStatus.UpdateAvailable);
    
    /// <summary>Mods not found on Forge</summary>
    public int NotOnForgeCount => Mods.Count(m => m.Status == ModHealthStatus.NotOnForge);
    
    /// <summary>Mods with errors</summary>
    public int ErrorCount => Mods.Count(m => m.Status == ModHealthStatus.Error);
    
    /// <summary>Mods incompatible with current SPT</summary>
    public int IncompatibleCount => Mods.Count(m => m.Status == ModHealthStatus.Incompatible);
    
    /// <summary>Mods with dependency issues</summary>
    public int DependencyIssuesCount => Mods.Count(m => m.HasDependencyIssues);
    
    /// <summary>Total missing dependencies across all mods</summary>
    public int TotalMissingDependencies => Mods.Sum(m => m.MissingDependencyCount);
    
    /// <summary>Overall error message if the check failed</summary>
    public string? Error { get; set; }
    
    /// <summary>Whether the check completed successfully</summary>
    public bool Success => string.IsNullOrEmpty(Error);
}

#endregion
