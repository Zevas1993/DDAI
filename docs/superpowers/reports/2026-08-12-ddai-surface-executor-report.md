# DDAI surface executor checkpoint

Recorded: 2026-08-12

## Outcome

The bridge now has strict, bounded executor and reversal foundations for terrain strokes, pattern regions, colorable pattern regions, cave regions, and roof regions. Terrain remains fail-closed when the required managed adapter is unavailable. Exact Godot 3.5.3 behavior tests exercise pattern, colorable-pattern, cave, and roof mutation, observation, tool-state restoration, category-specific reversal, and tamper rejection.

This is an implementation checkpoint, not public certification of the new surface families. The only advertised universal mutation remains `wall_polyline`.

## Live evidence

On the disposable saved `test.dungeondraft_map`, the installed bridge successfully applied one cave request and one roof request with correlated successful responses and revision movement from 0 to 2. Both changes remained visible after save, normal close, and reopen.

A second sacrificial cave was applied in an unsaved auxiliary process and then tested with Dungeondraft's ordinary Ctrl+Z. The cave remained because the native brush records multiple internal samples rather than one DDAI job. This disproves generic application Undo as the connector's rollback contract.

Consequently cave and roof executors are deliberately withheld from `certified_operation_types` until `ddai_undo_last_job` reverses the exact durable DDAI job evidence and passes live acceptance. The saved test map was not changed by the sacrificial undo experiment.

## Duplicate-mod incident

Dungeondraft reported a duplicate DDAI mod because a timestamped installation backup was visible under the active Mods root. The installation layout now keeps timestamped backups under `%APPDATA%\Dungeondraft\ddai\mod-backups`; the active `%LOCALAPPDATA%\DDAI\DungeondraftMods` root contains exactly one `DDAI` directory.

## Verification

- Exact production bridge under pinned Godot 3.5.3: surface executor class 6/6 passed after the capability-withholding RED/GREEN cycle.
- The prior fresh full Release run on the surface implementation passed 424 Core plus 354 App tests, 778/778 total.
- The saved map remained 317,364 bytes with timestamp 2026-08-12 15:43:58 after the unsaved sacrificial experiment.

## Next gate

Implement the durable `undo_last_job` mailbox command and `ddai_undo_last_job` MCP tool. It must require the latest completed DDAI job, exact current map identity/revision, complete reversal evidence, and no intervening edit. Only after that tool passes exact-Godot, restart/crash, duplicate, and live tests may cave or roof be added to the public certified capability list.
