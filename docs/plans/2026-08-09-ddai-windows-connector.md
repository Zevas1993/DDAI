# DDAI Windows Connector Implementation Plan

## Objective

Build a Windows-first, Apache-2.0 connector that allows ChatGPT, Claude Desktop, Gemini, and other MCP clients to create and modify complete dungeon and building maps in an already-open Dungeondraft map.

## Architecture

1. A self-contained .NET 9 x64 application provides setup, diagnostics, a local MCP `stdio` server, an approved-image import window, and an outbound ChatGPT relay agent.
2. A documented GDScript tool mod polls an atomic JSON mailbox under Dungeondraft's `user://` directory and applies versioned map plans using the public mod API.
3. Claude and Gemini invoke the local executable directly. ChatGPT uses a public MCP relay paired to the outbound local agent; the relay never receives purchased asset binaries.
4. A complete job is validated before mutation, checks the expected map revision, creates a backup, and applies as one grouped undoable transaction.

## Initial interfaces

- `ddai_status`
- `ddai_inspect_map`
- `ddai_search_assets`
- `ddai_validate_plan`
- `ddai_apply_plan`
- `ddai_undo_last_job`
- `ddai_request_asset`
- `ddai_export_map`

`MapPlan` uses grid-relative coordinates and includes a schema version, request ID, base revision, operation mode, rooms and surfaces, walls, portals, paths, objects, lights, labels, styles, and asset references.

## Task 1: Shared map-plan envelope and JSON contract

- Provide a .NET 9 `DDAI.Core` library with schema version `1.0`.
- Define the required wire fields `schema_version`, `request_id`, `base_revision`, `mode`, and `canvas` (`width`, `height`).
- Serialize deterministically as compact snake_case JSON with string-only operation modes.
- Deserialize the same wire format and reject unsupported versions, blank request IDs, negative revisions, and non-positive canvas dimensions.
- Return every validation issue in stable field order, using stable machine-readable codes and paths.
- Prove the contract with focused red-green unit tests and a warning-free Release build.

## Task 2: Atomic local mailbox and fake-mod harness

- Add request and response envelopes with schema version, request ID, command, timestamp, payload, success/error status, and structured error details.
- Implement a filesystem mailbox rooted at an explicitly supplied directory with `requests`, `processing`, `responses`, `failed`, and `journal` subdirectories.
- Publish JSON through same-directory temporary files followed by atomic rename; never expose a partially written message.
- Claim each request by atomically moving it from `requests` to `processing`, enforce a 1 MiB payload limit, reject traversal outside the mailbox root, and make duplicate request IDs idempotent.
- Add a fake-mod integration harness that claims a status request and writes a correlated response through the real mailbox implementation.
- Cover success, partial write, duplicate, malformed JSON, oversize, traversal, timeout, and crash-recovery behavior with red-green tests.

## Task 3: Dungeondraft mod and live status bridge

- Add a mod folder containing a `.ddmod` manifest targeting Dungeondraft 1.2.0.1 and a documented GDScript tool with `start()` and `update(delta)`.
- Poll the mailbox under Dungeondraft `user://ddai` without opening a network listener.
- Implement `status` first: return mod version, Dungeondraft version, whether a map is loaded, current level, map dimensions/revision when available, active mods, and supported command names.
- Write a runtime receipt on mod start and structured failure responses for unsupported commands or malformed requests.
- Provide an idempotent per-user mod installer that never edits Dungeondraft binaries or packaged assets and can diagnose a missing/disabled mod.
- Install against the local licensed Dungeondraft copy, restart or reload it through normal UI controls, and capture a successful live status round trip as evidence.

## Task 4: Runnable MCP server and local AI-client setup

