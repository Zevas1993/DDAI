# DDAI MCP Discovery and Inspection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Expose the accepted catalog, bounded image previews, generated-image import, runtime capabilities, and live map inspection through one client-independent MCP contract.

**Architecture:** Focused MCP tool classes delegate to services; tools contain annotations and content-shaping only. Read-only catalog calls never touch Dungeondraft, while inspection uses the existing atomic mailbox and a bounded read-only bridge command. Rich preview results return both structured metadata and a standard MCP ImageContentBlock.

**Tech Stack:** .NET 9, ModelContextProtocol 1.4.1, Microsoft.Extensions.Hosting 10.0.7, xUnit, atomic mailbox, Godot 3.5.3.

## Global Constraints

- This plan depends on the completed asset-catalog/generated-assets plan.
- Keep stdio stdout protocol-clean; diagnostics go to stderr.
- Tools are identical for Claude Desktop, ChatGPT Desktop, Gemini, Codex, and other compatible MCP clients.
- Search/preview/capabilities/inspection are read-only, non-destructive, and closed-world.
- Import is state-changing, non-destructive to the map, idempotent, and closed-world.
- Every result is bounded and omits original asset bytes, source paths, configuration content, and exception dumps.
- Preserve ModelContextProtocol 1.4.1 until a separately approved migration.
- Preserve unrelated dirty files.

---

## File responsibilities

- Create src/DDAI.App/Mcp/DdaiAssetTools.cs for search, preview, and import.
- Create src/DDAI.App/Mcp/DdaiCapabilityTools.cs for runtime feature discovery.
- Create src/DDAI.App/Mcp/DdaiMapTools.cs for bounded map inspection.
- Create src/DDAI.App/DdaiCapabilityService.cs and DdaiMapInspectionService.cs.
- Create src/DDAI.Core/Maps/MapSnapshotContracts.cs.
- Modify McpStdioServer.cs only for dependency and tool registration.
- Modify MailboxContracts.cs and ddai_bridge.gd for inspect_map.

### Task 1: Runtime capability contract

**Files:**
- Create: src/DDAI.App/DdaiCapabilityService.cs
- Create: src/DDAI.App/Mcp/DdaiCapabilityTools.cs
- Modify: src/DDAI.App/McpStdioServer.cs
- Create: tests/DDAI.App.Tests/Mcp/DdaiCapabilityToolTests.cs

**Interfaces:**
- GetCapabilities returns connector/mod/Dungeondraft versions, live state, map and catalog revisions, all categories, preview limits/formats, import limits, and one OperationCapability per operation with SchemaSupported, RuntimeCertified, and Reason.

- [ ] **Step 1: Write a failing metadata test**

~~~csharp
[Fact]
public void CapabilityTool_IsReadOnlyAndListsEveryCategory()
{
    var method = typeof(DdaiCapabilityTools).GetMethod(nameof(DdaiCapabilityTools.GetCapabilities));
    var attribute = method!.GetCustomAttribute<McpServerToolAttribute>()!;
    Assert.Equal("ddai_get_capabilities", attribute.Name);
    Assert.True(attribute.ReadOnly);
    Assert.False(attribute.Destructive);
}
~~~

- [ ] **Step 2: Run RED**

Run: dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --filter FullyQualifiedName~DdaiCapabilityToolTests

- [ ] **Step 3: Implement service and tool**

Register AssetCatalogRepository and DdaiCapabilityService in McpStdioServer. Chain WithTools<DdaiCapabilityTools>(). Use a single JsonSerializerOptions instance with snake_case. RuntimeCertified is false unless the live receipt's exact version and bridge capability list affirm the operation.

- [ ] **Step 4: Test closed/stale/live states, all fourteen categories, limits, and exact annotations**
- [ ] **Step 5: Run GREEN and commit**

~~~powershell
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --filter FullyQualifiedName~DdaiCapabilityToolTests
git add -- src/DDAI.App/DdaiCapabilityService.cs src/DDAI.App/Mcp/DdaiCapabilityTools.cs src/DDAI.App/McpStdioServer.cs tests/DDAI.App.Tests/Mcp/DdaiCapabilityToolTests.cs
git commit -m "feat: expose DDAI runtime capabilities"
~~~

### Task 2: Asset search MCP tool

**Files:**
- Create: src/DDAI.App/Mcp/DdaiAssetTools.cs
- Modify: src/DDAI.App/McpStdioServer.cs
- Create: tests/DDAI.App.Tests/Mcp/DdaiAssetToolTests.cs

