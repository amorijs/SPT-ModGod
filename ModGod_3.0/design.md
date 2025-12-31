# ModGod 3.0 Design Document

## Overview

ModGod 3.0 represents a significant architectural overhaul focused on simplifying the system by eliminating dual sources of truth. This document captures the design decisions, rationale, and considerations for the v3.0 release.

---

## Problem Statement

### Current State (v2.x)

ModGod currently maintains **two sources of truth**:

1. **Internal mod list** (`ModEntry` objects) - Tracks what was downloaded, download URLs, install paths, optional status, etc.
2. **Actual files on disk** - The real state of installed mods

This creates inherent complexity:
- State can drift between the internal list and filesystem
- Reconciliation logic is needed to detect and warn about mismatches
- Manual installations aren't automatically recognized
- More code surface area for bugs

### Goal

Eliminate the dual source of truth by making the **filesystem authoritative**. Metadata becomes supplementary annotations, not a competing source of truth.

---

## Core Design Philosophy

```
Filesystem = what exists (authoritative)
Metadata = how to treat it (supplementary)
```

If a file/directory exists on disk, it exists. Period. Metadata provides additional context (display name, optional flag, linked items) but never contradicts the filesystem.

**Key insight**: We don't need to track "what was downloaded" if we can see "what exists."

---

## Architecture

### Sources Model

The concept of "mods" is replaced with "source items." A source is a directory that ModGod manages (e.g., `BepInEx/plugins`, `SPT/user/mods`). Source items are the top-level files or directories within a source.

```
Source: "Server Mods" (SPT/user/mods)
├── SAIN/           <- source item
├── Fika/           <- source item
└── some-mod.dll    <- source item (single file)

Source: "Client Mods" (BepInEx/plugins)
├── SAIN/           <- source item (can be linked to above)
└── other-plugin/   <- source item
```

### Pure Function Rendering

The Sources tab renders as a pure function:

```
UI = render(filesOnDisk, sourcesMetadata)
```

- Scan the filesystem to find what exists
- Apply metadata for display names, optional flags, links
- Items without metadata use sensible defaults
- Orphaned metadata entries are ignored (or pruned on save)

**Rationale**: This eliminates reconciliation entirely. The UI always reflects reality.

---

## Metadata Structure

Single file: `ModGodData/modgod.json`

```json
{
  "sources": {
    "SPT/user/mods/SAIN": {
      "displayName": "SAIN - AI Overhaul",
      "optional": false,
      "linkedTo": ["BepInEx/plugins/SAIN"]
    },
    "BepInEx/plugins/SAIN": {
      "displayName": "SAIN - AI Overhaul",
      "optional": false,
      "linkedTo": ["SPT/user/mods/SAIN"]
    },
    "SPT/user/mods/SomeOptionalMod": {
      "displayName": "Some Optional Mod",
      "optional": true,
      "linkedTo": []
    }
  },
  "syncRules": {
    "exclusions": ["*.log", "cache/**", "*/node_modules/**"],
    "additionalRoots": []
  }
}
```

### What We Store

| Field | Purpose | Default if missing |
|-------|---------|-------------------|
| `displayName` | User-friendly name for UI | Directory/file name |
| `optional` | Whether clients can opt out | `false` (required) |
| `linkedTo` | Related items in other sources | `[]` (no links) |

### What We Don't Store

| Field | Why Not |
|-------|---------|
| `downloadUrl` | Adds complexity; reinstall via Health Check instead |
| `lastUpdatedAt` | Hash comparison handles sync; timestamp is redundant |
| `installPaths` | Filesystem IS the install paths |
| `status` | Derived from filesystem state |

**Rationale**: Minimize stored state. If it can be derived, don't store it.

---

## UI/UX Changes

### Tab Structure

| Tab | Purpose |
|-----|---------|
| **Sources** | View/manage source items, health status, actions, per-item sync rules |
| **Sync Rules** | Top-level view of all sync configuration |
| **Settings** | Server configuration, default install paths |

**Moved**: Forge Search is now part of "Add Mod" button flow, not a separate tab

