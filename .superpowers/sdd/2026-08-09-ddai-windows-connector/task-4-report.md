# Task 4 report: runnable MCP server and local AI-client setup

## Scope and commit

- Base commit: `487ff5b`.
- Delivery commit: this Task 4 commit; use `git log -1 --oneline` for the final immutable hash reported in the handoff.
- Preserved unrelated working-tree changes: `.gitignore`, `task-3-report.md`, `.claude/`, `AGENTS.md`, and `CLAUDE.md`.
- Added a `net9.0-windows` console application published as a self-contained Windows x64 single-file executable.
- Pinned the official stable `ModelContextProtocol` package to `1.4.1`; no prerelease MCP package is referenced.

## Red-green evidence

1. CLI/status RED: the new app tests failed to compile because the Task 4 CLI and status boundary did not exist. GREEN: six focused tests passed after adding strict command parsing, explicit mailbox/timeout overrides, and `DdaiStatusService` backed by the real `AtomicMailbox` and `FakeModHarness`.
2. Client-config RED: four focused tests failed to compile because there was no config merger. GREEN: the `JsonNode` transaction passed preservation, timestamped-backup, byte-idempotence, invalid-JSON no-write rollback, and DDAI-only uninstall tests.
3. MCP protocol RED: the official SDK client launched the published executable, but initialization failed with exit code `64` and `serve is not implemented yet.` GREEN: the same test published one `ddai.exe`, initialized over stdio, listed `ddai_status`, called it through a live fake mod using the real mailbox, and parsed the returned `ready` payload.
4. Setup lifecycle RED: setup initially copied `ddai.exe` before discovering a foreign same-named mod target. GREEN: preflight now rejects that target before any install/config mutation; the foreign manifest remains byte-identical.
5. Setup lifecycle GREEN: six tests cover explicit path overrides, the `%LOCALAPPDATA%\DDAI\DungeondraftMods` default, owned executable/metadata and mod installation, unrelated-file/mod preservation, idempotence, invalid-config preflight, DDAI-only uninstall, missing-heartbeat diagnosis, and honest running-self retention.

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

- Fresh Release suite: `241/241` passed (`224` core and `17` app), `0` failed, `0` skipped.
- Fresh Release build: succeeded with `0` warnings and `0` errors.
- Published artifact: `artifacts/task4/win-x64/ddai.exe`, one-file output, `73,762,197` bytes.
- Artifact SHA-256: `9D290307952D974C92EDE549AFF57BF0160F5447AA0E10E97BE40BEBF9AF81F6`.
- Installed executable SHA-256: `9D290307952D974C92EDE549AFF57BF0160F5447AA0E10E97BE40BEBF9AF81F6`.
- Direct published run: `status --json` emitted one JSON document and exited `2` with `status_timeout`, matching the current unobserved bridge state.
- Protocol proof: the `ModelContextProtocol` `1.4.1` client test launched a newly published self-contained EXE and proved initialize, list-tools, and call-tool `ddai_status`.

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
