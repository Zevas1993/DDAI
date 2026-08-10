# Dungeondraft Mod Consolidation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve Custom Snap and activate it beside DDAI in one safe per-user Dungeondraft Mods root.

**Architecture:** Extend the byte-preserving config editor to retain the decoded prior Mods path and require both mod IDs. Add an isolated, hash-verified filesystem consolidator that copies Custom Snap without owning or deleting it. Integrate both receipts into closed-app setup, diagnosis, and uninstall while keeping every running-app config/consolidation operation deferred.

**Tech Stack:** C# 13, .NET 9 `net9.0-windows`, xUnit 2.9.2, `System.Text.Json`, `System.Security.Cryptography`, existing single-file win-x64 CLI and MCP SDK integration tests.

## Global Constraints

- Certify Dungeondraft `1.2.0.1` on Windows first.
- Never close, restart, inject input into, patch, unpack, or redistribute Dungeondraft.
- Never read `config.ini` for activation, copy Custom Snap, or write `config.ini` while any `Dungeondraft` process is running.
- Never move, rename, edit, overwrite, or delete the original Custom Snap directory.
- Reject reparse points, links, traversal, ambiguous manifests, conflicting destinations, and concurrent source changes.
- Install DDAI only under `%LOCALAPPDATA%\DDAI\DungeondraftMods\DDAI` and copy Custom Snap only to the sibling `custom_snap` directory.
- Uninstall removes only proven DDAI-owned files; it leaves both the original and copied Custom Snap directories.
- Preserve unrelated dirty `.gitignore`, Task 3 report, `.claude/`, `AGENTS.md`, and `CLAUDE.md` changes.
- A fresh runtime heartbeat and correlated status response, not configuration presence, are the live acceptance proof.

---

### Task 1: Config Receipt and Both Required Mod IDs

**Files:**
- Modify: `src/DDAI.App/DungeondraftConfigEditor.cs`
- Modify: `src/DDAI.App/DungeondraftConfigTransaction.cs`
- Modify: `tests/DDAI.App.Tests/DungeondraftConfigEditorTests.cs`
- Modify: `tests/DDAI.App.Tests/DungeondraftConfigTransactionTests.cs`

**Interfaces:**
- Consumes: exact `config.ini` bytes, an absolute managed Mods directory, and required mod IDs.
- Produces: backward-compatible `DungeondraftConfigOwnership` with `PreviousModsDirectory`, plus a setup overload that activates both IDs exactly once.

- [ ] **Step 1: Write decoded-receipt and required-ID RED tests**

Add tests equivalent to:

```csharp
[Fact]
public void PlanSetup_RequiresCustomSnapAndDdaiExactlyOnceAndRecordsDecodedPriorPath()
{
    var original = Encoding.UTF8.GetBytes(
        "[Mods]\nactive_mods=[ \"Other.Mod\", \"Lievven.Snappy_Mod\", \"Lievven.Snappy_Mod\" ]\n" +
        "mods_directory=\"D:\\\\DungeonDraft\\\\Dungeondraft\\\\mods\\\\custom_snap\"\n");

    var edit = DungeondraftConfigEditor.PlanSetup(
        original,
        @"C:\Users\Chris\AppData\Local\DDAI\DungeondraftMods",
        [DungeondraftConfigEditor.CustomSnapModId, DungeondraftConfigEditor.DdaiModId]);

    var text = Encoding.UTF8.GetString(edit.ReplacementBytes);
    Assert.Contains("active_mods=[ \"Other.Mod\", \"Lievven.Snappy_Mod\", \"org.ddai.status_bridge\" ]", text);
    Assert.Equal(@"D:\DungeonDraft\Dungeondraft\mods\custom_snap", edit.Ownership.PreviousModsDirectory);
}
```

Also prove an empty `active_mods` array gains both IDs, non-required duplicates remain, repeated setup is byte-identical, duplicate requested IDs collapse, blank IDs reject, and old four-argument ownership construction still compiles.

- [ ] **Step 2: Run the focused tests and verify RED**

```powershell
dotnet test tests\DDAI.App.Tests\DDAI.App.Tests.csproj -c Release --filter "FullyQualifiedName~DungeondraftConfigEditorTests|FullyQualifiedName~DungeondraftConfigTransactionTests" --no-restore
```

Expected: compilation fails because `CustomSnapModId`, `PreviousModsDirectory`, and the required-ID overload do not exist.

- [ ] **Step 3: Extend the ownership record compatibly**

Use this signature so legacy JSON and existing test constructors remain valid:

