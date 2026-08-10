# Task 4 report: runnable MCP server and local AI-client setup

## Scope and commit

- Base commit: `487ff5b`.
- Initial delivery commit: `680125156a034da444ad5361d7c389298c32275d`.
- Independent-review fixes: this follow-up commit; use the immutable hash reported in the handoff.
- Preserved unrelated working-tree changes: `.gitignore`, `task-3-report.md`, `.claude/`, `AGENTS.md`, and `CLAUDE.md`.
- Added a `net9.0-windows` console application published as a self-contained Windows x64 single-file executable.
- Pinned the official stable `ModelContextProtocol` package to `1.4.1`; no prerelease MCP package is referenced.

## Red-green evidence

1. CLI/status RED: the new app tests failed to compile because the Task 4 CLI and status boundary did not exist. GREEN: six focused tests passed after adding strict command parsing, explicit mailbox/timeout overrides, and `DdaiStatusService` backed by the real `AtomicMailbox` and `FakeModHarness`.
2. Client-config RED: four focused tests failed to compile because there was no config merger. GREEN: the `JsonNode` transaction passed preservation, timestamped-backup, byte-idempotence, invalid-JSON no-write rollback, and DDAI-only uninstall tests.
3. MCP protocol RED: the official SDK client launched the published executable, but initialization failed with exit code `64` and `serve is not implemented yet.` GREEN: the same test published one `ddai.exe`, initialized over stdio, listed `ddai_status`, called it through a live fake mod using the real mailbox, and parsed the returned `ready` payload.
4. Setup lifecycle RED: setup initially copied `ddai.exe` before discovering a foreign same-named mod target. GREEN: preflight now rejects that target before any install/config mutation; the foreign manifest remains byte-identical.
5. Setup lifecycle GREEN: six tests cover explicit path overrides, the `%LOCALAPPDATA%\DDAI\DungeondraftMods` default, owned executable/metadata and mod installation, unrelated-file/mod preservation, idempotence, invalid-config preflight, DDAI-only uninstall, missing-heartbeat diagnosis, and honest running-self retention.

## Independent-review fixes

### P1: client-config ownership

- Verified the finding before editing: setup assigned `mcpServers.ddai` unconditionally and uninstall removed it by name alone.
- Unit RED: three focused cases failed because a foreign Claude or Gemini entry was accepted and foreign replacements were removed.
- Published-EXE RED: two lifecycle regressions reproduced the unsafe behavior through a newly published executable: setup returned `0` and installed files despite foreign entries, while uninstall deleted foreign replacement entries.
- GREEN: setup now accepts only an absent entry or the exact DDAI-owned shape for that client and absolute installed executable. A foreign or modified entry fails before connector/mod installation and before either config changes. Uninstall removes only that exact owned shape and preserves a user or foreign replacement while still removing proven DDAI-owned local files.
- Focused GREEN evidence: `ClientConfigMergerTests` passed `7/7`; published config-ownership tests passed `2/2`.

### P2: stdio failure cleanliness

- Verified the finding before editing: the generic operation-error catch serialized CLI JSON to stdout even after `serve --stdio` had been selected.
- Published-EXE RED: a real mailbox initialization fault (the mailbox root was an existing regular file) exited `1` but stdout began with `{"state":"error",...}`.
- GREEN: after successful command parsing, every caught serve-host `IOException`, `UnauthorizedAccessException`, setup exception, or config exception writes only to stderr and exits `1`; non-serve commands retain their structured JSON error output.
- Published-EXE GREEN: the same filesystem fault exited `1`, wrote exactly `0` stdout bytes, and wrote `189` diagnostic bytes to stderr. The official SDK happy path and all published ownership/protocol tests passed together `4/4`.
- No test-only production fault hook was added.

## Implementation summary

