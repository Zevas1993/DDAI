# DDAI Universal Plan Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Replace the rectangular-room-only public plan with a backward-compatible, revision-locked operation plan and crash-safe job engine that can host every Dungeondraft asset category.

**Architecture:** Version 2.0 adds a typed operations array while the parser continues accepting version 1.0 rectangular rooms. A pure .NET reference state machine defines durable transitions and recovery; the GDScript bridge mirrors it. The bridge advertises only exact-version certified executors and revalidates map/catalog revisions and asset fingerprints immediately before mutation.

**Tech Stack:** .NET 9, System.Text.Json polymorphism, xUnit, existing atomic mailbox, Godot 3.5.3, Dungeondraft 1.2.0.1.

## Global Constraints

- Depends on the catalog/discovery plans.
- Existing version 1.0 room calls remain byte-compatible and idempotent.
- Version 2.0 requires expected_map_id, base_revision, expected_catalog_revision, mode, canvas, and at least one operation.
- Maximum 500 operations, 10,000 geometry points, and 1 MiB canonical mailbox payload.
- No mutation uses cursor position, retained interactive state, private scene traversal, or silent fallback assets.
- Validate the whole plan before the durable prepared boundary.
- Identical request ID plus canonical plan returns the existing result; conflicting content fails.
- Unknown outcome is never replayed under a new request ID.
- Preserve unrelated dirty files.

---

## File responsibilities

- Create src/DDAI.Core/MapPlans/Operations/MapOperation.cs and one record file per operation family.
- Create UniversalMapPlanValidation.cs for ordered cross-operation validation.
- Extend MapPlanJson.cs with deterministic version 2.0 canonicalization/fingerprinting.
- Create UniversalPlanMigration.cs for version 1.0 room conversion.
- Create MapJobContracts.cs and UniversalMapJobStateMachine.cs for durable transitions.
- Create DdaiUniversalPlanService.cs for validation/apply/undo/save/export mailbox calls.
- Extend ddai_bridge.gd with generic dispatch, revision preflight, job journal, and command handlers.

### Task 1: Typed operation schema for every category

**Files:**
- Create: src/DDAI.Core/MapPlans/Operations/MapOperation.cs
- Create: src/DDAI.Core/MapPlans/Operations/SurfaceOperations.cs
- Create: src/DDAI.Core/MapPlans/Operations/StructureOperations.cs
- Create: src/DDAI.Core/MapPlans/Operations/PlacementOperations.cs
- Modify: src/DDAI.Core/MapPlans/MapPlan.cs
- Modify: src/DDAI.Core/MapPlans/MapPlanJson.cs
- Create: tests/DDAI.Core.Tests/MapPlans/UniversalMapPlanJsonTests.cs

**Interfaces:**
- MapOperation derived records: TerrainStroke, PatternRegion, ColorablePatternRegion, CaveRegion, RoofRegion, ObjectPlacement, WallPolyline, MaterialStroke, PortalPlacement, PathPolyline, LightPlacement, SimpleTileRegion, SmartTileRegion, SmartTileDoubleRegion.
- Every operation contains OperationId and LevelId. Asset-bearing records contain AssetRef. Geometry uses GridPoint, GridPolyline, or GridPolygon. `ObjectPlacement` carries rotation, scale, layer, sorting, shadow, block-light, and optional normalized custom-color RGBA so the wire contract covers every documented Object Tool placement control.

- [ ] **Step 1: Write failing round-trip tests containing all fourteen operation discriminators**

