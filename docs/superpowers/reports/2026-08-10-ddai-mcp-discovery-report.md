# DDAI MCP discovery and inspection acceptance report

Date: 2026-08-11 EDT

Branch: `feature/bootstrap-core`

Starting commit: `e5d0634ee4a549e608451f4e07261909d08faacb`

## Outcome

Task 6 closes the offline MCP discovery plan with an enriched status result and a published, self-contained Windows acceptance run through the official `ModelContextProtocol` 1.4.1 client. The status service keeps the atomic-mailbox `success`, `command`, `payload`, and `error` values unchanged and adds bounded discovery metadata read from the same bridge response and the accepted catalog repository.

No installation, reload, close, or other interaction with Dungeondraft PID 62504 occurred. The published artifact remains review-only; live installation follows review.

## Status contract

The snake-case MCP/CLI status document adds:

- `map_revision`: the exact string advertised by bridge payload `map_revision` or legacy `revision`, otherwise `null`;
- `catalog_revision` and `catalog_cache_age_milliseconds`;
- `catalog_live`, `catalog_complete`, and `catalog_entry_count`;
- `catalog_errors`, preserving the accepted manifest's code, message, and category records; and
- `bridge_capabilities`, preserving the exact ordered `supported_commands` list returned by the bridge.

Unavailable catalog fields remain `null` and catalog errors remain an empty list. Malformed optional enrichment fields are not promoted into synthetic capabilities and do not change the underlying mailbox success/error result. Catalog age and liveness are now derived from one clock sample; age milliseconds use a ceiling so the document cannot report `45000` with `catalog_live: false`. The bridge response is bounded by the 1 MiB atomic-mailbox contract, and the accepted manifest/errors are bounded by the 1 MiB catalog JSON contract.

TDD receipt:

- RED: `dotnet test tests\DDAI.App.Tests\DDAI.App.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Status_EnrichesTheUnchangedBridgeResponseWithBoundedDiscoveryState`
  - failed 1/1 with `KeyNotFoundException` at the first missing enriched status field;
- GREEN: the same command passed 1/1;
- round-one RED: the focused one-clock/absent/malformed/failed-response command failed 1/3 because the old double sample returned `catalog_live: false` at the exact 45-second age;
- round-one GREEN: the enriched, one-clock, absent/malformed, and failed-response status slice passed 4/4. The added cases preserve the exact failed mailbox `success`, `command`, cloned `payload`, and structured `error` while keeping malformed optional fields unpromoted.

## Published MCP acceptance

`McpClient.CreateAsync` initialized the one-file server over stdio, and `ListToolsAsync` returned exactly these eight tools:

| Tool | Top-level input | read-only | destructive | idempotent | open-world |
|---|---|---:|---:|---:|---:|
| `ddai_status` | none | true | false | false | false |
| `ddai_get_capabilities` | none | true | false | true | false |
| `ddai_search_assets` | `query` | true | false | true | false |
| `ddai_get_asset_preview` | `assetRef` | true | false | true | false |
| `ddai_import_asset` | `request` | false | false | true | false |
| `ddai_inspect_map` | `query` | true | false | true | false |
| `ddai_validate_plan` | `plan` | true | false | true | false |
| `ddai_apply_plan` | `plan` | false | true | true | false |

The committed [acceptance receipt](./2026-08-10-ddai-mcp-acceptance-receipt.json) is the canonical full-schema snapshot. The official client deep-compares every complete `inputSchema`, not selected names. It therefore locks object/array/scalar/null types, every required array, the complete universal-plan operation union and nested canvas/room/region/import/search shapes, list item types, search/inspection defaults, and the nine-tool public surface emitted by ModelContextProtocol 1.4.1. The receipt is 60,265 bytes with SHA-256 `370b9905ccdba4fc9bdc8606811627cd650b438b62d155fb13004977289c4278`.

The exact schema shapes are:

| Tool | Required input and nested schema constraints |
|---|---|
| `ddai_status` | object with no properties |
| `ddai_get_capabilities` | object with no properties |
| `ddai_get_asset_preview` | required string `assetRef` |
| `ddai_search_assets` | required object `query`; nine typed nullable/defaulted fields; string items for categories, pack IDs, and tags |
| `ddai_import_asset` | required object `request`; all eight generated fields in its required array; string-array tags; nullable string content/inbox alternatives |
| `ddai_inspect_map` | required object `query`; nullable region with required numeric `x`, `y`, `width`, `height`; nullable level/cursor and integer limit default 100 |
| `ddai_validate_plan` | required object `plan`; required schema/request/base/mode/canvas; required integer canvas dimensions; typed room array with five required item fields |
| `ddai_apply_plan` | same complete plan schema as validate |

Runtime bounds such as limit 100/500 are enforced and acceptance-tested below, but the generated schemas do not encode numeric `minimum`/`maximum` keywords. This report does not claim otherwise.

The acceptance used an isolated real `AtomicMailbox`, a fake bridge with exact `status`/`apply_plan`/`inspect_map` behavior, a cryptographically validated accepted catalog, and the real generated-asset store. Observed result shapes were:

- status and capabilities: one JSON text block;
- search and inspect: one JSON text block plus matching `structuredContent`;
- preview: matching JSON text/structured content plus one exact `image/png` block;
- import: matching JSON text/structured content plus one bounded `image/png` block;
- validate/apply: one JSON text block each;
- expected failures: `isError: true` with matching JSON text/structured content and stable `invalid_request` or `asset_not_found` codes.

The same client imported `Cross Tool Lantern`, then searched with `generated: true` and `includeStagedGenerated: true`; the returned staged `asset_ref` matched `sha256:<generated_asset_id>`, `generated` was true, and `placeable` was false. Search limit 101 and inspection limit 501 were rejected as `invalid_request`. A missing preview reference was shaped as `asset_not_found`.

Cancellation is no longer inferred from a local SDK token. The official 1.4.1 client sends a low-level `tools/call` with request ID 200, waits until the real mailbox peer has claimed the correlated processing file, then sends `notifications/cancelled` through `SendNotificationAsync`. Server stderr records the resulting `OperationCanceledException` before the local wait token is cancelled. The test verifies no mailbox response existed at cancellation, calls status successfully, deliberately publishes the late bridge response, verifies the processing-to-response transition, and calls capabilities successfully afterward.

The independent raw harness initializes, lists, and invokes all eight tools, including import-then-search, image, inspection, status, capability, mutation, validation, and shaped-error paths. It also sends request ID 20 plus `notifications/cancelled`, publishes a late mailbox response, and proves stdout never contains ID 20. Every nonempty stdout line/frame—initialization, list, eight tools, the second search, error, and post-cancellation status—is parsed as a JSON-RPC 2.0 response. Expected cancellation diagnostics remain on stderr only. The complete `McpPublishedIntegrationTests` slice passed 10/10, including separate source-publish immutability coverage.

Acceptance RED/GREEN history:

- retained artifact/schema RED: the published preview test failed 1/1 on the intentionally missing committed receipt; GREEN passed 1/1 after publishing and executing the exact retained path and matching the receipt;
- raw breadth RED: the exact tool-set assertion failed 1/1 with only `ddai_get_asset_preview` observed; GREEN passed 1/1 with all eight tools and all stdout frames parsed;
- cancellation RED: merely cancelling the high-level SDK call left the server diagnostic queue empty even after the bridge had claimed the request; GREEN passed 1/1 after explicit official-SDK `notifications/cancelled` tied to request ID 200;
- round-two artifact RED: the reviewer's unchanged focused command passed the three status tests and failed raw plus official SDK before launch because a test-triggered fresh publish had SHA-256 `8a0924ab...a4a87` while the receipt still named `7e666846...80945`;
- round-two GREEN: retained resolution became read-only, a single explicit final publish established the receipted bytes, the separate temp source-publish test preserved retained hash and timestamp, and both raw and SDK paths launched the same retained executable without modifying it;
- complete focused published acceptance: 10/10 passed.

## Limits verified or advertised

| Surface | Limit |
|---|---:|
| Atomic mailbox message | 1,048,576 bytes |
| Catalog JSON document | 1,048,576 bytes |
| Search page | 100 results |
| Search normalized query | 256 Unicode scalars |
| Search tags | 16 |
| Map inspection page | 500 items |
| Map inspection JSON | 1,048,576 bytes |
| Asset preview | 262,144 bytes, 256 px maximum edge |
| Generated import decoded image | 8,388,608 bytes |
| Generated import dimensions | 4,096 px maximum edge, 16,777,216 pixels |
| Generated import preview | 262,144 bytes |