**Interfaces:**
- SearchAssets accepts Query, Categories, Tags, PackIds, Generated, PreviewRequired, IncludeStagedGenerated, Limit, and Cursor.
- Returns snake_case AssetSearchResult from the prior plan.

- [ ] **Step 1: Write failing annotation, schema, and search-delegation tests**
- [ ] **Step 2: Run RED**

Run: dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --filter FullyQualifiedName~DdaiAssetToolTests

- [ ] **Step 3: Implement ddai_search_assets**

~~~csharp
[McpServerTool(
    Name = "ddai_search_assets",
    ReadOnly = true,
    Destructive = false,
    Idempotent = true,
    OpenWorld = false)]
public static CallToolResult SearchAssets(
    AssetSearchQuery query,
    AssetSearchService service) =>
    DdaiToolResults.FromSearch(service.Search(query));
~~~

Return catalog_unavailable as a tool-level CallToolResult error, not a JSON-RPC protocol error. Do not catch cancellation.

- [ ] **Step 4: Test every filter, stale metadata, pagination, invalid input, AllowThirdPartyUse warning preservation without filtering, and no source path leakage**
- [ ] **Step 5: Run GREEN and commit**

~~~powershell
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --filter FullyQualifiedName~DdaiAssetToolTests
git add -- src/DDAI.App/Mcp/DdaiAssetTools.cs src/DDAI.App/McpStdioServer.cs tests/DDAI.App.Tests/Mcp/DdaiAssetToolTests.cs
git commit -m "feat: expose asset search over MCP"
~~~

### Task 3: Rich bounded preview MCP result

**Files:**
- Modify: src/DDAI.App/Mcp/DdaiAssetTools.cs
- Modify: tests/DDAI.App.Tests/Mcp/DdaiAssetToolTests.cs
- Modify: tests/DDAI.App.Tests/McpPublishedIntegrationTests.cs

**Interfaces:**
- GetAssetPreview(assetRef) returns CallToolResult with StructuredContent, one TextContentBlock, and one ImageContentBlock when available.

- [ ] **Step 1: Write a failing content-block test**

~~~csharp
var result = DdaiAssetTools.GetAssetPreview(asset.AssetRef, repository);
Assert.False(result.IsError);
Assert.Single(result.Content.OfType<ImageContentBlock>());
Assert.Single(result.Content.OfType<TextContentBlock>());
Assert.NotNull(result.StructuredContent);
~~~

- [ ] **Step 2: Run RED**
- [ ] **Step 3: Implement lookup and ImageContentBlock.FromBytes**

Require the asset_ref to exist in the accepted snapshot and its preview hash to match the bounded file. On failure return IsError true with a stable code in structured content and a concise text block. Never return a file URI or resource path.

- [ ] **Step 4: Publish the executable and use the official SDK client to verify the image MIME, decoded dimensions, byte cap, structured metadata, and stdout cleanliness**
- [ ] **Step 5: Run focused GREEN and commit**

~~~powershell
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --filter "FullyQualifiedName~DdaiAssetToolTests|FullyQualifiedName~McpPublishedIntegrationTests"
git add -- src/DDAI.App/Mcp/DdaiAssetTools.cs tests/DDAI.App.Tests/Mcp/DdaiAssetToolTests.cs tests/DDAI.App.Tests/McpPublishedIntegrationTests.cs
git commit -m "feat: return bounded asset previews over MCP"
~~~

### Task 4: Generated-image import MCP tool

**Files:**
- Modify: src/DDAI.App/Mcp/DdaiAssetTools.cs
- Modify: src/DDAI.App/McpStdioServer.cs
- Modify: tests/DDAI.App.Tests/Mcp/DdaiAssetToolTests.cs
- Modify: tests/DDAI.App.Tests/McpPublishedIntegrationTests.cs

**Interfaces:**
- ImportAsset accepts GeneratedAssetImportRequest and returns GeneratedAssetImportResult.

- [ ] **Step 1: Write failing annotation, inline-base64, idempotency, and published-SDK tests**
- [ ] **Step 2: Run RED**
- [ ] **Step 3: Implement ddai_import_asset**

~~~csharp
[McpServerTool(
    Name = "ddai_import_asset",
    ReadOnly = false,
    Destructive = false,
    Idempotent = true,
    OpenWorld = false)]
public static Task<CallToolResult> ImportAssetAsync(
    GeneratedAssetImportRequest request,
    GeneratedAssetStore store,
    CancellationToken cancellationToken) =>
    DdaiToolResults.FromGeneratedImportAsync(store, request, cancellationToken);