~~~csharp
Assert.Collection(
    roundTrip.Operations,
    operation => Assert.IsType<TerrainStrokeOperation>(operation),
    operation => Assert.IsType<PatternRegionOperation>(operation),
    operation => Assert.IsType<ColorablePatternRegionOperation>(operation),
    operation => Assert.IsType<CaveRegionOperation>(operation),
    operation => Assert.IsType<RoofRegionOperation>(operation),
    operation => Assert.IsType<ObjectPlacementOperation>(operation),
    operation => Assert.IsType<WallPolylineOperation>(operation),
    operation => Assert.IsType<MaterialStrokeOperation>(operation),
    operation => Assert.IsType<PortalPlacementOperation>(operation),
    operation => Assert.IsType<PathPolylineOperation>(operation),
    operation => Assert.IsType<LightPlacementOperation>(operation),
    operation => Assert.IsType<SimpleTileRegionOperation>(operation),
    operation => Assert.IsType<SmartTileRegionOperation>(operation),
    operation => Assert.IsType<SmartTileDoubleRegionOperation>(operation));
~~~

- [ ] **Step 2: Run RED**

Run: dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --filter FullyQualifiedName~UniversalMapPlanJsonTests

- [ ] **Step 3: Implement strict discriminator conversion**

Use operation_type snake_case values matching the record names above. Reject missing, duplicate, unknown, or case-varied discriminators. Keep CurrentSchemaVersion = "2.0" while allowing "1.0" through the migration path.

- [ ] **Step 4: Extend fingerprint framing**

Frame every field explicitly in canonical operation order. Asset references, colors, floats, flags, points, and optional values must not depend on reflection property order or current culture. Reject NaN and infinity before fingerprinting.

- [ ] **Step 5: Test unknown fields, discriminator errors, culture independence, field changes, 1.0 compatibility, and deterministic bytes**
- [ ] **Step 6: Run GREEN and commit**

~~~powershell
dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --filter FullyQualifiedName~UniversalMapPlanJsonTests
git add -- src/DDAI.Core/MapPlans tests/DDAI.Core.Tests/MapPlans/UniversalMapPlanJsonTests.cs
git commit -m "feat: define universal map operations"
~~~

### Task 2: Complete ordered validation and version 1 migration

**Files:**
- Create: src/DDAI.Core/MapPlans/UniversalMapPlanValidation.cs
- Create: src/DDAI.Core/MapPlans/UniversalPlanMigration.cs
- Modify: src/DDAI.Core/MapPlans/RectangularRoomPlanValidation.cs
- Create: tests/DDAI.Core.Tests/MapPlans/UniversalMapPlanValidatorTests.cs

**Interfaces:**
- Validate(plan, catalog, capabilities) returns every issue in schema, envelope, revision, operation, geometry, asset, capability order.
- MigrateV1 converts one room into one closed WallPolyline with the same request ID and base revision and no asset substitution.

- [ ] **Step 1: Write failing all-category valid and aggregate-invalid tests**
- [ ] **Step 2: Run RED**
- [ ] **Step 3: Implement envelope validation**

Require schema 2.0, safe request/map/operation IDs, nonnegative revisions, add/patch modes only, positive canvas, 1..500 operations, unique operation IDs, known levels, and total points at most 10,000.

- [ ] **Step 4: Implement category-specific validation**

Require asset category match, finite bounded geometry, polygons with at least three unique points, polylines with at least two points, positive widths/scales/intensities, normalized RGBA hex (including optional object custom color), rotation in -360..360, valid layer/sorting enums, and capability RuntimeCertified true. Require every referenced generated asset to be active.

- [ ] **Step 5: Implement deterministic dependency order**

Canonical phases are map/level settings, terrain/caves, patterns/tiles/roofs, walls, portals, paths/materials, objects, and lights. Preserve caller order inside a phase and expose resolved operation IDs in validation output.

- [ ] **Step 6: Test integer/float overflow, every boundary, duplicate IDs, category mismatch, stale generated asset, unsupported capability, point cap, and migration equivalence**
- [ ] **Step 7: Run GREEN and commit**

~~~powershell
dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --filter "FullyQualifiedName~UniversalMapPlanValidatorTests|FullyQualifiedName~RectangularRoom"
git add -- src/DDAI.Core/MapPlans tests/DDAI.Core.Tests/MapPlans
git commit -m "feat: validate universal map plans"
~~~

### Task 3: Revision-locked map job contracts

