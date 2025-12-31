# ModGod 3.0 Implementation Summary

## Project Overview
ModGod is a mod synchronization solution for SPT Tarkov servers. Version 3.0 is a complete architectural overhaul located in `ModGod_3.0/` directory.

**CRITICAL CONSTRAINT**: NEVER modify files outside of `ModGod_3.0/` directory. The root level v2.x implementation must remain as reference.

---

## Architecture (v3.0)
- **Filesystem is authoritative** - no dual source of truth
- **`modgod.json`** stores only: sources metadata (displayName, optional, linkedTo) + syncRules
- **Manifest generated** from scanning filesystem + applying metadata
- **Client sends** `?optedIn=path1,path2` to get filtered manifest
- **Health Check merged** into Sources tab (no separate tab)
- **No stored downloadUrl** - reinstall searches Forge by mod name/GUID
- **"Mods" replaced with "source items"** - top-level files/directories in sync roots

Read `ModGod_3.0/design.md` for the full design specification.

---

## Current State
All three projects compile successfully via `ModGod_3.0/ModGod3.sln`. The Blazor UI loads correctly.

### Project Structure

1. **Server** (`ModGod_3.0/Server/ModGodServer.csproj`)
   - Output: `dist/SPT/user/mods/ModGodServer/`
   - Implements `IModWebMetadata` for SPT's Blazor support
   - Dependencies bundled: `SharpCompress.dll`, `System.Reflection.MetadataLoadContext.dll`, `ZstdSharp.dll`

2. **Updater** (`ModGod_3.0/ModGodUpdater/ModGodUpdater.csproj`)
   - Output: `dist/ModGodUpdater.dll`
   - Manifest-based sync with optional item selection

3. **Client** (`ModGod_3.0/Client/ModGodClientEnforcer.csproj`)
   - Output: `dist/BepInEx/plugins/ModGodClientEnforcer/`
   - BepInEx plugin for file verification

### Services Implemented (`ModGod_3.0/Server/Services/`)
| Service | Description |
|---------|-------------|
| `ConfigService.cs` | Config management, source item scanning |
| `ManifestService.cs` | File manifest generation |
| `MigrationService.cs` | v2.x to v3.0 migration |
| `GlobMatcher.cs` | Glob pattern matching for sync rules |
| `ForgeService.cs` | Forge API integration (search, mod details, addons, dependencies) |
| `ModDownloadService.cs` | Archive download & extraction with 7z support |
| `ModHealthService.cs` | DLL scanning for version detection, Forge update checking |

### HTTP Endpoints (`ModGod_3.0/Server/ModGodServer.cs`)

**Core API:**
- `GET /modgod/api/status` - Server status
- `GET /modgod/api/config` - Get config
- `POST /modgod/api/config` - Update config
- `GET /modgod/api/manifest` - Get file manifest (supports `?optedIn=`)
- `GET /modgod/api/file` - Download file

**Migration:**
- `GET /modgod/api/migration/check` - Check for v2.x data
- `POST /modgod/api/migration/run` - Run migration

**Source Items:**
- `GET /modgod/api/sources` - List source items
- `POST /modgod/api/sources/{path}/optional` - Toggle optional
- `DELETE /modgod/api/sources/{path}` - Delete source item
- `PUT /modgod/api/sources/{path}` - Update source item metadata

**Forge:**
- `GET /modgod/api/forge/status` - Check API key status
- `POST /modgod/api/forge/validate-key` - Validate and save API key
- `GET /modgod/api/forge/api-key` - Get current API key
- `DELETE /modgod/api/forge/api-key` - Remove API key
- `GET /modgod/api/forge/search` - Search mods
- `GET /modgod/api/forge/mod/{id}` - Get mod details
- `GET /modgod/api/forge/mod/{id}/addons` - Get mod addons
- `GET /modgod/api/forge/addon/{id}/versions` - Get addon versions

**Health:**
- `GET /modgod/api/health` - Run health check

### Blazor UI (`ModGod_3.0/Server/Web/`)
| Component | Description |
|-----------|-------------|
| `Pages/Home.razor` | Main page with tabs |
| `Shared/MainLayout.razor` | Layout with MudBlazor theme |
| `Components/Sources/SourcesTab.razor` | Source items list with health check integration |
| `Components/Sources/SourceItemCard.razor` | Individual card with version, update status, actions |
| `Components/SyncRules/SyncRulesTab.razor` | Sync rules management |
| `Components/Settings/SettingsTab.razor` | Settings |
| `Components/Dialogs/EditSourceItemDialog.razor` | Edit source item metadata |
| `Components/Dialogs/MigrationDialog.razor` | Migration wizard |

### Models (`ModGod_3.0/Server/Models/`)
- `ModGodConfig.cs` - Main config with sources metadata
- `SourceItem.cs` - Source item with health status
- `FileManifest.cs` - File manifest for sync
- `LegacyModels.cs` - v2.x models for migration

---

## What Remains To Implement

### High Priority
1. **Implement Update flow** - Currently shows "coming soon" in UI
   - Download new version from Forge using `ModDownloadService`
   - Extract and replace files
   - Handle file overwrites safely

2. **Implement Reinstall flow** - Currently shows "coming soon"
   - Search Forge by mod name/GUID
   - Download and extract to original location

3. **Add Mod dialog** - Forge search + direct URL download
   - Reference: v2.x `Server/Web/Components/Dialogs/AddModsDialog.razor`

### Medium Priority
4. **View Files dialog** - Show file tree for source item
   - Reference: v2.x `Server/Web/Components/Shared/FileTreeView.razor`

5. **Sync Rules per-item** - Expandable file tree with include/exclude controls
   - Allow setting sync rules on individual source items

6. **Settings Tab completion**
   - Default install paths editor
   - Forge API key management UI

### Low Priority
7. **Stats bar** - Show sync statistics
8. **Search/filter** - Filter source items in UI
9. **Loading states** - Better loading indicators

---

## Key Differences from v2.x

| Aspect | v2.x | v3.0 |
|--------|------|------|
| Source of truth | `modgod.json` stores full mod entries | Filesystem is authoritative, metadata supplementary |
| Mod tracking | `TrackedMods` list with URLs | `Sources` metadata keyed by path |
| Health Check | Separate tab | Integrated into Sources tab |
| Naming | "Mods" | "Source Items" |

---

## Reference Files (v2.x - DO NOT MODIFY)
Use these as reference for porting functionality:
- `Server/ModGodServer.cs` - HTTP endpoints pattern
- `Server/Services/` - Service implementations
- `Server/Web/Components/` - UI component patterns
- `Server/Web/Components/Dialogs/AddModsDialog.razor` - Add mod flow

---

## Build Commands
```bash
# Build all projects
dotnet build ModGod_3.0/ModGod3.sln

# Build server only
dotnet build ModGod_3.0/Server/ModGodServer.csproj

# Output goes to ModGod_3.0/dist/
```

---

## Instructions for Continuing

If you have questions about:
- The v3.0 architecture or design decisions → Read `ModGod_3.0/design.md`
- How v2.x implements a feature → Check the corresponding file in the root `Server/` directory
- Specific implementation details → Ask the user

Otherwise, proceed with implementing the remaining features listed above.
