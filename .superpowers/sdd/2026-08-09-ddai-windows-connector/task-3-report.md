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
