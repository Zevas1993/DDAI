# DDAI Library metadata and Used-parity report

Recorded: 2026-08-11

## Outcome and boundary

Task 7 is implemented and verified at the automated, pinned-Godot, no-MCP-headless, repository, and MCP layers. This report does **not** claim post-change live Dungeondraft acceptance. Dungeondraft PID 62504 was not queried, automated, reloaded, installed into, closed, or otherwise mutated.

The connector now indexes the documented Object Library standard-search data into safe per-asset `search_terms`, and correlates documented placed `Prop` objects to opaque catalog `asset_ref` values for read-only Used parity. It never selects Library content, changes tag state, invokes `ShowUsedObjects`, or mutates the map.

## Public API route and tag limitation

Production reads only `Global.Editor.ObjectLibraryPanel.searchEngine` at top-level tool scope. Godot 3.5.3 exposes bounded native `Dictionary.keys()` snapshots but no resumable dictionary iterator, so production caps each snapshot at 8,192 keys, then validates keys and incrementally inverts the documented `Dictionary[String, Array[Texture]]` at no more than eight key/texture operations per update. The resource identity is used only internally and is never serialized into public catalog data or errors.

No documented public `Global.Editor.Tools["ObjectTool"].Controls` key for a `TagsPanel` could be proved. Production therefore does not probe controls, traverse the UI, use the private `TagsPanel.ObjectTool` reference, or guess tag membership. Live loaded-asset `tags` remain empty. The pinned harness proves the exact intersection algorithm with an explicitly supplied documented `tagIndexLookup`, but that test seam is not represented as a discovered production route.

Documented `AssetPack.Keywords` remain discoverable as canonical pack-level `search_terms`; they are never relabeled as exact per-asset tags. Existing scalar delimiter behavior is preserved. The exact fixture proves `Religious; Ceremonial` yields `religious` and `ceremonial` search terms while the only exact tag remains the separately proved `ancient` membership.

## Bounds and failure behavior

The producer and reader enforce the same 64-search-term, 64-tag, and 128-Unicode-scalar per-value limits. Values must be well-formed, lowercase canonical, sorted, unique after canonicalization, space-collapsed, free of controlled/format/non-ASCII-whitespace scalars (including UTF-16 surrogate code points), and non-path-like. One checked-in JSON hostile-value corpus drives both the pinned-Godot producer and .NET reader expectations; a programmatic pinned-Godot fixture covers the unpaired-surrogate case JSON cannot faithfully carry. The producer also bounds metadata keys, textures per key, Library terms to 54 and pack keywords to 8 per asset so display/pack/keyword discovery fits the strict 64-term wire budget, total serialized sizes, and work to eight counted metadata validation/indexing operations per update. Over-bound or unsafe keyword sources fail closed instead of publishing a silently reduced replacement.

Malformed or unavailable Object Library index surfaces fail closed with the stable `library_metadata_invalid` code; malformed pack keyword metadata uses `catalog_metadata_invalid`. Neither path publishes a silently reduced replacement. Default assets may retain null pack metadata. Custom-pack ownership uses longest, segment-boundary-safe matching against documented `Global.Header.AssetManifest` paths.

## Read-only object correlation

The bridge inspects only documented `Level.Objects` children and documented `Prop.Rect`, `GetNodeID()`, and `Prop.Sprite.texture.resource_path`. It hashes the texture resource identity in memory and includes only that SHA-256 as an internal bridge correlation field. The revision hash includes the fingerprint so object texture changes change inspection state.

`DdaiMapInspectionService` resolves the fingerprint against exactly the accepted catalog revision and the exact `Objects` category. One Object match returns its opaque `asset_ref`; zero or multiple Object matches, or an omitted object fingerprint, return null plus the closed `object_asset_correlation` diagnostic. A same-fingerprint non-Object entry cannot make an Object ambiguous. The service clears the fingerprint in every correlated public result. MCP map-inspection and asset-search output tests reject both `resource_fingerprint` and raw resource paths.

## TDD receipts

Genuine exact-production catalog RED initially reported:

