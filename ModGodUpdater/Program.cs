using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using ModGod.Updater.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using Spectre.Console;

namespace ModGod.Updater;

class Program
{
    // Internal data folder for config files (must match Client/Server folder name)
    private static readonly string InternalDataFolderName = "ModGodData";
    private static readonly string TempDownloadPath = Path.Combine(Path.GetTempPath(), "ModGod");
    private static readonly string LogFileName = "ModGodUpdater.log";

    private static readonly string UpdaterVersion =
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Cache for compiled glob pattern regexes
    private static readonly Dictionary<string, System.Text.RegularExpressions.Regex?> _globCache = new();

    private static ClientConfig _clientConfig = new();
    private static List<DownloadedMod> _modsDownloaded = new();
    private static string _sptRoot = string.Empty;
    private static string _internalDataPath = string.Empty;
    private static StreamWriter? _logWriter;
    private static FileManifest? _cachedManifest;

    static async Task Main(string[] args)
    {
        Console.Title = "ModGod Updater";

        // Initialize logging (will be in current directory until we know SPT root)
        InitializeLogging(Directory.GetCurrentDirectory());

        try
        {
            await RunAsync();
        }
        catch (Exception ex)
        {
            Log($"FATAL ERROR: {ex}");
            AnsiConsole.MarkupLine($"[red]Fatal error:[/] {EscapeMarkup(ex.Message)}");
            AnsiConsole.MarkupLine("[grey]See ModGodUpdater.log for details.[/]");
        }
        finally
        {
            _logWriter?.Dispose();
        }

        WaitForExit();
    }

