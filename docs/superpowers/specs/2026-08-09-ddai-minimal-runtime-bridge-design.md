# DDAI Minimal Runtime Bridge Design

## Status

Approved in conversation on 2026-08-09. This design replaces the crash-prone runtime-introspection portion of Task 3 while preserving the Windows connector's full objective: ChatGPT, Claude Desktop, Gemini, and other MCP clients must be able to create and modify native, editable maps in an already-open licensed Dungeondraft installation.

## Evidence and problem statement

Dungeondraft 1.2.0.1 is stable on the target machine with no active mods. Five activated launches crashed with Windows exception `0xc0000005` at Dungeondraft executable RVA `0x13e07ca`. Every dump contains the same Godot `Variant` call frame, a Dictionary receiver, and a zero-argument call. Successive changes allowed execution to advance from `Dictionary.empty()` to `Dictionary.size()` and then to `Dictionary.keys()`, where it failed at the same native address. A DDAI-only launch still crashed, while a no-mod launch opened a blank map successfully.

The architecture must therefore assume that zero-argument Dictionary built-ins are unsafe in this optimized Dungeondraft runtime. The connector cannot rely on optional runtime introspection merely because the same GDScript is valid in a stock Godot editor.

## Goals

- Keep the Dungeondraft-resident code small, deterministic, and limited to mailbox transport plus narrowly defined native map operations.
- Keep AI reasoning, schema validation, plan construction, asset selection, backups, client configuration, and MCP protocol handling outside Dungeondraft in `ddai.exe`.
- Restore a crash-free DDAI-only launch and prove a real correlated `status` round trip before adding map mutations.
- Preserve the existing durable mailbox state machine, bounded messages, failure artifacts, request correlation, heartbeat, and restart recovery.
- Continue to the actual product outcome: a natural-language request from an AI client produces a visible, validated, reversible native map change in the open Dungeondraft map.

## Non-goals

- Do not downgrade or replace the user's Dungeondraft installation.
- Do not edit, unpack, patch, or redistribute Dungeondraft binaries or purchased assets.
- Do not generate an imitation map image instead of manipulating a native Dungeondraft map.
- Do not make Custom Snap or any other third-party mod part of the connector's required runtime path.
- Do not expose an inbound network listener from Dungeondraft or weaken the local mailbox boundary.

## Chosen architecture

```text
ChatGPT / Claude Desktop / Gemini / other MCP client
                         |
                         v
                  ddai.exe MCP server
        intent handling, validation, backups, policy
                         |
                         v
             bounded atomic JSON mailbox
                         |
                         v
          minimal DDAI GDScript runtime bridge
              status + native map commands
                         |
                         v
              already-open Dungeondraft map
```

`ddai.exe` remains the control plane. It exposes stable MCP tools, validates every public envelope, converts AI-produced plans into a canonical form, enforces revision and size limits, and waits for correlated responses. The GDScript bridge is the execution plane. It polls local files, validates the minimum required wire fields again, and invokes only the documented Dungeondraft operations needed for the requested command.

The bridge must not explore arbitrary `Global` state, enumerate optional dictionaries, or return diagnostic richness at the expense of application stability. Optional information is represented explicitly as unavailable.

## Runtime compatibility rules

1. Production GDScript may not call zero-argument Dictionary built-ins, including `empty()`, `size()`, `keys()`, `values()`, `clear()`, or `duplicate()`.
2. A keyed membership test such as `dictionary.has("code")` is permitted only where live dump progression has already demonstrated that exact call class succeeding. New Dictionary operations require separate runtime certification.
3. Array, string, and byte-array methods are not prohibited by this rule, but each new in-process API surface must be kept minimal and covered by package-contract tests.
4. Optional runtime properties must be accessed through the existing safe-property boundary. Missing or incompatible properties produce `null` or an explicit availability flag rather than failure.
5. The bridge never opens a socket. It reads and writes only beneath `user://ddai`.
6. Custom Snap remains installed but disabled during DDAI certification. Its compatibility is a separate optional concern and cannot block AI map creation.

## Status contract

The first repaired command remains `status`. Its response includes:

- bridge version and target Dungeondraft version;
- Dungeondraft version when safely available;
- whether a map is loaded;
- current level, canvas dimensions, and revision when safely available;
- supported DDAI command names;
- `active_mods: []` and `active_mods_available: false`.

