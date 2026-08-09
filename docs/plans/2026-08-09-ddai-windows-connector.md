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

## Delivery sequence

1. Define and test the shared protocol, map-plan types, validation, and deterministic JSON serialization.
2. Implement the atomic filesystem mailbox and fake-mod integration harness.
3. Add the Dungeondraft mod with status, asset inventory, room/floor/wall creation, grouped undo, and runtime receipts.
4. Add the MCP tools and transactional Claude/Gemini configuration setup.
5. Add approved PNG import, private local storage, and supported current-map embedding.
6. Add the outbound paired ChatGPT relay and hosted MCP endpoint.
7. Verify a clean-machine unsigned ZIP release on Windows 10/11 x64.

## Boundaries

- Certify Dungeondraft 1.2.0.1 first.
- Require the user to own Dungeondraft, enable the mod once, and open a map before AI control begins.
- Do not include, unpack, patch, or redistribute Dungeondraft executables, PCK data, maps, or purchased asset packs.
- Prefer installed assets. A user-approved ChatGPT-generated image enters DDAI only through an explicit local drag-and-drop action.
- First release covers dungeons and buildings; wilderness, caves, roofs, water, and multi-level generation follow later.

## Acceptance scenario

From a blank open map, the request "Create a haunted five-room crypt with an entrance hall, locked treasury, secret passage, furnishings, warm torchlight, labels, and doors" produces a validated, saved, undoable, and exportable Dungeondraft map without partial application on failure.
