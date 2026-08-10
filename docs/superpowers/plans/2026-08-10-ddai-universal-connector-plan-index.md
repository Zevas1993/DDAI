# DDAI Universal Connector Execution Index

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement these plans in order. Do not claim completion until the final acceptance task passes.

**Goal:** Deliver one non-admin Windows connector through which ChatGPT Desktop, Claude Desktop, Gemini, Codex, and other compatible MCP clients can inspect an open Dungeondraft map, discover and preview every loaded asset, import AI-generated images, construct maps across every official asset category, undo the connector's last job, save, and export.

## Approved invariants

- The live Dungeondraft mod is authoritative; the .NET catalog is a bounded cache.
- Every loaded asset in all fourteen official categories is searchable and placeable by opaque `asset_ref` when the live runtime certifies its executor: Terrain, Patterns, Patterns Colorable, Caves, Roofs, Objects, Walls, Materials, Portals, Paths, Lights, Simple Tiles, Smart Tiles, and Smart Tiles Double.
- Purchased source bytes and source paths never leave the local machine; MCP exposes only bounded previews and metadata.
- `AllowThirdPartyUse` remains visible metadata and a warning, not a search or placement filter.
- AI-generated images are normalized into a DDAI-owned library. Generated objects may use the documented embedded-object route immediately; pack-backed categories remain staged until a normal Dungeondraft packaging/reload cycle makes them live and cataloged.
- Every public tool is identical across clients: `ddai_status`, `ddai_get_capabilities`, `ddai_inspect_map`, `ddai_search_assets`, `ddai_get_asset_preview`, `ddai_import_asset`, `ddai_validate_plan`, `ddai_apply_plan`, `ddai_undo_last_job`, `ddai_save_map`, and `ddai_export_map`.
- Every mutation is correlated to exact `map_revision` and `catalog_revision` values so an agent cannot apply a stale plan to a changed map or asset set.
- Setup remains per-user and non-admin. No client is reported ready without a published-server initialize/list/call proof and the relevant real-client observation.
- Existing unrelated dirty files remain untouched.

## Required execution order

1. [Asset catalog and generated assets](2026-08-10-ddai-asset-catalog-and-generated-assets-plan.md)
   Establish strict categories, opaque identities, bounded previews, complete live snapshots, deterministic search, and the DDAI-owned generated-image library.
2. [MCP discovery and inspection](2026-08-10-ddai-mcp-discovery-and-inspection-plan.md)
   Expose capabilities, search, previews, import, and map inspection through the common MCP contract and published executable.
3. [Universal plan engine](2026-08-10-ddai-universal-plan-engine-plan.md)
   Define and validate revision-locked operations, crash-safe execution, job-scoped undo, save, and export.
4. [All-category native executors](2026-08-10-ddai-all-category-executors-plan.md)
   Certify and prove each category executor and generated-asset activation against Dungeondraft 1.2.0.1 on disposable maps.
5. [Client setup and acceptance](2026-08-10-ddai-client-setup-and-acceptance-plan.md)
   Automate ownership-safe desktop setup and perform cross-client plus full haunted-map acceptance.

## Cross-plan gates

- Run each task RED before implementation and GREEN after the smallest implementation.
- Before editing high-risk symbols, refresh GitNexus and run impact analysis; direct source verification remains authoritative if the graph is stale or incomplete.
- After every plan, run its focused tests, full Release tests/build, parser/static checks, package audit where applicable, and `git diff --check`.
- Install or replace only DDAI-owned files after the user closes Dungeondraft normally; never terminate the application or touch a user map for certification.
- Record failures honestly. A schema-supported but unproven executor remains `runtime_certified: false` and cannot be applied.
- Final completion requires a generated asset imported by one connected client, found/previewed by another, and used in a saved/reopened/exported disposable map together with all fourteen native categories.
