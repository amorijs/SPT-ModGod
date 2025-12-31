using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModGod3.Updater.Models;
using Spectre.Console;

namespace ModGod3.Updater;

class Program
{
    private static readonly string InternalDataFolderName = "ModGodData";
    private static readonly string LogFileName = "ModGodUpdater.log";

    private static readonly string UpdaterVersion =
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "3.0.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly Dictionary<string, Regex?> _globCache = new();

    private static ClientConfig _clientConfig = new();
    private static string _sptRoot = string.Empty;
    private static string _internalDataPath = string.Empty;
    private static StreamWriter? _logWriter;
    private static FileManifest? _cachedManifest;

    static async Task Main(string[] args)
    {
        Console.Title = "ModGod Updater 3.0";

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
            Log($"ModGod Updater 3.0 started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
        catch { }
    }

    static void Log(string message)
    {
        try { _logWriter?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}"); }
        catch { }
    }

    static string EscapeMarkup(string text) =>
        string.IsNullOrEmpty(text) ? text : text.Replace("[", "[[").Replace("]", "]]");

    static async Task RunAsync()
    {
        AnsiConsole.Write(new FigletText("ModGod 3.0").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[grey]SPT Mod Synchronization Tool[/]");
        AnsiConsole.WriteLine();

        // Find SPT root
        var currentDir = Directory.GetCurrentDirectory();
        if (IsSptDirectory(currentDir))
        {
            _sptRoot = currentDir;
        }
        else
        {
            Log("ERROR: Not in SPT directory");
            AnsiConsole.MarkupLine("[red]Error:[/] ModGodUpdater.exe must be in your SPT root directory.");
            return;
        }

        _internalDataPath = Path.Combine(_sptRoot, InternalDataFolderName);
        Directory.CreateDirectory(_internalDataPath);

        // Re-initialize logging
        _logWriter?.Dispose();
        InitializeLogging(_sptRoot);

        Log($"SPT Root: {_sptRoot}");
        AnsiConsole.MarkupLine($"[green]✓[/] SPT Root: [cyan]{_sptRoot}[/]");
        AnsiConsole.WriteLine();

        // Load or create config
        await LoadOrCreateConfigAsync();

        // Version check
        if (!await CheckVersionCompatibilityAsync())
            return;

        // Show mode
        AnsiConsole.Write(
            new Panel(new Markup("[bold green]STANDARD MODE[/]\n\n[grey]Syncing files from server...[/]"))
                .Header("[green]ModGod 3.0[/]")
                .BorderColor(Color.Green)
                .Padding(1, 1, 1, 1));
        AnsiConsole.WriteLine();

        // Confirm
        var confirm = AnsiConsole.Confirm("[yellow]Continue with sync?[/]", true);
        if (!confirm)
        {
            AnsiConsole.MarkupLine("[grey]Operation cancelled.[/]");
            return;
        }

        AnsiConsole.WriteLine();

        // Handle optional source items
        await HandleOptionalItemsAsync();

        // Sync files
        await SyncFilesAsync();

        AnsiConsole.WriteLine();
        Log("Sync complete!");
        AnsiConsole.MarkupLine("[green]Sync complete![/]");
    }

    static bool IsSptDirectory(string path)
    {
        return Directory.Exists(Path.Combine(path, "BepInEx")) ||
               Directory.Exists(Path.Combine(path, "SPT"));
    }

    static string GetConfigPath() => Path.Combine(_internalDataPath, "ModGodClient.json");

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
            _clientConfig.ServerUrl = AnsiConsole.Ask<string>("Enter server URL (e.g., [cyan]https://192.168.1.100:6969[/]):");
            _clientConfig.ServerUrl = _clientConfig.ServerUrl.TrimEnd('/');
            await SaveConfigAsync();
            AnsiConsole.MarkupLine($"[green]✓[/] Server URL saved");
            AnsiConsole.WriteLine();
        }
        else
        {
            _clientConfig.ServerUrl = _clientConfig.ServerUrl.TrimEnd('/');
            AnsiConsole.MarkupLine($"[green]✓[/] Server: [cyan]{_clientConfig.ServerUrl}[/]");
        }
    }

    static async Task SaveConfigAsync()
    {
        var json = JsonSerializer.Serialize(_clientConfig, JsonOptions);
        await File.WriteAllTextAsync(GetConfigPath(), json);
    }

