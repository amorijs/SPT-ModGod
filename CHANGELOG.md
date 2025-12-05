# Changelog

All notable changes to ModGod will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2024-12-05

### Added

- **Server Mod**
  - Modern Blazor web UI for managing mods
  - Add mods from direct download URLs (GitHub releases, etc.)
  - Multi-format archive support (`.zip`, `.7z`, `.rar`, `.tar.gz`, etc.)
  - Auto-detect install paths for standard mod structures
  - File Overwrite Rules - preserve specific files during reinstalls
  - Sync Exclusions - exclude server-generated files from client verification
  - Visual mod status indicators (Pending, Installed, Pending Removal)
  - Optional vs Required mod designation
  - Auto-install scripts for Windows (PowerShell) and Linux (Bash)
  - REST API for client communication

- **Client Enforcer Plugin**
  - File integrity verification using SHA256 hashes
  - In-game warning dialog for mod discrepancies
  - One-click updater launch from warning dialog
  - Support for sync exclusions from server
  - Required vs optional mod distinction

- **ModGod Updater**
  - Standalone single-file executable
  - First-time setup wizard
  - Beautiful console UI with progress tracking
  - Optional mod opt-in/out with persistence
  - Multi-format archive extraction

### Technical Details

- Built with .NET 9.0 (Server & Updater)
- Client plugin targets .NET Framework 4.7.1 for BepInEx compatibility
- Self-contained updater exe (~35MB)
- Cross-platform auto-install support

[1.0.0]: https://github.com/your-repo/releases/tag/v1.0.0

