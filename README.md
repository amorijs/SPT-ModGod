# ModGod

A mod synchronization system for **SPT Tarkov 4.0** that allows server owners to easily manage and distribute mods to their players.

## Components

### 1. Server Mod (`Server/`)

A C# server mod that provides:

- **Web UI** at `https://your-server:6969/ModGod/` for managing mods
- **REST API** for clients to fetch the mod list
- Download and cache mods from direct links (supports `.zip`, `.7z`, `.rar`, etc.)
- Auto-detect sync paths for standard mod structures

### 2. Sync Client (`SyncClient/`)

A standalone `.exe` that players run to sync their mods:

- Fetches mod list from server
- Downloads and installs required mods
- Allows players to opt-in/out of optional mods
- Supports all common archive formats

### 3. Client Plugin (`Client/`)

A BepInEx plugin that enforces mod requirements on game start:

- Verifies required mods are installed and up-to-date
- Shows in-game warning if mods are missing
- Option to launch sync tool directly from the warning

## Installation

### Server Setup

1. Build the solution or download the release
2. Copy `dist/SPT/user/mods/ModGodServer/` to your SPT server's `SPT/user/mods/` folder
3. Start the server and navigate to `https://127.0.0.1:6969/ModGod/`
4. Add mods via the Web UI

### Client Setup

1. Create a `ModGod` folder in the SPT root directory
2. Copy the contents of `dist/SyncClient/` to that folder
3. Run `ModGod.exe` and enter the server URL when prompted
4. Copy `dist/BepInEx/plugins/ModGod/` to the SPT client's `BepInEx/plugins/` folder

## Building

### Prerequisites

- .NET 9.0 SDK (for Server and SyncClient)
- .NET Framework 4.7.1 Developer Pack (for Client plugin)
- SPT 4.0 installation (for reference DLLs)

### Build Commands

```bash
# Build everything
dotnet build ModGod.sln

# Build individual projects
dotnet build Server/ModGodServer.csproj
dotnet build SyncClient/SyncClient.csproj
dotnet build Client/ModGod.csproj
```

### Configuration

Update the `SPTPath` property in `Client/ModGod.csproj` to match your SPT installation path.

## Project Structure

```
ModGod/
├── Server/                    # SPT server mod
│   ├── Models/                # Data models
│   ├── Services/              # Business logic
│   ├── Web/                   # Blazor UI
│   └── wwwroot/               # Static assets
│
├── SyncClient/                # Standalone sync tool
│   ├── Models/                # Data models
│   └── Program.cs             # Main logic
│
├── Client/                    # BepInEx client plugin
│   ├── Models/                # Data models
│   └── ModGod.cs        # Main plugin
│
└── dist/                      # Build output (gitignored)
    ├── SPT/user/mods/         # Server mod
    ├── SyncClient/            # Sync tool
    └── BepInEx/plugins/       # Client plugin
```

## License

MIT License - See LICENSE file for details.