    static void InitializeLogging(string directory)
    {
        try
        {
            var logPath = Path.Combine(directory, LogFileName);
            _logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
            Log($"ModGod Updater started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Log($"Working directory: {directory}");
        }
        catch
        {
            // Logging failed, continue without it
        }
    }

    static void Log(string message)
    {
        try
        {
            _logWriter?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
        catch
        {
            // Ignore logging errors
        }
    }

    /// <summary>
    /// Escape text for Spectre.Console markup (square brackets need to be doubled)
    /// </summary>
    static string EscapeMarkup(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Replace("[", "[[").Replace("]", "]]");
    }

    static async Task RunAsync()
    {
        AnsiConsole.Write(
            new FigletText("ModGodUpdater")
                .Color(Color.Cyan1));

        AnsiConsole.MarkupLine("[grey]SPT Mod Synchronization Tool[/]");
        AnsiConsole.WriteLine();

        // This exe should be in SPT root directly
        var currentDir = Directory.GetCurrentDirectory();

        if (IsSptDirectory(currentDir))
        {
            _sptRoot = currentDir;
        }
        else
        {
            Log("ERROR: Not in SPT directory");
            AnsiConsole.MarkupLine("[red]Error:[/] ModGodUpdater.exe must be in your SPT root directory.");
            AnsiConsole.MarkupLine("[grey]Expected structure:[/]");
            AnsiConsole.MarkupLine("[grey]  SPT/[/]");
            AnsiConsole.MarkupLine("[grey]  ├── BepInEx/[/]");
            AnsiConsole.MarkupLine("[grey]  ├── SPT/[/]");
            AnsiConsole.MarkupLine("[grey]  ├── ModGodData/[/]");
            AnsiConsole.MarkupLine("[grey]  └── [cyan]ModGodUpdater.exe[/][/]");
            return;
        }

        _internalDataPath = Path.Combine(_sptRoot, InternalDataFolderName);
        Directory.CreateDirectory(_internalDataPath);

        // Re-initialize logging in the SPT root directory
        _logWriter?.Dispose();
        InitializeLogging(_sptRoot);

        Log($"SPT Root: {_sptRoot}");
        AnsiConsole.MarkupLine($"[green]✓[/] SPT Root: [cyan]{_sptRoot}[/]");
        AnsiConsole.WriteLine();

        // Load or create config
        await LoadOrCreateConfigAsync();
        await LoadModsDownloadedAsync();

        // Early version check - verify updater version matches server before proceeding
        if (!await CheckVersionCompatibilityAsync())
        {
            return; // Version mismatch - user needs to update
        }

        // Check for headless mode and show appropriate banner
        if (_clientConfig.Headless)
        {
            AnsiConsole.Write(
                new Panel(
                        new Markup("[bold cyan] HEADLESS MODE[/]\n\n" +
                                   "[grey]This client is configured as a headless raid-hosting instance.\n" +
                                   "Only files explicitly configured for headless syncing will be downloaded.\n" +
                                   "Mod downloads will be skipped.[/]"))
                    .Header("[cyan]Headless Client[/]")
                    .BorderColor(Color.Cyan1)
                    .Padding(1, 1, 1, 1));
            AnsiConsole.WriteLine();

            Log("Running in HEADLESS mode");

            // Confirmation prompt
            var confirm = AnsiConsole.Confirm("[yellow]Continue in headless mode?[/]", true);
            if (!confirm)
            {
                AnsiConsole.MarkupLine("[grey]Operation cancelled. Edit ModGodClient.json to change mode.[/]");
                Log("User cancelled headless mode operation");
                return;
            }

            AnsiConsole.WriteLine();
        }
        else
        {
            AnsiConsole.Write(
                new Panel(
                        new Markup("[bold green] STANDARD MODE[/]\n\n" +
                                   "[grey]This client will download and sync all configured mods\n" +
                                   "and files from the ModGod server.[/]"))
                    .Header("[green]Standard Client[/]")
                    .BorderColor(Color.Green)
                    .Padding(1, 1, 1, 1));
            AnsiConsole.WriteLine();

            Log("Running in STANDARD mode");

            // Confirmation prompt
            var confirm = AnsiConsole.Confirm("[yellow]Continue with mod sync?[/]", true);
            if (!confirm)
            {
                AnsiConsole.MarkupLine("[grey]Operation cancelled.[/]");
                Log("User cancelled standard mode operation");
                return;
            }

            AnsiConsole.WriteLine();
        }

        // Fetch server config
        Log("Fetching server config...");
        var serverConfig = await FetchServerConfigAsync();
        if (serverConfig == null)
        {
            Log("ERROR: Failed to fetch server config");
            return;
        }

        Log($"Found {serverConfig.ModList.Count} mod(s) on server");
        AnsiConsole.MarkupLine($"[green]✓[/] Found [cyan]{serverConfig.ModList.Count}[/] mod(s) on server");
        AnsiConsole.WriteLine();

        // Manifest already cached during version check, use it for exclusions and sync roots
        var exclusions = BuildExclusionSet(_cachedManifest?.SyncExclusions);
        var syncRoots = BuildSyncRootsSet(_cachedManifest?.SyncRoots);
        if (exclusions.Count > 0)
        {
            Log($"Loaded {exclusions.Count} exclusion pattern(s)");
        }
        if (syncRoots.Count > 0)
        {
            Log($"Loaded {syncRoots.Count} sync root(s): {string.Join(", ", syncRoots)}");
        }

        // Process mods (skip for headless clients)
        if (_clientConfig.Headless)
        {
            AnsiConsole.MarkupLine("[cyan]ℹ️[/] Skipping mod downloads (headless mode)");
            Log("Skipping mod downloads (headless mode)");
        }
        else
        {
            Log("Processing mods...");
            await ProcessModsAsync(serverConfig, exclusions, syncRoots);
        }

        AnsiConsole.WriteLine();

        // File verification and sync
        Log("Starting file verification...");
        await SyncFilesAsync();

        AnsiConsole.WriteLine();
        Log("Sync complete!");
        AnsiConsole.MarkupLine("[green]Sync complete![/]");
    }

    static bool IsSptDirectory(string path)
    {
        var bepInExPath = Path.Combine(path, "BepInEx");
        var sptPath = Path.Combine(path, "SPT");

        return Directory.Exists(bepInExPath) || Directory.Exists(sptPath);
    }

    static string GetConfigPath() => Path.Combine(_internalDataPath, "ModGodClient.json");
    static string GetModsDownloadedPath() => Path.Combine(_internalDataPath, "modsDownloaded.json");

    static async Task LoadOrCreateConfigAsync()
    {
        var configPath = GetConfigPath();

        if (File.Exists(configPath))
        {
            var json = await File.ReadAllTextAsync(configPath);
            _clientConfig = JsonSerializer.Deserialize<ClientConfig>(json, JsonOptions) ?? new ClientConfig();
        }

        if (string.IsNullOrWhiteSpace(_clientConfig.ServerUrl))
        {
            AnsiConsole.MarkupLine("[yellow]First time setup - please enter the server URL[/]");

            _clientConfig.ServerUrl = AnsiConsole.Ask<string>(
                "Enter server URL (e.g., [cyan]https://192.168.1.100:6969[/]):");

            // Ensure URL doesn't have trailing slash
            _clientConfig.ServerUrl = _clientConfig.ServerUrl.TrimEnd('/');

            await SaveConfigAsync();
            AnsiConsole.MarkupLine($"[green]✓[/] Server URL saved to [cyan]{configPath}[/]");
            AnsiConsole.WriteLine();
        }
        else
        {
            // Always ensure no trailing slash (in case config was edited manually)
            _clientConfig.ServerUrl = _clientConfig.ServerUrl.TrimEnd('/');
            AnsiConsole.MarkupLine($"[green]✓[/] Server: [cyan]{_clientConfig.ServerUrl}[/]");
        }
    }

    static async Task SaveConfigAsync()
    {
        var json = JsonSerializer.Serialize(_clientConfig, JsonOptions);
        await File.WriteAllTextAsync(GetConfigPath(), json);
    }

    static async Task LoadModsDownloadedAsync()
    {
        var modsPath = GetModsDownloadedPath();
        if (File.Exists(modsPath))
        {
            var json = await File.ReadAllTextAsync(modsPath);
            _modsDownloaded = JsonSerializer.Deserialize<List<DownloadedMod>>(json, JsonOptions) ?? new();
        }
    }

    /// <summary>
    /// Check version compatibility with server before proceeding with any downloads.
    /// Returns true if compatible, false if version mismatch detected.
    /// </summary>
    static async Task<bool> CheckVersionCompatibilityAsync()
    {
        return await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync("Checking server compatibility...", async ctx =>
            {
                try
                {
                    Log("Checking version compatibility with server...");

                    // Fetch manifest to get server version
                    var manifest = await FetchManifestAsync(_clientConfig.Headless);
                    if (manifest == null)
                    {
                        Log("WARNING: Could not fetch manifest for version check, proceeding anyway");
                        return true; // Allow to continue if we can't check
                    }

                    // Cache the manifest for later use
                    _cachedManifest = manifest;

                    if (!string.IsNullOrEmpty(manifest.ModGodVersion) && manifest.ModGodVersion != UpdaterVersion)
                    {
                        Log($"VERSION MISMATCH: Server={manifest.ModGodVersion}, Updater={UpdaterVersion}");

                        AnsiConsole.WriteLine();
                        AnsiConsole.Write(
                            new Panel(
                                new Markup($"[red bold]VERSION MISMATCH[/]\n\n" +
                                           $"Server version: [cyan]{EscapeMarkup(manifest.ModGodVersion)}[/]\n" +
                                           $"Updater version: [cyan]{EscapeMarkup(UpdaterVersion)}[/]\n\n" +
                                           "[yellow]Please download and install the correct version of ModGod.[/]"))
                                .Header("[red]Update Required[/]")
                                .BorderColor(Color.Red)
                                .Padding(1, 1, 1, 1));
                        AnsiConsole.WriteLine();

                        return false;
                    }

                    Log($"Version check passed: Server={manifest.ModGodVersion ?? "unknown"}, Updater={UpdaterVersion}");
                    AnsiConsole.MarkupLine($"[green]✓[/] Server compatible (v{EscapeMarkup(manifest.ModGodVersion ?? UpdaterVersion)})");

                    return true;
                }
                catch (Exception ex)
                {
                    Log($"Version check failed: {ex.Message}");
                    AnsiConsole.MarkupLine($"[yellow]⚠[/] Could not verify server version: {EscapeMarkup(ex.Message)}");
                    return true; // Allow to continue on error
                }
            });
    }

    static async Task SaveModsDownloadedAsync()
    {
        var json = JsonSerializer.Serialize(_modsDownloaded, JsonOptions);
        await File.WriteAllTextAsync(GetModsDownloadedPath(), json);
    }

    // Create HttpClient that accepts self-signed certificates (SPT 4.0 uses HTTPS with self-signed cert)
    static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (message, cert, chain, sslPolicyErrors) => true
        };
        var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ModGodUpdater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    static async Task<ServerConfig?> FetchServerConfigAsync()
    {
        // Pass headless parameter so server can filter mods appropriately
        var url = _clientConfig.Headless
            ? $"{_clientConfig.ServerUrl}/modgod/api/config?headless=true"
            : $"{_clientConfig.ServerUrl}/modgod/api/config";

        return await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync("Fetching server mod list...", async ctx =>
            {
                try
                {
                    using var client = CreateHttpClient();

                    var response = await client.GetAsync(url);

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        AnsiConsole.MarkupLine(
                            $"[red]Server returned {(int)response.StatusCode} ({response.StatusCode})[/]");
                        if (!string.IsNullOrWhiteSpace(errorContent))
                        {
                            AnsiConsole.MarkupLine(
                                $"[grey]Response: {EscapeMarkup(errorContent.Substring(0, Math.Min(200, errorContent.Length)))}[/]");
                        }

                        AnsiConsole.MarkupLine($"[grey]URL: {EscapeMarkup(url)}[/]");
                        return null;
                    }

                    var json = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<ServerConfig>(json, JsonOptions);
                }
                catch (HttpRequestException ex)
                {
                    AnsiConsole.MarkupLine($"[red]Failed to connect to server:[/] {EscapeMarkup(ex.Message)}");
                    AnsiConsole.MarkupLine($"[grey]URL: {EscapeMarkup(url)}[/]");
                    return null;
                }
            });
    }

