# Task 3 report: Dungeondraft mod and live status bridge

## Scope delivered

- Added redistributable `mods/DDAI` package with `ddai_bridge.ddmod`, targeting Dungeondraft `1.2.0.1`, and `scripts/ddai_bridge.gd` with `start()` and `update(delta)`.
- The bridge polls only `user://ddai` (`requests`, `processing`, `responses`, `failed`, and `journal`); it creates no network listener.
- `status` returns the mod version, target/runtime Dungeondraft-version availability, map-loaded state, current level, dimensions, revision, active-mod availability/list, and supported command names. Fields that cannot be safely obtained from the documented runtime surface remain `null` or explicitly unavailable.
- `start()` writes `user://ddai/runtime-receipt.json`. Unsupported commands receive an `unsupported_command` response; malformed but uncorrelatable envelopes receive a structured `failed` record with `malformed_request`.
- Added `tools/Install-DDAIMod.ps1`: it accepts an explicit mod directory, copies only the uniquely owned `DDAI` target folder, validates its manifest before updating an existing target, never edits binaries/PCK/maps/purchased assets, and is idempotent. Diagnosis reports missing, malformed, version-mismatched, running, or installed-but-unobserved runtime states.

## Red-green evidence

1. RED: `DungeondraftModPackageTests.Package_TargetsDungeondraft1201AndImplementsMailboxStatusBridge` failed because the `.ddmod` manifest was absent.
2. GREEN: the same package test passed after adding the manifest and mailbox bridge.
3. RED: two filesystem integration tests failed because `tools/Install-DDAIMod.ps1` was absent.
4. GREEN: both passed after implementation. During their first green run, TDD debugging corrected three concrete compatibility defects: two-character backslash-to-char conversion, invalid `Split-Path -LiteralPath -Parent` binding, and unavailable `Get-FileHash`; the final implementation uses .NET path and SHA-256 APIs.

## Final verification

```
dotnet test tests\DDAI.Core.Tests\DDAI.Core.Tests.csproj -c Release --no-restore
Passed: 38, Failed: 0, Skipped: 0

dotnet build src\DDAI.Core\DDAI.Core.csproj -c Release --no-restore
Build succeeded. 0 Warning(s), 0 Error(s)
```

## Live evidence and safety boundary

- Verified Dungeondraft executable version: `1.2.0.1`.
- Installed only `D:\DungeonDraft\Dungeondraft\mods\DDAI`; installer returned `state=installed` and did not target `custom_snap`.
- Post-install diagnostic: `state=installed_not_observed`, `code=runtime_receipt_missing`.
- Dungeondraft remained running as PID `43208` throughout; no restart, close, reload, or map mutation was attempted.

## Remaining live-test blocker

The active Dungeondraft window is an unsaved `Tabula Rasa*` map. The newly installed mod cannot write its start receipt or service a status request until the user saves as appropriate and reloads/restarts Dungeondraft through normal UI controls. No live status round trip is claimed.

## Fix round 1 (review findings closed)

- Invalid or noncanonical envelopes now always write a structured `failed` record and never publish a correlated response. The GDScript validates the canonical SHA-256 filename before any status handling, rejects `payload: null`, and accepts only valid RFC 3339-compatible timestamps.
- Start receipts are unique session records and the mod writes atomically published session heartbeats every ten seconds. Diagnostics call the mod `running` only for a matching heartbeat newer than 30 seconds; stale, missing, malformed, or mismatched heartbeat data remains `installed_not_observed`.
- The installer compares the owned target file set exactly. A valid owned target with an extra `.gd` now undergoes staged replacement and is moved to a recoverable backup under `user-data\ddai\mod-backups`; no target-only executable script survives the replacement.
- RED: four focused review tests failed against the original bridge/installer (canonical response path, fresh/stale heartbeat behavior, and target-only script). GREEN: focused static/integration tests passed 6/6; three real `AtomicMailbox` conformance cases cover filename mismatch, null payload, and invalid timestamp.
- Final fix-round verification: Release tests passed `44/44`; Release build passed with `0` warnings/errors.
- Reinstalled only `D:\DungeonDraft\Dungeondraft\mods\DDAI`; installer returned `state=repaired` with backup `C:\Users\ChrisBoyd\AppData\Roaming\Dungeondraft\ddai\mod-backups\DDAI-20260809T2220438229517-5b4e3dbd99d14140aeebaeb691c0338f`. Diagnostics now return `installed_not_observed` / `runtime_heartbeat_missing` until a normal UI reload occurs.

## Fix round 2

- Heartbeat elapsed time advances on every `update(delta)` call; the 10-second cadence is inside the 30-second diagnostic freshness window.
- Response publication distinguishes `created`, verified byte-equivalent `idempotent`, and `response_conflict`; conflicts leave the preoccupied response untouched and move the claim to a structured failed record.
- Heartbeats use a bounded flat set (maximum eight files). Diagnosis reads only the newest eight nonrecursive files and rejects timestamps more than five seconds in the future or older than 30 seconds.
- RED tests covered timer ordering and future heartbeat misclassification; final Release tests passed `45/45` and the build had `0` warnings/errors.
- Reinstalled only `D:\DungeonDraft\Dungeondraft\mods\DDAI` through staged owned-folder replacement; backup: `C:\Users\ChrisBoyd\AppData\Roaming\Dungeondraft\ddai\mod-backups\DDAI-20260809T2226479314321-c3753691c32e49f19bef31d9c2a6ea41`. Diagnosis remains `installed_not_observed` / `runtime_heartbeat_missing` pending a user-approved UI reload.

## Fix round 3

- A processing claim is removed only for `created` or `verified_idempotent` response results. `response_conflict` and `write_failed` produce diagnostics while retaining the canonical claim for recovery.
- Startup recovery validates stranded processing envelopes, routes invalid claims to `failed`, and atomically requeues valid unresolved claims without overwriting existing request files.
- Heartbeats rotate through eight fixed monotonic slots rather than wall-clock-derived retention ordering. Diagnosis remains bounded to eight flat records and validates freshness/future skew.
- RED static contract check failed before the response/recovery/slot changes; Release verification after the changes passed `45/45` tests and the build had `0` warnings/errors.
- Reinstalled only the DDAI-owned folder; backup: `C:\Users\ChrisBoyd\AppData\Roaming\Dungeondraft\ddai\mod-backups\DDAI-20260809T2233063611179-bdf7e6ded4764086b10e483d00ff24af`. Diagnosis is still `installed_not_observed` / `runtime_heartbeat_missing` pending normal UI reload.