```csharp
public sealed record DungeondraftConfigOwnership(
    string ManagedModsDirectory,
    bool PreviousModsDirectoryPresent,
    string? PreviousModsDirectoryLiteral,
    string ModId,
    string? PreviousModsDirectory = null);
```

Add `public const string CustomSnapModId = "Lievven.Snappy_Mod";`. Populate `PreviousModsDirectory` from the same strict string parser used for `mods_directory`; use `null` when the key was absent.

- [ ] **Step 4: Add the required-ID overload**

Keep the existing editor entry point delegating to DDAI-only behavior:

```csharp
public static DungeondraftConfigEdit PlanSetup(byte[] originalBytes, string managedModsDirectory) =>
    PlanSetup(originalBytes, managedModsDirectory, [DdaiModId]);

public static DungeondraftConfigEdit PlanSetup(
    byte[] originalBytes,
    string managedModsDirectory,
    IReadOnlyList<string> requiredModIds);
```

Validate that the list is non-null and every ID is nonblank. De-duplicate required IDs ordinally. Preserve every non-required value and its duplicates in original order; emit each required ID once, retaining its first existing position and appending missing IDs in requested order. Keep `ownership.ModId` equal to `DdaiModId` so uninstall remains backward compatible.

Add the matching transaction overload while retaining the existing signature:

```csharp
public DungeondraftConfigTransactionPlan PlanSetup(
    string configPath,
    string managedModsDirectory,
    IReadOnlyList<string> requiredModIds);
```

It must read the existing regular file once and call the editor's required-ID overload. Add a transaction test proving both IDs survive the durable apply and second-run idempotence path.

- [ ] **Step 5: Run focused and application tests and verify GREEN**

```powershell
dotnet test tests\DDAI.App.Tests\DDAI.App.Tests.csproj -c Release --filter "FullyQualifiedName~DungeondraftConfigEditorTests|FullyQualifiedName~DungeondraftConfigTransactionTests" --no-restore
dotnet test tests\DDAI.App.Tests\DDAI.App.Tests.csproj -c Release --no-restore
```

Expected: all editor and application tests pass with zero failures.

- [ ] **Step 6: Commit Task 1**

```powershell
git add -- src/DDAI.App/DungeondraftConfigEditor.cs src/DDAI.App/DungeondraftConfigTransaction.cs tests/DDAI.App.Tests/DungeondraftConfigEditorTests.cs tests/DDAI.App.Tests/DungeondraftConfigTransactionTests.cs
git commit -m "feat: preserve required Dungeondraft mods"
```

---

### Task 2: Hash-Verified Custom Snap Consolidator

**Files:**
- Create: `src/DDAI.App/DungeondraftModConsolidator.cs`
- Create: `tests/DDAI.App.Tests/DungeondraftModConsolidatorTests.cs`

**Interfaces:**
- Consumes: the decoded original Custom Snap directory and absolute managed Mods root.
- Produces: immutable plan, update, and receipt records plus `PlanCustomSnap`, `Apply`, and `IsCurrent`.

- [ ] **Step 1: Write exact-copy, idempotence, and rejection RED tests**

Use these public contracts in the tests:

```csharp
public sealed record DungeondraftModConsolidationReceipt(
    string SourceDirectory,
    string DestinationDirectory,
    string ModId);

public sealed record DungeondraftModFile(string RelativePath, long Length, string Sha256);

public sealed record DungeondraftModConsolidationPlan(
    string SourceDirectory,
    string DestinationDirectory,
    string ModId,
    IReadOnlyList<DungeondraftModFile> Files);

public sealed record DungeondraftModConsolidationUpdate(
    string State,
    bool Changed,
    DungeondraftModConsolidationReceipt? Receipt);
```

Write focused cases proving:

```csharp
var plan = consolidator.PlanCustomSnap(source, managedRoot);
var update = consolidator.Apply(plan);
Assert.Equal("copied", update.State);
Assert.Equal(SourceHashes(source), SourceHashes(Path.Combine(managedRoot, "custom_snap")));
Assert.Equal(sourceBytes, File.ReadAllBytes(sourceManifest));
Assert.Equal("already_current", consolidator.Apply(plan).State);
```

