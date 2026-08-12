# DDAI exact-runtime operation capability report

## Outcome

Task 1 of the all-category executor plan is complete. The connector now publishes one closed-world capability record for each of the 14 universal operation types. Runtime mutation certification is separated from route presence, reflection evidence, property evidence, and transient tool busy state.

The exact Windows runtime gate is bound to the installed Dungeondraft 1.2.0.1 executable, not the `.ddmod` minimum-version declaration:

- path verified externally: `D:\DungeonDraft\Dungeondraft\Dungeondraft.exe`
- file version/product version: `1.2.0.1`
- executable size: `31,782,536` bytes
- SHA-256: `C14DDDDBAADA43610E763F73F0C2786983CA4440578ACDE0AC9668CDA358AF02`

An unallowlisted executable is reported as `unknown`, makes the connector runtime state `incompatible`, and certifies no mutation operation.

## Live evidence

The owned mod was installed under `C:\Users\ChrisBoyd\AppData\Local\DDAI\DungeondraftMods\DDAI` and Dungeondraft was opened normally with the saved disposable test map. Fresh session `1786536575-2758` reported:

- exact operation records: `14`
- routes present: `14`
- runtime-certified operations: `1`
- certified operation: `wall_polyline`
- exact observed Dungeondraft version: `1.2.0.1`
- remaining operations: `executor_not_live_certified`
- Dungeondraft process remained responsive with title `test - Dungeondraft`

The initially created recoverable backup was moved intact from the scanned Mods directory to `C:\Users\ChrisBoyd\AppData\Roaming\Dungeondraft\ddai\mod-backups`; the active Mods directory contains zero DDAI backup copies, eliminating the duplicate-mod popup.

## Verification

- strict capability tests: `14/14`
- pinned Godot 3.5.3 certifier, package, and universal bridge tests: `24/24`
- full Release Core suite: `407/407`
- full Release App suite: `353/353`
- full Release build: `0` warnings, `0` errors
- installed/source script hashes: identical for all three DDAI scripts
- independent review: CLEAN, no Critical or Important findings

## Remaining scope

Only `wall_polyline` is mutation-certified. The other 13 operations remain visible to AI clients as schema-supported, route-present, and explicitly not live-certified. They must be implemented and pass disposable-map create/observe/reverse/save/reopen evidence before their certification flags can become true.
