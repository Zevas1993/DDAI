# DDAI Client Setup and Acceptance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Make one non-admin setup connect the installed DDAI MCP server to ChatGPT/Codex through a validated local plugin and to Claude Desktop/Gemini through ownership-safe client adapters, then prove real map construction.

**Architecture:** Client adapters share one ownership-checked transaction model but emit each client's native configuration shape. ChatGPT/Codex use a personal DDAI plugin whose .mcp.json launches the same installed stdio server; Claude and Gemini retain direct JSON entries. Setup never claims a client works until initialize/list/call succeeds through the published executable and the real client boundary is observed.

**Tech Stack:** .NET 9 Windows installer, Codex plugin manifest and personal marketplace, Claude/Gemini JSON configs, ModelContextProtocol 1.4.1, xUnit, one-file win-x64 publish.

## Global Constraints

- Non-admin, per-user installation only.
- Installed executable: %LOCALAPPDATA%\DDAI\ddai.exe.
- Personal plugin source: %USERPROFILE%\plugins\ddai-dungeondraft.
- Personal plugin activation uses the supported Codex plugin CLI against the validated local plugin source; product code does not hand-edit marketplace.json.
- ChatGPT/Codex plugin name: ddai-dungeondraft.
- Every client launches the same absolute executable with args exactly ["serve", "--stdio"].
- Setup proves ownership before replacement/removal, backs up changed client files, writes transactionally, preserves unrelated entries, and rolls back cross-client failure.
- Uninstall preserves generated assets and maps unless the user separately requests their deletion.
- Do not edit ChatGPT Desktop's empty config.json as if it were Claude's mcpServers format.
- Preserve unrelated dirty files.

---

## File responsibilities

- Create integrations/ddai-dungeondraft/.codex-plugin/plugin.json and .mcp.json as source-controlled plugin templates.
- Create integrations/ddai-dungeondraft/skills/ddai-mapmaking/SKILL.md for the search-preview-validate-apply workflow.
- Create src/DDAI.App/Clients/IClientAdapter.cs and focused Claude, Gemini, and ChatGptCodex adapters.
- Reduce ClientConfigMerger.cs to the shared transactional JSON primitive.
- Extend LocalSetupService and CLI paths/results with plugin and client-probe receipts.
- Extend README with one-command setup, client refresh, generated assets, and diagnostics.

### Task 1: Extract ownership-safe client adapters

**Files:**
- Create: src/DDAI.App/Clients/IClientAdapter.cs
- Create: src/DDAI.App/Clients/JsonMcpClientAdapter.cs
- Create: src/DDAI.App/Clients/ClaudeDesktopAdapter.cs
- Create: src/DDAI.App/Clients/GeminiAdapter.cs
- Modify: src/DDAI.App/ClientConfigMerger.cs
- Modify: tests/DDAI.App.Tests/ClientConfigMergerTests.cs

**Interfaces:**
- IClientAdapter exposes Kind, Detect(), PlanSetup(executable), PlanUninstall(executable), and Diagnose(executable).
- ClientMutationPlan contains target path, original hash/bytes, replacement bytes, ownership proof, backup path, and rollback action.

- [ ] **Step 1: Write failing tests showing existing Claude/Gemini behavior through adapters**
- [ ] **Step 2: Run RED**
- [ ] **Step 3: Extract without behavior change**

Claude owns command plus exact args. Gemini owns the same plus trust false. A foreign or modified ddai entry blocks setup before any client changes. Uninstall removes only an exact owned entry.

- [ ] **Step 4: Re-run all existing ownership/published tests**
- [ ] **Step 5: Commit**

~~~powershell
git add -- src/DDAI.App/Clients src/DDAI.App/ClientConfigMerger.cs tests/DDAI.App.Tests/ClientConfigMergerTests.cs
git commit -m "refactor: isolate MCP client adapters"
~~~

### Task 2: Create and validate the ChatGPT/Codex DDAI plugin

**Files:**
- Create: integrations/ddai-dungeondraft/.codex-plugin/plugin.json
- Create: integrations/ddai-dungeondraft/.mcp.json
- Create: integrations/ddai-dungeondraft/skills/ddai-mapmaking/SKILL.md
- Create: tests/DDAI.App.Tests/ChatGptCodexPluginTests.cs

