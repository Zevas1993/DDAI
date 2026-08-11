# DDAI All-Category Native Executors Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Live-certify and implement native, observable, reversible execution for every official Dungeondraft asset category.

**Architecture:** Each executor is registered separately with exact required methods, idle checks, mutation route, postcondition, and reversal evidence. A read-only certification command checks only documented objects and methods; a capability remains false until a disposable-map mutation, undo, save, and reopen proof passes on Dungeondraft 1.2.0.1.

**Tech Stack:** Godot 3.5.3 GDScript, Dungeondraft Modding API, Dungeondraft 1.2.0.1, .NET reference state machine, xUnit static/conformance tests.

## Global Constraints

- Depends on the universal plan engine.
- Dungeondraft 1.2.0.1 only.
- Never mutate the user's working map during certification; use a newly created disposable map.
- Never call a tool while its isDrawing/isPainting/isDragging/worker-busy state is true.
- Enable and disable interactive tools in guaranteed cleanup paths.
- Resolve asset_ref through the current live catalog immediately before use.
- Require one category-specific observable postcondition; a returned method call is not success.
- Capture category-specific reversal evidence before mutation.
- Any crash after a native call and before observation is mutation_outcome_unknown and is never replayed.
- Preserve all existing wall crash-safety tests and unrelated dirty files.

---

## File responsibilities

- Create mods/DDAI/scripts/ddai_operation_certifier.gd for read-only exact-version probes.
- Extend mods/DDAI/scripts/ddai_bridge.gd only through focused executor functions.
- Create one static test file per executor family under tests/DDAI.Core.Tests/Executors.
- Extend UniversalMapJobStateMachine tests with category-specific observation and reversal receipts.
- Keep public capability data in src/DDAI.App/DdaiCapabilityService.cs.

### Task 1: Exact-version capability certification harness

**Files:**
- Create: mods/DDAI/scripts/ddai_operation_certifier.gd
- Modify: mods/DDAI/scripts/ddai_bridge.gd
- Create: tests/DDAI.Core.Tests/Executors/DungeondraftOperationCertifierTests.cs
- Modify: src/DDAI.App/DdaiCapabilityService.cs

- [ ] **Step 1: Write failing tests requiring one probe record per fourteen operation types**
- [ ] **Step 2: Run RED**

Run: dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --filter FullyQualifiedName~DungeondraftOperationCertifierTests

- [ ] **Step 3: Implement certify_runtime read-only command**

Each record contains OperationType, ToolName, RequiredMethods, RequiredProperties, Available, Busy, ExactDungeondraftVersion, and Reason. Use has_method for methods and fixed documented property reads guarded by object/null/type checks. The command never calls Enable, Confirm, End, Draw, Paint, Fill, Create, Load, Restore, or Export.

- [ ] **Step 4: Run the exact parser and live read-only probe on a disposable map**
- [ ] **Step 5: Persist the certified matrix in the report and expose it through ddai_get_capabilities**
- [ ] **Step 6: Commit**

~~~powershell
git add -- mods/DDAI src/DDAI.App/DdaiCapabilityService.cs tests/DDAI.Core.Tests/Executors/DungeondraftOperationCertifierTests.cs
git commit -m "feat: certify native operation capabilities"
~~~

### Task 2: Terrain, pattern, cave, and roof executors

**Files:**
- Modify: mods/DDAI/scripts/ddai_bridge.gd
- Create: tests/DDAI.Core.Tests/Executors/DungeondraftSurfaceExecutorTests.cs
- Modify: tests/DDAI.Core.Tests/Mailbox/UniversalMapJobStateMachineTests.cs

**Interfaces and fixed routes:**
- Terrain: current Level.Terrain; SetTexture(texture, slot); Paint or Fill; observe splat-image hash and idle renderer; reversal uses cloned splat images and prior texture slots.
- Patterns and colorable patterns: configure PatternShapeTool Texture/Color/Rotation/Layer, then Level.PatternShapes.DrawPolygon(points, false); observe GetShapes count and NodeID; reversal removes only recorded NodeID.
- Caves: configure CaveBrush and CaveMesh texture/colors; mutate through the certified MarchingSquares route; wait until IsMeshWorkerBusy false; observe bitmap/hash and generated-wall state; reversal restores captured cave bitmap/colors/textures.
- Roofs: configure RoofTool texture/type/width/shade, use DrawRect for rectangles or WorldUI points plus FinishShape for polygons; observe Level.Roofs child count and NodeID; reversal removes the recorded roof.

