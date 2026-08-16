# DDAI object placement live certification

Recorded: 2026-08-16

## Outcome

**`object_placement` is certified** against Dungeondraft 1.2.0.1. It is the second
runtime-certified operation after `wall_polyline`.

The full contract was proven live on a disposable map: applied at the exact requested
grid position with the correct asset, observed with its native node id recorded,
committed, then reversed with the map returning to its pre-apply state fingerprint.

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

## Defects fixed to reach this

Four, three of which were latent bugs affecting operations beyond this one.

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

## Architecture note

Dungeondraft exposes two surfaces. Tool APIs drive the interactive UI and silently do
nothing unless that tool is selected, which a mod cannot arrange from a background
handler. Container APIs are the mod-facing surface. `wall_polyline` looks tool-driven
but actually uses `WorldUI.AddPolyPoint` plus `WallTool.EndWall`, so it was a misleading
template for the others.

Creating through the container bypasses the tool's finalisation, so the caller must
assign the node id, register the search index entry, and compute the bounds.

## Not yet proven

Save, close and reopen persistence for a placed object. The reversal test necessarily
removes the object, so persistence needs its own run.

## Verification

- Core suite `448/448`, App suite `363/363`
- installed mod hashes identical to source
- no crash event or dump during the session