~~~

Successful content contains structured metadata plus the generated preview image. Validation errors set IsError true. Cancellation leaves no idempotency receipt unless the import was durably completed.

- [ ] **Step 4: Verify an official SDK client imports a generated PNG and then finds it with include_staged_generated**
- [ ] **Step 5: Run GREEN and commit**

~~~powershell
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --filter "FullyQualifiedName~DdaiAssetToolTests|FullyQualifiedName~McpPublishedIntegrationTests"
git add -- src/DDAI.App/Mcp/DdaiAssetTools.cs src/DDAI.App/McpStdioServer.cs tests/DDAI.App.Tests/Mcp
git commit -m "feat: import AI generated assets over MCP"
~~~

### Task 5: Bounded live map inspection

**Files:**
- Create: src/DDAI.Core/Maps/MapSnapshotContracts.cs
- Modify: src/DDAI.Core/Mailbox/MailboxContracts.cs
- Create: src/DDAI.App/DdaiMapInspectionService.cs
- Create: src/DDAI.App/Mcp/DdaiMapTools.cs
- Modify: src/DDAI.App/McpStdioServer.cs
- Modify: mods/DDAI/scripts/ddai_bridge.gd
- Create: tests/DDAI.Core.Tests/Maps/MapSnapshotContractTests.cs
- Create: tests/DDAI.App.Tests/Mcp/DdaiMapInspectionToolTests.cs

**Interfaces:**
- Mailbox command inspect_map accepts Region, Level, Limit, and Cursor.
- MapSnapshotPage contains MapId, MapRevision, Canvas, GridSize, Levels, Items, NextCursor, and Truncated.
- The public MCP method is named ddai_inspect_map and serializes MapRevision as map_revision and the correlated catalog revision as catalog_revision.

- [ ] **Step 1: Write failing strict snapshot and tool tests**
- [ ] **Step 2: Run RED**
- [ ] **Step 3: Implement control-plane contracts and service**

Validate region coordinates, limit 1..500, cursor correlation, response command/request ID, 1 MiB cap, and exact map revision.

- [ ] **Step 4: Implement bridge inspect_map without mutation**

Walk only documented current-level containers and serialize native node ID, kind, bounds, level, and catalog asset_ref when resolvable. Stop at the requested limit, emit a deterministic cursor, and report unsupported container kinds instead of traversing private scene nodes. Add inspect_map to SUPPORTED_COMMANDS.

- [ ] **Step 5: Run exact GDScript parser, static listener scan, fake bridge round-trip, and official SDK test**
- [ ] **Step 6: Run GREEN and commit**

~~~powershell
dotnet test DDAI.slnx -c Release --filter "FullyQualifiedName~MapSnapshot|FullyQualifiedName~DdaiMapInspection"
git add -- src/DDAI.Core/Maps src/DDAI.Core/Mailbox/MailboxContracts.cs src/DDAI.App/DdaiMapInspectionService.cs src/DDAI.App/Mcp/DdaiMapTools.cs src/DDAI.App/McpStdioServer.cs mods/DDAI/scripts/ddai_bridge.gd tests
git commit -m "feat: inspect live Dungeondraft maps"
~~~

### Task 6: Enriched status and published MCP acceptance

**Files:**
- Modify: src/DDAI.App/DdaiStatusService.cs
- Modify: src/DDAI.App/McpStdioServer.cs
- Modify: tests/DDAI.App.Tests/McpPublishedIntegrationTests.cs
- Create: docs/superpowers/reports/2026-08-10-ddai-mcp-discovery-report.md

- [ ] **Step 1: Write a failing status test for map/catalog revision, cache age, completeness, and bridge capability list**
- [ ] **Step 2: Implement enrichment without changing mailbox status semantics**
- [ ] **Step 3: Publish one-file win-x64 and use ModelContextProtocol 1.4.1 client to initialize, list every tool, call status/capabilities/search/preview/import/inspect, and assert zero stdout contamination**
- [ ] **Step 4: Run full Release tests, build, package-vulnerability audit, PowerShell AST, exact Godot parser, listener scan, and git diff --check**
- [ ] **Step 5: Record exact artifact hash, size, tool schemas, content blocks, limits, and remaining live gaps**
- [ ] **Step 6: Commit**

~~~powershell
git add -- src/DDAI.App tests/DDAI.App.Tests/McpPublishedIntegrationTests.cs docs/superpowers/reports/2026-08-10-ddai-mcp-discovery-report.md
git commit -m "feat: complete MCP asset discovery surface"
~~~
