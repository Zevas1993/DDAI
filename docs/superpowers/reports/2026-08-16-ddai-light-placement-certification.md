# DDAI light placement certification

Date: 2026-08-16

Target runtime: Dungeondraft 1.2.0.1 on Windows

## Result

`light_placement` is runtime-certified. The disposable-map evidence covers public
MCP apply, exact inspection, save/close/reopen persistence, and durable exact-job
reversal. The operation uses the documented persistent `Lights.CreateLight(false)`
route and the documented `World.DeleteNodeByID(id)` asset-instance deletion route.
It does not activate or mutate the interactive LightTool.

## Live apply and inspection

- Public official-SDK MCP request: `light-live-20260816-0750`
- Map revision: `0 -> 1`
- Result: `success=true`, `outcome_unknown=false`
- Plan fingerprint:
  `a2ff4c4f2415506d44308b219dcda8ebe8fafb67356c8a52f3c9063c808731c6`
- Asset reference:
  `sha256:29e22f60de8bfd249282c7b01cbbec1c487a96d9fdec69700aec2b3163446d79`
- Requested position/range/intensity/color/shadows:
  `(18,10)`, `2`, `0.75`, `#ffeeddcc`, `true`
- Source-built MCP inspection returned node ID `4`, kind `light`, bounds
  `(16,8,4,4)`, and the same opaque asset reference.

The first source-built request also exposed a real fingerprint crash: the bridge
treated `light_placement` as a region and accessed a nonexistent `region` field.
The exact Godot regression reproduces that failure and now proves parity with the
C# `MapPlanJson.Fingerprint` field order. The crashed request recovered as a
correlated `map_revision_mismatch`; the map file hash did not change.

Independent review found a numeric boundary in the pre-mutation route: checking
`texture_scale` alone did not prove that `texture size * texture_scale` remained
representable, while positive double values below the minimum normal single could
round to zero in Dungeondraft's Light2D properties. An exact Godot RED used a
512-pixel texture with range `1e37`, range `1e-300`, and intensity `1e-300`.
The final preflight rejects all three before `CreateLight`; the fixture proves its
call count and the `Lights` child count remain unchanged. Valid live inputs follow
the same already-certified route.

## Save and reopen

- Pre-save map SHA-256:
  `36B88F0C96AEAC9D27322BA6DE5D848D254B5414E36E271C5713F181D2589134`
- Persisted map SHA-256:
  `AF88BF1F5C270BB202391523178E5F48F8AE2C37710092EC8D8E97D22E0AE539`
- Dungeondraft save data stored position `Vector2(4608,2560)`, range `2`,
  intensity `0.75`, color `ccffeedd`, texture
  `res://textures/lights/point.png`, shadows `true`, and node ID `4`.
- After normal close and reopen, MCP inspection returned the same node ID,
  opaque asset reference, bounds, and represented-state fingerprint
  `60bdbfd700ff7eeba16244323c967350d48cdc240979c7520e1cd4fa6bc147f6`.

## Exact reversal

The first two reversal attempts found two distinct safety defects and remained
fail-closed:

1. The bridge checked registry disappearance immediately after `queue_free`, even
   though the durable state machine has a later observation transition.
2. Direct container removal left Dungeondraft's `World` node registry populated.
   The light disappeared visually, but undo correctly returned `outcome_unknown`.

The final implementation prevalidates every exact node ID and `Lights` parent,
calls the documented `World.DeleteNodeByID(id)` route, then uses the next durable
observation transition to prove both registry and container absence.

Final live cycle on an independent copy of the automatic pre-light backup:

- Baseline file SHA-256:
  `36B88F0C96AEAC9D27322BA6DE5D848D254B5414E36E271C5713F181D2589134`
- Baseline MCP represented-state fingerprint:
  `f59475b31d2f4702ef208560676b565a0e973edabbb31ac70ebb64fc3fbcfeff`
- Apply request: `light-world-delete-20260816-080415`
- Apply result: `success=true`, revision `0 -> 1`, `outcome_unknown=false`
- Undo request: `light-world-undo-20260816-080431`
- Undo result: `success=true`, revision `1 -> 2`, `outcome_unknown=false`
- Post-undo MCP light count: `0`
- Post-undo item set: the same two baseline objects
- Post-undo represented-state fingerprint:
  `f59475b31d2f4702ef208560676b565a0e973edabbb31ac70ebb64fc3fbcfeff`
- Exact baseline match: `true`

The public MCP route was proven by the first apply. Later rollback repetitions
used the same production `AtomicMailbox` directly because the complete accepted
catalog was older than the current 45-second MCP freshness window after restart.
All inspection checks used the source-built MCP server because the installed
desktop executable predates the new `light` inspection contract.

## Installed source identity

- Source bridge:
  `D:\DungeonDraft\DDAI\.worktrees\bootstrap-core\mods\DDAI\scripts\ddai_bridge.gd`
- Active installed bridge:
  `C:\Users\ChrisBoyd\AppData\Local\DDAI\DungeondraftMods\DDAI\scripts\ddai_bridge.gd`
- Source and installed SHA-256:
  `8216556EEE7338CACE7914C60440530CC7E7837CDC5EF760884747B4632AF21B`

The pre-hardening installed bridge was copied recoverably outside the mods tree to
`C:\Users\ChrisBoyd\AppData\Local\DDAI\backups\bridge\ddai_bridge-20260816T1226354134186Z.gd.bak`
before the final install. Keeping the backup outside `DungeondraftMods` prevents it
from being discovered as a duplicate mod.

The long-lived ChatGPT/Claude MCP processes were not terminated. A final
self-contained connector publish/install is still required after the complete
verification gate so those clients receive the new inspection contract on their
next normal restart.

## Verification

- Exact Godot 3.5.3 light/container behavior: green
- Exact hostile light numeric RED then GREEN: derived-geometry overflow, range
  underflow, and intensity underflow all reject before mutation
- Focused Core executor/inspection contracts: 33/33 green
- Focused App MCP inspection contracts: 11/11 green
- Full serialized Release tests: App 364/364 plus Core 450/450 = 814/814
- Release build: zero warnings and zero errors
- Direct/transitive vulnerable-package audit: none found
- Changed-project formatting, diff checks, and listener scan: clean
- Independent Critical/Important review: CLEAN after the hostile numeric boundary
  fix and final installed/source hash reconciliation
