# DDAI durable map identity live certification

Recorded: 2026-08-15

## Outcome

**Durable map identity is certified.** `map_id` survived a full Dungeondraft restart and a save/close/reopen cycle, and distinct maps receive distinct identities. Defect D1 is fixed.

## Evidence

Mod `0.3.0` installed, all five files hash-identical to source, exactly one `DDAI` directory under the active Mods root.

**Distinctness.** Two maps created in one session reported different identities:

| Map | map_id |
|---|---|
| first new map | `ed91ee311d7d9ae72aefe7359cb09b3937296cf5f69edc0d54671721fd747be2` |
| second new map | `eede331670fc9bf0c5cb20515592b8ce4ca3a59ad43d0e219e6bb762e4a343df` |

**Durability across restart.** The second map was saved as `test1`, Dungeondraft was closed normally and relaunched, and the map reopened:

| | before | after |
|---|---|---|
| process | PID 8456, started 00:46:39 | PID 33812, started 02:33:02 |
| bridge session | earlier session | `1786775590-7928` |
| `map_id` | `eede3316...a343df` | `eede3316...a343df` |
| `map_identity_state` | `bound` | `bound` |

The previous implementation hashed the bridge session id together with the world instance id, so a new process guaranteed a new identity. An identical value across a restart could not have been produced by the old scheme.

## Open question

`map_identity_state` reported `bound` with `stored_uuid_valid: true` on maps that DDAI had never mutated, and `_bind_map_identity()` has exactly one call site, inside `_preflight_universal_plan`, which runs only on mutation paths. No plan was applied during this session.

The uuid demonstrably reached the map file, since it survived save and reopen, so a write did occur. When it executed is not accounted for. This does not weaken the certification, because the observable contract held in both directions, but it means the `pending` to `bound` transition has not been observed directly and should be before the state field is relied on to warn an agent that work will not persist.

## Known limitation at time of certification

`ddai_inspect_map` returned `invalid_response` throughout. The connector serving this session was the previous published executable, which predates the `map_identity_state` field and validates the inspection payload strictly. The rebuilt `0.3.0` artifact accepts it and the full suite passes against it. Re-run inspection against the rebuilt server before treating that tool as certified.

## Verification

- Core suite `437/437`, App suite `363/363`
- installed mod hashes identical to source
- no crash event or dump during the session