The bridge will not enumerate `Global.ActiveMods`. Losing this optional diagnostic field does not reduce map-generation capability.

## Map-plan and MCP progression

The existing `MapPlan` envelope remains the public foundation: schema version, request ID, base revision, operation mode, and canvas are required and validated by `DDAI.Core`. Development proceeds in evidence-gated slices:

1. **Stable transport:** DDAI-only launch remains alive, writes a fresh heartbeat, recovers the existing processing claim, and returns a correlated live `status` response.
2. **First native mutation:** extend `ddai_apply_plan` and the mailbox with one rectangular-room plan in `add` mode. Against an already-open blank map, the bridge creates a visible native room footprint with its enclosing walls through documented Dungeondraft mod APIs, then proves Dungeondraft's undo reverses the whole job. Runtime/API inspection maps this fixed behavior to the smallest supported native calls before implementation begins.
3. **Structured plan application:** extend `MapPlan` with rooms and surfaces, then walls, portals, objects, lights, labels, styles, and installed-asset references. Each addition receives its own validation and live proof.
4. **Complete job semantics:** validate the whole plan and expected revision before mutation, create a recoverable backup, apply as one grouped undoable job, and return no success until the resulting state is verified.
5. **AI-client acceptance:** expose `ddai_inspect_map`, `ddai_search_assets`, `ddai_validate_plan`, `ddai_apply_plan`, `ddai_undo_last_job`, and `ddai_export_map`; verify direct local MCP use from Claude Desktop and Gemini, then add the outbound-only paired relay required by desktop ChatGPT.

This ordering is not a reduction of scope. It is the shortest evidence-backed path to the complete haunted-crypt acceptance map without asking the user to absorb repeated native crashes.

## Error handling and recovery

- Request and response files remain capped at 1 MiB and use same-directory temporary publication followed by a non-overwriting rename.
- A claimed request is deleted only after the exact journaled response is durably published and verified.
- Malformed, unsupported, conflicting, or uncorrelated inputs fail closed with structured errors and retain enough source material for recovery.
- Startup recovery advances one durable transition at a time and remains idempotent across interruption.
- Optional status data never makes the command fail. Required command preconditions, including an absent map or revision mismatch, do fail before mutation.
- A map job that cannot guarantee rollback or grouped undo is rejected before the first map change.

## Installation and coexistence

- Setup remains non-administrative and installs only DDAI-owned files beneath the per-user installation root.
- Configuration and client JSON changes remain transactional, backed up, idempotent, and ownership-checked.
- Setup never starts, closes, or restarts Dungeondraft automatically.
- Installed third-party mod files are preserved. DDAI certification activates only `org.ddai.status_bridge`; Custom Snap is not automatically reactivated until it has separate compatibility evidence.
- Claude Desktop and Gemini retain direct `stdio` entries. Desktop ChatGPT requires the later paired outbound relay because it cannot directly launch a local stdio server.

## Verification strategy

Offline verification must include:

- a package compatibility contract that rejects every prohibited zero-argument Dictionary call without forbidding unrelated array/string calls;
- the executable .NET bridge reference state machine and mailbox conformance suites;
- deterministic timestamp, correlation, crash-recovery, size-cap, traversal, and ownership tests;
- published single-file MCP initialize, list-tools, and call-tool tests with protocol-clean stdout;
- a warning-free Release build and a scoped diff review.

Live verification must include, in order:

1. no-mod control remains stable;
2. DDAI-only launch remains running on a blank map;
3. a fresh runtime receipt and heartbeat appear;
4. the stranded processing request completes safely;
5. `ddai.exe status --json` returns a correlated success;
6. the official MCP client calls the installed `ddai_status` against the live bridge;
7. the first map tool makes a visible native change, supports undo, and survives save/reopen;
8. the complete five-room haunted crypt can be created, validated, saved, undone, reopened, and exported from a natural-language request.

No static test may be used to claim a live Dungeondraft acceptance result.

## Acceptance criteria

The bridge-redesign slice is complete only when Dungeondraft 1.2.0.1 runs with DDAI as the sole active mod without crashing and a real status request traverses the installed MCP executable, atomic mailbox, and live mod to a correlated response.

The connector objective is complete only when a supported AI client can request the haunted five-room crypt and DDAI produces a native, editable, validated, saved, undoable, reopenable, and exportable Dungeondraft map without partial application on failure.