## Artifact receipt

- Path: `artifacts/task6/win-x64/ddai.exe`
- Files in publish directory: 1
- Runtime: self-contained `win-x64`
- Size: 87,047,866 bytes
- SHA-256: `3a1b6dc48f83699f16a6bfaff80f105d0fd034b5713b9ed26bbf3945f7e3ebf2`

One explicit `dotnet publish` invocation generated this retained executable after the final source state. Published-process acceptance never publishes or writes that directory: it resolves the existing path and rechecks path, one-file contents, size, and hash against the committed JSON receipt and this report before launch. A separate source-freshness test publishes current source only to a temporary directory and proves the retained artifact's hash and last-write time remain unchanged; no hash equality between separate publishes is assumed. The artifact directory is machine-local/ignored evidence and is not part of the source commit.

## Verification receipts

- Reviewer-exact round-two focus: the unchanged five-test command passed 5/5; external before/after checks proved retained SHA-256 and last-write time unchanged.
- `dotnet test tests\DDAI.App.Tests\DDAI.App.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~McpPublishedIntegrationTests`: passed 10/10; retained SHA-256 and last-write time remained unchanged.
- Full Release tests run sequentially to avoid unrelated cross-project filesystem contention: App passed 311/311 and Core passed 325/325, 636/636 total. Two parallel `dotnet test DDAI.slnx` attempts observed four different untouched filesystem/timing tests fail; every exact failure passed immediately in isolation. Both parallel attempts also proved the retained artifact unchanged.
- `dotnet build DDAI.slnx -c Release --no-restore`: succeeded with 0 warnings and 0 errors.
- `dotnet list DDAI.slnx package --vulnerable --include-transitive`: no vulnerable packages for `DDAI.App`, `DDAI.Core`, both test projects, or `DDAI.McpProbe` using the configured NuGet sources.
- `dotnet format src\DDAI.App\DDAI.App.csproj --verify-no-changes --no-restore` and the same scoped to `tests\DDAI.App.Tests\McpPublishedIntegrationTests.cs`: passed. `dotnet format` 9.0.201 cannot parse `DDAI.slnx`; unscoped test-project verification also reports a pre-existing whitespace issue in untouched `AssetCatalogRepositoryTests.cs:537`.
- PowerShell AST for `tools/Install-DDAIMod.ps1`: 0 parse errors, 1,462 tokens.
- Pinned Godot 3.5.3 exact bridge parser, exact bounded inspection behavior, and package listener test: 3/3 passed with empty parser/behavior stderr.
- Durable network-listener scan across `src`, `mods`, and `tools`: 0 listener hits.
- `git diff --check`: rerun immediately before the scoped commit.
- GitNexus round-one pre-change impact: `DdaiStatusService` LOW (2 direct callers, 1 affected `RunAsync` flow); `McpPublishedIntegrationTests` and `McpStdioServer` LOW (0 upstream dependents). Round-two pre-change impact for `McpPublishedIntegrationTests` was LOW with 0 upstream dependents. Final round-two staged-only detection reported LOW across 7 changed symbols, 0 affected execution flows, and exactly the three scoped Task 6 files.

## Remaining live gaps

- The acceptance is published-process proof against a fake bridge/catalog/store, not a live Dungeondraft 1.2.0.1 session or a desktop-client-specific adapter.
- The packaged bridge currently returns `revision: null` from `status`; therefore live `map_revision` remains null until the bridge advertises one. `ddai_inspect_map` remains the current source of the exact SHA-256 map revision.
- `ddai_get_capabilities` still reports map revision as null and runtime-certifies only its existing schema-operation set. Status separately exposes the bridge's exact `status`, `apply_plan`, and `inspect_map` advertisement.
- The fixture catalog proves the accepted-cache contract with one object and zero counts for the other categories; it is not evidence of a fresh, complete live user catalog.
- Generated imports remain staged and non-placeable until a normal Dungeondraft asset reload activates them in an accepted live catalog.
- No artifact was installed, no Dungeondraft process was touched, and no real-client acceptance was claimed.
