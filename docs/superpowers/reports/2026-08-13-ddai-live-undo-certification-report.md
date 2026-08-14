# DDAI live job-scoped undo certification

Recorded: 2026-08-13

## Outcome

`ddai_undo_last_job` passed live certification against Dungeondraft 1.2.0.1 through the published connector. Job-scoped reversal is proven bit-exact, idempotent, selective, and fail-closed on identity and ordering violations.

This clears the gate recorded in the surface executor checkpoint. Cave and roof may now proceed to their own live create/undo/save/reopen certification.

Four defects were found. None falsify the reversal contract; two affect the reported outcome and one affects identity durability.

## Environment

- Dungeondraft 1.2.0.1, exact runtime gate satisfied
- mod_version `0.2.1`, connector `0.1.0`
- disposable saved `test` map, 40x30, one level
- installed `ddai.exe` SHA-256 `3A1B6DC4…7F3EBF2`, byte-identical to `artifacts/task6/win-x64/ddai.exe`
- installed mod scripts byte-identical to `mods/DDAI`
- active Mods root contained exactly one `DDAI` directory
- baseline map fingerprint `ada75c99b316dec9da2643d99d0b987086610834454ce912f9eb31b56f726db5`, 24 native items

## Certified behavior

| # | Property | Evidence | Result |
|---|---|---|---|
| 1 | Job-scoped reversal | applied `wall_polyline` node 46, revision 0→1; undo revision 1→2; fingerprint returned to exactly `ada75c99…` | PASS |
| 2 | Idempotent undo replay | same undo re-sent, byte-identical response, revision unchanged | PASS |
| 3 | Map-identity fail-closed | undo with foreign `expected_map_id` → `map_id_mismatch`, `outcome_unknown:false`, map unchanged | PASS |
| 4 | Latest-job-only | job A then job B; undo targeting A → `undo_job_unavailable`, clean, map unchanged | PASS |
| 5 | Selective reversal | undo B removed only B's wall; A's node 47 and all 24 baseline items preserved; fingerprint exactly the post-A value `6bafeade…` | PASS |
| 6 | Apply idempotency after timeout | timed-out apply had in fact mutated; retry with same `request_id` returned true result and created no duplicate | PASS |

Property 5 is the decisive one: reversal is bound to recorded native node identifiers, not to application-global Undo.

## Defects

### D1 — `map_id` is a session identity, not a map identity

`ddai_bridge.gd` `_current_map_id()` returns
`sha256("session_id=" + _session_id + "world_instance_id=" + Global.World.get_instance_id())`.

Observed live: the map fingerprint and all 24 items were unchanged while `map_id` moved from `4ac0991e…` to `9cad63ef…` after a mod rescan.

Consequences:
- the same map reopened later is a different `map_id`, so no plan or undo receipt survives a reload
- a durable job recorded before a bridge restart can never satisfy the undo identity check, making it permanently unreversible
- agents must re-read `map_id` immediately before every validate/apply

Severity: high. It contradicts the durable-undo intent and the "crash safe" framing, and the field name misleads clients.

### D2 — first mutation attempt times out while succeeding

Two of three applies returned `apply_timeout` with `outcome_unknown:true` and `retry_with_same_request_id:true`, yet the map had already been mutated. Retrying with the same `request_id` returned the correct result and created no duplicate.

Correctness holds because of idempotency, but every mutation costs two round-trips and emits a false "outcome unknown" that an autonomous agent must be trained to handle. The deadline appears too short for the first mutation round-trip.

Severity: medium. Correctness intact, reliability and agent ergonomics poor.

### D3 — undo of an already-reversed job times out instead of rejecting

Re-targeting a reversed job with a fresh `request_id` returned `undo_timeout` with `outcome_unknown:true`. Verification showed the map fingerprint still at baseline and the bridge fully responsive. The unknown-outcome signal was false.

Compare with D-4 case (property 4), which returns the clean `undo_job_unavailable`. The already-reversed path should reject the same way.

Severity: medium. A false `outcome_unknown` is the most alarming state the protocol can report.

### D4 — undo depth is one and is not restored

After undoing B, job A was no longer undoable (`undo_job_unavailable`). Reversing the newer job does not reinstate the older job's evidence. Node 47 from job A remained on the map permanently from the connector's perspective.

This may be intended, but it is undocumented and materially limits multi-step agent workflows: an agent cannot walk back a sequence of jobs.

Severity: low to medium, pending a decision on intent.

## Map state at close

Revision 5, fingerprint `6bafeadea5925c3422157cde39e47b0b1b14a80a81ce5e7d9e5f131af66ccddd`. One extra `wall_polyline` (node 47, grid 2,2 size 5x4) remains from job A and is not connector-reversible. The disposable map was never saved during certification. All 24 pre-existing items are intact.

## Next gate

1. Fix D1 by deriving `map_id` from durable map identity rather than session and world instance.
2. Raise or adapt the mutation deadline and make the already-reversed undo path reject cleanly (D2, D3).
3. Decide and document undo depth (D4).
4. Proceed to live cave and roof certification, then publish those capability flags.
