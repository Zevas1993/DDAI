# DDAI map data probe live gate result

Recorded: 2026-08-14

## Outcome

**GATE PASSED.** `Global.ModMapData` exists on Dungeondraft 1.2.0.1 and holds a Dictionary. The durable map identity design proceeds.

The redesigned probe, which uses direct member access and no reflection of any kind, returned a clean record without destabilising the application. This follows an earlier attempt whose reflective probe crashed the host; see the crash report for that analysis.

## Live evidence

Fresh disposable map `Tabula Rasa`, 40x30, created after a normal restart.

```json
"map_data_probe": {
  "available": true,
  "is_dictionary": true,
  "discovery": "direct_member",
  "ddai_key_present": false,
  "foreign_key_count": 1,
  "stored_uuid_valid": false,
  "reason": "map_data_present"
}
```

- `available` and `is_dictionary` true: the store is real and correctly typed.
- `discovery` `direct_member`: direct access succeeded, matching the behaviour of the shipping `Lievven.Snappy_Mod`.
- `ddai_key_present` false: correct for a map DDAI has never written to.
- `foreign_key_count` 1: another mod already holds a key on a newly created map. `ModMapData` is a live shared store, so preserving foreign keys is a real requirement rather than a theoretical one.

Process remained responsive with title `Tabula Rasa - Dungeondraft`. No Application Error or Windows Error Reporting event was raised.

## Remaining scope

`map_id` still reports the session-derived value (`786abd4d...` this session) because resolution, minting, and binding are Tasks 3 to 5 and are not implemented. This gate establishes only that the storage mechanism the design depends on is available and usable.

## Next

Proceed to Task 3. Binding must write only the DDAI-owned key and leave the observed foreign key untouched.
