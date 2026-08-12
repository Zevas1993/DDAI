# DDAI universal wall executor report

Recorded: 2026-08-11

## Outcome and boundary

Task 5 implements the first revision-locked universal-plan executor in the Dungeondraft bridge: `wall_polyline`. It is now live-certified on the disposable saved test map in Dungeondraft 1.2.0.1 through the installed MCP executable. The live run added one unsaved test wall; no user-authored saved map existed or was modified.

The bridge accepts strict schema-2 plans before the legacy schema-1 room route, validates exact plan and operation shapes, binds the plan to the open map revision, accepted asset-catalog revision/fingerprint, documented current level, idle WallTool state, and uniquely resolved live wall texture. Only an explicit `wall_polyline` function is dispatchable.

## Durable execution and recovery

Each poll crosses one durable boundary: prepared journal, native call held only in memory, applied IDs, observed IDs, next-operation preparation, reversal, terminal result, response publication, completion receipt, claim deletion, then journal cleanup. Restart from prepared or reversing state never replays an uncertain native call. A map/session change converts an active job to a correlated `outcome_unknown` response before any old node ID is observed or reversed.

Observation failure retains the just-created wall ID and reverses that current operation first. Reversal requires both World registry absence and absence from every documented `Level.Walls` container after a yielded frame. The final represented-state fingerprint is captured only after that postcondition; status on the following frame does not advance the revision again.

Recovered journals enforce positive, unique, globally bounded node IDs; exact flattened observations; state-specific current-ID disjointness/equality; exact reversal index semantics; and exact committed success/reversed/outcome-unknown response shapes. Completion receipts bind the exact committed journal and response hashes. A present journal must match that hash before cleanup or duplicate reconciliation; after verified journal cleanup, the immutable completion receipt remains independently sufficient for same-request idempotency.

## Cross-language identity

Plan fingerprints encode every finite double as canonical big-endian IEEE-754 bits (`16` lowercase hex digits) in both .NET and Godot. This avoids locale, decimal-rounding, and exponent-format differences. The target Godot parser underflows magnitudes below `1e-300`, so the universal validator and bridge reject nonzero numeric magnitudes below that bound rather than silently changing plan geometry.

Preflight failures recover the submitted catalog revision's immutable fingerprint from the canonical pointer or either durable pointer slot. Map, level, and catalog-race errors therefore retain the original submitted map/catalog correlation fields. If the original accepted catalog identity is unavailable or conflicting, the bridge blocks instead of publishing an uncorrelated response.

The accepted-catalog reader now requires the exact nine-field manifest contract, all fourteen canonical category counts, unique bounded chunk receipts, count consistency, strict error records, a canonical UTC timestamp, and the canonical manifest fingerprint used by the .NET reader. That fingerprint is recomputed locally and binds every chunk hash and byte/count receipt. The bridge also revalidates the current pointer and complete canonical manifest identity immediately before each native operation. A behavior test substitutes a different valid chunk and matching chunk hash while retaining the old claimed catalog fingerprint; the bridge rejects it before mutation.

## TDD and executable evidence

Genuine REDs covered missing v2 routing, missing executor state, unsupported operation dispatch, lost applied IDs on observation failure, loose journal invariants, uncorrelated preflight failures, completion tampering, duplicate-after-cleanup liveness, trailing-junk asset references, full-double fingerprint drift, shared Godot user-data races, and pre-frame wall deletion/revision drift.

Current focused GREEN evidence:

- exact production script under pinned Godot 3.5.3: wall creation, observation, reversal, yielded-frame postcondition, stable revision, prepared restart, nonzero-base map/session change, strict plan failures, catalog-race correlation, journal/response/completion tamper rejection, and duplicate-after-cleanup idempotency;
- .NET/Godot fingerprint parity for high-precision plan coordinates and a numeric corpus from `1e-300` through the maximum grid coordinate;
- Core plan/executor/package/conformance focus: 244/244 in the broad filter plus a clean isolated exact-Godot replay after one concurrent harness timeout;
- connector universal-plan service focus, including post-publication preflight rejection: 13/13;
- fresh full Core suite: 405/405;
- fresh full App suite: 349/349;
- final full Release total: 754/754;
- Release build: zero warnings and zero errors;
- pinned Godot parser, listener scan, and exact executor replay: 3/3;
- vulnerable-package audit: no vulnerable direct or transitive packages;
- changed-file formatting and diff checks: clean.

An earlier full App run had two timing-sensitive test failures outside this change: one catalog quarantine-count race and one raw cancellation-log string assertion. Both passed in isolation, and the fresh final App run passed all 349 tests. One broad focused Core run also made the exact Godot child hit its 30-second harness bound while 243 companion tests passed; the exact test immediately passed alone in 906 ms, and the subsequent full Core run passed all 405 tests in four seconds.

## Public API route

The wall executor uses the documented `WallTool.Enable`, `Global.WorldUI.AddPolyPoint`, `WallTool.EndWall`, `Global.World.HasNodeID`, and persisted `node_id` metadata routes. It preserves the prior wall texture/color and active-tool state. Observation scans only the bounded documented `Level.Walls` containers and correlates persisted IDs exactly once.

## Remaining acceptance

Independent final review is clean with no remaining Critical or Important findings. The owned mod was installed at `C:\Users\ChrisBoyd\AppData\Local\DDAI\DungeondraftMods\DDAI`; source and installed script SHA-256 were both `D4ED513265ED6F727AE5575639D5F1906BC3C8EA4012CD7D69B66A89B31D8580`.

Live acceptance used Dungeondraft PID 72160 and the saved disposable 40 by 30 map. After a fresh 1,947-entry catalog was accepted at revision `1786510365298`, the installed MCP server validated request `live-wall-final-proof-20260812-0053` and returned `success:true`, `outcome_unknown:false`, and map-job revision 1 from `ddai_apply_plan`. A subsequent native `inspect_map` response found exactly two walls: the pre-existing fixture wall and node 2 at grid bounds `x=5,y=25,width=7,height=0`. Replaying the identical MCP request returned the byte-equivalent successful result, and a second inspection still found exactly two walls, proving no duplicate mutation. Dungeondraft remained responsive and the new test wall remained visibly unsaved.

The live run exposed and fixed the final cross-language issue: map inspection previously rejected an axis-aligned wall because its native `GlobalRect` has one zero extent. Godot and .NET now require finite non-negative extents with at least one positive dimension; horizontal and vertical lines are accepted while a zero-by-zero point remains invalid. The focused exact-Godot and .NET regression set passed 23/23, and an independent reviewer returned CLEAN on this bounds delta.

Fresh post-live verification passed all 755 Release tests (406 Core and 349 App). The Release build completed with zero warnings and zero errors; changed-file formatting and staged diff checks were clean. GitNexus classified the seven-file staged delta as medium risk with one affected map-validation flow.

This certifies the current `wall_polyline` path, not every planned Dungeondraft operation category. Object placement, portals/doors, paths, roofs, terrain/material painting, text/lights, save/export, and explicit undo still require their own native executors and live certification before the connector can build arbitrary complete maps autonomously.

The final staged GitNexus scan covers 16 files and 78 changed symbols, reports one affected validation flow, and assigns medium risk. The full Core/App, exact-Godot, build, and independent-review gates above are the controlling evidence for that cross-language surface.
