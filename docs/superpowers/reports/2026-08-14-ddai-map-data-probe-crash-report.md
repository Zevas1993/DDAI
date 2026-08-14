# DDAI map data probe crash report

Recorded: 2026-08-14

## Outcome

The Task 2 live gate did not answer whether `Global.ModMapData` exists. It crashed Dungeondraft 1.2.0.1 instead. The gate still did its job: it ran on a newly created disposable map, the user's saved map was untouched, and the failure was contained and understood.

## What happened

The installed probe detected `ModMapData` two ways: property-list reflection, and a name-based `Object` lookup. The second path was added deliberately after review raised the risk that a plain host field would not enumerate, and that a false `available: false` would abandon a valid design for the wrong reason.

Sequence:

- `20:03:15Z` mod loaded, runtime receipt written, session `1786737795-16959`
- `20:03:58Z` `ddai_status` request written and claimed into `processing/`
- `20:03:59Z` `Application Error`, faulting application `Dungeondraft.exe` version `1.2.0.1`
- `20:04:01Z` Windows Error Reporting bucket recorded, dump written to `%LOCALAPPDATA%\CrashDumps\Dungeondraft.exe.14628.dmp`

Dump analysis with the pinned debugger shows access violation `c0000005` and a four-frame GDScript interpreter cycle repeating down the entire stack:

```
Dungeondraft!main+0x13de6fa
Dungeondraft!main+0xa3296
Dungeondraft!main+0xb22a3
Dungeondraft!main+0x12cc988
Dungeondraft!main+0x13de68e   <- repeats
```

That is unbounded recursion terminating in stack exhaustion, not a null dereference.

## What was and was not established

**Established.** Reflective access to `Global` from the probe crashes the host. The crash occurs while servicing a `status` request, which is exactly when the probe runs.

**Not established.** Which of the two reflective calls recursed. The probe made both in the same function and no instrumentation separated them. Attributing the crash to the name-based lookup alone was an inference, not a measurement, and is not recorded here as fact.

**Independent evidence.** Direct member access works on this build. The shipping `Lievven.Snappy_Mod` 1.2.5 reads `Global.ModMapData[TOOL_ID]` directly and functions normally.

## Resolution

The probe now uses direct member access only. Neither reflective mechanism is used, which makes the fix correct regardless of which one recursed.

A pinned-Godot fixture (`tests/DDAI.Core.Tests/Executors/GodotFixtures/MapIdentity`) supplies a fake host that records any reflective lookup. A regression fails the harness offline instead of taking down a user's application. A static test additionally forbids property-list reflection and name-based lookup inside `probe_map_data`.

Fixture `Global` stubs now declare `ModMapData`, matching the real host, because reaching for a property that does not exist is itself the reflection being avoided.

## Recovery

The installed mod was rolled back to the pre-probe build taken immediately before installation. The crashing build is preserved at `%APPDATA%\Dungeondraft\ddai\mod-backups\DDAI-CRASHED-probe-20260814`. The active Mods root contains exactly one `DDAI` directory. The user's `test.dungeondraft_map` remained 317,364 bytes with its original timestamp throughout.

## Verification

- Core suite `430/430`, App suite `363/363`
- map identity tests `5/5`, including the hostile-reflection fixture marker
- no reflective host access remains in either mod script

## Next gate

The original question is still unanswered: does `Global.ModMapData` exist and hold a dictionary on this build? Answering it requires installing the redesigned probe and repeating the live run on a disposable map.

Before that run, decide whether a crash during a live gate should automatically restore the previous mod build. This run required manual rollback, which is acceptable once and poor practice to rely on.