    static async Task<bool> CheckVersionCompatibilityAsync()
    {
        return await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync("Checking server compatibility...", async ctx =>
            {
                try
                {
                    _cachedManifest = await FetchManifestAsync();
                    if (_cachedManifest == null)
                    {
                        Log("WARNING: Could not fetch manifest for version check");
                        return true;
                    }

                    if (!string.IsNullOrEmpty(_cachedManifest.ModGodVersion) &&
                        _cachedManifest.ModGodVersion != UpdaterVersion)
                    {
                        Log($"VERSION MISMATCH: Server={_cachedManifest.ModGodVersion}, Updater={UpdaterVersion}");
                        AnsiConsole.WriteLine();
                        AnsiConsole.Write(
                            new Panel(new Markup($"[red bold]VERSION MISMATCH[/]\n\n" +
                                                 $"Server version: [cyan]{EscapeMarkup(_cachedManifest.ModGodVersion)}[/]\n" +
                                                 $"Updater version: [cyan]{EscapeMarkup(UpdaterVersion)}[/]\n\n" +
                                                 "[yellow]Please download the correct version of ModGod.[/]"))
                                .Header("[red]Update Required[/]")
                                .BorderColor(Color.Red)
                                .Padding(1, 1, 1, 1));
                        return false;
                    }

                    AnsiConsole.MarkupLine($"[green]✓[/] Server compatible (v{EscapeMarkup(_cachedManifest.ModGodVersion ?? UpdaterVersion)})");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"Version check failed: {ex.Message}");
                    AnsiConsole.MarkupLine($"[yellow]⚠[/] Could not verify server version: {EscapeMarkup(ex.Message)}");
                    return true;
                }
            });
    }

    static async Task HandleOptionalItemsAsync()
    {
        if (_cachedManifest == null) return;

        var optionalItems = _cachedManifest.SourceItems.Where(s => s.Optional).ToList();
        if (optionalItems.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No optional items available.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[bold]Optional Source Items[/]");
        AnsiConsole.WriteLine();

        var choices = optionalItems.Select(s =>
        {
            var isOptedIn = _clientConfig.OptedInItems.Contains(s.Path, StringComparer.OrdinalIgnoreCase);
            var status = isOptedIn ? " [green](installed)[/]" : " [grey](not installed)[/]";
            return $"{EscapeMarkup(s.DisplayName)}{status}";
        }).ToList();

        var preSelected = choices.Where(c => c.Contains("[green]")).ToList();

        var prompt = new MultiSelectionPrompt<string>()
            .Title("Select optional items to install:")
            .NotRequired()
            .PageSize(10)
            .InstructionsText("[grey](Press [cyan]<space>[/] to toggle, [cyan]<enter>[/] to accept)[/]")
            .AddChoices(choices);

        foreach (var item in preSelected)
            prompt.Select(item);

        var selected = AnsiConsole.Prompt(prompt);

        // Update opted-in list
        _clientConfig.OptedInItems.Clear();
        foreach (var opt in optionalItems)
        {
            var choiceText = choices[optionalItems.IndexOf(opt)];
            if (selected.Contains(choiceText))
            {
                _clientConfig.OptedInItems.Add(opt.Path);
            }
        }

        await SaveConfigAsync();
        AnsiConsole.WriteLine();
    }

    static async Task SyncFilesAsync()
    {
        AnsiConsole.MarkupLine("[bold]File Verification[/]");
        AnsiConsole.WriteLine();

        // Fetch manifest with opted-in items
        var optedInParam = _clientConfig.OptedInItems.Count > 0
            ? string.Join(",", _clientConfig.OptedInItems.Select(Uri.EscapeDataString))
            : null;

        var manifest = await FetchManifestAsync(optedInParam);
        if (manifest == null)
        {
            AnsiConsole.MarkupLine("[yellow]Could not fetch file manifest. Skipping sync.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[green]✓[/] Manifest: [cyan]{manifest.Files.Count}[/] files from server");

        var exclusions = BuildExclusionSet(manifest.Exclusions);
        var issues = new List<FileSyncIssue>();

        // Verify files
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync("Verifying files...", async ctx =>
            {
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
                            SourceItem = entry.SourceItem,
                            Required = entry.Required,
                            ServerSize = entry.Size
                        });
                        continue;
                    }

                    try
                    {
                        var localHash = ComputeFileHash(fullPath);
                        if (!localHash.Equals(entry.Hash, StringComparison.OrdinalIgnoreCase))
                        {
                            issues.Add(new FileSyncIssue
                            {
                                Action = FileSyncAction.Update,
                                RelativePath = relativePath,
                                SourceItem = entry.SourceItem,
                                Required = entry.Required,
                                ServerSize = entry.Size
                            });
                        }
                    }
                    catch
                    {
                        issues.Add(new FileSyncIssue
                        {
                            Action = FileSyncAction.Update,
                            RelativePath = relativePath,
                            SourceItem = entry.SourceItem,
                            Required = entry.Required,
                            ServerSize = entry.Size
                        });
                    }
                }

                // Check for extra files
                foreach (var syncRoot in manifest.SyncRoots)
                {
                    var fullDir = Path.Combine(_sptRoot, syncRoot.Replace('/', Path.DirectorySeparatorChar));
                    if (!Directory.Exists(fullDir)) continue;

                    foreach (var file in Directory.GetFiles(fullDir, "*", SearchOption.AllDirectories))
                    {
                        var relativePath = Path.GetRelativePath(_sptRoot, file).Replace('\\', '/');
                        if (manifest.Files.ContainsKey(relativePath)) continue;
                        if (IsExcluded(relativePath, exclusions)) continue;

                        issues.Add(new FileSyncIssue
                        {
                            Action = FileSyncAction.Delete,
                            RelativePath = relativePath,
                            SourceItem = "Unknown",
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

        // Handle issues
        var missing = issues.Where(i => i.Action == FileSyncAction.Download).ToList();
        var modified = issues.Where(i => i.Action == FileSyncAction.Update).ToList();
        var extra = issues.Where(i => i.Action == FileSyncAction.Delete).ToList();

        AnsiConsole.MarkupLine($"[yellow]Found {issues.Count} issue(s):[/]");
        if (missing.Any()) AnsiConsole.MarkupLine($"  [red]• {missing.Count} missing file(s)[/]");
        if (modified.Any()) AnsiConsole.MarkupLine($"  [yellow]• {modified.Count} modified file(s)[/]");
        if (extra.Any()) AnsiConsole.MarkupLine($"  [blue]• {extra.Count} extra file(s)[/]");
        AnsiConsole.WriteLine();

        // Download missing files
        if (missing.Count > 0)
        {
            var download = AnsiConsole.Confirm($"Download {missing.Count} missing file(s)?", true);
            if (download)
            {
                await DownloadFilesAsync(missing);
            }
        }

        // Handle modified files
        if (modified.Count > 0)
        {
            var update = AnsiConsole.Confirm($"Update {modified.Count} modified file(s)?", true);
            if (update)
            {
                await DownloadFilesAsync(modified);
            }
        }

        // Handle extra files
        if (extra.Count > 0)
        {
            AnsiConsole.MarkupLine($"[blue]Found {extra.Count} extra file(s) not in manifest.[/]");
            var delete = AnsiConsole.Confirm("Delete extra files?", false);
            if (delete)
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
                        AnsiConsole.MarkupLine($"  [red]![/] Failed: {EscapeMarkup(issue.RelativePath)} - {EscapeMarkup(ex.Message)}");
                    }
                }
            }
        }
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
                        AnsiConsole.MarkupLine($"  [red]✗[/] {EscapeMarkup(file.RelativePath)}");
                    }

                    task.Increment(1);
                }
            });
    }

    static async Task<bool> DownloadFileAsync(string relativePath)
    {
        try
        {
            var encodedPath = Uri.EscapeDataString(relativePath.Replace('\\', '/'));
            var url = $"{_clientConfig.ServerUrl}/modgod/api/file/{encodedPath}";

            using var client = CreateHttpClient();
            client.Timeout = TimeSpan.FromMinutes(5);

            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Log($"Download failed: {response.StatusCode} for {relativePath}");
                return false;
            }

            var fullPath = Path.Combine(_sptRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await using var fileStream = new FileStream(fullPath, FileMode.Create);
            await response.Content.CopyToAsync(fileStream);

            return true;
        }
        catch (Exception ex)
        {
            Log($"Download error for {relativePath}: {ex.Message}");
            return false;
        }
    }

    static async Task<FileManifest?> FetchManifestAsync(string? optedIn = null)
    {
        try
        {
            var url = $"{_clientConfig.ServerUrl}/modgod/api/manifest";
            if (!string.IsNullOrEmpty(optedIn))
                url += $"?optedIn={optedIn}";

            using var client = CreateHttpClient();
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

    static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ModGodUpdater/3.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
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

    static bool IsExcluded(string relativePath, HashSet<string> exclusions)
    {
        var norm = relativePath.Replace('\\', '/').TrimStart('/');
        foreach (var pattern in exclusions)
        {
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                if (GlobMatch(norm, pattern)) return true;
            }
            else
            {
                if (norm.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                    norm.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    static bool GlobMatch(string path, string pattern)
    {
        try
        {
            if (!_globCache.TryGetValue(pattern, out var regex))
            {
                regex = CompileGlobPattern(pattern);
                _globCache[pattern] = regex;
            }
            return regex?.IsMatch(path) ?? false;
        }
        catch { return false; }
    }

    static Regex? CompileGlobPattern(string pattern)
    {
        try
        {
            var regexPattern = "^";
            pattern = pattern.Replace('\\', '/').TrimStart('/');

            for (int i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                if (c == '*')
                {
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                        {
                            regexPattern += "(.*/)?";
                            i += 2;
                        }
                        else
                        {
                            regexPattern += ".*";
                            i++;
                        }
                    }
                    else { regexPattern += "[^/]*"; }
                }
                else if (c == '?') { regexPattern += "[^/]"; }
                else if (c == '.') { regexPattern += "\\."; }
                else if (c == '/' || c == '\\') { regexPattern += "/"; }
                else if ("[](){}+^$|".Contains(c)) { regexPattern += "\\" + c; }
                else { regexPattern += c; }
            }

            regexPattern += "$";
            return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        }
        catch { return null; }
    }

    static void WaitForExit()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Press any key to exit...[/]");
        Console.ReadKey(true);
    }
}