### Sources Tab (Main View)

```
┌─ Server Mods (SPT/user/mods) ──────────────────────────────┐
│                                                             │
│  ▼ SAIN - AI Overhaul                       [v3.1.0] ✓     │
│      Linked: BepInEx/plugins/SAIN                          │
│      [Reinstall] [Mark Optional]                           │
│                                                             │
│  ▼ Fika                                     [scanning...]  │
│      ...                                                    │
│                                                             │
│  ► SomeOptionalMod                          [Optional] ✓   │
│                                                             │
│                              [Add Mod] [Rescan All]        │
└─────────────────────────────────────────────────────────────┘
```

**Features**:
- Source items render immediately from filesystem
- Health status (version, update available) loads async per-item
- User triggers scan via "Rescan All" button
- Actions (reinstall, update, mark optional, remove) available inline
- Tooltip on scan: "Scan for updates, addons, etc."
- **Per-item sync rules**: Expanding a source item shows its file tree with sync rule controls (include/exclude files/directories)

**Rationale**: Merging Health Check into Sources puts everything about a mod in one place. No context switching. Per-item sync rules provide quick access without leaving the Sources tab.

### Multi-Path Linking

Some mods install to multiple directories (e.g., SAIN has both server and client components).

**Behavior**:
- Auto-detect on add: if same download contents appears in different sources, auto link them
- Manual control: users can create/edit/remove links
- Warning: if a linked item is missing on disk, show warning on the source item
- Delete: if user removes a linked item, prompt to remove all linked items

**Rationale**: This is a tradeoff. We lose the single-ModEntry-multiple-paths elegance, but gain filesystem-as-truth simplicity. Linking is a UX convenience layer, not a data model requirement.

---

## Sync Rules Tab

Top-level view of all sync configuration across all sources.

```
┌─ Sync Configuration ───────────────────────────────────────┐
│                                                             │
│  Sync Roots:                                                │
│    ✓ SPT/user/mods                                         │
│    ✓ BepInEx/plugins                                       │
│    + Add sync root                                         │
│                                                             │
│  Global Exclusions:                                         │
│    • *.log                                                  │
│    • cache/**                                               │
│    • */node_modules/**                                      │
│    + Add exclusion                                         │
│                                                             │
│  [View Full File Tree]                                     │
└─────────────────────────────────────────────────────────────┘
```

### Sync Rules: Two Entry Points, Same Data

Users can edit sync rules from two places:

| Entry Point | Scope | Use Case |
|-------------|-------|----------|
| **Sources Tab** | Per-item subtree | "I want to exclude some files from this specific mod" |
| **Sync Rules Tab** | All sources, full tree | "I want to see everything that's being synced" |

Both views read/write the same underlying `syncRules` data. Changes in one are reflected in the other.

**Rationale**: Sources tab provides quick, contextual access. Sync Rules tab provides the complete picture. Users choose based on their task.

---

## Settings Tab

### Default Install Paths

When adding a new mod (via Forge or manual upload), ModGod needs to know where to install the archive contents.

**Behavior**:
1. User initiates "Add Mod" flow (Forge search or file upload)
2. ModGod extracts/inspects archive contents
3. Archive paths are matched against **default install paths** (e.g., `BepInEx/plugins`, `SPT/user/mods`)
4. If a match is found, install destination is auto-populated
5. User reviews and can edit final install paths before confirming
6. On confirm, files are extracted and source item metadata is created (including auto-detected links)

**Example**:
```
Archive contains:
  BepInEx/plugins/MyMod/MyMod.dll
  SPT/user/mods/MyMod/package.json

Default install paths configured:
  - BepInEx/plugins
  - SPT/user/mods

Result: Auto-populates both destinations, auto-links the two source items
```

**Rationale**: Same behavior as v2.x. Default install paths reduce friction when adding mods while preserving user control over final destinations.

---

## Updater Script Changes

### Current Flow (v2.x)

1. Show required mods (from ModEntry list)
2. Let user select optional mods
3. Download mods from their download URLs
4. File verification (hash comparison)

