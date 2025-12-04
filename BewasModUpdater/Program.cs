using System.Net.Security;
using System.Text.Json;
using BewasModSync.Updater.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using Spectre.Console;

namespace BewasModSync.Updater;

class Program
{
    // Internal data folder for config files
    private static readonly string InternalDataFolderName = "BewasModSyncInternalData";
    private static readonly string TempDownloadPath = Path.Combine(Path.GetTempPath(), "BewasModSync");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static ClientConfig _clientConfig = new();
    private static List<DownloadedMod> _modsDownloaded = new();
    private static string _sptRoot = string.Empty;
    private static string _internalDataPath = string.Empty;

    static async Task Main(string[] args)
    {
        Console.Title = "BewasModSync Updater";

        AnsiConsole.Write(
            new FigletText("BewasModUpdater")
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
            AnsiConsole.MarkupLine("[red]Error:[/] BewasModUpdater.exe must be in your SPT root directory.");
            AnsiConsole.MarkupLine("[grey]Expected structure:[/]");
            AnsiConsole.MarkupLine("[grey]  SPT/[/]");
            AnsiConsole.MarkupLine("[grey]  ├── BepInEx/[/]");
            AnsiConsole.MarkupLine("[grey]  ├── SPT/[/]");
            AnsiConsole.MarkupLine("[grey]  ├── BewasModSyncInternalData/[/]");
            AnsiConsole.MarkupLine("[grey]  └── [cyan]BewasModUpdater.exe[/][/]");
            WaitForExit();
            return;
        }

        _internalDataPath = Path.Combine(_sptRoot, InternalDataFolderName);
        Directory.CreateDirectory(_internalDataPath);

        AnsiConsole.MarkupLine($"[green]✓[/] SPT Root: [cyan]{_sptRoot}[/]");
        AnsiConsole.WriteLine();

        // Load or create config
        await LoadOrCreateConfigAsync();
        await LoadModsDownloadedAsync();

        // Fetch server config
        var serverConfig = await FetchServerConfigAsync();
        if (serverConfig == null)
        {
            WaitForExit();
            return;
        }

        AnsiConsole.MarkupLine($"[green]✓[/] Found [cyan]{serverConfig.ModList.Count}[/] mod(s) on server");
        AnsiConsole.WriteLine();

        // Process mods
        await ProcessModsAsync(serverConfig);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]Sync complete![/]");
        WaitForExit();
    }

    static bool IsSptDirectory(string path)
    {
        var bepInExPath = Path.Combine(path, "BepInEx");
        var sptPath = Path.Combine(path, "SPT");

        return Directory.Exists(bepInExPath) || Directory.Exists(sptPath);
    }

    static string GetConfigPath() => Path.Combine(_internalDataPath, "BewasModSyncClient.json");
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BewasModUpdater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    static async Task<ServerConfig?> FetchServerConfigAsync()
    {
        var url = $"{_clientConfig.ServerUrl}/bewasmodsync/api/config";

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
                            AnsiConsole.MarkupLine($"[grey]Response: {errorContent.Substring(0, Math.Min(200, errorContent.Length))}[/]");
                        }
                        AnsiConsole.MarkupLine($"[grey]URL: {url}[/]");
                        return null;
                    }

                    var json = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<ServerConfig>(json, JsonOptions);
                }
                catch (HttpRequestException ex)
                {
                    AnsiConsole.MarkupLine($"[red]Failed to connect to server:[/] {ex.Message}");
                    AnsiConsole.MarkupLine($"[grey]URL: {url}[/]");
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
            AnsiConsole.MarkupLine($"  [grey]○[/] {mod.ModName} [grey](skipped)[/]");
            return;
        }

        if (!needsUpdate)
        {
            AnsiConsole.MarkupLine($"  [green]✓[/] {mod.ModName} [grey](up to date)[/]");
            return;
        }

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync($"Downloading {mod.ModName}...", async ctx =>
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

                    ctx.Status($"Installing {mod.ModName}...");

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
                    AnsiConsole.MarkupLine($"  [red]✗[/] {mod.ModName} - Failed: {ex.Message}");
                    return;
                }
            });

        AnsiConsole.MarkupLine($"  [green]✓[/] {mod.ModName} [cyan](installed)[/]");
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

    static void WaitForExit()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Press any key to exit...[/]");
        Console.ReadKey(true);
    }
}

