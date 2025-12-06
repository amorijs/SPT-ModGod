# Changelog

All notable changes to ModGod will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2024-12-06

### Added

- **Server Mod**

  - Modern Blazor web UI for managing mods at `https://server:6969/modgod/`
  - **Forge Integration**
    - Search mods directly from SP-Tarkov Forge with autocomplete
    - Debounced search-as-you-type (400ms)
    - Version selector with SPT compatibility info
    - Paste Forge mod page URLs for quick import
    - Secure API key storage in server config
  - Add mods from direct download URLs (GitHub releases, etc.)
  - Multi-format archive support (`.zip`, `.7z`)
  - Auto-detect install paths for standard mod structures
  - **Stats Dashboard** - Clickable cards to filter by status
    - Total Mods, Installed, Pending Install, Required, Optional
  - **Quick Search** - Filter mod list by name
  - File Overwrite Rules - preserve specific files during reinstalls
  - Sync Exclusions - exclude server-generated files from client verification
  - Visual mod status indicators (Pending, Installed, Pending Removal)
  - Optional vs Required mod designation
  - ModGod self-distribution - clients can download ModGod itself from the server
  - Auto-install scripts for Windows (PowerShell) and Linux (Bash)
  - REST API for client communication and Forge integration

- **Client Enforcer Plugin**

  - File integrity verification using SHA256 hashes
  - In-game warning dialog for mod discrepancies
  - Grouped warnings by mod folder for clarity
  - One-click updater launch from warning dialog
  - Support for sync exclusions from server
  - Required vs optional mod distinction

- **ModGod Updater**
  - Standalone single-file executable (~35MB)
  - First-time setup wizard for server URL
  - Beautiful console UI with progress tracking (Spectre.Console)
  - Optional mod opt-in/out with persistence
  - Multi-format archive extraction

### Technical Details

- Built with .NET 9.0 (Server & Updater)
- Client plugin targets .NET Framework 4.7.1 for BepInEx compatibility
- Self-contained updater exe
- Cross-platform auto-install support (Windows & Linux)
- Forge API integration with secure key validation

[1.0.0]: https://github.com/your-repo/releases/tag/v1.0.0
