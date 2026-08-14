# DDAI durable map identity design

Recorded: 2026-08-14

Addresses defect D1 from the [live undo certification report](../reports/2026-08-13-ddai-live-undo-certification-report.md).

## Problem

`ddai_bridge.gd` derives map identity from session state:

```gdscript
func _current_map_id():
	if Global.World == null:
		return ""
	return (_framed_string("session_id=", _session_id) + "world_instance_id=" + str(Global.World.get_instance_id()) + "\n").sha256_text()
```

`_session_id` is `str(OS.get_unix_time()) + "-" + str(OS.get_ticks_msec())`, assigned once at bridge start. `Global.World.get_instance_id()` changes whenever the world node is re-instantiated.

Observed live on 2026-08-13: the map content fingerprint and all 24 native items were unchanged while `map_id` moved from `4ac0991e…` to `9cad63ef…` after a mod rescan.

The field is therefore a session identity wearing a map identity's name. Consequences:

- the same map reopened is a different `map_id`, so no plan or receipt survives a reload
- a durable job recorded before a bridge restart can never satisfy the undo identity check
- agents must re-read `map_id` immediately before every validate/apply, with no way to know why

## Goal

`map_id` identifies the map. It is stable across mod reload, application restart, and map close/reopen, and differs between different maps.

This serves the product goal of several MCP clients — Claude Desktop, ChatGPT/Codex, and Gemini — building maps against one shared connector. Those clients connect and reconnect independently, so a session-scoped identity desynchronizes them: one client's plan becomes unapplicable the moment another triggers a reload, and no client can confirm it is editing the same map another just modified. Durable identity is the precondition for correlating multi-client work on one map.

## Non-goals

- Making undo survive a restart. Reversal evidence also records native node identifiers, and this change alone does not establish their durability. That is a separate defect and must not be implied by this change.

  API review does suggest node identifiers persist: `World` exposes `NodeLookup`, `nextNodeID`, `AssignSpecificNodeID`, and `HasNodeID` ("Checks if a node id exists/has already been used") alongside `Save` and `Load`, and the Serializing Mod Data tutorial directs mods to store a `node_id` and later recover the instance through `Global.World.GetNodeByID(id)`. Restart-durable undo therefore looks feasible once identity is durable, but it requires its own live proof and its own certification.
- Fixing the mutation deadline (D2) or the already-reversed undo path (D3).
- Changing undo depth (D4).

## Design

### Identity model

Map identity is a 64-character lowercase hex string stored in Dungeondraft's per-map mod storage:

```
Global.ModMapData["org.ddai.status_bridge"]["map_uuid"]
```

`Global.ModMapData` is a dictionary keyed by mod id that Dungeondraft serializes into the map file.

**It is undocumented.** A 2026-08-14 review of the official Dungeondraft Modding API found no mention of `ModMapData` in the Globals page, the Serializing Mod Data tutorial, the World, MapSettings, or AssetPack references, or the Megasploot GitHub modding wiki. The sole evidence it exists is the third-party Custom Snap Mod (`Lievven.Snappy_Mod` 1.2.5), which calls `Global.ModMapData[TOOL_ID]` to store and load per-map settings and works against 1.2.0.1.

The official persistence route is instead a `user://` file written with Godot's `File` class — the mechanism DDAI's mailbox already uses. That route cannot express per-map identity without already having a map identity to key it by, which is circular for this problem.

The API also exposes no map identity of any kind: no file path, filename, or GUID. `World.Title` is user-editable and not unique. `MapSettings` carries only grid and camera styling.

Relying on `ModMapData` is therefore a deliberate bet on observed, unsupported runtime behavior. This is consistent with how DDAI certifies every other route — the wall finalization route is likewise certified by live proof against an exact executable hash, not by documentation — and the connector already refuses to operate against any build other than the allowlisted 1.2.0.1. The bet is bounded by that same gate.

`_current_map_id()` no longer references `_session_id` or `world_instance_id`.

### Resolution and minting

On map load, the bridge resolves identity once and caches it for the session:

1. Read `Global.ModMapData["org.ddai.status_bridge"]`.
2. If it holds a `map_uuid` that is a valid 64-hex string, adopt it. State is `bound`.
3. Otherwise generate a fresh UUID **in memory only** and cache it. State is `pending`. Nothing is written.
4. If no map is open, identity is the empty string. State is `unbound`.

A malformed or wrong-typed stored value is treated as absent: the bridge mints a replacement rather than failing. It never throws on foreign data in `ModMapData`, and it never removes or rewrites another mod's keys.

Generation uses the existing session entropy plus the world instance id, hashed to 64 hex. Uniqueness, not unpredictability, is the requirement.

### Binding

The cached UUID is written to `ModMapData` during `apply_plan` preflight, before any native mutation, and only when state is `pending`. After the write, state is `bound`.

Read-only commands — `status`, `inspect_map`, and every catalog command — never write. A freshly opened map is never marked modified by DDAI.

Because the UUID is minted in memory at load, **binding never changes the identity value**. It changes only durability. An agent that reads `map_id` from `status` and then applies a plan sees the same `map_id` throughout.

Writing to `ModMapData` marks the map modified, exactly as any native edit does. The value reaches disk when the user saves.

### States

| State | Meaning |
|---|---|
| `bound` | The UUID is present in `ModMapData`, either read at load or written by a mutation. It persists when the map is saved. |
| `pending` | The UUID exists in this session only. No mutation has occurred and nothing was written. The next mutation binds it. |
| `unbound` | No map is open. `map_id` is the empty string. |