**Files:**
- Create: src/DDAI.Core/MapPlans/MapJobContracts.cs
- Create: src/DDAI.Core/Mailbox/UniversalMapJobStateMachine.cs
- Create: tests/DDAI.Core.Tests/Mailbox/UniversalMapJobStateMachineTests.cs

**Interfaces:**
- Durable states: prepared, operation_applied, operation_observed, committed, reversing, reversed, outcome_unknown.
- Job journal contains request ID, plan fingerprint, map ID, starting map revision, catalog revision/fingerprint, next operation index, observed native node IDs, and canonical response.

- [ ] **Step 1: Write one fault-injection test at every durable boundary**
- [ ] **Step 2: Run RED**
- [ ] **Step 3: Implement one-durable-transition-per-advance reference model**

Never combine native mutation, observation, response publish, claim deletion, or journal deletion into one transition. Reconcile duplicate requests by request ID and plan fingerprint. An unobserved post-call crash becomes outcome_unknown and never advances or replays.

- [ ] **Step 4: Add conflicting journal/response, locked file, interrupted reversal, oversize response, and concurrent duplicate tests**
- [ ] **Step 5: Run GREEN and commit**

~~~powershell
dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --filter FullyQualifiedName~UniversalMapJobStateMachineTests
git add -- src/DDAI.Core/MapPlans/MapJobContracts.cs src/DDAI.Core/Mailbox/UniversalMapJobStateMachine.cs tests/DDAI.Core.Tests/Mailbox/UniversalMapJobStateMachineTests.cs
git commit -m "feat: model crash safe universal map jobs"
~~~

### Task 4: Control-plane universal apply service

**Files:**
- Create: src/DDAI.App/DdaiUniversalPlanService.cs
- Modify: src/DDAI.App/McpStdioServer.cs
- Modify: src/DDAI.App/Mcp/DdaiMapTools.cs
- Modify: src/DDAI.App/DdaiPlanService.cs
- Create: tests/DDAI.App.Tests/DdaiUniversalPlanServiceTests.cs
- Modify: tests/DDAI.App.Tests/McpPlanToolTests.cs

**Interfaces:**
- ddai_validate_plan and ddai_apply_plan accept version 1 or 2 and return the canonical version, resolved order, map/catalog revisions, fingerprint, issues, and runtime checks.

- [ ] **Step 1: Write failing no-publish-on-invalid, revision conflict, real-mailbox fake-bridge, duplicate, conflict, and timeout tests**
- [ ] **Step 2: Run RED**
- [ ] **Step 3: Implement validation and apply**

Load one accepted catalog snapshot, validate against capabilities, serialize once, enforce 1 MiB, publish apply_plan, correlate command/request/fingerprint/map/catalog, and retain unknown-outcome evidence on timeout. Keep DdaiPlanService as the version 1 facade calling the universal service.

- [ ] **Step 4: Update MCP descriptions and schemas without changing tool names**
- [ ] **Step 5: Run focused GREEN and commit**

~~~powershell
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --filter "FullyQualifiedName~DdaiUniversalPlanServiceTests|FullyQualifiedName~McpPlanToolTests|FullyQualifiedName~DdaiPlanServiceTests"
git add -- src/DDAI.App tests/DDAI.App.Tests
git commit -m "feat: apply universal plans through MCP"
~~~

### Task 5: Mirror generic job state in GDScript

**Files:**
- Modify: mods/DDAI/scripts/ddai_bridge.gd
- Modify: src/DDAI.Core/Mailbox/DungeondraftBridgeStateMachine.cs
- Modify: tests/DDAI.Core.Tests/DungeondraftBridgeStateMachineTests.cs
- Modify: tests/DDAI.Core.Tests/MailboxBridgeConformanceTests.cs
- Create: tests/DDAI.Core.Tests/MapPlans/DungeondraftUniversalPlanScriptTests.cs

**Interfaces:**
- GDScript _advance_apply_plan_claim mirrors UniversalMapJobStateMachine.
- _preflight_universal_plan verifies session, map ID/revision, catalog revision/fingerprint, asset_ref/category/fingerprint, idle tools, and executor certification.

