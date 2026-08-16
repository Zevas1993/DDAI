# DDAI object placement live certification

Recorded: 2026-08-16

## Outcome

**`object_placement` is certified** against Dungeondraft 1.2.0.1. It is the second
runtime-certified operation after `wall_polyline`.

The full contract was proven live on a disposable map: applied at the exact requested
grid position with the correct asset, observed with its native node id recorded,
committed, then reversed with the map returning to its pre-apply state fingerprint.
An independent second run also proved save, normal close, reopen, and exact inspection
of the persisted object.

## Live evidence

Mod `0.3.0`, disposable copy of a saved map, connector `a7a4188d`.

**Apply.** Request `cert-object-20260816-030`, one `object_placement` at grid (10, 10):

```
job state    committed
observed ids 0
map revision 0 -> 1
undoable     target=cert-object-20260816-030 rev=1
```

Inspection after apply:

```json
{"node_id":0,"kind":"object",
 "bounds":{"x":9.580078,"y":9.417969,"width":0.839844,"height":1.164062},
 "asset_ref":"sha256:000674b4...3056e"}
```

The bounds centre on exactly (10, 10) and the asset reference matches the plan.

**Reverse.** `ddai_undo_last_job` targeting the same request:

```
undo job state  committed
items after     []
map_revision    af2b9a23... -> f4e87b21...
```

`f4e87b21...` is the identical fingerprint recorded for this map before the apply, so
the reversal restored the exact prior state rather than merely deleting something.

**Save and reopen.** Request `persist-object-20260816-002` placed the same catalog
object at grid `(12, 10)` after a fresh catalog snapshot:

```
catalog revision 1786877201703
job state       committed
observed id     2
map revision    0 -> 1
```

The MCP apply response was successful with `outcome_unknown=false`. Before saving,
`ddai_inspect_map` returned one object with the exact requested asset reference and
bounds centred on `(12, 10)`. Dungeondraft's own save file then contained exactly one
object with `node_id=2`, `position=Vector2(3072, 2560)`, and texture
`res://textures/objects/vegetation/fallen/log_01.png`.

Dungeondraft was closed normally and the exact file was reopened. Post-reopen MCP
inspection returned the same node id, opaque asset reference, bounds, and represented
state fingerprint `d83dbca3...`. This closes the persistence requirement without
depending on the in-memory Prop.

**Layer fidelity.** The first persistence run also exposed that the requested layer
`100` was being saved as `0`. After the executor began assigning the Prop's documented
Node2D z-index, a fresh bridge-mailbox request `persist-object-layer-20260816-004`
placed another copy at grid `(14, 10)`. Dungeondraft's save file contains that exact
second object as `node_id=3`, `position=Vector2(3584, 2560)`, `layer=100`, with the
same requested texture. The pre-fix object remains `layer=0`, providing a direct
before/after control in the disposable file.

## Defects fixed to reach this

Seven, four of which were latent bugs affecting operations beyond this one.

**Journal rejected node id zero.** `_read_map_job_journal` required node ids to be
positive. `World.AssignNodeID` hands out `nextNodeID` and increments, so the first node
registered in a session receives 0. The journal became unreadable, every subsequent tick
bailed out as blocked, and the job froze at `operation_applied` with no error. This
affected any operation creating the first node in a session, walls included; it stayed
hidden only because certification always ran against maps whose counter had advanced.
`_runtime_node_id` already accepted `>= 0`, so the validator contradicted the reader.

**Reversal never verified itself.** `_reverse_addressable_surface` returned `true`
unconditionally after `SelectTool.Delete()`. Undo therefore reported success while the
object remained on the map. This is the worst failure mode available to a rollback
contract, and it applied to pattern, roof, path and tile reversals too.

**Reversal used a tool route.** `SelectTool.SelectThing` plus `Delete` does nothing
while that tool is not the selected one. Objects now reverse through the container
route, mirroring creation: withdraw the search index entry, unparent, free.

**Prop bounds were never computed.** `Rect` is derived by the tool's finalisation. On
the container route it stayed `(0,0,0,0)`, and `_inspection_item` rejects a zero-area
rect, so a single object made the whole map uninspectable.

**Container-created Props were not finalized for saving.** `Objects.CreateObject()`
parents a Prop but does not transition it through the documented persisted lifecycle.
The live object disappeared after save/reopen and the map file's `objects` array was
empty. The executor now calls the documented `ObjectTool.Record(prop)` on the exact
container-created Prop, then performs the separately documented manual search-table
registration required for direct `CreateObject()` callers. It never activates the
tool, drives a preview, calls `Next()`, or calls `Confirm()`.

**Reloaded Props have no serialized derived Rect.** The corrected object persisted,
but its public `Rect` was zero after reopening and inspection failed closed with
`inspection_state_invalid`. Inspection now reconstructs an axis-aligned local bound
from documented position, scale, and Sprite texture size only when Rect is invalid.
The calculation is read-only, bounded, rejects non-finite/zero geometry, and preserves
the exact pre-close represented-state fingerprint.

**Requested object layer was ignored.** Direct container creation defaults the Prop
z-index to zero. The executor now assigns the already validated DDAI layer before
Record; the exact Godot save fixture and live Dungeondraft save both prove layer `100`.

## Architecture note

Dungeondraft exposes two surfaces. Tool APIs drive the interactive UI and silently do
nothing unless that tool is selected, which a mod cannot arrange from a background
handler. Container APIs are the mod-facing surface. `wall_polyline` looks tool-driven
but actually uses `WorldUI.AddPolyPoint` plus `WallTool.EndWall`, so it was a misleading
template for the others.

Creating through the container bypasses the interactive preview. The caller must pass
the exact Prop through `ObjectTool.Record`, register the direct-created Prop in the
search index, and compute live bounds. Inspection must also tolerate Dungeondraft's
save format omitting that derived Rect and reconstruct it without mutating the map.

## Verification

- Final post-correction Core suite `448/448`, App suite `363/363`
- Release build `0` warnings / `0` errors
- installed bridge at
  `C:\Users\ChrisBoyd\AppData\Local\DDAI\DungeondraftMods\DDAI\scripts\ddai_bridge.gd`
  and worktree source SHA-256 both
  `8507A3D3FF7415D7947FE4B5ECB69B3D1BEE01623FED727E6668483691B5B2EF`
- no crash event or dump during the session