Add rejection cases for zero/two direct `.ddmod` files, a foreign `unique_id`, a source reparse point, a nested file/directory reparse point, destination outside the managed root, an existing differing destination, and a source file changed after planning. Assert no `.custom_snap.ddai-stage-*` directories remain.

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test tests\DDAI.App.Tests\DDAI.App.Tests.csproj -c Release --filter "FullyQualifiedName~DungeondraftModConsolidatorTests" --no-restore
```

Expected: compilation fails because the consolidator and records do not exist.

- [ ] **Step 3: Implement strict planning**

Create `DungeondraftModConsolidator` with:

```csharp
public DungeondraftModConsolidationPlan PlanCustomSnap(string sourceDirectory, string managedModsDirectory);
public DungeondraftModConsolidationUpdate Apply(DungeondraftModConsolidationPlan plan);
public bool IsCurrent(DungeondraftModConsolidationReceipt receipt);
```

Normalize absolute paths. Require source and managed root to differ. Reject `FileAttributes.ReparsePoint` on the source and every descendant. Require exactly one top-level `*.ddmod`, parse it as JSON, and require `unique_id == DungeondraftConfigEditor.CustomSnapModId`. Enumerate regular files only, reject any relative path escaping the source, and record sorted ordinal relative paths, lengths, and uppercase SHA-256 hashes. Destination is exactly `Path.Combine(managedRoot, "custom_snap")`; verify it remains beneath the normalized managed root.

- [ ] **Step 4: Implement staged copy and verification**

If destination exists, compare its strict file snapshot to the plan: identical returns `already_current`; otherwise throw `DungeondraftConfigException`. If absent, copy to `Path.Combine(managedRoot, $".custom_snap.ddai-stage-{Guid.NewGuid():N}")` using `FileMode.CreateNew`. Re-snapshot the source and stage after copying and require both to equal the plan before `Directory.Move(stage, destination)`. Clean the stage in `finally`. Convert I/O and access failures to `DungeondraftConfigException` without deleting source or destination.

`IsCurrent` re-plans from the receipt's source and returns true only when its destination equals the canonical managed sibling and the source/destination snapshots match; return false on validation or I/O failure.

- [ ] **Step 5: Run focused and application tests and verify GREEN**

```powershell
dotnet test tests\DDAI.App.Tests\DDAI.App.Tests.csproj -c Release --filter "FullyQualifiedName~DungeondraftModConsolidatorTests" --no-restore
dotnet test tests\DDAI.App.Tests\DDAI.App.Tests.csproj -c Release --no-restore
```

Expected: all consolidator and application tests pass with no orphan stages.

- [ ] **Step 6: Commit Task 2**

```powershell
git add -- src/DDAI.App/DungeondraftModConsolidator.cs tests/DDAI.App.Tests/DungeondraftModConsolidatorTests.cs
git commit -m "feat: consolidate Custom Snap safely"
```

---

### Task 3: Lifecycle Integration, Published Proof, and Safe Deployment

**Files:**
- Modify: `src/DDAI.App/LocalSetupService.cs`
- Modify: `tests/DDAI.App.Tests/SetupLifecycleTests.cs`
- Modify: `tests/DDAI.App.Tests/DungeondraftActivationPublishedTests.cs`
- Modify: `.superpowers/sdd/2026-08-09-ddai-windows-connector/task-4-report.md`

**Interfaces:**
- Consumes: Task 1's decoded config receipt and both-ID setup overload; Task 2's consolidation plan/update/receipt.
- Produces: receipt-aware setup and diagnosis, closed-app activation, user-preserving uninstall, published EXE evidence, and current-machine acceptance status.

- [ ] **Step 1: Write lifecycle integration RED tests**

Extend the lifecycle sandbox so the selected source directory directly contains the real Custom Snap manifest/scripts fixture. Add cases asserting:

```csharp
var running = new LocalSetupService(time, new StubProcessProbe(true)).Setup(paths);
Assert.Equal("deferred_dungeondraft_running", running.ModConsolidation.State);
Assert.False(Directory.Exists(Path.Combine(paths.ModsDirectory, "custom_snap")));
Assert.Equal(originalConfig, File.ReadAllBytes(paths.DungeondraftConfigPath));

