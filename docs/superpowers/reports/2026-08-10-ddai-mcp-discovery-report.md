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

Unavailable catalog fields remain `null` and catalog errors remain an empty list. Malformed optional enrichment fields are not promoted into synthetic capabilities and do not change the underlying mailbox success/error result. The bridge response is bounded by the 1 MiB atomic-mailbox contract, and the accepted manifest/errors are bounded by the 1 MiB catalog JSON contract.

TDD receipt:

- RED: `dotnet test tests\DDAI.App.Tests\DDAI.App.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Status_EnrichesTheUnchangedBridgeResponseWithBoundedDiscoveryState`
  - failed 1/1 with `KeyNotFoundException` at the first missing enriched status field;
- GREEN: the same command passed 1/1;
- wider status regression: `FullyQualifiedName~CliAndStatusTests|FullyQualifiedName~Status_EnrichesTheUnchangedBridgeResponseWithBoundedDiscoveryState` passed 13/13.

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

Schema assertions covered all top-level parameters, the nine search fields (`query`, `categories`, `tags`, `packIds`, `generated`, `previewRequired`, `includeStagedGenerated`, `limit`, `cursor`), the four inspection fields (`region`, `level`, `limit`, `cursor`), the preview reference, and all eight import fields already covered by the dedicated published import test.

The acceptance used an isolated real `AtomicMailbox`, a fake bridge with exact `status`/`apply_plan`/`inspect_map` behavior, a cryptographically validated accepted catalog, and the real generated-asset store. Observed result shapes were:

- status and capabilities: one JSON text block;
- search and inspect: one JSON text block plus matching `structuredContent`;
- preview: matching JSON text/structured content plus one exact `image/png` block;
- import: matching JSON text/structured content plus one bounded `image/png` block;
- validate/apply: one JSON text block each;
- expected failures: `isError: true` with matching JSON text/structured content and stable `invalid_request` or `asset_not_found` codes.

The same client imported `Cross Tool Lantern`, then searched with `generated: true` and `includeStagedGenerated: true`; the returned staged `asset_ref` matched `sha256:<generated_asset_id>`, `generated` was true, and `placeable` was false. A deliberately unanswered inspection was cancelled through the official client and surfaced as `OperationCanceledException`. Search limit 101 and inspection limit 501 were rejected as `invalid_request`. A missing preview reference was shaped as `asset_not_found`.

The direct stdout capture test parsed every non-empty stdout line as JSON-RPC 2.0 and found no contamination. The complete `McpPublishedIntegrationTests` slice passed 6/6.

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
- Size: 86,871,226 bytes
- SHA-256: `32e99cce9fc07b6e70cfa4555e02fdf2a1d206fb0c5cceaf972aec4843a1c67e`

The artifact directory is machine-local/ignored evidence and is not part of the source commit.

## Verification receipts

- `dotnet test DDAI.slnx -c Release --no-restore`: 325/325 Core and 307/307 App tests passed; 632/632 total.
- `dotnet build DDAI.slnx -c Release --no-restore`: succeeded with 0 warnings and 0 errors.
- `dotnet list DDAI.slnx package --vulnerable --include-transitive`: no vulnerable packages for `DDAI.App`, `DDAI.Core`, both test projects, or `DDAI.McpProbe` using the configured NuGet sources.
- PowerShell AST for `tools/Install-DDAIMod.ps1`: 0 parse errors, 1,462 tokens.
- Pinned Godot 3.5.3 exact bridge parser, exact bounded inspection behavior, and package listener test: 3/3 passed with empty parser/behavior stderr.
- Durable network-listener scan across `src`, `mods`, and `tools`: 0 listener hits.
- `git diff --check`: clean before report creation; rerun immediately before commit.
- GitNexus pre-change impact: `DdaiStatusService` LOW (2 direct callers, 1 affected `RunAsync` flow); `McpStdioServer` LOW (0 upstream dependents). The final staged-only detector reported HIGH across 46 changed symbols and 12 affected processes because it counted the expanded integration harness and the status/host registrations. Its four files were exactly the Task 6 scope, and its affected processes were confined to expected `GetStatusAsync` mailbox paths and `McpStdioServer.RunAsync` registration paths.

## Remaining live gaps

- The acceptance is published-process proof against a fake bridge/catalog/store, not a live Dungeondraft 1.2.0.1 session or a desktop-client-specific adapter.
- The packaged bridge currently returns `revision: null` from `status`; therefore live `map_revision` remains null until the bridge advertises one. `ddai_inspect_map` remains the current source of the exact SHA-256 map revision.
- `ddai_get_capabilities` still reports map revision as null and runtime-certifies only its existing schema-operation set. Status separately exposes the bridge's exact `status`, `apply_plan`, and `inspect_map` advertisement.
- The fixture catalog proves the accepted-cache contract with one object and zero counts for the other categories; it is not evidence of a fresh, complete live user catalog.
- Generated imports remain staged and non-placeable until a normal Dungeondraft asset reload activates them in an accepted live catalog.
- No artifact was installed, no Dungeondraft process was touched, and no real-client acceptance was claimed.
