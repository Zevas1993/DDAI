# Dungeondraft Mod Activation Design

## Purpose

DDAI setup must activate the local Dungeondraft bridge with as little manual work as possible while protecting open maps and unrelated Dungeondraft settings. The certified target is Dungeondraft 1.2.0.1 on Windows.

The approved policy is closed-app configuration: DDAI never closes or restarts Dungeondraft. If Dungeondraft is running, setup may install or repair DDAI-owned files and AI-client entries, but it does not edit Dungeondraft configuration and reports activation as pending. Rerunning setup after Dungeondraft is closed completes activation. Once activated, normal Dungeondraft launches require no recurring setup step.

## Configuration Contract

Dungeondraft persists mod selection in `%APPDATA%\Dungeondraft\config.ini` under `[Mods]`:

```ini
[Mods]
active_mods=[ "Lievven.Snappy_Mod" ]
mods_directory="D:\\DungeonDraft\\Dungeondraft\\mods\\custom_snap"
```

Setup changes only these two keys:

- `mods_directory` becomes the absolute DDAI-managed custom Mods root, normally `%LOCALAPPDATA%\DDAI\DungeondraftMods`.
- `active_mods` retains every existing unique ID in its original order and adds `org.ddai.status_bridge` exactly once.

Custom Snap (`Lievven.Snappy_Mod`) and all other active mod IDs are preserved. All sections, keys, comments, whitespace outside the two owned values, text encoding, BOM, and newline convention remain unchanged. If `[Mods]` or either key is absent, DDAI inserts only the missing structure using the file's existing newline convention.

The parser accepts the Godot configuration syntax observed in Dungeondraft 1.2.0.1: a double-quoted string for `mods_directory` and a bracketed, comma-separated array of double-quoted strings for `active_mods`. Backslash and quote escaping must round-trip. Duplicate IDs are retained except that duplicate DDAI IDs collapse to one. Unsupported or ambiguous syntax fails closed without rewriting the file.

## Components and Data Flow

`DungeondraftConfigActivator` is an isolated component with three operations:

1. `Plan` reads the exact bytes, detects encoding/newlines, parses `[Mods]`, and returns the proposed bytes plus an ownership receipt. It performs no writes.
2. `Apply` verifies that the source hash still matches the planned hash, writes a same-directory stage, creates an exact-byte timestamped sibling backup, and atomically replaces the configuration.
3. `Uninstall` removes only `org.ddai.status_bridge` from the current active-mod list. It restores the prior `mods_directory` value only when the current value still equals DDAI's managed root and the install receipt proves the prior value. A user-modified directory is preserved.

`LocalSetupService` runs all existing source, mod, and Claude/Gemini preflights before requesting an activation plan. A process probe determines whether any `Dungeondraft` process is running. When running, configuration activation is skipped and the structured setup result is `activation_pending_dungeondraft_running`. When closed, the planned activation is applied after DDAI-owned files and AI-client configuration have succeeded.

The install metadata records the Dungeondraft configuration path, managed Mods root, prior `mods_directory` presence/value, expected post-setup value, and the DDAI mod ID. It does not store unrelated configuration contents. Exact backups remain the recovery source.

## Failure and Recovery Behavior

- Missing `config.ini`: setup leaves Dungeondraft configuration untouched and reports `activation_pending_config_missing`, instructing the user to launch Dungeondraft once, close it, and rerun setup.
- Running Dungeondraft: no configuration read-modify-write occurs; setup reports activation pending and never sends close or restart input.
- Malformed or unsupported `[Mods]` values: activation fails before any Dungeondraft configuration write and names the offending key.
- Concurrent modification: the pre-write hash check aborts without replacing the new user version.
- Stage, backup, replace, or verification failure: the original configuration remains or is restored from the exact backup; the stage is cleaned when possible.
- Idempotent setup: if the directory and DDAI ID are already correct, no write and no additional backup occur.
- Uninstall without ownership proof: DDAI files and client entries follow their existing ownership rules, but Dungeondraft configuration is not changed.

Setup and uninstall results expose configuration state separately from connector, mod, and AI-client state so a partially activated installation is never reported as fully ready.

## Diagnosis

Diagnosis distinguishes:

- `activation_pending_dungeondraft_running`
- `activation_pending_config_missing`
- `activation_pending_config_mismatch`
- `configured_waiting_for_reload`
- `runtime_heartbeat_fresh`

`runtime_heartbeat_fresh` remains the only proof that the running Dungeondraft instance loaded the bridge. Merely finding the mod files or configuration values is not runtime proof.

## Verification

Automated tests must cover:

- preserving Custom Snap, unrelated IDs, sections, comments, encoding, BOM, and CRLF/LF;
- missing section and missing-key insertion;
- escaping and exact DDAI deduplication;
- malformed arrays, malformed strings, duplicate keys, and unsupported syntax failing closed;
- running-process and missing-config pending states with zero configuration writes;
- exact-byte backup, same-directory atomic replace, concurrent-change rejection, rollback, and idempotence;
- uninstall removing only DDAI and conditionally restoring the prior directory;
- CLI structured results and current setup/diagnosis compatibility;
- full Release regression, self-contained publish, and protocol-clean MCP tests.

Live acceptance requires Dungeondraft to be closed for setup, then launched normally with a map open. DDAI must observe a fresh runtime receipt and heartbeat and complete a real `ddai_status` round trip. Custom Snap must remain active. No Dungeondraft executable, PCK data, map, or purchased asset content is modified or redistributed.
