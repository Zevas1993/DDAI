# DDAI asset catalog generated-activation live report

Recorded: 2026-08-11T08:33:41.4749534-04:00

## Outcome

`PASS`

The ownership-proven DDAI setup installed only the reviewed DDAI mod into the configured per-user mods directory. Claude Desktop and Gemini configuration entries were already current and were not rewritten. The final installed asset-catalog script matched the reviewed source byte-for-byte:

```text
source SHA-256:    0D0E28BAD4A500E6C5347BFCF0697FE185CF25A0D7BFAFD626357A436D8E9172
installed SHA-256: 0D0E28BAD4A500E6C5347BFCF0697FE185CF25A0D7BFAFD626357A436D8E9172
setup result: repaired / setup_complete
```

Dungeondraft 1.2.0.1 was launched normally and a disposable 40x30 blank map was opened. PID 62504 remained responsive with title `Tabula Rasa - Dungeondraft`. The application remains open on that disposable map.

## Final accepted live snapshot

The production `AssetCatalogRepository`, loaded from the Release build, accepted the public catalog and opened every advertised preview.

```text
session_id: 1786451473-8708
catalog_revision: 1786451604288
catalog_fingerprint: a20e1b3bbcca292652b280377d5dd0e9fea8711d58c6a3e23e70cac2d037f586
snapshot_at: 2026-08-11T12:33:23.0000000+00:00
immutable manifest committed: 2026-08-11T08:33:24.3720186-04:00
production validation: 2026-08-11T08:33:41.4749534-04:00
complete: true
live: true
age at validation: 18.482 seconds
chunks: 1
errors: 0
total entries: 1,947
preview hashes: 1,947
preview opens: 1,947 succeeded / 0 failed
preview bytes opened: 37,854,187
maximum preview bytes: 214,721
process responding: true
```

All fourteen required category counts were observed:

| Category | Count |
|---|---:|
| Terrain | 13 |
| Patterns | 0 |
| Patterns Colorable | 0 |
| Caves | 2 |
| Roofs | 8 |
| Objects | 1,792 |
| Walls | 10 |
| Materials | 12 |
| Portals | 39 |
| Paths | 44 |
| Lights | 3 |
| Simple Tiles | 22 |
| Smart Tiles | 1 |
| Smart Tiles Double | 1 |

## Live defect discovery and correction

The first reviewed tool-scope build produced session `1786450509-62130` with the same 1,947 entries, zero errors, and 1,947/1,947 preview opens. That run exposed a freshness defect: `snapshot_at` was captured before approximately 109 seconds of enumeration and preview construction, so the repository's 45-second freshness policy marked the newly committed catalog stale.

Commit `ad731e5` moved timestamp acquisition to the first manifest finalization and made it one-shot across candidate and commit retries. Independent review found no Critical or Important issue. The final live snapshot above was stamped approximately 1.37 seconds before its immutable manifest commit and was `Live=true` when the production reader validated it.

The underlying live enumeration defect was also resolved without a filesystem fallback. Diagnostic session `1786449576-7845` proved all nested adapter calls returned Godot type code 0 (`nil`). Commit `17bc449` now routes `Script.GetAssetList` through a function reference owned by the top-level Dungeondraft tool script. Independent review found no Critical or Important issue.

## Parser, automated, and stability evidence

- Exact Godot 3.5.3 parser: both packaged scripts exited 0 with empty stderr.
- Exact production Godot regression: 1/1 passed, including typed arrays, bounded wrong-type diagnostics, top-level tool-scope wiring, and one-shot finalization timestamp semantics.
- Covering package/static tests: 20/20 passed.
- Full Release suite: 582/582 passed.
- Release build: 0 warnings and 0 errors.
- Isolated headless publication/recovery harness passed all state, binding, timeout, and recovery receipts.
- Real wire validator accepted its 166-entry fixture and rejected the duplicate collision.

No deliberate process crash was induced. That would be destructive and was not required to validate the disposable-map workflow. No crash occurred during either post-fix live cycle. PID 65588 closed normally after the first 1,947-entry acceptance; PID 62504 then completed the final 1,947-entry acceptance and remained responsive. Automated crash-boundary and recovery coverage remains green.

## Remaining scope

This report certifies live loaded-asset discovery, bounded previews, and staged-generated activation plumbing. The later MCP discovery, universal plan, all-category executor, and multi-client acceptance plans remain separate work; this report does not claim those later connector layers are complete.