**Interfaces:**
- plugin.json name ddai-dungeondraft, semver matching connector, mcpServers "./.mcp.json", Developer Tools category, Interactive/Write capabilities, and at most three starter prompts.
- .mcp.json has one ddai server with an installer-substituted absolute command and exact args.

- [ ] **Step 1: Use the plugin-creator scaffold script with --path <repo>\integrations --with-mcp and --with-skills, then validate the generated integrations/ddai-dungeondraft structure**
- [ ] **Step 2: Write failing repository tests for manifest names, real paths, no placeholder markers, exact MCP entry, and workflow skill**
- [ ] **Step 3: Fill the manifest**

Use repository https://github.com/Zevas1993/DDAI, license MIT, developer DDAI Project, and starter prompts for creating a map, inspecting the open map, and searching assets. The skill instructs the agent to call status/capabilities, search assets, inspect previews, validate the full plan, then apply once with the same request ID on retry.

- [ ] **Step 4: Run plugin-creator validate_plugin.py against the integration source**
- [ ] **Step 5: Run focused tests and commit**

~~~powershell
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --filter FullyQualifiedName~ChatGptCodexPluginTests
git add -- integrations/ddai-dungeondraft tests/DDAI.App.Tests/ChatGptCodexPluginTests.cs
git commit -m "feat: add ChatGPT and Codex DDAI plugin"
~~~

### Task 3: Transactional personal-plugin installer

**Files:**
- Create: src/DDAI.App/Clients/ChatGptCodexPluginAdapter.cs
- Modify: src/DDAI.App/LocalSetupService.cs
- Modify: src/DDAI.App/CliOptions.cs
- Modify: tests/DDAI.App.Tests/SetupLifecycleTests.cs
- Create: tests/DDAI.App.Tests/ChatGptCodexPluginAdapterTests.cs

**Interfaces:**
- Adapter installs source to %USERPROFILE%\plugins\ddai-dungeondraft and invokes the supported Codex plugin CLI to activate ddai-dungeondraft from that validated local source.
- Ownership receipt stores source hash, installed hash, CLI activation receipt, executable path, and plugin version.

- [ ] **Step 1: Write failing fresh install, idempotence, upgrade, foreign-plugin, CLI-activation-failure, rollback, and uninstall tests**
- [ ] **Step 2: Run RED**
- [ ] **Step 3: Implement staged plugin directory and supported CLI activation**

Render .mcp.json with the installed absolute executable. Stage and validate the whole plugin before swapping. Create a timestamped recoverable backup on upgrade. Refuse to overwrite an unreceipted or modified ddai-dungeondraft source. Invoke the supported plugin CLI for activation/deactivation and capture its result without editing marketplace.json directly. Uninstall removes only exact owned source and activation.

- [ ] **Step 4: Validate the rendered installed plugin using validate_plugin.py in integration tests when Python is available; repository schema tests remain mandatory**
- [ ] **Step 5: Run GREEN and commit**

~~~powershell
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --filter "FullyQualifiedName~ChatGptCodexPluginAdapterTests|FullyQualifiedName~SetupLifecycleTests"
git add -- src/DDAI.App tests/DDAI.App.Tests
git commit -m "feat: install ChatGPT DDAI plugin"
~~~

### Task 4: One-command setup, diagnosis, and preservation

**Files:**
- Modify: src/DDAI.App/LocalSetupService.cs
- Modify: src/DDAI.App/CliApplication.cs
- Modify: src/DDAI.App/CliOptions.cs
- Modify: tests/DDAI.App.Tests/SetupLifecycleTests.cs
- Modify: tests/DDAI.App.Tests/PublishedConfigOwnershipTests.cs

- [ ] **Step 1: Write failing tests for all three client families in one atomic setup**
- [ ] **Step 2: Run RED**
- [ ] **Step 3: Preflight every client and Dungeondraft target before copying any file**

Return per-client states absent, installed, already_current, activation_pending, foreign_conflict, modified, and running. Setup completion requires owned installed executable/mod/plugin/configs; live readiness remains separate.

- [ ] **Step 4: Make uninstall preserve generated-assets, preview cache, maps, exports, and foreign configs**
- [ ] **Step 5: Add diagnose checks for plugin schema/hash, executable hash, exact args, fresh bridge heartbeat, complete catalog, and last client probe**
- [ ] **Step 6: Run published ownership reproductions and commit**

