# 😇 SPT ModGod

<div align="center">

**Server-Side Mod Manager + Client Sync — The Complete End-to-End Solution for SPT Tarkov 4.0**

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![SPT Version](https://img.shields.io/badge/SPT-4.0-blue.svg)](https://www.sp-tarkov.com/)
[![.NET](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com/)

_Manage your server mods from a web UI. Sync them to your players automatically._

</div>

---

## 📑 Table of Contents

- [Overview](#-overview)
- [Screenshots](#-screenshots)
- [Features](#-features)
- [Project Structure](#-project-structure)
- [Installation](#-installation)
- [Configuration](#-configuration)
- [Usage Guide](#-usage-guide)
- [Building from Source](#️-building-from-source)
- [API Endpoints](#-api-endpoints)
- [Linux Support](#-linux-support)
- [Troubleshooting](#-troubleshooting)
- [License](#-license)
- [Credits](#-credits)

---

## 🎯 Overview

ModGod is a complete mod synchronization solution for SPT Tarkov servers. It allows server administrators to manage mods through a sleek web interface, while automatically ensuring all connected clients have the correct mods and server files installed.

### Why ModGod?

- **Easy mod installation** - Install mods via Forge search or direct download URLs
- **Clients always up to date** - Clients automatically verify their mods match the server
- **Control synced files** - Configure which directories sync to clients, exclude specific files/folders, and use smart default exclusion patterns (logs, cache, dev files)
- **Preserve your configs** - When updating or reinstalling a mod you can easily set overwrite rules on specific files/directories, letting you protect server-side customizations

---

## 📸 Screenshots

### Web UI Dashboard

![Dashboard](docs/images/dashboard.png)
_The main dashboard with mod cards, stats filtering, and quick search_

### Forge Integration

<!-- TODO: Add screenshot of the Forge search with autocomplete dropdown -->

![Forge Search](docs/images/forge-search.png)
_Search and add mods directly from SP-Tarkov Forge with autocomplete_

### Add Mods Dialog

<!-- TODO: Add screenshot of the Add Mods dialog showing version selector -->

![Add Mods](docs/images/add-mods.png)
_Add mods with version selection and SPT compatibility info_

### Mod Management

<!-- TODO: Add screenshot of the edit mod dialog with file overwrite options -->

![Edit Mod](docs/images/edit-mod.png)
_Configure file overwrites and mod settings_

### Client Warning Screen

<!-- TODO: Add screenshot of the in-game warning dialog -->

![Client Warning](docs/images/client-warning.png)
_In-game warnings for missing or modified files_

### ModGod Updater

<!-- TODO: Add screenshot of the updater console UI -->

![Updater](docs/images/updater.png)
_Standalone updater with progress tracking_

---

## ✨ Features

### 🖥️ Server Mod

- **Modern Web UI** at `https://your-server:6969/modgod/`
- **Forge Integration** - Search and add mods directly from [SP-Tarkov Forge](https://forge.sp-tarkov.com/)
  - Debounced search-as-you-type with autocomplete
  - Version selector with SPT compatibility info
  - Or paste a Forge mod page URL directly
- **Direct URL Support** - Add mods from GitHub releases, etc.
- Supports archive formats `.zip` & `.7z`
- Auto-detect install paths for standard mod structures
- **File Overwrite Rules** - Choose which files to preserve during installs/reinstalls
- **Sync Rules** - Unified configuration for both player and headless clients:
  - Configure sync paths (source → target directory mappings)
  - Exclude specific files/folders from syncing
  - Smart default exclusion patterns for logs, cache, dev files, and common mod artifacts
  - Profile selector to switch between Player and Headless configurations
- **Stats Dashboard** - Clickable cards to filter by status (Total, Installed, Pending, Required, Optional)
- Pending changes system with visual status indicators
- **Auto-Install Scripts** - PowerShell (Windows) script that wait for server shutdown then install

### 🎮 Client Enforcer Plugin

- **File Integrity Verification** - Compares client files against server manifest using SHA256 hashes
- **In-Game Warnings** - Upon game launch, shows detailed warnings for missing, modified, or extra files
- **One-Click Updates** - Launch the updater directly from the warning dialog
- Respects sync rules and exclusions from server configuration
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
│   │   ├── ForgeService.cs          # Forge API integration
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

1. **Download the release** from the [Releases page](https://github.com/your-repo/releases)
2. **Extract and copy** the contents of the zip to your SPT installation
3. **Start your SPT server**
4. **Access the Web UI** at `<YOUR_SERVER_URL>/modgod/` eg: `https://127.0.0.1:6969/modgod/`
5. **Add mods** using search or direct download URLs

### Client Setup

1. **Extract and copy** the contents of the zip to your SPT installation
2. **Run `ModGodUpdater.exe`** and enter your server URL when prompted
3. **Launch the game** - the enforcer plugin will verify your mods

---

## 🔧 Configuration

### Server Configuration

All server configuration is stored in `<SPT_ROOT>/ModGodData/`:

| File                     | Description                                       |
| ------------------------ | ------------------------------------------------- |
| `serverConfig.json`      | Mod list, sync rules (player & headless), etc.    |
| `stagingIndex.json`      | Downloaded mod cache index                        |
| `pendingOperations.json` | Queued install/remove operations                  |
| `staging/`               | Downloaded and extracted mod files                |

### Client Configuration

Client configuration is also stored in `<SPT_ROOT>/ModGodData/`:

| File                  | Description                                         |
| --------------------- | --------------------------------------------------- |
| `ModGodClient.json`   | Server URL and settings (including `headless` flag) |
| `modsDownloaded.json` | List of downloaded mods with opt-in status          |

---

## 📖 Usage Guide

### Adding Mods (Server)

> ⚠️ **Warning:** If you download mods using a mod manager, you will NOT receive support from the SPT community. For community support, you must install mods manually.

#### Option 1: Forge Search

1. Open the Web UI at `https://your-server:6969/modgod/`
2. Click **"Add Mods"**
3. In the **"From Forge"** tab:
   - Enter your Forge API key (get one at [forge.sp-tarkov.com/user/api-tokens](https://forge.sp-tarkov.com/user/api-tokens))
   - Start typing to search for mods
   - Select a mod from the dropdown
   - Choose a version (defaults to latest)
   - Click **"Download & Stage"**

#### Option 2: Direct URLs

1. Switch to the **"Direct URLs"** tab
2. Paste direct download URLs (one per line or space-separated)
3. Click **"Download & Stage"**

#### After Adding Mods

1. Review the results, if needed make edits, and click **"Apply Changes"**
2. The auto-installer will launch and wait for server shutdown, wait for installer to complete
3. Start your server to apply the changes

### Managing File Overwrites

When installing/reinstalling a mod, you can control which files get overwritten during reinstalls:

1. Click on a mod card to open the edit dialog
2. Scroll to **"Files to be Overwritten"**
3. Uncheck any files/directories you want to preserve (e.g., `config.json`)
4. These paths will not be written to your server

When installing mods, there is a helpful alert on the card if it will overwrite any files.

### Sync Rules

The **Sync Rules** tab provides unified configuration for both player and headless clients. Use the profile selector at the top to switch between configurations.

#### Sync Paths

Configure which directories sync from your server to clients:

1. Go to the **"Sync Rules"** tab
2. Select **"Player Clients"** or **"Headless Clients"** profile
3. Add or remove sync paths (source directory on server → target directory on client)
4. Default paths are `BepInEx/plugins` and `SPT/user/mods`

#### File/Folder Exclusions

Exclude specific files or folders from syncing to prevent unnecessary client warnings:

1. In the **Sync Rules** tab, scroll to **"File/Folder Exclusions"**
2. Browse the file tree and click to toggle exclusions
3. Excluded items appear with a strikethrough
4. Click **"Apply Changes"** to save

#### Default Exclusion Patterns

ModGod includes smart default patterns that automatically exclude common files that shouldn't sync:

- Log files (`**/*.log`, `**/logs/**`)
- Cache and temp files (`**/cache/**`, `**/*.tmp`)
- Development files (`.git`, `node_modules`, TypeScript sources)
- SPT core files (clients have their own)
- Common mod artifacts (Fika cache, Realism backups, etc.)

Toggle **"Use Default Exclusions"** to enable/disable, or expand **"Advanced: Exclusion Patterns"** to customize the patterns.

### Headless Client Setup

Headless clients are dedicated raid-hosting instances (e.g., for Fika). They have their own sync configuration separate from player clients.

> ⚠️ Headless clients must be installed in a separate directory from your SPT server.

#### Server Configuration

1. Go to the **"Sync Rules"** tab
2. Switch to the **"Headless Clients"** profile
3. Configure sync paths for what headless clients need
4. Set exclusions as needed
5. Click **"Apply Changes"**

#### Headless Client Installation

1. Copy **ModGodUpdater.exe** to your headless client's SPT root folder
2. Run the updater and enter your server URL when prompted
3. Edit `ModGodData/ModGodClient.json` and set `"headless": true`
4. Run the updater again — it will sync files from the Headless profile

Example `ModGodClient.json` for headless:

```json
{
  "serverUrl": "https://your-server:6969",
  "headless": true
}
```

### Filtering Mods

- **Stats Cards**: Click on "Total Mods", "Installed", "Pending Install", "Required", or "Optional" to filter the mod list
- **Search Bar**: Type in the search box to filter mods by name

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

| Endpoint                         | Method | Description                            |
| -------------------------------- | ------ | -------------------------------------- |
| `/modgod/`                       | GET    | Web UI                                 |
| `/modgod/api/config`             | GET    | Server configuration (mod list)        |
| `/modgod/api/manifest`           | GET    | File manifest with hashes              |
| `/modgod/api/manifest/headless`  | GET    | Filtered manifest for headless clients |
| `/modgod/api/status`             | GET    | Server status check                    |
| `/modgod/api/forge/status`       | GET    | Check if Forge API key exists          |
| `/modgod/api/forge/validate-key` | POST   | Validate and save Forge API key        |
| `/modgod/api/forge/search`       | GET    | Search mods on Forge                   |
| `/modgod/api/forge/mod/{id}`     | GET    | Get mod details from Forge             |

---

## 🐧 Linux Support

ModGod fully supports Linux servers:

- On Linux, file operations are handled immediately when you click "Apply Changes" (no script needed)
- Unlike Windows, Linux doesn't lock files while the server is running, so changes apply instantly
- The updater works cross-platform via .NET

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
- Check **Sync Rules** exclusions if warnings are for server-generated files

### Web UI Not Loading

- Verify SPT server is running
- Check the URL: `https://127.0.0.1:6969/modgod/` (note: HTTPS)
- Accept the self-signed certificate warning in your browser

### Forge Search Not Working

- Ensure you have a valid Forge API key configured
- Get an API key at [forge.sp-tarkov.com/user/api-tokens](https://forge.sp-tarkov.com/user/api-tokens)
- Check that your API key has the required permissions

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