- [ ] **Step 1: Add failing conformance/static tests for every durable route and fail-closed preflight**
- [ ] **Step 2: Run RED**
- [ ] **Step 3: Implement strict version 2 parser and generic dispatcher**

Parse without unsupported Dictionary zero-argument built-ins. Limit arrays before loops. Write prepared journal before the first native call. Call _execute_operation only through a dictionary of explicitly certified operation strings. Persist and observe one operation before advancing.

- [ ] **Step 4: Mirror recovery, duplicate reconciliation, reversal, and outcome_unknown routes in the .NET reference model and GDScript static contract**
- [ ] **Step 5: Run exact Godot 3.5.3 parser, listener scan, focused tests, and commit**

~~~powershell
dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --filter "FullyQualifiedName~DungeondraftBridgeStateMachine|FullyQualifiedName~MailboxBridgeConformance|FullyQualifiedName~DungeondraftUniversalPlan"
git add -- mods/DDAI/scripts/ddai_bridge.gd src/DDAI.Core/Mailbox tests/DDAI.Core.Tests
git commit -m "feat: execute revision locked map jobs"
~~~

### Task 6: Job-scoped undo

**Files:**
- Modify: src/DDAI.Core/Mailbox/MailboxContracts.cs
- Modify: src/DDAI.App/DdaiUniversalPlanService.cs
- Modify: src/DDAI.App/Mcp/DdaiMapTools.cs
- Modify: mods/DDAI/scripts/ddai_bridge.gd
- Create: tests/DDAI.App.Tests/DdaiUndoServiceTests.cs
- Create: tests/DDAI.Core.Tests/MapPlans/DungeondraftUndoScriptTests.cs

- [ ] **Step 1: Write failing tests proving undo requires the latest completed DDAI job, exact current revision, and complete reversal evidence**
- [ ] **Step 2: Run RED**
- [ ] **Step 3: Add undo_last_job mailbox command and MCP tool**

The bridge reverses recorded native node IDs and category-specific snapshots in reverse operation order. It refuses after an intervening edit, missing node, different map, unsupported reverse executor, or ambiguous job. Success increments map revision and records reversed.

- [ ] **Step 4: Test duplicate undo, partial reversal crash, user edit, unknown job, and no generic unscoped undo call**
- [ ] **Step 5: Run GREEN, parser, and commit**

~~~powershell
dotnet test DDAI.slnx -c Release --filter "FullyQualifiedName~Undo"
git add -- src mods tests
git commit -m "feat: add job scoped map undo"
~~~

### Task 7: Capability-gated save and export

**Files:**
- Modify: src/DDAI.App/Mcp/DdaiMapTools.cs
- Modify: src/DDAI.App/DdaiUniversalPlanService.cs
- Modify: mods/DDAI/scripts/ddai_bridge.gd
- Create: tests/DDAI.App.Tests/DdaiSaveExportTests.cs
- Create: tests/DDAI.Core.Tests/MapPlans/DungeondraftSaveExportScriptTests.cs

- [ ] **Step 1: Write failing tests for existing-save-path requirement, approved export root, format enum, collision-safe name, and asynchronous export observation**
- [ ] **Step 2: Run RED**
- [ ] **Step 3: Add ddai_save_map only after a read-only live probe identifies and certifies the exact Dungeondraft 1.2.0.1 save API**

If no documented/certifiable non-dialog save route exists, capability remains false and the tool returns save_path_required or unsupported_operation without UI automation.

- [ ] **Step 4: Add ddai_export_map using Global.Exporter.Start mode 0..4, approved root confinement, file creation observation, timeout, and no overwrite**
- [ ] **Step 5: Run focused tests, parser, disposable live export, and commit**

~~~powershell
dotnet test DDAI.slnx -c Release --filter "FullyQualifiedName~SaveExport"
git add -- src mods tests
git commit -m "feat: add safe map save and export"
~~~