~~~powershell
git add -- src/DDAI.App tests/DDAI.App.Tests
git commit -m "feat: automate universal desktop client setup"
~~~

### Task 5: Published artifact and client protocol probes

**Files:**
- Modify: tools/DDAI.McpProbe/Program.cs
- Modify: tests/DDAI.App.Tests/McpProbeTests.cs
- Modify: tests/DDAI.App.Tests/McpPublishedIntegrationTests.cs

**Interfaces:**
- Probe modes cover initialize, tools/list, status, capabilities, search, preview, import, inspect, validate, apply, undo, and export.
- Probe receipt includes executable SHA-256, SDK version, tool names/schema hashes, content block types, start/end timestamps, and exit/stdout/stderr evidence.

- [ ] **Step 1: Write failing probe receipt tests**
- [ ] **Step 2: Implement every mode with ModelContextProtocol 1.4.1 and real AtomicMailbox plus fake/live peer selection**
- [ ] **Step 3: Publish self-contained single-file win-x64 and run all read-only modes plus fake mutation**
- [ ] **Step 4: Assert protocol-clean stdout under success, validation failure, mailbox failure, cancellation, and host startup exception**
- [ ] **Step 5: Commit**

~~~powershell
git add -- tools/DDAI.McpProbe tests/DDAI.App.Tests
git commit -m "test: probe the complete published MCP server"
~~~

### Task 6: Actual per-user installation and desktop-client proof

**Files:**
- Create: docs/superpowers/reports/2026-08-10-ddai-client-compatibility-report.md

- [ ] **Step 1: Run full Release tests/build, vulnerable-package audit, parser, AST, listener, sensitive-data, and git diff checks**
- [ ] **Step 2: Ask the user to close Dungeondraft normally; run setup once, then a second idempotence run**
- [ ] **Step 3: Hash installed/source executable and mod; validate installed plugin; verify Claude/Gemini unrelated servers are byte/semantic preserved and the plugin CLI reports the expected DDAI activation**
- [ ] **Step 4: Reopen a disposable Dungeondraft map and require fresh heartbeat, complete catalog, and successful published status/search/preview**
- [ ] **Step 5: Restart or refresh each client normally, never by process termination**
- [ ] **Step 6: In Claude Desktop and Gemini, list DDAI tools and call status/search/preview**
- [ ] **Step 7: Install/reinstall ddai-dungeondraft through the personal marketplace helper, then start a new ChatGPT/Codex task as required by the plugin lifecycle and list/call the same tools**
- [ ] **Step 8: Use one client to import a generated image and another to search/preview it, proving the shared server state**
- [ ] **Step 9: Record exact versions, config/plugin receipts, tool schemas, client screenshots or logs, and any unsupported client capability without overclaiming**
- [ ] **Step 10: Commit the report**

~~~powershell
git add -- docs/superpowers/reports/2026-08-10-ddai-client-compatibility-report.md
git commit -m "test: prove desktop MCP client compatibility"
~~~

### Task 7: End-user haunted-map acceptance and documentation

**Files:**
- Modify: README.md
- Create: docs/user-guide.md
- Create: docs/superpowers/reports/2026-08-10-ddai-final-acceptance-report.md

- [ ] **Step 1: Write the one-command Windows setup, normal reload, diagnosis, uninstall, generated-asset, and recovery instructions**
- [ ] **Step 2: Document all MCP tools with one concise example workflow and stable error remediation**
- [ ] **Step 3: From a new disposable 40 by 30 map, ask a connected desktop agent to create a five-room haunted crypt using every category and one AI-generated asset**
- [ ] **Step 4: Prove validate, apply, inspect, exact job undo, reapply, save, close/reopen persistence, PNG export, and UniversalVTT export**
- [ ] **Step 5: Prove no new Dungeondraft crash event/dump, fresh heartbeat, correct revisions, and no original source path/image disclosure**
- [ ] **Step 6: Run the full completion audit requirement-by-requirement against the approved design and all coordinated plans**
- [ ] **Step 7: Run final Release tests/build and commit**

~~~powershell
dotnet test DDAI.slnx -c Release
dotnet build DDAI.slnx -c Release --no-restore
git diff --check
git add -- README.md docs/user-guide.md docs/superpowers/reports/2026-08-10-ddai-final-acceptance-report.md
git commit -m "docs: complete DDAI desktop map connector"
~~~