```text
DDAI_LIBRARY_METADATA_INDEX:False
DDAI_LIBRARY_METADATA_HOSTILE_FAILS_CLOSED:True
```

After the metadata implementation, the pinned Godot 3.5.3 fixture passed realistic texture dictionaries, duplicate/case/spacing values, exact tag intersection, hostile keys, null/wrong types, deterministic sorting/deduplication, per-update bounds, top-level tool-scope wiring, missing-panel failure behavior, and public path nondisclosure.

A later pack-keyword RED again isolated `DDAI_LIBRARY_METADATA_INDEX:False`; the minimum producer change made the same receipt true while preserving `tags` as exact-membership-only.

Genuine object-correlation RED reported `DDAI_INSPECTION_OBJECT_CORRELATION:False` while the existing inspection receipts remained true. GREEN covers documented object enumeration, deterministic bounds/order/revision, unique correlation, null-on-ambiguous/missing correlation, and path/fingerprint nondisclosure.

.NET RED first failed because the internal fingerprint field and unique catalog correlation did not exist. Later adversarial REDs proved a primitive item leaked `InvalidOperationException`, absent object fingerprints lacked the correlation diagnostic, cross-category fingerprints created false ambiguity, and asset search exposed the internal fingerprint. A final pinned-Godot RED proved U+D800 was accepted by the producer while the .NET reader rejected it. GREEN contract and MCP tests cover bounded `JsonException`, the internal-only field, unique/ambiguous/missing/absent outcomes, Object-category correlation, catalog-revision binding, sanitized public serialization across both inspection and search, and shared surrogate rejection.

## Executable evidence

- Focused catalog/map contract and exact-Godot Core set: 39/39 passed; focused repository/asset/map MCP App set: 56/56 passed.
- Exact parser, listener scan, catalog behavior, and map-inspection behavior replay: 4/4 passed under pinned Godot 3.5.3 with empty fixture stderr.
- Pack-keyword, missing-fingerprint, Object-category, and no-fingerprint-leak regressions are included in those focused totals.
- Pack keyword query `ceremonial` finds the fixture asset; exact tag filter `ceremonial` returns no items.
- Isolated transformed copy of the accepted real catalog passed the production repository test at exactly 1,947 entries and 1,947/1,947 preview opens. This is real-catalog reader/preview-preservation evidence, not the production-GDScript headless gate.
- Final exact-production no-MCP headless root: `D:\dt7-e7154ea9`. Godot 3.5.3 exited 0 with zero stderr bytes, published 166 entries, retained 127 bounded internal errors, and returned true for bounded work, null-image preservation, both receipt recoveries, timeout preservation, helper receipt, conflicting-response recovery, and public commit-binding rejection.
- The real production repository accepted that exact output: `entries=166`, `errors=128` (127 ordinary plus `errors_truncated` summary), `chunks=1`, fingerprint `24c3a7053a91b8d6d3dbccea1f2191411f13aa31654b37e5b76747e084423be5`, and `4/4` advertised entry previews opened from one content-addressed preview file. An isolated collision copy was split into receipt-bound chunks before a cross-chunk duplicate was rejected.
- Fresh serialized Release tests: App 315/315 and Core 329/329, 644/644 total.
- Release build: zero warnings and zero errors.
- NuGet vulnerability audit: no vulnerable direct or transitive packages. The separate deprecation query reports only the repository's existing xUnit 2.9.2 legacy notice.
- Project-level `dotnet format --verify-no-changes` passed for Core, App, Core.Tests, and App.Tests. The installed formatter does not accept the repository's `.slnx` workspace directly.
- `git diff --check` reported no whitespace errors; Windows line-ending notices are the repository's existing normalization behavior.

## Remaining live acceptance

Only after independent review is clean may the controller request the normal user-close/install/reopen sequence. Required live proof remains a new session, catalog revision and fingerprint; exact category/entry/chunk/error totals; counts with multiple search terms and exact tags; representative opaque non-filename queries; all preview opens; placed-object correlation; responsiveness; and no crash. Until then, this change is automated/headless evidence only.