- Commands: `serve --stdio`, `status --json`, `setup`, `diagnose`, and `uninstall`.
- Explicit overrides: `--mailbox-root`, `--timeout-ms`, `--source-exe`, `--source-mod-directory`, `--install-root`, `--mods-directory`, `--dungeondraft-user-data`, `--claude-config`, and `--gemini-config`.
- Stdio logging is configured to stderr at every level; the MCP SDK owns stdin/stdout. The published-EXE SDK integration test is the executable stdout-cleanliness proof because any preamble or diagnostic text would break initialization.
- `ddai_status` uses `AtomicMailbox.PublishRequest` and `AtomicMailbox.WaitForResponse`; it is not backed by a mock. The fake mod is used only by the integration test as the live mailbox peer.
- Claude and Gemini merges update only `mcpServers.ddai`. Both use the absolute installed executable with `args: ["serve", "--stdio"]`; Gemini additionally uses `trust: false`.
- All config files are parsed and planned before any write. Changed files receive same-directory timestamped exact-byte backups and same-directory stages. A concurrent file change aborts the transaction; applied targets are restored on a later write failure.
- Setup validates both client configs, the source mod, and any existing target mod before copying. The connector installation owns only `ddai.exe` and `install-metadata.json`; the mod installer owns only a target whose manifest identifies `org.ddai.status_bridge` for Dungeondraft `1.2.0.1`.
- Uninstall validates ownership before deletion, removes only `mcpServers.ddai`, the proven owned current mod, and the proven owned executable/metadata. When the installed executable is the running process, it returns `partial` / `running_executable_retained` and keeps both executable and ownership metadata.

## Verification

- Fresh Release suite after review fixes: `247/247` passed (`224` core and `23` app), `0` failed, `0` skipped.
- Fresh Release build: succeeded with `0` warnings and `0` errors.
- Published artifact: `artifacts/task4/win-x64/ddai.exe`, one-file output, `73,762,197` bytes.
- Review-fixed artifact SHA-256: `D8B5CA196006C0805CF5B3B1AF718A39E2B7A01BB5C5C85D9250123A30E9BF29`.
- Installed executable SHA-256: `9D290307952D974C92EDE549AFF57BF0160F5447AA0E10E97BE40BEBF9AF81F6`.
- Direct published run: `status --json` emitted one JSON document and exited `2` with `status_timeout`, matching the current unobserved bridge state.
- Protocol proof: the `ModelContextProtocol` `1.4.1` client test launched a newly published self-contained EXE and proved initialize, list-tools, and call-tool `ddai_status`.
- The review-fixed artifact was not installed on the current machine during this fix pass; the real Claude/Gemini configs and existing installation were deliberately left untouched.

## Current-machine setup evidence

- Installed executable: `C:\Users\ChrisBoyd\AppData\Local\DDAI\ddai.exe`.
- Installed mod: `C:\Users\ChrisBoyd\AppData\Local\DDAI\DungeondraftMods\DDAI`.
- First setup: `installed` / `setup_complete`, exit `0`.
- Second setup: `already_current` / `setup_already_current`, exit `0`; both config results reported `changed: false` and no new backup.
- Claude exact-byte backup: `C:\Users\ChrisBoyd\AppData\Roaming\Claude\claude_desktop_config.json.ddai-backup-20260810T0014385841883Z-76d70531294e4172b83794772e3053cd.json`, SHA-256 `85016533815B007A94CC8C06D9E7FD0003D2D72834B6ECE03BFDE39BC397B6AC` (equal to the pre-setup file hash).
- Gemini exact-byte backup: `C:\Users\ChrisBoyd\.gemini\settings.json.ddai-backup-20260810T0014385841883Z-c0367d4ef4de4ede93338991eafaf889.json`, SHA-256 `976EDBBFE1C560914525E272AD7FF7DC13E8A9369F342430ABA2F15A9922ABBD` (equal to the pre-setup file hash).
- Claude servers after setup: `n8n-docs`, `One-Stop-Shop-N8N-MCP`, `ddai`. Both unrelated server nodes compare equal to the backup versions.
- Gemini servers after setup: `MCP_DOCKER`, `ddai`. `MCP_DOCKER` compares equal to its backup version; DDAI has `trust: false`.
- Exactly one DDAI backup exists for each client config after the idempotence run.
- Claude Desktop and Gemini were not restarted or reloaded.

## Remaining live gap and concerns

- Current diagnosis is `installed_not_observed` / `mods_directory_not_selected_or_mod_disabled`, exit `2`. Dungeondraft has not emitted a fresh heartbeat or status response from the custom Mods root.
- The user must select `C:\Users\ChrisBoyd\AppData\Local\DDAI\DungeondraftMods` as the custom Mods directory, enable DDAI, and reload through normal Dungeondraft UI controls. Task 4 did not close, reload, or otherwise interact with Dungeondraft.
- Consequently, the real current-machine `ddai_status` round trip remains unproven; the published-EXE SDK round trip is proven against the real mailbox with the isolated fake live peer.
- Task 4's standalone EXE accepts an explicit existing mod source directory. Bundling the mod beside the executable belongs to the Task 8 release ZIP; the current-machine setup used the tracked `mods\DDAI` source explicitly.
- Uninstall was proven only in isolated automated tests and was not run against the current-machine installation.