var closed = new LocalSetupService(time, new StubProcessProbe(false)).Setup(paths);
Assert.Equal("copied", closed.ModConsolidation.State);
Assert.Contains("Lievven.Snappy_Mod", File.ReadAllText(paths.DungeondraftConfigPath));
Assert.Contains("org.ddai.status_bridge", File.ReadAllText(paths.DungeondraftConfigPath));
```

Also prove repeated setup preserves metadata bytes, a conflicting copied destination fails before config mutation, diagnosis returns `activation_pending_mod_consolidation` when the receipt copy is missing/different, running uninstall retains all files, and closed uninstall removes DDAI/restores the prior directory while leaving both Custom Snap copies.

- [ ] **Step 2: Run lifecycle tests and verify RED**

```powershell
dotnet test tests\DDAI.App.Tests\DDAI.App.Tests.csproj -c Release --filter "FullyQualifiedName~SetupLifecycleTests" --no-restore
```

Expected: compilation fails because lifecycle results and metadata do not expose `ModConsolidation`.

- [ ] **Step 3: Extend result and metadata contracts compatibly**

Add `DungeondraftModConsolidationUpdate ModConsolidation` to setup, diagnosis, and uninstall result records. Extend `InstallMetadata` with optional final parameter:

```csharp
IReadOnlyList<DungeondraftModConsolidationReceipt>? ConsolidatedMods = null
```

Legacy metadata without this property must deserialize and must never authorize deletion of copied content.

- [ ] **Step 4: Integrate closed-app planning and application**

When the process probe reports running, return a consolidation update with state `deferred_dungeondraft_running` and preserve the current no-config-read/write behavior.

When closed, read existing owned metadata, plan config activation using:

```csharp
DungeondraftConfigEditor.PlanSetup(
    configBytes,
    paths.ModsDirectory,
    [DungeondraftConfigEditor.CustomSnapModId, DungeondraftConfigEditor.DdaiModId]);
```

Choose the consolidation source from the preserved first metadata receipt when present; otherwise require `configPlan.Ownership.PreviousModsDirectory`. Plan both config and consolidation before connector/mod/client mutation. Apply consolidation before the config transaction, then persist the first config and consolidation receipts together. On metadata failure, restore `config.ini`; leave the verified Custom Snap copy because it is explicitly user-preserving content.

- [ ] **Step 5: Integrate diagnosis and uninstall**

After owned connector/mod checks and before configured-waiting status, validate the consolidation receipt and destination. Return `activation_pending_mod_consolidation` when no valid receipt/current copy exists. Preserve fresh-heartbeat precedence.

Running uninstall returns before any mutation. Closed uninstall uses only the config receipt, removes only the DDAI directory/owned executable/client entries, restores the prior Mods path when still owned, and reports the consolidation copy as `retained_user_content` without deleting it.

- [ ] **Step 6: Strengthen the published activation test**

In `DungeondraftActivationPublishedTests`, populate the sandbox's original selected directory with a Custom Snap fixture. If a real Dungeondraft process is open, assert setup exit `2`, no consolidation copy, and byte-exact config. Otherwise assert exit `0`, exact config backup, both IDs once, exact source/destination Custom Snap hashes, unrelated Claude/Gemini entries preserved, and second-run byte idempotence. Re-run the official SDK initialize/list/call test in the focused gate.

- [ ] **Step 7: Run final static verification**

```powershell
dotnet test DDAI.slnx -c Release --no-restore
dotnet build DDAI.slnx -c Release --no-restore
dotnet publish src\DDAI.App\DDAI.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o artifacts\dungeondraft-mod-consolidation\win-x64
git diff --check
npx gitnexus analyze
npx gitnexus detect-changes --repo DDAI --scope staged
```

Expected: all tests pass, build has zero warnings/errors, publish contains one `ddai.exe`, GitNexus identifies only intended lifecycle flows after scoped staging, and unrelated dirty files remain unstaged.

- [ ] **Step 8: Update the report and commit Task 3**

Record exact RED/GREEN counts, artifact size/hash, staged impact, installed hashes, current process state, config before/after hashes, backup path/hash, both active IDs, heartbeat slot, and correlated status request ID. Do not claim the final live proof until those runtime artifacts exist.

```powershell
git add -- src/DDAI.App/LocalSetupService.cs tests/DDAI.App.Tests/SetupLifecycleTests.cs tests/DDAI.App.Tests/DungeondraftActivationPublishedTests.cs .superpowers/sdd/2026-08-09-ddai-windows-connector/task-4-report.md
git commit -m "feat: preserve Custom Snap during activation"
```

- [ ] **Step 9: Run current-machine acceptance without controlling Dungeondraft**

While Dungeondraft is open, install the fresh artifact and assert `deferred_dungeondraft_running`, no Custom Snap copy, and byte-identical config. Ask the user to close Dungeondraft normally. Then run setup once, verify the exact backup and both mod folders/IDs, and ask the user to launch Dungeondraft normally. Finally require:

```powershell
& "$env:LOCALAPPDATA\DDAI\ddai.exe" diagnose --json
& "$env:LOCALAPPDATA\DDAI\ddai.exe" status --json --timeout-ms 5000
```

Expected: `runtime_heartbeat_fresh`, status exit `0`, a correlated response, and Custom Snap still present in both its original and copied locations. If the user has not yet closed/relaunched Dungeondraft, report that exact external acceptance step without weakening the static or published evidence.