`bound` means "will persist on save", not "already on disk". A map that is bound but never saved yields a fresh identity next session. This is stated plainly in user documentation rather than encoded as a fourth state, because the bridge cannot observe a save reliably.

### Wire contract

`map_id` keeps its field name, position, and shape. One field is added:

```
map_identity_state: "bound" | "pending" | "unbound"
```

It appears in the `ddai_status` payload and the `ddai_inspect_map` response, alongside the existing `map_id`.

.NET changes are confined to:

- `src/DDAI.Core/Maps/MapSnapshotContracts.cs` — carry the new field
- `src/DDAI.App/DdaiStatusService.cs` and `DdaiMapInspectionService.cs` — surface it
- `src/DDAI.App/Mcp/DdaiMapTools.cs` — schema

Identity checks in `apply_plan` and `undo_last_job` are unchanged in structure. They continue to compare `expected_map_id` against `_current_map_id()` and fail closed with `map_id_mismatch`.

### Compatibility

This is a breaking change to durable state. Job journals and undo receipts keyed to old session-derived identities can never match a new identity and are unusable. They are discarded on upgrade rather than migrated: undo depth is one, and that evidence was already session-local in practice.

`mod_version` moves `0.2.1` → `0.3.0`. The mailbox schema version is unchanged, since no message shape is removed or repurposed. Capability receipts and the operation certification matrix are unaffected.

### Accepted limitations

- **Copied maps share identity.** Save As, or copying a `.dungeondraft_map` file, duplicates the stored UUID, so two files report one identity. Undo additionally checks revision and state fingerprint, which narrows the practical risk. Accepted in exchange for identity surviving rename and move.
- **Never-saved maps are session-scoped.** A map that is never saved cannot carry durable identity.
- **Foreign writes.** Another mod could overwrite DDAI's key. The bridge re-mints rather than failing, and the resulting identity change is reported honestly through `map_identity_state`.

## Primary risk

The design rests on undocumented behavior: that `Global.ModMapData` is writable by DDAI and round-trips through save and reopen. Custom Snap Mod's shipping use of it is strong evidence, but it is not proof for this mod, this key, or this build, and no vendor documentation backs it.

The implementation plan must therefore, in order:

1. Add a read-only probe to `mods/DDAI/scripts/ddai_operation_certifier.gd` reporting presence, type, and DDAI-key contents of `Global.ModMapData`, calling no mutating method.
2. Run that probe live against 1.2.0.1 on a disposable map.
3. Only then implement binding.
4. Prove live: mint, mutate, save, close, reopen, and require the identical `map_id` with state `bound`.

If the probe shows `ModMapData` is unavailable or not persisted, this design is void. The obvious fallbacks are already closed: the API exposes no map file path to hash, and a `user://` sidecar needs a map key it cannot obtain. The remaining option would be to embed identity in a durable native map element, which is materially more invasive.

In that case the correct action is not to substitute a weaker scheme silently. It is to stop, rename the wire field to state plainly that identity is session-scoped, document that durable identity is unavailable on this build, and bring the question back for a decision.

## Testing

**Exact pinned Godot 3.5.3 fixture tests** in a new `tests/DDAI.Core.Tests/Executors/DungeondraftMapIdentityTests.cs` with its fixture under `tests/DDAI.Core.Tests/Executors/GodotFixtures/MapIdentity`, following the existing `SurfaceExecutors` fixture pattern:

- bound: a valid stored UUID is adopted verbatim
- pending: absent storage mints in memory and writes nothing
- malformed: non-string, wrong length, non-hex, or wrong type re-mints and writes nothing
- read-only isolation: `status` and `inspect_map` leave `ModMapData` byte-identical
- binding: `apply_plan` preflight writes exactly the cached UUID under exactly the DDAI key, preserving foreign keys
- stability: identity value is identical before and after binding
- idempotence: a second mutation does not rewrite or change the stored UUID

**.NET tests:**

- contract round-trip for `map_identity_state` across all three values
- status and inspection expose the field
- an unknown or missing state value fails closed rather than defaulting to `bound`

**Live proof:** apply, save, close, reopen, and require the identical `map_id` with state `bound`, then confirm a second map has a different identity.

## Sources

Reviewed 2026-08-14. `ModMapData` appears in none of them.

- [Dungeondraft Modding API — Introduction](https://megasploot.github.io/DungeondraftModdingAPI/)
- [Globals](https://megasploot.github.io/DungeondraftModdingAPI/Globals/)
- [Serializing Mod Data](https://megasploot.github.io/DungeondraftModdingAPI/tutorials/SerializingModData/)
- [World reference](https://megasploot.github.io/DungeondraftModdingAPI/reference/World/)
- [MapSettings reference](https://megasploot.github.io/DungeondraftModdingAPI/reference/MapSettings/)
- [Megasploot/Dungeondraft modding wiki](https://github.com/Megasploot/Dungeondraft/wiki/Dungeondraft-Modding)

Local evidence: `D:\DungeonDraft\Dungeondraft\mods\custom_snap\scripts\snappy_mod.gd` lines 1230-1243.

## Success criteria

1. Reopening the same saved map yields the identical `map_id`.
2. A mod reload without reopening the map yields the identical `map_id`.
3. Two different maps never share an identity, except by file copy.
4. No read-only command modifies `ModMapData` or marks the map dirty.
5. Full Release Core and App suites pass with zero warnings.