    static async Task ProcessModsAsync(ServerConfig serverConfig, HashSet<string> exclusions, HashSet<string> syncRoots)
    {
        var requiredMods = serverConfig.ModList.Where(m => !m.Optional).ToList();
        var optionalMods = serverConfig.ModList.Where(m => m.Optional).ToList();

        if (requiredMods.Any())
        {
            AnsiConsole.MarkupLine("[bold]Required Mods[/]");
            AnsiConsole.WriteLine();

            foreach (var mod in requiredMods)
            {
                await ProcessModAsync(mod, isOptional: false, exclusions, syncRoots);
            }
        }

        if (optionalMods.Any())
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Optional Mods[/]");
            AnsiConsole.WriteLine();

            var optionalChoices = optionalMods.Select(m =>
            {
                var downloaded = _modsDownloaded.Find(d => d.DownloadUrl == m.DownloadUrl);
                var isTrackedAsInstalled = downloaded?.OptIn == true;
                var allFilesExist = CheckModFilesExist(m.ModName);
                var isInstalled = isTrackedAsInstalled || allFilesExist;
                var status = isInstalled ? " [green](installed)[/]" : " [grey](not installed)[/]";
                return $"{EscapeMarkup(m.ModName)}{status}";
            }).ToList();

            var preSelected = optionalChoices.Where(c => c.Contains("[green]")).ToList();

            var prompt = new MultiSelectionPrompt<string>()
                .Title("Select optional mods to install:")
                .NotRequired()
                .PageSize(10)
                .InstructionsText("[grey](Press [cyan]<space>[/] to toggle, [cyan]<enter>[/] to accept)[/]")
                .AddChoices(optionalChoices);

            foreach (var item in preSelected)
            {
                prompt.Select(item);
            }

            var selectedNames = AnsiConsole.Prompt(prompt);

            AnsiConsole.WriteLine();

            foreach (var mod in optionalMods)
            {
                var index = optionalMods.IndexOf(mod);
                var choiceName = optionalChoices[index];
                var isSelected = selectedNames.Any(s => s == choiceName);

                await ProcessModAsync(mod, isOptional: true, exclusions, syncRoots, optIn: isSelected);
            }
        }