### New Flow (v3.0)

1. Show required source items (informational)
2. Let user select optional source items
3. File verification downloads directly from server

**Key change**: No separate "mod download" step with individual archives. The file verification step handles everything via manifest-based sync.

**Rationale**: 
- Simpler flow (one sync mechanism instead of two)
- Server load is acceptable (streaming files vs serving archives)
- No need to track download URLs

### Client Selections

Client sends opted-in optional mods when fetching manifest:

```
GET /modgod/api/manifest?optedIn=SAIN,Fika,SomeOtherMod
```

Server returns filtered manifest containing only:
- Required source items (always included)
- Opted-in optional source items

**Rationale**: Server-side filtering keeps clients simple. They just sync what's in the manifest.

---

## Client Enforcer Changes

### Current Behavior

1. Fetch manifest from server
2. Verify all files in manifest (existence + hash)
3. Scan for extra files
4. Show warning if issues found

### v3.0 Behavior

Same logic, but:
- Client sends opted-in selections with manifest request
- Server returns filtered manifest
- Client enforcer verifies filtered manifest (no client-side filtering needed)

**Rationale**: Client enforcer stays dead simple. All the complexity of "which optional mods did this client choose" lives on the server or in the manifest request.

---

## Health Check Integration

### Current State

Separate Health Check tab that:
- Scans DLL metadata for version info
- Checks Forge for updates and addons
- Allows reinstall (requires stored download URL)

### v3.0 State

Health Check merged into Sources tab:
- Scan triggered by user ("Rescan All" button)
- Results displayed inline on each source item
- Reinstall searches Forge by mod name (no stored URL needed)
- Update available shown if Forge has newer version
- Tooltip on the button: "Scan for updates and addons"

**Reinstall flow**:
1. User clicks "Reinstall" on a source item
2. Health Check identifies mod (via DLL metadata)
3. Search Forge for that mod
4. Download and overwrite (no need to remove first)

**Rationale**: Removing download URL storage simplifies metadata. Health Check can identify mods via DLL scanning, so we can always find them on Forge.

---

## Migration Path (v2.x to v3.0)

### Server Migration

This should follow a similar UX pattern to how we handled migration from 1.0 to 2.0. Please reference the code for this UX.

1. Read existing `ModEntry` list
2. Scan filesystem for actual source items
3. Generate `sources` metadata:
   - `displayName` from `ModEntry.ModName`
   - `optional` from `ModEntry.Optional`
   - `linkedTo` inferred from `ModEntry.InstallPaths` (if multiple)
4. Generate `syncRules` from existing sync path configuration

### Client Migration

1. Delete `modsDownloaded.json` (no longer needed)
2. Client config (`ModGodClient.json`) remains the same
3. On first run, client will sync based on manifest

---

## What Stays the Same

| Component | Changes |
|-----------|---------|
| Forge API integration | No changes |
| Archive extraction | No changes |
| File hashing (SHA256) | No changes |
| Manifest structure | Minor updates for source items |
| Client config | No changes |
| Warning GUI | No changes |

---

## Open Questions / Future Considerations

1. **Partial scan**: Should we support scanning individual source items, or always "Rescan All"? (Decided: always "Rescan All" for now)

2. **Scan caching**: Should we cache scan results with a "last scanned" timestamp? (Decided: skip for now)

3. **Link conflicts**: What if user manually creates conflicting links? (Decided: allow it, show warning)

4. **Source ordering**: Should users be able to reorder sources in the UI? (Decided: no, for now)

5. **Bulk operations**: Select multiple source items for bulk mark-optional, bulk remove? (Decided: no, for now)

---

## Summary

ModGod 3.0 simplifies the architecture by:

1. **Eliminating dual sources of truth** - Filesystem is authoritative
2. **Reducing stored metadata** - Only store what can't be derived
3. **Merging related features** - Health Check into Sources tab
4. **Simplifying client flow** - Server-filtered manifests

The result is a more maintainable codebase with fewer state synchronization bugs and a cleaner mental model for users.
