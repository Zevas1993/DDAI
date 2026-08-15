# DDAI job-scoped undo report

## Outcome

The connector now exposes `ddai_undo_last_job` as a ninth MCP tool and routes it through a separate durable `undo_last_job` mailbox command. The bridge reverses only the native node identifiers and surface evidence recorded for one completed DDAI job. It does not invoke Dungeondraft's generic Undo command.

Undo is accepted only when the target request, map identity, current map-job revision, current state fingerprint, recorded operations, and all recorded native nodes still match. Intervening edits, missing nodes, changed maps, malformed evidence, open-world responses, and publication failures fail closed. A restart after the durable reversing transition but before the native call returns `outcome_unknown` without replaying the destructive operation. Completed retries return the byte-correlated prior response even after the successful undo advanced the live revision.

## Duplicate-mod incident

The screenshot warning was caused by a timestamped DDAI backup under the active Mods root. The active `%LOCALAPPDATA%\DDAI\DungeondraftMods` directory now contains exactly one `DDAI` directory. Installation code and PowerShell installation both place recoverable backups under `%APPDATA%\Dungeondraft\ddai\mod-backups`, outside Dungeondraft's scanned Mods tree.

## Verification

- Exact pinned Godot 3.5.3 behavior: completed job reversed, same undo replayed idempotently, intervening edit rejected, missing node rejected, and restart-before-reversal returned unknown without deleting the wall.
- Focused undo/MCP verification: 18/18 passed.
- Published ModelContextProtocol 1.4.1 acceptance: raw JSON-RPC and official SDK both discover and call all nine tools; stdout remains protocol-only.
- Full Release Core suite: 426/426 passed.
- Full Release App suite: 362/362 passed.
- Release build: 0 warnings and 0 errors.
- Canonical retained single-file artifact: `artifacts/task6/win-x64/ddai.exe`, 87,048,378 bytes, SHA-256 `a7a4188d747c5a7d750e276255485b6ca9b8789fef33edb306e10e1e84ade2d8`.
- Canonical nine-tool schema receipt: 60,265 bytes, SHA-256 `370b9905ccdba4fc9bdc8606811627cd650b438b62d155fb13004977289c4278`.
- GitNexus: 50 changed symbols, 2 affected undo flows, medium risk; full suites are the controlling evidence.

## Live boundary

Three Dungeondraft processes were still open at verification time: one clean saved `test` map, one unsaved `test*` sacrificial map, and one blank window. No force-close was performed. The ownership-proven setup installed the exact verified executable and mod bytes while correctly returning `activation_pending_dungeondraft_running`; installed/source hashes match and the Mods root still contains only one `DDAI` directory. Runtime activation and live undo remain deferred until those windows can be closed normally and Dungeondraft reloaded without risking user state.