        await SaveModsDownloadedAsync();
    }

    static async Task ProcessModAsync(ModEntry mod, bool isOptional, HashSet<string> exclusions, HashSet<string> syncRoots, bool optIn = true)
    {
        // Skip protected/baked-in mods (e.g., ModGod itself) - they don't need downloading
        if (mod.IsProtected || string.IsNullOrWhiteSpace(mod.DownloadUrl))
        {
            AnsiConsole.MarkupLine($"  [green]✓[/] {EscapeMarkup(mod.ModName)} [grey](installed)[/]");
            return;
        }

        var downloaded = _modsDownloaded.Find(d => d.DownloadUrl == mod.DownloadUrl);
        var needsUpdate = downloaded == null || downloaded.LastUpdated != mod.LastUpdated;

        // For optional mods that aren't opted in, remove if installed (tracked or detected on disk)
        if (isOptional && !optIn)
        {
            var isTrackedAsInstalled = downloaded?.OptIn == true;
            var filesExistOnDisk = CheckModFilesExist(mod.ModName);
            
            if (isTrackedAsInstalled || filesExistOnDisk)
            {
                // User is opting out - remove files (whether tracked or just present on disk)
                await RemoveModFilesAsync(mod.ModName);
                if (downloaded != null)
                {
                    _modsDownloaded.Remove(downloaded);
                }
                AnsiConsole.MarkupLine($"  [red]✗[/] {EscapeMarkup(mod.ModName)} [grey](removed)[/]");
            }
            else
            {
                // Not installed at all, just skip
                if (downloaded != null)
                {
                    _modsDownloaded.Remove(downloaded);
                }
                AnsiConsole.MarkupLine($"  [grey]○[/] {EscapeMarkup(mod.ModName)} [grey](skipped)[/]");
            }
            return;
        }

        if (!needsUpdate)
        {
            AnsiConsole.MarkupLine($"  [green]✓[/] {EscapeMarkup(mod.ModName)} [grey](up to date)[/]");
            return;
        }

        var downloadSuccess = false;

        try
        {
            // Download mod with progress
            using var client = CreateHttpClient();
            client.Timeout = TimeSpan.FromMinutes(30); // Long timeout for large mods

            // Get headers first to determine content length
            using var response = await client.GetAsync(mod.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            var tempExtractPath = Path.Combine(TempDownloadPath, Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempExtractPath);
            var archivePath = Path.Combine(tempExtractPath, "mod.archive");

            // Download with progress bar
            await AnsiConsole.Progress()
                .AutoClear(true)
                .HideCompleted(true)
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new DownloadedColumn(),
                    new SpinnerColumn()
                })
                .StartAsync(async ctx =>
                {
                    var downloadTask = ctx.AddTask($"[cyan]{EscapeMarkup(mod.ModName)}[/]", maxValue: totalBytes > 0 ? totalBytes : 100);

                    await using var contentStream = await response.Content.ReadAsStreamAsync();
                    await using var fileStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                        totalRead += bytesRead;

                        if (totalBytes > 0)
                        {
                            downloadTask.Value = totalRead;
                        }
                        else
                        {
                            // Unknown size - just pulse the progress
                            downloadTask.IsIndeterminate = true;
                        }
                    }

                    downloadTask.Value = downloadTask.MaxValue;
                });

            // Extract and install with progress bar
            await AnsiConsole.Progress()
                .AutoClear(true)
                .HideCompleted(true)
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn()
                })
                .StartAsync(async ctx =>
                {
                    var extractTask = ctx.AddTask($"[yellow]Extracting {EscapeMarkup(mod.ModName)}[/]", maxValue: 100);

                    // Check if this is a 7z archive and if we have native 7z available
                    var is7z = Is7zArchive(archivePath);
                    var sevenZipPath = is7z ? Find7ZipExecutable() : null;

                    if (is7z && sevenZipPath != null)
                    {
                        // Use native 7z for fast extraction
                        Log($"Using native 7-Zip for {mod.ModName}");
                        var success = await ExtractWith7ZipAsync(archivePath, tempExtractPath, sevenZipPath, extractTask);
                        if (!success)
                        {
                            throw new Exception("Native 7z extraction failed");
                        }
                    }
                    else
                    {
                        // Fall back to SharpCompress
                        if (is7z)
                        {
                            Log($"WARNING: Using SharpCompress for 7z archive (slow) - install 7-Zip for faster extraction");
                        }

                        using (var archive = ArchiveFactory.Open(archivePath))
                        {
                            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                            extractTask.MaxValue = entries.Count;

                            foreach (var entry in entries)
                            {
                                entry.WriteToDirectory(tempExtractPath, new ExtractionOptions
                                {
                                    ExtractFullPath = true,
                                    Overwrite = true
                                });
                                extractTask.Increment(1);
                            }
                        }
                    }

                    File.Delete(archivePath);

                    var filesToInstall = new List<(string Source, string Target)>();
                    var skippedExcluded = 0;
                    var skippedNotInSyncPath = 0;

                    foreach (var installPath in mod.InstallPaths)
                    {
                        var sourcePath = Path.Combine(tempExtractPath, installPath[0]);
                        var targetRel = installPath[1].Replace("<SPT_ROOT>", "").TrimStart('/', '\\');
                        var targetPath = Path.Combine(_sptRoot, targetRel);

                        if (Directory.Exists(sourcePath))
                        {
                            foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
                            {
                                var relativePath = Path.GetRelativePath(sourcePath, file);
                                var destFile = Path.Combine(targetPath, relativePath);
                                var destRelative = Path.GetRelativePath(_sptRoot, destFile).Replace('\\', '/');

                                // Check if path is within a registered sync path
                                if (!IsInSyncPath(destRelative, syncRoots))
                                {
                                    Log($"Skipping (not in sync path): {destRelative}");
                                    skippedNotInSyncPath++;
                                    continue;
                                }

                                // Check if path is excluded
                                if (IsExcluded(destRelative, exclusions))
                                {
                                    Log($"Skipping excluded: {destRelative}");
                                    skippedExcluded++;
                                    continue;
                                }

                                filesToInstall.Add((file, destFile));
                            }
                        }
                        else if (File.Exists(sourcePath))
                        {
                            var fileName = Path.GetFileName(sourcePath);
                            var destFile = Path.Combine(targetPath, fileName);
                            var destRelative = Path.GetRelativePath(_sptRoot, destFile).Replace('\\', '/');

                            // Check if path is within a registered sync path
                            if (!IsInSyncPath(destRelative, syncRoots))
                            {
                                Log($"Skipping (not in sync path): {destRelative}");
                                skippedNotInSyncPath++;
                                continue;
                            }

                            // Check if path is excluded
                            if (IsExcluded(destRelative, exclusions))
                            {
                                Log($"Skipping excluded: {destRelative}");
                                skippedExcluded++;
                                continue;
                            }

                            filesToInstall.Add((sourcePath, destFile));
                        }
                    }

                    if (skippedExcluded > 0 || skippedNotInSyncPath > 0)
                    {
                        Log($"Skipped {skippedExcluded} excluded + {skippedNotInSyncPath} outside sync paths for {mod.ModName}");
                    }

                    if (filesToInstall.Count > 0)
                    {
                        var installTask = ctx.AddTask($"[green]Installing {EscapeMarkup(mod.ModName)}[/]", maxValue: filesToInstall.Count);

                        foreach (var (source, target) in filesToInstall)
                        {
                            var dir = Path.GetDirectoryName(target);
                            if (!string.IsNullOrEmpty(dir))
                            {
                                Directory.CreateDirectory(dir);
                            }
                            File.Copy(source, target, overwrite: true);
                            installTask.Increment(1);
                        }
                    }

                    // Clean up temp directory
                    Directory.Delete(tempExtractPath, true);

                    await Task.CompletedTask;
                });

            // Update downloaded mods list
            if (downloaded != null)
            {
                downloaded.LastUpdated = mod.LastUpdated;
                downloaded.OptIn = optIn;
            }
            else
            {
                _modsDownloaded.Add(new DownloadedMod
                {
                    ModName = mod.ModName,
                    DownloadUrl = mod.DownloadUrl,
                    LastUpdated = mod.LastUpdated,
                    OptIn = optIn
                });
            }

            downloadSuccess = true;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [red]✗[/] {EscapeMarkup(mod.ModName)} - Failed: {EscapeMarkup(ex.Message)}");
            return;
        }

        if (downloadSuccess)
        {
            AnsiConsole.MarkupLine($"  [green]✓[/] {EscapeMarkup(mod.ModName)} [cyan](installed)[/]");
        }
    }

    static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectory(dir, destSubDir);
        }
    }

    static async Task SyncFilesAsync()
    {
        Log("Starting file sync...");

        if (_clientConfig.Headless)
        {
            AnsiConsole.MarkupLine("[bold cyan]Headless File Sync[/]");
            AnsiConsole.MarkupLine("[grey]Syncing only headless-specific files...[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[bold]File Verification[/]");
        }

        AnsiConsole.WriteLine();

        var manifest = _cachedManifest;
        if (manifest == null)
        {
            Log($"Fetching manifest (headless={_clientConfig.Headless})...");
            manifest = await FetchManifestAsync(_clientConfig.Headless);
            if (manifest == null)
            {
                Log("WARNING: Could not fetch manifest");
                AnsiConsole.MarkupLine("[yellow]Could not fetch file manifest. Skipping file sync.[/]");
                return;
            }
            Log($"Manifest received: {manifest.Files.Count} files");
        }
        else
        {
            Log($"Using cached manifest: {manifest.Files.Count} files");
        }

        if (_clientConfig.Headless)
        {
            AnsiConsole.MarkupLine(
                $"[green]✓[/] Headless Manifest: [cyan]{manifest.Files.Count}[/] files configured for sync");
            if (manifest.Files.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No files configured for headless syncing. Configure in ModGod UI.[/]");
                return;
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Manifest: [cyan]{manifest.Files.Count}[/] files from server");
        }

        // Ensure all sync root directories exist (even if empty on server)
        EnsureSyncDirectoriesExist(manifest.SyncRoots);

        var exclusions = BuildExclusionSet(manifest.SyncExclusions);

        // Find issues
        var issues = new List<FileSyncIssue>();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync("Verifying files...", async ctx =>
            {
                // Check for missing/modified files
                foreach (var kvp in manifest.Files)
                {
                    var relativePath = kvp.Key;
                    var entry = kvp.Value;

                    // Skip files from optional mods the user hasn't opted into
                    if (!entry.Required)
                    {
                        var modDownloaded = _modsDownloaded.Find(d => d.ModName == entry.ModName);
                        if (modDownloaded == null || !modDownloaded.OptIn)
                        {
                            continue; // User hasn't opted into this optional mod
                        }
                    }

                    var fullPath = Path.Combine(_sptRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

                    if (!File.Exists(fullPath))
                    {
                        issues.Add(new FileSyncIssue
                        {
                            Action = FileSyncAction.Download,
                            RelativePath = relativePath,
                            ModName = entry.ModName,
                            Required = entry.Required,
                            ServerSize = entry.Size
                        });
                        continue;
                    }

                    // Check hash
                    try
                    {
                        var localHash = ComputeFileHash(fullPath);
                        if (!localHash.Equals(entry.Hash, StringComparison.OrdinalIgnoreCase))
                        {
                            issues.Add(new FileSyncIssue
                            {
                                Action = FileSyncAction.Update,
                                RelativePath = relativePath,
                                ModName = entry.ModName,
                                Required = entry.Required,
                                ServerSize = entry.Size
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"ERROR hashing file {relativePath}: {ex.Message}");
                        // Treat as modified if we can't hash it
                        issues.Add(new FileSyncIssue
                        {
                            Action = FileSyncAction.Update,
                            RelativePath = relativePath,
                            ModName = entry.ModName,
                            Required = entry.Required,
                            ServerSize = entry.Size
                        });
                    }
                }

                // Scan for extra files (skip for headless mode - only sync specific files, don't delete extras)
                if (!_clientConfig.Headless)
                {
                    // Use syncRoots from manifest if available, otherwise fall back to defaults
                    var syncDirs = manifest.SyncRoots?.Count > 0
                        ? manifest.SyncRoots.ToArray()
                        : new[] { "BepInEx/plugins", "SPT/user/mods" };

                    Log($"Scanning {syncDirs.Length} sync root(s) for extra files: {string.Join(", ", syncDirs)}");

                    foreach (var syncDir in syncDirs)
                    {
                        var fullDir = Path.Combine(_sptRoot, syncDir.Replace('/', Path.DirectorySeparatorChar));
                        if (!Directory.Exists(fullDir)) continue;

                        foreach (var file in Directory.GetFiles(fullDir, "*", SearchOption.AllDirectories))
                        {
                            var relativePath = Path.GetRelativePath(_sptRoot, file).Replace('\\', '/');

                            // Skip if in manifest
                            if (manifest.Files.ContainsKey(relativePath)) continue;

                            // Skip if excluded
                            if (IsExcluded(relativePath, exclusions)) continue;

                            issues.Add(new FileSyncIssue
                            {
                                Action = FileSyncAction.Delete,
                                RelativePath = relativePath,
                                ModName = "Unknown",
                                Required = false
                            });
                        }
                    }
                }

                await Task.CompletedTask;
            });

        // Track total failures for final summary
        var totalFailures = 0;
        var totalSuccesses = 0;

        if (issues.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]✓[/] All files verified - no issues found!");
            return;
        }

        // Group issues by type
        var missing = issues.Where(i => i.Action == FileSyncAction.Download).ToList();
        var modified = issues.Where(i => i.Action == FileSyncAction.Update).ToList();
        var extra = issues.Where(i => i.Action == FileSyncAction.Delete).ToList();

        Log($"Issues found: {missing.Count} missing, {modified.Count} modified, {extra.Count} extra");

        AnsiConsole.MarkupLine($"[yellow]Found {issues.Count} issue(s):[/]");
        if (missing.Any()) AnsiConsole.MarkupLine($"  [red]• {missing.Count} missing file(s)[/]");
        if (modified.Any()) AnsiConsole.MarkupLine($"  [yellow]• {modified.Count} modified file(s)[/]");
        if (extra.Any()) AnsiConsole.MarkupLine($"  [blue]• {extra.Count} extra file(s)[/]");
        AnsiConsole.WriteLine();

        // Handle missing files
        if (missing.Any())
        {
            AnsiConsole.MarkupLine("[bold red]Missing Files[/]");

            // List missing files grouped by mod
            var groupedMissing = missing.GroupBy(f => f.ModName).OrderBy(g => g.Key);
            foreach (var group in groupedMissing)
            {
                AnsiConsole.MarkupLine($"  [grey]{EscapeMarkup(group.Key)}[/]");
                foreach (var file in group.OrderBy(f => f.RelativePath))
                {
                    var sizeStr = file.ServerSize.HasValue ? $" ({file.ServerSize.Value / 1024}KB)" : "";
                    AnsiConsole.MarkupLine($"    [red]•[/] {EscapeMarkup(file.RelativePath)}{sizeStr}");
                    Log($"  Missing: {file.RelativePath}");
                }
            }

            AnsiConsole.WriteLine();

            var downloadMissing = AnsiConsole.Confirm($"Download {missing.Count} missing file(s)?", true);

            if (downloadMissing)
            {
                Log("Downloading missing files...");
                var (success, fail) = await DownloadFilesAsync(missing, _clientConfig.Headless);
                totalSuccesses += success;
                totalFailures += fail;
            }
            else
            {
                Log("User skipped downloading missing files");
            }

            AnsiConsole.WriteLine();
        }

        // Handle modified files (prompt)
        if (modified.Any())
        {
            AnsiConsole.MarkupLine("[bold yellow]Modified Files[/]");
            AnsiConsole.MarkupLine("[grey]These files exist locally but don't match the server version.[/]");
            AnsiConsole.WriteLine();

            foreach (var issue in modified)
            {
                var sizeStr = issue.ServerSize.HasValue ? $" ({issue.ServerSize.Value / 1024}KB)" : "";
                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title(
                            $"[yellow]{EscapeMarkup(issue.RelativePath)}[/]{sizeStr} [grey]({EscapeMarkup(issue.ModName)})[/]")
                        .AddChoices("Overwrite with server version", "Keep local version", "Skip all remaining"));

                if (choice == "Overwrite with server version")
                {
                    var success = await DownloadFileAsync(issue.RelativePath, _clientConfig.Headless);
                    if (success)
                    {
                        AnsiConsole.MarkupLine($"  [green]✓[/] Updated");
                        totalSuccesses++;
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"  [red]✗[/] Update failed");
                        totalFailures++;
                    }
                }
                else if (choice == "Keep local version")
                {
                    AnsiConsole.MarkupLine($"  [grey]○[/] Skipped");
                }
                else // Skip all
                {
                    AnsiConsole.MarkupLine($"  [grey]○[/] Skipping remaining modified files");
                    break;
                }
            }

            AnsiConsole.WriteLine();
        }

        // Show final summary if there were any download attempts
        if (totalSuccesses > 0 || totalFailures > 0)
        {
            AnsiConsole.WriteLine();
            if (totalFailures > 0)
            {
                AnsiConsole.MarkupLine($"[yellow]Summary:[/] [green]{totalSuccesses} succeeded[/], [red]{totalFailures} failed[/]");
                Log($"Sync summary: {totalSuccesses} succeeded, {totalFailures} failed");
            }
            else
            {
                AnsiConsole.MarkupLine($"[green]Summary:[/] All {totalSuccesses} file(s) downloaded successfully");
                Log($"Sync summary: All {totalSuccesses} files downloaded successfully");
            }
        }

        // Handle extra files (prompt)
        if (extra.Any())
        {
            AnsiConsole.MarkupLine("[bold blue]Extra Files[/]");
            AnsiConsole.MarkupLine("[grey]These files exist locally but are not in the server's mod list.[/]");
            AnsiConsole.WriteLine();

            var deleteChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"How do you want to handle {extra.Count} extra file(s)?")
                    .AddChoices("Review one by one", "Delete all", "Keep all"));

            if (deleteChoice == "Delete all")
            {
                foreach (var issue in extra)
                {
                    var fullPath = Path.Combine(_sptRoot, issue.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        File.Delete(fullPath);
                        AnsiConsole.MarkupLine($"  [red]✗[/] Deleted: {EscapeMarkup(issue.RelativePath)}");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine(
                            $"  [red]![/] Failed to delete: {EscapeMarkup(issue.RelativePath)} - {EscapeMarkup(ex.Message)}");
                    }
                }
            }
            else if (deleteChoice == "Review one by one")
            {
                foreach (var issue in extra)
                {
                    var choice = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title($"[blue]{EscapeMarkup(issue.RelativePath)}[/]")
                            .AddChoices("Delete", "Keep", "Skip all remaining"));

                    if (choice == "Delete")
                    {
                        var fullPath = Path.Combine(_sptRoot,
                            issue.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                        try
                        {
                            File.Delete(fullPath);
                            AnsiConsole.MarkupLine($"  [red]✗[/] Deleted");
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"  [red]![/] Failed: {EscapeMarkup(ex.Message)}");
                        }
                    }
                    else if (choice == "Keep")
                    {
                        AnsiConsole.MarkupLine($"  [grey]○[/] Kept");
                    }
                    else // Skip all
                    {
                        AnsiConsole.MarkupLine($"  [grey]○[/] Keeping remaining extra files");
                        break;
                    }
                }
            }
            else
            {
                AnsiConsole.MarkupLine("[grey]Keeping all extra files.[/]");
            }
        }
    }

    static async Task<FileManifest?> FetchManifestAsync(bool headless = false)
    {
        var endpoint = headless ? "/modgod/api/manifest/headless" : "/modgod/api/manifest";
        var url = $"{_clientConfig.ServerUrl}{endpoint}";

        try
        {
            using var client = CreateHttpClient();
            Log($"Fetching manifest from: {url}");
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Log($"Manifest request failed: {response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<FileManifest>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Log($"Error fetching manifest: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Check if all files for a mod exist on disk.
    /// Used to detect manually installed mods that aren't tracked in modsDownloaded.json.
    /// </summary>
    static bool CheckModFilesExist(string modName)
    {
        if (_cachedManifest == null)
        {
            return false;
        }

        var modFiles = _cachedManifest.Files
            .Where(kvp => kvp.Value.ModName.Equals(modName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // If no files found for this mod in manifest, can't determine installation status
        if (modFiles.Count == 0)
        {
            return false;
        }

        // Check if ALL files exist on disk
        foreach (var kvp in modFiles)
        {
            var relativePath = kvp.Key;
            var fullPath = Path.Combine(_sptRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            
            if (!File.Exists(fullPath))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Remove all files belonging to a mod when user opts out.
    /// Uses the cached manifest to find files belonging to the mod.
    /// </summary>
    static async Task RemoveModFilesAsync(string modName)
    {
        if (_cachedManifest == null)
        {
            Log($"Cannot remove mod files for {modName} - no manifest cached");
            return;
        }

        var filesToRemove = _cachedManifest.Files
            .Where(kvp => kvp.Value.ModName.Equals(modName, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();

        if (filesToRemove.Count == 0)
        {
            Log($"No files found for mod {modName} in manifest");
            return;
        }

        Log($"Removing {filesToRemove.Count} file(s) for opted-out mod: {modName}");
        var removedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in filesToRemove)
        {
            var fullPath = Path.Combine(_sptRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            
            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    Log($"  Deleted: {relativePath}");
                    
                    // Track parent directory for cleanup
                    var dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        removedDirs.Add(dir);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"  Failed to delete {relativePath}: {ex.Message}");
            }
        }

        // Clean up empty directories (from deepest to shallowest)
        await Task.Run(() => CleanupEmptyDirectories(removedDirs));
    }

    /// <summary>
    /// Remove empty directories after file deletion, working from deepest to shallowest.
    /// Stops at sync root boundaries.
    /// </summary>
    static void CleanupEmptyDirectories(HashSet<string> directories)
    {
        // Sort by depth (deepest first) to ensure we clean up from bottom to top
        var sortedDirs = directories
            .SelectMany(d => GetDirectoryChain(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar))
            .ToList();

        foreach (var dir in sortedDirs)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    // Don't delete sync root directories themselves
                    var relativePath = Path.GetRelativePath(_sptRoot, dir).Replace('\\', '/');
                    var syncRoots = _cachedManifest?.SyncRoots ?? new List<string> { "BepInEx/plugins", "SPT/user/mods" };
                    
                    if (syncRoots.Any(r => r.Equals(relativePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue; // Don't delete sync root itself
                    }

                    Directory.Delete(dir);
                    Log($"  Removed empty directory: {relativePath}");
                }
            }
            catch (Exception ex)
            {
                Log($"  Failed to remove directory {dir}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Get all directories in the chain from the given directory up to (but not including) SPT root.
    /// </summary>
    static IEnumerable<string> GetDirectoryChain(string directory)
    {
        var current = directory;
        while (!string.IsNullOrEmpty(current) && 
               !current.Equals(_sptRoot, StringComparison.OrdinalIgnoreCase) &&
               current.StartsWith(_sptRoot, StringComparison.OrdinalIgnoreCase))
        {
            yield return current;
            current = Path.GetDirectoryName(current);
        }
    }

    static string ComputeFileHash(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hashBytes = sha256.ComputeHash(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    static HashSet<string> BuildExclusionSet(IEnumerable<string>? exclusions)
    {
        return new HashSet<string>(
            (exclusions ?? []).Select(p => p.Replace('\\', '/').TrimStart('/')),
            StringComparer.OrdinalIgnoreCase);
    }

    static HashSet<string> BuildSyncRootsSet(IEnumerable<string>? syncRoots)
    {
        // Default to standard sync paths if none provided (backwards compatibility)
        var roots = syncRoots?.ToList() ?? new List<string>();
        if (roots.Count == 0)
        {
            roots = new List<string> { "BepInEx/plugins", "SPT/user/mods" };
        }
        return new HashSet<string>(
            roots.Select(p => p.Replace('\\', '/').TrimStart('/')),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ensure all sync root directories exist on the client, even if they're empty on the server.
    /// This ensures the client has the same directory structure as the server.
    /// </summary>
    static void EnsureSyncDirectoriesExist(IEnumerable<string>? syncRoots)
    {
        var roots = syncRoots?.ToList() ?? new List<string>();
        if (roots.Count == 0)
        {
            roots = new List<string> { "BepInEx/plugins", "SPT/user/mods" };
        }

        foreach (var root in roots)
        {
            // Skip paths that look like files (have an extension) - only create directories
            if (Path.HasExtension(root))
            {
                Log($"Skipping sync root that looks like a file: {root}");
                continue;
            }

            var fullPath = Path.Combine(_sptRoot, root.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(fullPath))
            {
                try
                {
                    Directory.CreateDirectory(fullPath);
                    Log($"Created sync directory: {root}");
                }
                catch (Exception ex)
                {
                    Log($"Failed to create sync directory {root}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Check if a path is within one of the registered sync roots.
    /// Only files within sync roots should be installed/synced.
    /// </summary>
    static bool IsInSyncPath(string relativePath, HashSet<string> syncRoots)
    {
        if (syncRoots.Count == 0) return true; // No restrictions if no sync roots defined

        var norm = relativePath.Replace('\\', '/').TrimStart('/');

        foreach (var root in syncRoots)
        {
            // Check if path equals the sync root or is under it
            if (norm.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                norm.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    static bool IsExcluded(string relativePath, HashSet<string> exclusions)
    {
        var norm = relativePath.Replace('\\', '/').TrimStart('/');

        foreach (var pattern in exclusions)
        {
            // Check if it's a glob pattern (contains *, ?, or **)
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                if (GlobMatch(norm, pattern))
                    return true;
            }
            else
            {
                // Exact match or prefix match for non-glob patterns
                if (norm.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                    norm.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Simple glob pattern matching for exclusions.
    /// Supports: * (any chars except /), ** (any chars including /), ? (single char)
    /// </summary>
    static bool GlobMatch(string path, string pattern)
    {
        try
        {
            // Check cache first
            if (!_globCache.TryGetValue(pattern, out var regex))
            {
                regex = CompileGlobPattern(pattern);
                _globCache[pattern] = regex;
            }

            return regex?.IsMatch(path) ?? false;
        }
        catch
        {
            return false;
        }
    }

    static System.Text.RegularExpressions.Regex? CompileGlobPattern(string pattern)
    {
        try
        {
            var regexPattern = "^";
            var i = 0;
            pattern = pattern.Replace('\\', '/').TrimStart('/');

            while (i < pattern.Length)
            {
                var c = pattern[i];

                if (c == '*')
                {
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                        {
                            regexPattern += "(.*/)?";
                            i += 3;
                        }
                        else
                        {
                            regexPattern += ".*";
                            i += 2;
                        }
                    }
                    else
                    {
                        regexPattern += "[^/]*";
                        i++;
                    }
                }
                else if (c == '?')
                {
                    regexPattern += "[^/]";
                    i++;
                }
                else if (c == '.')
                {
                    regexPattern += "\\.";
                    i++;
                }
                else if (c == '/' || c == '\\')
                {
                    regexPattern += "/";
                    i++;
                }
                else if ("[](){}+^$|".Contains(c))
                {
                    regexPattern += "\\" + c;
                    i++;
                }
                else
                {
                    regexPattern += c;
                    i++;
                }
            }

            regexPattern += "$";

            return new System.Text.RegularExpressions.Regex(
                regexPattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(100));
        }
        catch
        {
            return null;
        }
    }

    static async Task<(int successCount, int failCount)> DownloadFilesAsync(List<FileSyncIssue> files, bool headless = false)
    {
        var successCount = 0;
        var failCount = 0;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn()
            })
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"Downloading {files.Count} file(s)", maxValue: files.Count);

                foreach (var file in files)
                {
                    task.Description = $"Downloading: {Path.GetFileName(file.RelativePath)}";

                    var success = await DownloadFileAsync(file.RelativePath, headless);
                    if (success)
                    {
                        AnsiConsole.MarkupLine($"  [green]✓[/] {EscapeMarkup(file.RelativePath)}");
                        successCount++;
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"  [red]✗[/] {EscapeMarkup(file.RelativePath)} - download failed");
                        failCount++;
                    }

                    task.Increment(1);
                }

                task.Description = "Download complete";
            });

        return (successCount, failCount);
    }

    static async Task<bool> DownloadFileAsync(string relativePath, bool headless = false)
    {
        try
        {
            // URL encode the path
            var encodedPath = Uri.EscapeDataString(relativePath.Replace('\\', '/'));
            var url = $"{_clientConfig.ServerUrl}/modgod/api/file/{encodedPath}";

            // Add headless query parameter if in headless mode
            if (headless)
            {
                url += "?headless=true";
            }

            using var client = CreateHttpClient();
            client.Timeout = TimeSpan.FromMinutes(5);

            Log($"Downloading: {url}");
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Log($"Download failed: {response.StatusCode} for {relativePath}");
                return false;
            }

            var fullPath = Path.Combine(_sptRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

            // Ensure directory exists
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Write file
            await using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fileStream);

            return true;
        }
        catch (Exception ex)
        {
            Log($"Download error for {relativePath}: {ex.Message}");
            return false;
        }
    }

    static void WaitForExit()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Press any key to exit...[/]");
        Console.ReadKey(true);
    }

    /// <summary>
    /// Find 7z executable - checks ModGodData/tools first, then system installations
    /// </summary>
    static string? Find7ZipExecutable()
    {
        var isWindows = OperatingSystem.IsWindows();

        // First, check for bundled 7z in ModGodData/tools (shared location for server and updater)
        var modGodDataTools = Path.Combine(_sptRoot, "ModGodData", "tools");

        if (isWindows)
        {
            var bundledPath = Path.Combine(modGodDataTools, "7z.exe");
            if (File.Exists(bundledPath))
            {
                Log($"[7z] Using bundled 7z.exe: {bundledPath}");
                return bundledPath;
            }

            // Fall back to system 7-Zip installations
            var systemPaths = new[]
            {
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "7-Zip", "7z.exe"),
            };

            foreach (var path in systemPaths)
            {
                if (File.Exists(path))
                {
                    Log($"[7z] Found system 7-Zip at: {path}");
                    return path;
                }
            }
        }
        else
        {
            // Linux/macOS: Check for bundled 7zz in ModGodData/tools
            var bundledPath = Path.Combine(modGodDataTools, "7zz");
            if (File.Exists(bundledPath))
            {
                Log($"[7z] Using bundled 7zz: {bundledPath}");
                return bundledPath;
            }

            // Check for 7z in PATH
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
                            Log($"[7z] Found 7-Zip in PATH: {cmd}");
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
    /// Extract archive using native 7z (much faster for 7z/LZMA archives)
    /// </summary>
    static async Task<bool> ExtractWith7ZipAsync(string archivePath, string extractPath, string sevenZipPath, ProgressTask? progressTask = null)
    {
        Log($"[7z] Extracting with native 7-Zip: {Path.GetFileName(archivePath)}");

        var startInfo = new ProcessStartInfo
        {
            FileName = sevenZipPath,
            Arguments = $"x \"{archivePath}\" -o\"{extractPath}\" -y -bsp1 -bse1",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var startTime = DateTime.UtcNow;
        int lastPercent = 0;

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
                    numStart--;
                numStart++;

                var numStr = line[numStart..percentIndex].Trim();
                if (int.TryParse(numStr, out var percent) && percent >= 0 && percent <= 100)
                {
                    if (progressTask != null && percent > lastPercent)
                    {
                        progressTask.Value = percent;
                        lastPercent = percent;
                    }
                }
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Log($"[7z] {e.Data}");
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Wait for process to complete
        while (!process.HasExited)
        {
            await Task.Delay(100);

            var elapsed = DateTime.UtcNow - startTime;
            if (elapsed.TotalMinutes >= 30)
            {
                Log("[7z] Extraction timed out after 30 minutes");
                try { process.Kill(); } catch { }
                return false;
            }
        }

        await Task.Run(() => process.WaitForExit());

        var totalElapsed = DateTime.UtcNow - startTime;

        if (process.ExitCode == 0)
        {
            Log($"[7z] Extraction complete in {totalElapsed.TotalSeconds:F1}s");
            if (progressTask != null) progressTask.Value = 100;
            return true;
        }
        else
        {
            Log($"[7z] Extraction failed with exit code {process.ExitCode}");
            return false;
        }
    }

    /// <summary>
    /// Detect if an archive is a 7z file (needs native extraction for speed)
    /// </summary>
    static bool Is7zArchive(string archivePath)
    {
        try
        {
            using var archive = ArchiveFactory.Open(archivePath);
            return archive.Type == SharpCompress.Common.ArchiveType.SevenZip;
        }
        catch
        {
            return false;
        }
    }
}
