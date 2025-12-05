# ModGod

<div align="center">

**Server-Side Mod Manager + Client Sync — The Complete End-to-End Solution for SPT Tarkov 4.0**

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![SPT Version](https://img.shields.io/badge/SPT-4.0-blue.svg)](https://www.sp-tarkov.com/)
[![.NET](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com/)

_Manage your server mods from a web UI. Sync them to your players automatically._

</div>

---

> ⚠️ **Early Development Notice**
>
> ModGod is currently in early development (v1.0). While functional, you may encounter bugs, missing features, or breaking changes. Please report issues and feedback to help improve the project!

---

## 🎯 Overview

ModGod is a complete mod synchronization solution for SPT Tarkov servers. It allows server administrators to manage mods through a sleek web interface, while automatically ensuring all connected clients have the correct mods and server files installed.

### Why ModGod?

- **No more "wrong mod version" issues** - Clients automatically verify their mods match the server
- **Easy mod distribution** - Add mods via download URL, players download automatically
- **Preserve your configs** - File overwrite rules let you protect server-side customizations
- **Cross-platform support** - Auto-install scripts for both Windows and Linux servers

---

## ✨ Features

### 🖥️ Server Mod

- **Modern Web UI** at `https://your-server:6969/modgod/`
- Add mods from direct download URLs (GitHub releases, etc.)
- Supports archive formats `.zip` & `.7z`
- Auto-detect install paths for standard mod structures
- **File Overwrite Rules** - Choose which files to preserve during installs/reinstalls
- **Sync Exclusions** - Exclude any server files from client verification
- Pending changes system with visual status indicators
- **Auto-Install Scripts** - PowerShell (Windows) and Bash (Linux) scripts that wait for server shutdown then install

### 🎮 Client Enforcer Plugin

- **File Integrity Verification** - Compares client files against server manifest using SHA256 hashes
- **In-Game Warnings** - Shows detailed warnings for missing, modified, or extra files
- **One-Click Updates** - Launch the updater directly from the warning dialog
- Respects sync exclusions from server configuration
- Distinguishes between required and optional mods

### 📦 ModGod Updater

- **Standalone executable** - No installation required
- First-time setup wizard for server URL
- Downloads and installs required mods automatically
- Optional mod selection with opt-in/out persistence
- Progress tracking with pretty console UI (powered by Spectre.Console)
- Self-contained single-file exe (~35MB)

---

## 📁 Project Structure

```
ModGod/
├── Server/                          # SPT server mod
│   ├── Models/                      # Data models (ServerConfig, ModEntry, etc.)
│   ├── Services/                    # Business logic
│   │   ├── ConfigService.cs         # Configuration & auto-install scripts
│   │   ├── ManifestService.cs       # File manifest generation
│   │   ├── ModDownloadService.cs    # Mod downloading & extraction
│   │   └── ModInstallService.cs     # Mod installation logic
│   ├── Web/                         # Blazor Server UI
│   │   ├── Pages/Home.razor         # Main management page
│   │   └── Shared/MainLayout.razor  # Layout & theming
│   └── wwwroot/                     # Static assets
│
├── Client/                          # BepInEx client plugin
│   ├── ModGodClientEnforcer.cs      # Main plugin with verification logic
│   └── Models/ModsDownloaded.cs     # Client-side models
│
├── ModGodUpdater/                   # Standalone sync tool
│   ├── Program.cs                   # Main updater logic
│   └── Models/ClientConfig.cs       # Client configuration
│
└── dist/                            # Build output
    ├── BepInEx/plugins/ModGodClientEnforcer/
    ├── SPT/user/mods/ModGodServer/
    └── ModGodUpdater.exe
```

---

## 🚀 Installation

### Server Setup

1. **Download the release** or build from source
2. **Copy server mod** to your SPT installation:
   ```
   dist/SPT/user/mods/ModGodServer/ → <SPT_ROOT>/SPT/user/mods/ModGodServer/
   ```
3. **Start your SPT server**
4. **Access the Web UI** at `https://127.0.0.1:6969/modgod/`
5. **Add mods** using the "Add Mods" button with direct download URLs

### Client Setup

1. **Copy the client plugin** to your SPT installation:
   ```
   dist/BepInEx/plugins/ModGodClientEnforcer/ → <SPT_ROOT>/BepInEx/plugins/ModGodClientEnforcer/
   ```
2. **Copy the updater** to your SPT root:
   ```
   dist/ModGodUpdater.exe → <SPT_ROOT>/ModGodUpdater.exe
   ```
3. **Run `ModGodUpdater.exe`** and enter your server URL when prompted
4. **Launch the game** - the enforcer plugin will verify your mods

---

## 🔧 Configuration

### Server Configuration

All server configuration is stored in `<SPT_ROOT>/ModGodData/`:

| File                     | Description                             |
| ------------------------ | --------------------------------------- |
| `serverConfig.json`      | Mod list, sync exclusions, and settings |
| `stagingIndex.json`      | Downloaded mod cache index              |
| `pendingOperations.json` | Queued install/remove operations        |
| `staging/`               | Downloaded and extracted mod files      |

### Client Configuration

Client configuration is stored in `<SPT_ROOT>/ModGodData/`:

| File                  | Description                                |
| --------------------- | ------------------------------------------ |
| `ModGodClient.json`   | Server URL and settings                    |
| `modsDownloaded.json` | List of downloaded mods with opt-in status |

---

## 📖 Usage Guide

### Adding Mods (Server)

1. Open the Web UI at `https://your-server:6969/modgod/`
2. Click **"Add Mods"**
3. Paste direct download URLs (one per line)
4. Choose if mods are optional or required
5. Click **"Download & Stage"**
6. Review the results and click **"Apply Changes"**
7. The auto-installer will launch and wait for server shutdown

### Managing File Overwrites

When editing a mod, you can control which files get overwritten during reinstalls:

1. Click on a mod card to open the edit dialog
2. Scroll to **"Files to be Overwritten"**
3. Uncheck any files you want to preserve (e.g., `config.json`)
4. These files will be skipped during future installs

### Sync Exclusions

Prevent client warnings for server-generated files:

1. Go to the **"Sync Exclusions"** tab
2. Uncheck files/directories that shouldn't be synced to clients
3. Click **"Save Exclusions"**
4. Clients will ignore these paths during verification

---

## 🛠️ Building from Source

### Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [.NET Framework 4.7.1 Developer Pack](https://dotnet.microsoft.com/download/dotnet-framework/net471) (for client plugin)
- SPT 4.0 installation (for reference DLLs)

### Build Commands

```bash
# Build everything
dotnet build ModGod.sln

# Build individual projects
dotnet build Server/ModGodServer.csproj
dotnet build Client/ModGodClientEnforcer.csproj
dotnet build ModGodUpdater/ModGodUpdater.csproj

# Publish updater as single-file exe (automatic during solution build)
dotnet publish ModGodUpdater/ModGodUpdater.csproj -c Release
```

### Configuration

Update `SPTPath` in project files to match your SPT installation:

- `Server/ModGodServer.csproj` - Line 25
- `Client/ModGodClientEnforcer.csproj` - Line 28

---

## 🔌 API Endpoints

The server exposes the following REST endpoints:

| Endpoint               | Method | Description                     |
| ---------------------- | ------ | ------------------------------- |
| `/modgod/`             | GET    | Web UI                          |
| `/modgod/api/config`   | GET    | Server configuration (mod list) |
| `/modgod/api/manifest` | GET    | File manifest with hashes       |
| `/modgod/api/status`   | GET    | Server status check             |

---

## 🐧 Linux Support

ModGod includes Bash script generation for Linux servers:

1. When you click "Apply Changes", both PowerShell and Bash scripts are generated
2. On Linux, the Bash script runs via `nohup` with output logged to `modgod_install.log`
3. Scripts wait for server shutdown before installing mods

---

## ❓ Troubleshooting

### "Setup Required" Warning on Client

- Run `ModGodUpdater.exe` in your SPT root folder
- Ensure `ModGodData/ModGodClient.json` exists with the correct server URL

### Mods Not Installing

- Check that the auto-installer script is running (PowerShell window on Windows)
- Verify the mod URLs are direct download links (not page links)
- Check `ModGodData/staging/` for downloaded files

### File Verification Failures

- Ensure client mods match the server's installed versions
- Run the updater to sync missing/outdated mods
- Check sync exclusions if warnings are for server-generated files

### Web UI Not Loading

- Verify SPT server is running
- Check the URL: `https://127.0.0.1:6969/modgod/` (note: HTTPS)
- Accept the self-signed certificate warning in your browser

---

## 📜 License

MIT License - See [LICENSE](LICENSE) file for details.

---

## 🙏 Credits

- **Bewa** - Creator and maintainer
- Built for the [SPT Tarkov](https://www.sp-tarkov.com/) community
- Powered by [MudBlazor](https://mudblazor.com/), [SharpCompress](https://github.com/adamhathcock/sharpcompress), and [Spectre.Console](https://spectreconsole.net/)

---

<div align="center">

**[Report Bug](https://github.com/your-repo/issues) · [Request Feature](https://github.com/your-repo/issues)**

</div>