## Dungeondraft activation automation extension

This section supersedes the manual Mods-directory selection guidance above. The connector now owns a strict, closed-application transaction for only `[Mods].active_mods` and `[Mods].mods_directory`; it never closes or restarts Dungeondraft.

- Task 1 commit: `2b9c4d1` (`feat: edit Dungeondraft mod configuration safely`). Strict UTF-8/UTF-16 parsing preserves BOM, newline convention, comments, unrelated sections/keys, Custom Snap, and non-DDAI duplicate mod IDs. Ambiguous syntax, invalid encodings/escapes, duplicate owned keys/sections, NULs, and mixed newlines fail closed.
- Task 2 commit: `ed1e8e3` (`feat: transact Dungeondraft mod activation`). Same-directory durable stage and rollback files, exact sibling backups, two concurrency checks, byte verification, idempotence, and orphan cleanup are covered by focused tests.
- Setup now detects any `Dungeondraft` process. While it is running, connector/mod/client installation may be repaired, but `config.ini` is neither read for activation nor written; setup returns exit `2` with `activation_pending_dungeondraft_running`.
- With Dungeondraft closed and a valid existing `config.ini`, setup adds `org.ddai.status_bridge` exactly once, preserves `Lievven.Snappy_Mod`, selects the DDAI-managed Mods root, creates an exact sibling backup, and persists the previous directory literal in owned installation metadata.
- Repeated setup is byte-idempotent and preserves the original ownership receipt. Closed-app uninstall uses that receipt to remove only DDAI and restore the proven prior directory. A user-changed directory and legacy metadata without a receipt are retained.
- Diagnosis now distinguishes `activation_pending_dungeondraft_running`, `activation_pending_config_missing`, `activation_pending_config_mismatch`, `configured_waiting_for_reload`, and `runtime_heartbeat_fresh`. Only a fresh runtime heartbeat proves the bridge loaded.

### Fresh verification

- Focused lifecycle, published activation/ownership, and official MCP SDK suite: `20/20` passed.
- Full Release suite: `284/284` passed (`224` core and `60` app), `0` failed, `0` skipped.
- Release build: `0` warnings and `0` errors.
- Published one-file Windows x64 artifact: `artifacts/dungeondraft-activation/win-x64/ddai.exe`, `73,794,453` bytes, SHA-256 `00B463C66D661E81BD98D57449A7BC4E8FDCA6F6899F6D4808650C5EEFDCAE23`.
- GitNexus indexed `1,429` nodes, `3,072` edges, and `90` flows. `LocalSetupService` has medium impact (`11` direct dependents and one CLI process); those direct paths are covered by the lifecycle and CLI suites. Broad dirty-tree detection also included the unrelated Task 3 report and was not used as scoped commit evidence.

### Current-machine safe-defer evidence

- Dungeondraft PID `54360` was running non-admin with window title `Tabula Rasa - Dungeondraft`; it was not closed, restarted, or controlled.
- The fresh artifact was installed to `C:\Users\ChrisBoyd\AppData\Local\DDAI\ddai.exe`; its SHA-256 equals the published artifact hash above.
- Live setup returned exit `2`, state `activation_pending`, code `activation_pending_dungeondraft_running`. Claude and Gemini DDAI entries were already current and received no new backups.
- `C:\Users\ChrisBoyd\AppData\Roaming\Dungeondraft\config.ini` SHA-256 was `D3A1EF7C3C16D2EA9ACF1FBB01D76E0193723BF5FAD49FC59F122E6A6E4B0E15` both before and after setup.
- Installed `diagnose --json` returned exit `2` with the same activation-pending code. Installed `status --json --timeout-ms 1500` returned exit `2` / `status_timeout`; no live bridge heartbeat or correlated status response exists yet.
- Remaining acceptance step: after the user closes Dungeondraft normally, rerun fresh setup, verify the exact backup and both mod IDs, then launch Dungeondraft normally and require `runtime_heartbeat_fresh` plus a correlated status response.
