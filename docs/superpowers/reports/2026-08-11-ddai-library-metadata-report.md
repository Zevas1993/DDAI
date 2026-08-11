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

## Live acceptance and follow-up fixes — 2026-08-11

Live certification was completed on a disposable blank 40 by 30 map. The first instrumented run proved the catalog was not merely slow: it failed after loading 538 documented Library keys and processing 801 key-to-texture associations because one real Library search key could not be represented by the connector's canonical public metadata format. The corrected producer omits only that unusable synonym, emits the bounded `library_metadata_term_ignored` diagnostic without the term or resource path, and retains fail-closed behavior for malformed collections, textures, tag indexes, and wire records.

The existing catalog tool now publishes an eight-slot, 4,096-byte maximum private progress receipt. It contains only schema/session, phase, bounded counters, and bounded diagnostics; exact Godot 3.5.3 coverage proves that Library terms, `res://` identities, and filenames are absent. The production reader accepts this receipt as liveness evidence only when it is strict-schema, `published`, at most 30 seconds old, exact-session matched to the accepted manifest, and exact-entry-count matched. Malformed slots are isolated, while mismatched, stale, failed, future, or unbounded receipts cannot promote a cached snapshot.

The same exact-session rule now renews the one-time runtime-start receipt from the bridge's existing rotating heartbeat slots. This prevents `ddai_get_capabilities` from reporting a live, responsive Dungeondraft process as stale merely because it has been open longer than 30 seconds.

Independent review then exposed four strictness gaps. The repaired producer validates an unsafe term's collection, every texture, and resource identity incrementally before omitting only term insertion. The reader now requires all 20 unique receipt fields, rejects duplicates and missing fields, and enforces terminal counter/error correlations. Catalog liveness stores the producer timestamp rather than a latched boolean, so it expires dynamically. Runtime startup and heartbeat receipts now use the existing handle-validated, reparse-resistant local filesystem reader, with heartbeat files capped at 4,096 bytes. A final live RED also exposed the producer retaining its last raw-enumeration index after clearing the array; the producer now resets that terminal index to zero and the exact Godot fixture locks the invariant.

Live evidence:

- Dungeondraft PID `55444` remained responsive with the disposable `Tabula Rasa` map open.
- Catalog session: `1786484372-209555`.
- Catalog revision: `1786484714855`.
- Producer totals: 538 Library keys, 6,094 validated associations, 14/14 categories, 1,947/1,947 entries, one safely ignored synonym, no other error. The four-association increase proves the ignored term's textures were validated rather than bypassed.
- The production repository accepted an isolated copy and opened 1,947/1,947 hash-bound previews; the live `current.json` hash remained unchanged during that verification.
- Installed MCP acceptance negotiated protocol `2025-11-25`, listed all eight canonical tools, reported `runtime_state: live` and `catalog_state: live`, certified status/apply, returned a live `Objects` search result (`log 01`), and returned its 44,665-byte `image/png` preview.
- Live `ddai_inspect_map` returned a stable map ID and SHA-256 map revision, canvas 40 by 30, one level, zero items, no cursor, and no mutation.
- Final self-contained executable: 86,890,170 bytes; SHA-256 `AAFB1AF2150126EB07E44FBF3B621F78E6A3E5AAA44BACF9EA59D318CA3F1A6C`. The installed executable hash matched exactly.
- Final source/installed `ddai_asset_catalog.gd` SHA-256: `6170ED52CC6E7671BFF95A89CA16DF8616B185C6E91541F03B0FA51A102E5B3D`.
- Running-app setup correctly returned exit 2 / `activation_pending_dungeondraft_running`, changed neither Claude nor Gemini configuration, and left Dungeondraft responsive; the owned executable update was installed and hash verified.
- Final serialized Release tests: App 322/322 plus Core 329/329, 651/651 total. Release build: zero warnings and zero errors. Changed-file formatting, diff checks, listener scan, and vulnerable-package audit were clean.
- Post-hardening installed MCP acceptance negotiated protocol `2025-11-25`, listed the exact eight tools, reported runtime/catalog/status live, searched `Objects` for `log 01`, decoded its 44,665-byte PNG preview, and inspected the responsive blank 40 by 30 map. The MCP server exited cleanly with stdout containing only JSON-RPC frames.

Remaining product work is the separately planned universal map-plan executor coverage. Status still intentionally leaves undocumented version/active-mod/current-level fields unavailable; live map identity, revision, canvas, levels, and items are supplied by `ddai_inspect_map` instead.
