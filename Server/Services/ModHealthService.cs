using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using ModGod.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace ModGod.Services;

/// <summary>
/// Service for checking the health and update status of installed mods.
/// Uses a scan-first approach: scans all DLLs, then correlates with tracked mods.
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
    /// Run a health check on all installed mods
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

        // Step 2: Build a map of DLL paths to scanned mods (normalized paths for comparison)
        var dllPathToMod = scannedMods.ToDictionary(
            m => NormalizePath(m.DllPath),
            m => m,
            StringComparer.OrdinalIgnoreCase
        );

        // Step 3: Correlate with tracked ModEntries
        var trackedGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modEntries = configService.Config.ModList
            .Where(m => m.Status == ModStatus.Installed)
            .ToList();

        foreach (var modEntry in modEntries)
        {
            // Find DLLs from this mod's installedFiles
            var matchedMod = FindMatchingScannedMod(modEntry, dllPathToMod);
            
            if (matchedMod != null)
            {
                trackedGuids.Add(matchedMod.Guid);
                var healthInfo = new ModHealthInfo
                {
                    TrackedMod = modEntry,
                    ScannedMod = matchedMod,
                    InstalledVersion = matchedMod.Version
                };
                result.Mods.Add(healthInfo);
            }
            // If no DLL found, the mod might not have DLLs (config-only mod) - skip it
        }

        // Step 4: Add untracked mods (DLLs not associated with any ModEntry)
        foreach (var scannedMod in scannedMods)
        {
            if (!trackedGuids.Contains(scannedMod.Guid))
            {
                var healthInfo = new ModHealthInfo
                {
                    TrackedMod = null,
                    ScannedMod = scannedMod,
                    InstalledVersion = scannedMod.Version
                };
                result.Mods.Add(healthInfo);
            }
        }

        // Step 5: Check each mod against Forge
        logger.Info($"Checking {result.Mods.Count} mods against Forge...");
        foreach (var healthInfo in result.Mods)
        {
            await CheckModHealthAsync(healthInfo, result.SptVersion);
        }

        // Step 6: Check dependencies for mods that were found on Forge
        logger.Info("Checking mod dependencies...");
        await CheckAllDependenciesAsync(result.Mods, scannedMods);

        // Sort: tracked first, then by name
        result.Mods.Sort((a, b) =>
        {
            // Tracked mods first
            if (a.IsTracked != b.IsTracked)
                return a.IsTracked ? -1 : 1;
            // Then by display name
            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });

        var depIssues = result.DependencyIssuesCount;
        var depMessage = depIssues > 0 ? $", {depIssues} with dependency issues" : "";
        logger.Success($"Health check complete: {result.UpToDateCount} up-to-date, {result.UpdatesAvailableCount} updates, {result.NotOnForgeCount} not on Forge, {result.UntrackedCount} untracked{depMessage}");

        _cachedResult = result;
        _cacheTime = DateTime.UtcNow;
        return result;
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
    /// Find a scanned mod that matches a ModEntry based on installedFiles
    /// </summary>
    private ScannedMod? FindMatchingScannedMod(ModEntry modEntry, Dictionary<string, ScannedMod> dllPathToMod)
    {
        // Check installedFiles for DLLs
        foreach (var file in modEntry.InstalledFiles)
        {
            if (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                continue;

            var fullPath = Path.Combine(configService.SptRoot, file.Replace('/', Path.DirectorySeparatorChar));
            var normalizedPath = NormalizePath(fullPath);
            
            if (dllPathToMod.TryGetValue(normalizedPath, out var scannedMod))
            {
                return scannedMod;
            }
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
                healthInfo.LatestDownloadUrl = ForgeService.BuildDownloadUrl(forgeMod.Id, forgeMod.Slug, latestCompatible.Version);
            }
            else if (latestOverall != null)
            {
                // No compatible version found, show latest anyway
                healthInfo.LatestVersion = latestOverall.Version;
                healthInfo.LatestSptConstraint = latestOverall.SptVersionConstraint;
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
    /// Normalize a file path for comparison
    /// </summary>
    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Check dependencies for all mods that have a Forge mod ID.
    /// Calls the API for each mod individually (as per Forge API design).
    /// </summary>
    private async Task CheckAllDependenciesAsync(List<ModHealthInfo> mods, List<ScannedMod> scannedMods)
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

        logger.Info($"Checking dependencies for {modsToCheck.Count} mods (one API call per mod)...");

        var modsWithDeps = 0;
        var totalDeps = 0;

        // Check each mod individually (Forge API returns dependency tree per mod)
        foreach (var healthInfo in modsToCheck)
        {
            var modId = healthInfo.ForgeModId!.Value;
            var version = healthInfo.InstalledVersion!;
            
            // Extra logging for ItemInfo
            var isItemInfo = healthInfo.DisplayName.Contains("ItemInfo", StringComparison.OrdinalIgnoreCase);
            if (isItemInfo)
            {
                logger.Info($"*** Checking ItemInfo: ID={modId}, Version={version}");
            }

            var response = await forgeService.GetModDependenciesAsync([(modId, version)]);
            
            if (response == null || !response.Success)
            {
                continue;
            }
            
            // The API returns the DEPENDENCIES as the mods list (not the requesting mod with dependencies array)
            // So if we query ItemInfo, the API returns [ColorConverterAPI] directly
            // We need to treat ALL returned mods as dependencies of the mod we queried
            
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
                var depInfo = new DependencyInfo
                {
                    ModId = dep.Id,
                    Guid = dep.Guid,
                    Name = dep.Name,
                    Slug = dep.Slug,
                    // LatestCompatibleVersion contains the recommended version info
                    LatestVersion = dep.LatestCompatibleVersion?.Version,
                    DownloadLink = dep.LatestCompatibleVersion?.Link
                };

                // Check if this dependency is installed by looking up its GUID
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
    /// Check if an installed version satisfies a version constraint
    /// </summary>
    private static bool IsVersionSatisfied(string? installed, string? constraint)
    {
        if (string.IsNullOrEmpty(installed) || string.IsNullOrEmpty(constraint))
            return true;

        try
        {
            var range = new SemanticVersioning.Range(constraint);
            var version = new SemanticVersioning.Version(CleanVersion(installed));
            return range.IsSatisfied(version);
        }
        catch
        {
            // Can't parse, assume satisfied
            return true;
        }
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