- [ ] **Step 1: Write static RED tests for exact idle checks, routes, postconditions, cleanup, and reversal records**
- [ ] **Step 2: Add .NET fault-injection cases for each surface family**
- [ ] **Step 3: Implement terrain and pattern executors; run focused tests and parser**
- [ ] **Step 4: Live-certify terrain and pattern on a disposable map, one operation and one job-scoped undo each**
- [ ] **Step 5: Implement cave and roof executors; run focused tests and parser**
- [ ] **Step 6: Live-certify cave and roof, including worker completion, undo, save, close, and reopen**
- [ ] **Step 7: Enable only the four passing capability flags and commit**

~~~powershell
dotnet test DDAI.slnx -c Release --filter "FullyQualifiedName~SurfaceExecutor|FullyQualifiedName~UniversalMapJobStateMachine"
git add -- mods/DDAI/scripts/ddai_bridge.gd tests/DDAI.Core.Tests/Executors tests/DDAI.Core.Tests/Mailbox src/DDAI.App/DdaiCapabilityService.cs
git commit -m "feat: execute native surface operations"
~~~

### Task 3: Wall and portal executors

**Files:**
- Modify: mods/DDAI/scripts/ddai_bridge.gd
- Create: tests/DDAI.Core.Tests/Executors/DungeondraftStructureExecutorTests.cs
- Modify: tests/DDAI.Core.Tests/RectangularRoomPlanValidatorTests.cs

**Interfaces and fixed routes:**
- Walls retain the proven WorldUI.AddPolyPoint plus WallTool.EndWall(true/false) route, generalized to a bounded polyline and selected catalog wall texture/color.
- Portals use the current wall's documented AddPortal route when attached and PortalTool only for certified freestanding placement; observe portal count, NodeID, wall association, texture, and closed state.

- [ ] **Step 1: Write RED tests for open/closed wall polylines, texture selection, no cursor point, portal wall association, and busy-state rejection**
- [ ] **Step 2: Generalize the existing wall implementation without changing version 1 rectangle behavior**
- [ ] **Step 3: Implement attached and freestanding portal execution with exact postconditions**
- [ ] **Step 4: Run parser, focused tests, and live wall/door create-undo-save-reopen proof**
- [ ] **Step 5: Commit**

~~~powershell
git add -- mods/DDAI/scripts/ddai_bridge.gd tests/DDAI.Core.Tests/Executors tests/DDAI.Core.Tests/RectangularRoomPlanValidatorTests.cs
git commit -m "feat: execute native wall and portal operations"
~~~

### Task 4: Path, material, object, and light executors

**Files:**
- Modify: mods/DDAI/scripts/ddai_bridge.gd
- Create: tests/DDAI.Core.Tests/Executors/DungeondraftPlacementExecutorTests.cs

**Interfaces and fixed routes:**
- Paths configure PathTool Texture/Width/Smoothness/layer/sorting/fades, StartPath, set exact edit points through the certified active Pathway, and EndPath(loop); observe Pathways count and NodeID.
- Materials configure MaterialBrush.SetMaterial, layer, size, smoothness, and mutate its certified MaterialMesh; observe a captured mesh/bitmap hash and worker idle state.
- Objects configure ObjectTool texture/rotation/scale/layer/sorting/shadow/block-light and optional custom color through `ChangeColor`, create or update Preview at the explicit plan position, and Confirm; observe Objects count, NodeID, texture identity, transform, layer/sorting, shadow/block-light, and applied custom color when requested.
- Lights configure LightTool texture/range/intensity/color/shadows, CreatePreview, place through the certified Lights container route, and observe light NodeID and properties.

- [ ] **Step 1: Write RED tests for exact configuration order, idle checks, explicit positions, every Object Tool option (rotation, scale, layer, sorting, shadow, block-light, custom color), postconditions, and cleanup**
- [ ] **Step 2: Implement path and material, run parser/tests, and live-certify create/undo**
- [ ] **Step 3: Implement object and light, run parser/tests, and live-certify create/undo**
- [ ] **Step 4: Save, close, reopen, and inspect all four native elements**
- [ ] **Step 5: Enable only proven flags and commit**

~~~powershell
git add -- mods/DDAI/scripts/ddai_bridge.gd tests/DDAI.Core.Tests/Executors src/DDAI.App/DdaiCapabilityService.cs
git commit -m "feat: execute native placement operations"
~~~

### Task 5: Simple, smart, and double-smart tile executors

