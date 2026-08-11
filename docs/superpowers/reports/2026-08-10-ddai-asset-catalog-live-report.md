# DDAI asset catalog generated-activation live report

Recorded: 2026-08-10T20:31:51.0847854-04:00

## Outcome

`NEEDS_CONTEXT`

The pre-live generated/catalog integration and automated Release verification are complete. Live installation and runtime certification were not attempted because Dungeondraft was already running. The process was inspected read-only:

```text
Id: 22892
ProcessName: Dungeondraft
StartTime: 2026-08-10 10:45:22 -04:00
Path: D:\DungeonDraft\Dungeondraft\Dungeondraft.exe
```

No process input was sent. DDAI did not close, kill, automate, restart, install into, or alter the running Dungeondraft session or its open map.

## Automated pre-live evidence

The generated-catalog integration test imports a real normalized PNG through `GeneratedAssetStore`, reads its immutable staged manifest through `AssetCatalogRepository`, and searches through `AssetSearchService`.

- Default search excludes the staged item.
- `include_staged_generated = true` includes it with `placeable = false`.
- A different live generated entry does not activate it.
- Accepted catalog revision/fingerprint/snapshot metadata remains authoritative and unchanged by the staged overlay.
- After accepted live revision 42 contains the exact staged `sha256:` asset reference, default search returns the live entry with `placeable = true`.
- Opt-in search returns that same authoritative live entry once, with no staged duplicate.

Focused result: `1 passed, 0 failed, 0 skipped`.

Assets regression result: `135 passed, 0 failed, 0 skipped`.

Full Release result: `567 passed, 0 failed, 0 skipped` (`312` Core and `255` App).

Release build result: succeeded with `0 warnings, 0 errors`.

## Live evidence ledger

The following fields are deliberately not populated from fixtures or assumptions:

| Required evidence | Status | Reason |
|---|---|---|
| Fresh DDAI/Dungeondraft session identifier | NOT OBSERVED | Dungeondraft was already running; no install/reload/reopen was authorized or performed. |
| Live catalog revision | NOT OBSERVED | No fresh runtime catalog receipt was produced. |
| Live catalog fingerprint | NOT OBSERVED | No fresh runtime catalog receipt was produced. |
| Terrain count | NOT OBSERVED | No live runtime query. |
| Patterns count | NOT OBSERVED | No live runtime query. |
| Patterns Colorable count | NOT OBSERVED | No live runtime query. |
| Caves count | NOT OBSERVED | No live runtime query. |
| Roofs count | NOT OBSERVED | No live runtime query. |
| Objects count | NOT OBSERVED | No live runtime query. |
| Walls count | NOT OBSERVED | No live runtime query. |
| Materials count | NOT OBSERVED | No live runtime query. |
| Portals count | NOT OBSERVED | No live runtime query. |
| Paths count | NOT OBSERVED | No live runtime query. |
| Lights count | NOT OBSERVED | No live runtime query. |
| Simple Tiles count | NOT OBSERVED | No live runtime query. |
| Smart Tiles count | NOT OBSERVED | No live runtime query. |
| Smart Tiles Double count | NOT OBSERVED | No live runtime query. |
| Total live entries | NOT OBSERVED | No live runtime query. |
| Preview successes/failures | NOT OBSERVED | No live preview sampling. |
| Maximum observed preview bytes | NOT OBSERVED | No live preview sampling. |
| Runtime responsiveness | NOT OBSERVED | No fresh runtime session. |
| Parser output | NOT OBSERVED | No fresh live catalog artifacts. |
| Pre/post crash evidence | NOT OBSERVED | No disposable live runtime exercise. |

## Exact user action required

Save any wanted map changes and close Dungeondraft normally. Then rerun Task 6 from the committed pre-live state so the existing ownership-proven DDAI setup path can install only DDAI-owned files. DDAI must not launch Dungeondraft; the user must open/reload a disposable map normally before the fresh session, catalog, category, preview, responsiveness, parser, and crash evidence can be recorded.
