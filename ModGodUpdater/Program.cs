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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static ClientConfig _clientConfig = new();
    private static List<DownloadedMod> _modsDownloaded = new();
    private static string _sptRoot = string.Empty;
    private static string _internalDataPath = string.Empty;
    private static StreamWriter? _logWriter;

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

        // Process mods
        Log("Processing mods...");
        await ProcessModsAsync(serverConfig);

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
        var url = $"{_clientConfig.ServerUrl}/modgod/api/config";

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
                        AnsiConsole.MarkupLine($"[red]Server returned {(int)response.StatusCode} ({response.StatusCode})[/]");
                        if (!string.IsNullOrWhiteSpace(errorContent))
                        {
                            AnsiConsole.MarkupLine($"[grey]Response: {EscapeMarkup(errorContent.Substring(0, Math.Min(200, errorContent.Length)))}[/]");
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

    static async Task ProcessModsAsync(ServerConfig serverConfig)
    {
        var requiredMods = serverConfig.ModList.Where(m => !m.Optional).ToList();
        var optionalMods = serverConfig.ModList.Where(m => m.Optional).ToList();

        // Process required mods first
        if (requiredMods.Any())
        {
            AnsiConsole.MarkupLine("[bold]Required Mods[/]");
            AnsiConsole.WriteLine();

            foreach (var mod in requiredMods)
            {
                await ProcessModAsync(mod, isOptional: false);
            }
        }

        // Handle optional mods
        if (optionalMods.Any())
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Optional Mods[/]");
            AnsiConsole.WriteLine();

            // Show selection for optional mods
            var optionalChoices = optionalMods.Select(m =>
            {
                var downloaded = _modsDownloaded.Find(d => d.DownloadUrl == m.DownloadUrl);
                var status = downloaded?.OptIn == true ? " [green](installed)[/]" : " [grey](not installed)[/]";
                return $"{m.ModName}{status}";
            }).ToList();

            // Pre-select mods that are already installed
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

                await ProcessModAsync(mod, isOptional: true, optIn: isSelected);
            }
        }

        await SaveModsDownloadedAsync();
    }

    static async Task ProcessModAsync(ModEntry mod, bool isOptional, bool optIn = true)
    {
        var downloaded = _modsDownloaded.Find(d => d.DownloadUrl == mod.DownloadUrl);
        var needsUpdate = downloaded == null || downloaded.LastUpdated != mod.LastUpdated;

        // For optional mods that aren't opted in, skip
        if (isOptional && !optIn)
        {
            if (downloaded != null)
            {
                downloaded.OptIn = false;
            }
            AnsiConsole.MarkupLine($"  [grey]○[/] {EscapeMarkup(mod.ModName)} [grey](skipped)[/]");
            return;
        }

        if (!needsUpdate)
        {
            AnsiConsole.MarkupLine($"  [green]✓[/] {EscapeMarkup(mod.ModName)} [grey](up to date)[/]");
            return;
        }

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync($"Downloading {EscapeMarkup(mod.ModName)}...", async ctx =>
            {
                try
                {
                    // Download mod (use CreateHttpClient for SSL certificate handling)
                    using var client = CreateHttpClient();
                    client.Timeout = TimeSpan.FromMinutes(30); // Long timeout for large mods

                    var response = await client.GetAsync(mod.DownloadUrl);
                    response.EnsureSuccessStatusCode();

                    // Stream download to file (handles large files efficiently)
                    var tempExtractPath = Path.Combine(TempDownloadPath, Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempExtractPath);
                    
                    var archivePath = Path.Combine(tempExtractPath, "mod.archive");
                    await using (var fileStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        await response.Content.CopyToAsync(fileStream);
                    }

                    // Extract using SharpCompress (supports .zip, .7z, .rar, .tar.gz, etc.)
                    using (var archive = ArchiveFactory.Open(archivePath))
                    {
                        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                        {
                            entry.WriteToDirectory(tempExtractPath, new ExtractionOptions
                            {
                                ExtractFullPath = true,
                                Overwrite = true
                            });
                        }
                    }
                    File.Delete(archivePath);

                    ctx.Status($"Installing {EscapeMarkup(mod.ModName)}...");

                    // Copy files according to install paths
                    foreach (var installPath in mod.InstallPaths)
                    {
                        var sourcePath = Path.Combine(tempExtractPath, installPath[0]);
                        var targetPath = installPath[1].Replace("<SPT_ROOT>", _sptRoot);

                        if (Directory.Exists(sourcePath))
                        {
                            CopyDirectory(sourcePath, targetPath);
                        }
                        else if (File.Exists(sourcePath))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                            File.Copy(sourcePath, targetPath, overwrite: true);
                        }
                    }

                    // Clean up temp directory
                    Directory.Delete(tempExtractPath, true);

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
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"  [red]✗[/] {EscapeMarkup(mod.ModName)} - Failed: {EscapeMarkup(ex.Message)}");
                    return;
                }
            });

        AnsiConsole.MarkupLine($"  [green]✓[/] {EscapeMarkup(mod.ModName)} [cyan](installed)[/]");
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
        AnsiConsole.MarkupLine("[bold]File Verification[/]");
        AnsiConsole.WriteLine();

        // Fetch manifest
        Log("Fetching manifest...");
        var manifest = await FetchManifestAsync();
        if (manifest == null)
        {
            Log("WARNING: Could not fetch manifest");
            AnsiConsole.MarkupLine("[yellow]Could not fetch file manifest. Skipping file sync.[/]");
            return;
        }

        Log($"Manifest received: {manifest.Files.Count} files");
        AnsiConsole.MarkupLine($"[green]✓[/] Manifest: [cyan]{manifest.Files.Count}[/] files from server");

        // Build exclusion set
        var exclusions = new HashSet<string>(
            manifest.SyncExclusions.Select(p => p.Replace('\\', '/').TrimStart('/')),
            StringComparer.OrdinalIgnoreCase);

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

                // Scan for extra files
                var syncDirs = new[] { "BepInEx/plugins", "SPT/user/mods" };
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

                await Task.CompletedTask;
            });

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
                await DownloadFilesAsync(missing);
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
                        .Title($"[yellow]{EscapeMarkup(issue.RelativePath)}[/]{sizeStr} [grey]({EscapeMarkup(issue.ModName)})[/]")
                        .AddChoices("Overwrite with server version", "Keep local version", "Skip all remaining"));

                if (choice == "Overwrite with server version")
                {
                    await DownloadFileAsync(issue.RelativePath);
                    AnsiConsole.MarkupLine($"  [green]✓[/] Updated");
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
                        AnsiConsole.MarkupLine($"  [red]![/] Failed to delete: {EscapeMarkup(issue.RelativePath)} - {EscapeMarkup(ex.Message)}");
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
                        var fullPath = Path.Combine(_sptRoot, issue.RelativePath.Replace('/', Path.DirectorySeparatorChar));
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

    static async Task<FileManifest?> FetchManifestAsync()
    {
        var url = $"{_clientConfig.ServerUrl}/modgod/api/manifest";

        try
        {
            using var client = CreateHttpClient();
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<FileManifest>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    static string ComputeFileHash(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hashBytes = sha256.ComputeHash(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    static bool IsExcluded(string relativePath, HashSet<string> exclusions)
    {
        var norm = relativePath.Replace('\\', '/').TrimStart('/');
        return exclusions.Any(ex => 
            norm.Equals(ex, StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith(ex + "/", StringComparison.OrdinalIgnoreCase));
    }

    static async Task DownloadFilesAsync(List<FileSyncIssue> files)
    {
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
                    
                    var success = await DownloadFileAsync(file.RelativePath);
                    if (success)
                    {
                        AnsiConsole.MarkupLine($"  [green]✓[/] {EscapeMarkup(file.RelativePath)}");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"  [red]✗[/] {EscapeMarkup(file.RelativePath)} - download failed");
                    }

                    task.Increment(1);
                }

                task.Description = "Download complete";
            });
    }

    static async Task<bool> DownloadFileAsync(string relativePath)
    {
        try
        {
            // URL encode the path
            var encodedPath = Uri.EscapeDataString(relativePath.Replace('\\', '/'));
            var url = $"{_clientConfig.ServerUrl}/modgod/api/file/{encodedPath}";

            using var client = CreateHttpClient();
            client.Timeout = TimeSpan.FromMinutes(5);

            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
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
        catch
        {
            return false;
        }
    }

    static void WaitForExit()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Press any key to exit...[/]");
        Console.ReadKey(true);
    }
}

