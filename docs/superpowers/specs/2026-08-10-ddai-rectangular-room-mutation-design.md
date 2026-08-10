# DDAI Rectangular Room Mutation Design

## Status

Approved by the user's standing instruction to proceed on 2026-08-10. This is the first native-map mutation slice after the live `ddai_status` bridge became stable. It is intentionally narrow but must advance the unchanged product objective: AI clients create and modify native, editable Dungeondraft maps rather than images that merely resemble maps.

## Scope and success boundary

This slice adds two MCP tools, `ddai_validate_plan` and `ddai_apply_plan`, and one mailbox command, `apply_plan`. From an already-open blank Dungeondraft map, an AI client supplies one grid-relative rectangular room. DDAI validates every enforceable request and runtime precondition before mutation and uses Dungeondraft's documented wall tool to create one closed native wall polyline. One normal Dungeondraft undo action must remove the whole room.

The slice is successful only when the installed MCP executable, real mailbox, loaded DDAI mod, and Dungeondraft 1.2.0.1 produce and undo the visible room without a new native crash. Static tests and a fake mod are necessary but cannot satisfy the live acceptance criterion.

This slice does not claim that the full connector is complete. Floors, doors, objects, lights, labels, asset search, save/export automation, programmatic job undo, ChatGPT relay transport, and the haunted five-room crypt remain later slices of the existing connector plan.

## Evidence and API choice

The chosen path is Dungeondraft's documented built-in tool surface:

- [`WallTool`](https://github.com/Megasploot/Dungeondraft/wiki/WallTool) is obtained from `Global.Editor.Tools["WallTool"]`; its `Confirm()` method turns the current `WorldUI` polyline into a native wall under the current level's `Walls` node.
- [`WorldUI`](https://github.com/Megasploot/Dungeondraft/wiki/WorldUI) documents `AddPolyPoint(Vector2)` for exact global points and `ClearPolyline()` for cleanup.
- [`Tool`](https://github.com/Megasploot/Dungeondraft/wiki/Tool) requires `Enable()` before work and `Disable()` afterward.
- [`World`](https://github.com/Megasploot/Dungeondraft/wiki/World) documents the fixed `Width`, `Height`, `GridSize`, and `CurrentLevelId` properties plus `GetLevelByID(int)`.
- [`Level`](https://github.com/Megasploot/Dungeondraft/wiki/Level) documents `Walls` and explicitly specifies `get_children()` to obtain that level's native walls. The bridge requires the wall-child count to increase by exactly one before returning success.

Two alternatives were rejected. Directly constructing scene nodes depends on private runtime structure and bypasses built-in editor history. Editing a `.dungeondraft_map` file would not safely mutate the already-open map and risks corrupting or overwriting user work.

No undocumented programmatic undo API is assumed. This slice proves that the native wall created through `WallTool.Confirm()` participates in the normal Dungeondraft undo stack. `ddai_undo_last_job` is not exposed until a programmatic, job-scoped undo mechanism is separately documented and live-certified.

## Architecture

```text
AI client
   |
   | MCP: ddai_validate_plan / ddai_apply_plan
   v
ddai.exe
   | canonicalize, validate, correlate, time out
   v
atomic JSON mailbox
   | apply_plan request + durable mutation intent
   v
DDAI GDScript bridge on Dungeondraft's main update thread
   | enable WallTool -> build one closed polyline -> Confirm -> cleanup
   v
native Dungeondraft wall + built-in undo record
```

`ddai.exe` remains the control plane. It owns the public schema, stable validation issues, MCP annotations, timeouts, and mailbox correlation. The GDScript bridge remains the execution plane. It repeats all mutation-critical checks using a deliberately small parser and invokes only the documented Dungeondraft calls required for this room.

The command uses the existing 1 MiB bounded, atomic, idempotent mailbox. The same `request_id` with the same canonical plan returns the same result. Reusing a request ID with different content is a conflict and never mutates the map.

## Map plan contract

`MapPlan` keeps schema version `1.0` and gains a `rooms` array. Keeping the version is intentional: the original 1.0 contract defined the envelope as the foundation and explicitly reserved rooms for extension. Existing envelope-only deserialization remains valid with an empty room array, while `ddai_apply_plan` imposes the stricter command-specific requirement below.

The first supported room shape is:

```json
{
  "schema_version": "1.0",
  "request_id": "room-job-001",
  "base_revision": 0,
  "mode": "add",
  "canvas": { "width": 40, "height": 30 },
  "rooms": [
    { "id": "room-entrance", "x": 8, "y": 7, "width": 10, "height": 8 }
  ]
}
```

All room coordinates and dimensions are integer grid cells. `(x, y)` is the room's top-left grid intersection. Width and height are wall-to-wall spans, so the wall vertices are `(x,y)`, `(x+width,y)`, `(x+width,y+height)`, `(x,y+height)`, then `(x,y)` to close the polyline.

General envelope validation remains stable and ordered. Command-specific validation then requires:

1. `mode` is exactly `add`;
2. `base_revision` is `0` for this blank-map-only slice;
3. `rooms` contains exactly one room;
4. room `id` is nonblank and uses the same safe identifier language as mailbox request IDs;
5. `x` and `y` are nonnegative;
6. `width` and `height` are positive;
7. the rectangle fits inside the declared canvas without integer overflow;
8. `Global.World.Width`, `Global.World.Height`, `Global.World.GridSize`, `Global.World.CurrentLevelId`, and `Global.World.GetLevelByID(id).Walls.get_children()` can be read through the fixed documented path during API certification; and
9. the runtime canvas matches the declared canvas before any wall point is added.

Complete blank-map inspection is not available through the currently certified runtime surface. For this slice, an open blank map is an explicit live-test and operator precondition rather than a claim the bridge can enforce. `ddai_apply_plan` is marked destructive so the AI host presents its normal write confirmation. The bridge still rejects canvas mismatch and every enforceable geometry/runtime failure before mutation. Map inspection and revision enforcement must be implemented before multi-room or modification plans are enabled.

Validation returns every issue in deterministic field order. Stable codes include `unsupported_mode`, `unsupported_base_revision`, `invalid_room_count`, `invalid_room_id`, `invalid_room_x`, `invalid_room_y`, `invalid_room_width`, `invalid_room_height`, `room_out_of_bounds`, `map_not_available`, `canvas_unavailable`, and `canvas_mismatch`.

The first slice rejects rather than guesses when the live map dimensions, current level, grid size, wall tool, or end-of-wall success observation is unavailable. It does not fall back to fixed pixel sizes, private scene paths, reflection, or Custom Snap.

## MCP tools

### `ddai_validate_plan`

This tool is read-only, non-destructive, and idempotent. It accepts the structured `MapPlan` object and performs all validation that can be completed outside Dungeondraft. It returns:

- `valid`;
- the canonical plan when valid;
- all stable validation issues when invalid; and
- `runtime_checks_required`, listing canvas, grid scale, active level, wall tool, and wall-count checks that only the loaded bridge can perform.

It never publishes a mailbox mutation request.

### `ddai_apply_plan`

This tool is destructive and idempotent for an identical `request_id` plus canonical plan. It runs the same control-plane validation first. Invalid plans return without publishing. Valid plans publish one `apply_plan` mailbox request whose envelope request ID equals `plan.request_id` and whose payload is the canonical plan.

A successful result includes the request ID, one created native wall, the room ID, `undo_available: true`, and `undo_instruction: "Use Dungeondraft Undo once"`. It must not claim programmatic undo, save, export, or revision advancement. A timeout reports an unknown result and preserves the mailbox state for diagnosis; it must not encourage blind retry with a new request ID.

## Runtime preflight and mutation sequence

The GDScript bridge handles `apply_plan` on Dungeondraft's normal update callback and performs this sequence:

1. Parse and revalidate the complete plan without calling prohibited zero-argument Dictionary built-ins.
2. Read `Global.World.Width`, `Global.World.Height`, `Global.World.GridSize`, and `Global.World.CurrentLevelId`, then resolve the level through `Global.World.GetLevelByID(id)`. The implementation plan must first add a package contract for those exact tokens and a live read-only certification. Any crash or unavailable value stops this API path; it is not replaced by runtime reflection.
3. Resolve `Global.Editor.Tools["WallTool"]`, `Global.WorldUI`, and the current level's `Walls`; record the pre-mutation wall count from `Walls.get_children()`.
4. Confirm that runtime canvas dimensions equal the plan canvas.
5. Durably publish a mutation-intent record containing schema version, request ID, canonical-plan fingerprint, command, and state `prepared`.
6. Enable `WallTool`.
7. Clear any existing `WorldUI` polyline.
8. Convert the five grid vertices to exact global positions using the certified live grid size and call `AddPolyPoint` in order.
9. Call `WallTool.Confirm()` exactly once.
10. Read the current level's wall children again and require its count to equal the pre-mutation count plus one.
11. In a guaranteed cleanup path, call `WorldUI.ClearPolyline()` and `WallTool.Disable()` once each.
12. Advance the mutation intent to `confirmed`, create the normal response journal, atomically publish the correlated success response, delete the claimed request, then delete the journals according to the existing durable state machine.

The bridge may process only one mutation at a time. While one is active, status and duplicate reconciliation may continue only where they cannot re-enter Dungeondraft tools.

## Crash safety and recovery

Map mutations cannot use the status command's automatic replay semantics. A crash can occur after `Confirm()` changes the map but before the response is durable, and the bridge has no certified way to prove whether the wall exists after restart.

Therefore:

- If recovery finds a `prepared` mutation intent without a durable `confirmed` transition and response, it does not call `Confirm()` again.
- It returns or retains a structured `mutation_outcome_unknown` failure with the original request and intent evidence. The user is told to inspect the map and use Dungeondraft Undo if a room appeared.
- A duplicate request with the same fingerprint follows the existing response when one exists. Without a confirmed response, it remains ambiguous and is never reapplied automatically.
- A conflicting fingerprint for the same request ID fails closed.
- Failure before the durable intent is written makes no map change.
- Failure after tool enablement but before `Confirm()` runs the cleanup path and returns a non-applied error.
- Failure after `Confirm()` but before success observation is ambiguous, even if no wall is visible to DDAI.

This policy prefers an explicit unresolved result over a duplicated room or hidden partial application.

## Error handling

Every expected failure is returned as a structured mailbox error with stable `code`, `message`, and `path` where applicable. Public errors do not include local absolute paths, purchased asset names, or raw exception dumps.

The bridge rejects the request before mutation for invalid JSON, unsupported schema, command/request correlation mismatch, wrong mode, nonzero base revision, invalid or out-of-bounds geometry, unavailable map state, canvas mismatch, unavailable wall tool, unavailable success observation, or an existing ambiguous intent.

Cleanup failure changes the result to `cleanup_failed` and retains recovery evidence. The bridge does not report success merely because `Confirm()` returned. Native crashes remain evidenced through Windows crash dumps and the durable intent state.

## Testing and evidence

Implementation follows red-green TDD and preserves all existing mailbox and status tests.

Offline tests must prove:

- deterministic room JSON and backward-compatible empty-room deserialization;
- complete, stable validation issue ordering, including overflow-safe bounds checks;
- MCP metadata and behavior for validate/apply;
- no mailbox publish for invalid plans;
- real-mailbox application through a fake bridge, including exact request/plan correlation;
- identical duplicate idempotency and conflicting duplicate rejection;
- mutation-intent recovery at every durable boundary;
- no automatic replay after the ambiguous `Confirm()` boundary;
- GDScript static contracts for the exact documented tool sequence, guaranteed cleanup, fixed runtime property path, and the existing prohibited Dictionary-call rules;
- official MCP SDK initialize, list-tools, validate-plan, and apply-plan calls against the published executable;
- the full Release suite and build with zero failures, warnings, or errors.

Live certification must be performed with Dungeondraft 1.2.0.1 and DDAI as the sole active mod:

1. record a baseline process, heartbeat, status response, and crash-dump directory state;
2. use a blank 40-by-30 map and a room at `(8,7)` sized `10` by `8` cells;
3. call `ddai_validate_plan` through the official MCP SDK and require `valid: true`;
4. call `ddai_apply_plan` through the official MCP SDK;
5. visually confirm one closed native wall rectangle and no extra segments;
6. use Dungeondraft Undo once and visually confirm the whole rectangle disappears;
7. after the undo, apply the same geometry with a fresh request ID, save, close, and reopen the map through normal user UI, then confirm the native wall survives save/reopen;
8. verify the Dungeondraft process remains alive, a fresh heartbeat continues, and no new crash dump or Application Error event appears.

If any live step cannot be observed, the result remains incomplete. In particular, a successful mailbox response without a visible native wall and undo proof is not acceptance.

## Installation and user safety

Setup remains non-administrative, transactional, ownership-checked, and limited to DDAI-owned files. DDAI never edits Dungeondraft binaries, purchased asset packs, or existing map files directly. Custom Snap stays optional and disabled during certification.

The installer must not update the loaded mod while Dungeondraft is running. It publishes and installs the new executable/mod only after the user closes Dungeondraft normally, then asks the user to reopen a blank map. DDAI never terminates or restarts Dungeondraft automatically.

## Follow-on path to the full connector

After this room is live-certified, the same plan and execution boundaries expand in small native-operation slices: room floors/surfaces, multiple rooms and corridors, portals/doors, installed objects, lights, labels, map inspection and revision enforcement, grouped multi-operation jobs, job-scoped programmatic undo, save/export, and finally the haunted five-room crypt acceptance map. Claude Desktop and Gemini continue using direct stdio. ChatGPT uses the supported remote MCP/tunnel path available to the user's ChatGPT workspace; no inbound Dungeondraft listener is introduced.