- Add a self-contained Windows executable with `serve --stdio`, `status --json`, `setup`, `diagnose`, and `uninstall` commands.
- Use the official C# MCP SDK and expose `ddai_status` backed by the real Dungeondraft mailbox rather than a mock.
- Keep stdout protocol-clean in stdio mode and route diagnostics to stderr or structured log files.
- Transactionally merge `ddai` into Claude Desktop and Gemini MCP configuration, preserving all unrelated settings and creating timestamped backups.
- Make setup idempotent and uninstall remove only DDAI-owned entries and files.
- Prove MCP initialize/list-tools/call-tool over stdio plus real `ddai_status` against the running Dungeondraft instance.

## Task 5: Native dungeon and building generation

- Extend `MapPlan` with grid-relative rooms/surfaces, walls, portals, paths, objects, lights, labels, styles, and semantic or exact asset references.
- Expose `ddai_inspect_map`, `ddai_search_assets`, `ddai_validate_plan`, `ddai_apply_plan`, `ddai_undo_last_job`, and `ddai_export_map`.
- Resolve only assets installed for that user and never send purchased asset binaries through an AI service or relay.
- Validate the entire plan, expected revision, geometry, bounds, collisions, and asset references before mutation.
- Create a backup and apply through documented Dungeondraft APIs as one grouped undoable job; failures must leave no partial map changes.
- Live-test creation, modification, undo, save/reopen, and export of the five-room haunted crypt acceptance map.

## Task 6: User-approved generated-asset import

- Add `ddai_request_asset` and a local drag-and-drop approval window; the explicit drop/import action is the approval boundary.
- Accept PNG only, enforce size and dimension limits, validate transparency when the selected category requires it, trim transparent borders, and reject unsafe paths or malformed images.
- Store approved art in a private local DDAI library, embed supported current-map objects, and build private Dungeondraft packs for reusable categories that require reload.
- Never call a paid image API or assume ChatGPT-generated images are automatically available to MCP.
- Test approval, rejection, duplicate content, malformed files, private storage, embedding, and reload-required results.

## Task 7: Paired ChatGPT relay

- Add an open-source hosted Streamable HTTP MCP endpoint and an outbound-only TLS local relay agent; do not require an inbound firewall rule.
- Pair a ChatGPT MCP session to one local device with a short-lived, high-entropy one-time code and rotate session credentials after pairing.
- Forward structured tool envelopes only, retain no map or purchased-asset content, redact operational logs, and fail closed when the device disconnects or a session expires.
- Keep Claude, Gemini, and other local MCP clients on direct stdio without the relay.
- Verify pairing, status, map-plan dispatch, reconnect, expiry, wrong-code rejection, concurrent-session isolation, and offline-device behavior.

## Task 8: Windows packaging and full acceptance

- Produce a reproducible unsigned self-contained Windows x64 ZIP containing the executable, mod, notices, and checksums but no Dungeondraft software or user assets.
- Verify install, repair, upgrade, diagnostics, and uninstall without administrator access on clean Windows 10 and Windows 11 environments without preinstalled .NET or Node.
- Verify Claude, Gemini, and paired ChatGPT independently from natural-language request through native Dungeondraft map creation and export.
- Run the haunted five-room crypt acceptance scenario and retain runtime receipts, test output, exported map evidence, and a tracked release checklist.

## Boundaries

- Certify Dungeondraft 1.2.0.1 first.
- Require the user to own Dungeondraft, enable the mod once, and open a map before AI control begins.
- Do not include, unpack, patch, or redistribute Dungeondraft executables, PCK data, maps, or purchased asset packs.
- Prefer installed assets. A user-approved ChatGPT-generated image enters DDAI only through an explicit local drag-and-drop action.
- First release covers dungeons and buildings; wilderness, caves, roofs, water, and multi-level generation follow later.

## Acceptance scenario

From a blank open map, the request "Create a haunted five-room crypt with an entrance hall, locked treasury, secret passage, furnishings, warm torchlight, labels, and doors" produces a validated, saved, undoable, and exportable Dungeondraft map without partial application on failure.
