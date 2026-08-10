# Dungeondraft Mod Consolidation Design

**Date:** 2026-08-09
**Status:** Approved approach; written-spec review pending

## Goal

Make the Windows connector activate DDAI and Custom Snap together without using Dungeondraft's installation directory as the selected Mods root. Preserve the user's original Custom Snap installation and continue to prohibit `config.ini` writes while Dungeondraft is running.

## Observed State

- Dungeondraft `1.2.0.1` is running non-admin from `D:\DungeonDraft\Dungeondraft`.
- `config.ini` currently selects `D:\DungeonDraft\Dungeondraft\mods\custom_snap` directly and currently records `active_mods=[ ]`.
- The selected directory directly contains `snappy_mod.ddmod` with unique ID `Lievven.Snappy_Mod`.
- DDAI is installed separately at `%LOCALAPPDATA%\DDAI\DungeondraftMods\DDAI`.
- Dungeondraft expects one selected parent Mods directory whose children are individual mod folders. Therefore, switching the selected directory to the current DDAI root without consolidation would make Custom Snap unavailable.

## Approved Layout

The connector will maintain this per-user layout:

```text
%LOCALAPPDATA%\DDAI\DungeondraftMods\
├── DDAI\
│   ├── ddai_bridge.ddmod
│   └── scripts\...
└── custom_snap\
    ├── snappy_mod.ddmod
    ├── scripts\...
    └── icons\...
```

The original `D:\DungeonDraft\Dungeondraft\mods\custom_snap` directory remains unchanged. The connector copies it; it never moves, renames, edits, or deletes it.

## Discovery and Validation

The strict config editor will retain both the raw previous Mods-directory literal and its decoded absolute path in the ownership receipt. Consolidation is eligible only when all of these are true:

1. The previous path is an existing regular directory.
2. Exactly one `.ddmod` file exists directly in that directory.
3. That manifest declares `unique_id` equal to `Lievven.Snappy_Mod`.
4. The manifest is valid JSON and all source entries remain beneath the source directory.
5. The destination resolves beneath the DDAI-managed Mods root and is not a reparse point.

Unsupported, ambiguous, missing, or conflicting layouts fail closed before `config.ini` changes.

## Copy Transaction and Ownership

A dedicated `DungeondraftModConsolidator` will plan and apply the Custom Snap copy:

- Copy to a same-parent temporary directory under the managed Mods root.
- Copy regular files and ordinary directories only; reject reparse points and links.
- Verify the staged relative-file list, sizes, and SHA-256 hashes against the source.
- If `custom_snap` is absent, atomically rename the verified stage into place.
- If `custom_snap` already exists and is byte-identical, report `already_current` and write nothing.
- If it exists but differs, fail closed and preserve both source and destination.
- Clean DDAI-created stages on every success or handled failure.

Installation metadata will retain an optional consolidation receipt containing the absolute source path, absolute destination path, and mod ID. Repeated setup preserves the first receipt. Legacy metadata remains readable.

The copied Custom Snap directory is treated as user-preserving content. Uninstall removes only the owned `DDAI` directory and restores the prior `mods_directory` from the activation receipt; it does not delete the copied Custom Snap directory.

## Activation Flow

Setup proceeds as follows:

1. Validate the source executable, DDAI mod, and AI-client configurations before mutation.
2. Install or repair the owned connector and DDAI mod, and configure Claude and Gemini as before.
3. If Dungeondraft is running, stop here with `activation_pending_dungeondraft_running`; do not read `config.ini` for activation, copy Custom Snap, or write `config.ini`.
4. When Dungeondraft is closed, validate Dungeondraft configuration, consolidation source, and any existing consolidation destination.
5. Copy or verify Custom Snap in the managed root.
6. Atomically update `config.ini` so `active_mods` contains exactly one `Lievven.Snappy_Mod` and exactly one `org.ddai.status_bridge`, while preserving every unrelated ID and setting.
7. Select `%LOCALAPPDATA%\DDAI\DungeondraftMods`, persist both ownership receipts, and report the exact backup.
8. The user launches Dungeondraft normally. Only a fresh DDAI heartbeat proves runtime activation.

DDAI never closes, restarts, or controls Dungeondraft.

## Diagnosis

Diagnosis keeps the existing ownership and heartbeat ordering and adds consolidation checks:

- Missing or conflicting Custom Snap copy: `activation_pending_mod_consolidation`.
- Running without heartbeat: `activation_pending_dungeondraft_running`.
- Closed with missing/invalid config: existing config-missing or mismatch codes.
- Correct config without heartbeat: `configured_waiting_for_reload`.
- Fresh heartbeat: `runtime_heartbeat_fresh`.

Status remains a real mailbox round trip and must not report success from configuration presence alone.

## Tests and Acceptance

Automated tests will prove:

- exact Custom Snap copy and hash verification;
- source byte preservation;
- idempotence without a second copy or metadata rewrite;
- rejection of symlinks/reparse points, traversal, ambiguous manifests, and conflicting destinations;
- running-app setup defers consolidation and leaves `config.ini` byte-exact;
- closed-app setup preserves Custom Snap and activates both IDs exactly once;
- uninstall restores the prior directory, removes only DDAI, and leaves both original and copied Custom Snap content;
- legacy receipts fail closed without deleting or overwriting user content;
- published single-file setup emits one JSON document and preserves protocol-clean stdout.

Current-machine acceptance requires this sequence:

1. Leave the current Dungeondraft process untouched while installing the new connector build; consolidation remains pending.
2. User closes Dungeondraft normally.
3. Run setup once and verify the exact `config.ini` backup and both active IDs.
4. User launches Dungeondraft normally and opens a map.
5. Require `runtime_heartbeat_fresh` and a correlated `status` response while Custom Snap remains available.

Native map construction remains a later slice; a successful status round trip proves only the connector and bridge transport.