**Files:**
- Modify: mods/DDAI/scripts/ddai_bridge.gd
- Create: tests/DDAI.Core.Tests/Executors/DungeondraftTileExecutorTests.cs

**Interfaces:**
- Resolve the catalog tileset to its exact loaded ID.
- Configure FloorShapeTool.SmartTileId and the documented FloorShapes/FloorTileMap route.
- Observe the created floor shape, generated tile cells, border walls, NodeIDs, and selected tileset.
- Reversal removes the recorded shape and its generated border walls only.

- [ ] **Step 1: Write RED tests requiring three distinct category checks and no interchange between their asset_ref values**
- [ ] **Step 2: Run the read-only certifier against FloorShapeTool, FloorShapes, and FloorTileMap**
- [ ] **Step 3: Implement simple tile and live-certify create/undo/save/reopen**
- [ ] **Step 4: Implement smart tile and live-certify create/undo/save/reopen**
- [ ] **Step 5: Implement double-smart tile and live-certify create/undo/save/reopen**
- [ ] **Step 6: Run parser/full focused tests, enable proven flags, and commit**

~~~powershell
git add -- mods/DDAI/scripts/ddai_bridge.gd tests/DDAI.Core.Tests/Executors src/DDAI.App/DdaiCapabilityService.cs
git commit -m "feat: execute native tile operations"
~~~

### Task 6: AI-generated asset activation

**Files:**
- Create: src/DDAI.App/Assets/GeneratedAssetActivationService.cs
- Modify: src/DDAI.App/Mcp/DdaiAssetTools.cs
- Modify: mods/DDAI/scripts/ddai_bridge.gd
- Create: tests/DDAI.App.Tests/Assets/GeneratedAssetActivationTests.cs
- Create: tests/DDAI.Core.Tests/Executors/DungeondraftGeneratedAssetTests.cs

**Interfaces:**
- Object images use the documented ObjectTool.EmbebObject(file) route after copying a normalized PNG to the DDAI-owned bridge inbox.
- Pack-backed category bundles are staged under the DDAI-owned generated source tree using the installed example_template.zip layout and return staged_reload_required until a Dungeondraft-generated pack is loaded and cataloged.

- [ ] **Step 1: Write RED tests for owned-root confinement, one-time bridge token, PNG-only embedded object, activation states, and no false persistence claim**
- [ ] **Step 2: Implement generated object activation and live-certify placement, save, close, reopen, and preview/search**
- [ ] **Step 3: Implement category bundle manifests requiring the exact image set: cave floor/wall; roof tiles/ridge/edge/hip; wall body/end; tile image plus tileset data; single image for terrain/pattern/object/material/portal/path/light**
- [ ] **Step 4: Stage the owned source tree and invoke only the user-approved Dungeondraft packaging flow; never modify an existing foreign pack**
- [ ] **Step 5: After normal reload, require the live catalog to map every generated_asset_id to asset_ref before marking active**
- [ ] **Step 6: Test removal refusal while a known map references the generated asset, then commit**

~~~powershell
git add -- src/DDAI.App/Assets src/DDAI.App/Mcp/DdaiAssetTools.cs mods/DDAI/scripts/ddai_bridge.gd tests
git commit -m "feat: activate AI generated Dungeondraft assets"
~~~

### Task 7: Full mixed-category live map

**Files:**
- Create: docs/superpowers/reports/2026-08-10-ddai-all-category-live-report.md
- Modify: tests/DDAI.App.Tests/McpPublishedIntegrationTests.cs

- [ ] **Step 1: Build one version 2 plan containing at least one operation from every category plus one generated object**
- [ ] **Step 2: Call validate and apply through the published executable with the official MCP SDK**
- [ ] **Step 3: Inspect the visible map and bounded map snapshot; reconcile every operation ID to its observed native element or surface receipt**
- [ ] **Step 4: Call ddai_undo_last_job and prove only this job disappears**
- [ ] **Step 5: Reapply with a fresh ID, save, close, reopen, inspect persistence, and export PNG plus UniversalVTT**
- [ ] **Step 6: Verify responsive process, fresh heartbeat, no new dump/Application Error, and exact parser success**
- [ ] **Step 7: Run full Release tests/build and commit the report/test**

~~~powershell
dotnet test DDAI.slnx -c Release
dotnet build DDAI.slnx -c Release --no-restore
git diff --check
git add -- tests/DDAI.App.Tests/McpPublishedIntegrationTests.cs docs/superpowers/reports/2026-08-10-ddai-all-category-live-report.md
git commit -m "test: prove all category map construction"
~~~
