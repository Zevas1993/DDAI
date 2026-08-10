# DDAI Minimal Runtime Bridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Dungeondraft 1.2.0.1 run crash-free with DDAI as its sole active mod and prove a real `ddai_status` call through the installed MCP executable, atomic mailbox, and live bridge.

**Architecture:** Keep `ddai.exe` as the validation and MCP control plane, and reduce the loaded GDScript bridge to bounded mailbox execution without optional Dictionary enumeration. Decouple setup and diagnosis from Custom Snap so the live certification configuration activates only DDAI while preserving all third-party files and historical ownership records.

**Tech Stack:** .NET 9, C# 13, xUnit 2.9.2, official `ModelContextProtocol` 1.4.1 SDK, Godot 3.x-compatible GDScript, Windows PowerShell, atomic JSON mailbox.

## Global Constraints

- Target Windows 10/11 x64 and Dungeondraft `1.2.0.1`; setup must not require administrator rights.
- Never edit, unpack, patch, or redistribute Dungeondraft binaries, maps, PCK data, or purchased assets.
- Never start, close, restart, or control Dungeondraft automatically; configuration and installed-mod writes occur only after the user closes it normally.
- Preserve unrelated dirty work: `.gitignore`, `.superpowers/sdd/2026-08-09-ddai-windows-connector/task-3-report.md`, `.claude/`, `AGENTS.md`, and `CLAUDE.md` are outside this plan.
- Preserve the existing 1 MiB mailbox cap, strict timestamp language, request/response correlation, durable journal transitions, traversal defenses, and failure retention.
- Production GDScript must not invoke zero-argument Dictionary built-ins. Keyed `Dictionary.has(key)` is allowed only on the already-certified request, claim, error, response, and journal paths.
- Custom Snap files remain user content and are not deleted or overwritten. Custom Snap is not required, copied, activated, or diagnosed by the stable DDAI path.
- No static or fake-peer test may be reported as live Dungeondraft proof.

---

### Task 1: Remove the confirmed GDScript crash surface

**Files:**
- Modify: `mods/DDAI/scripts/ddai_bridge.gd:46-50,77-80,288-299,422-450`
- Modify: `tests/DDAI.Core.Tests/DungeondraftModPackageTests.cs:69-87`

**Interfaces:**
- Consumes: `_claim_next_request() -> Dictionary`, `_validate_request(request, file_name) -> Dictionary`, `_status_payload() -> Dictionary`.
- Produces: `status.payload.active_mods = []`, `status.payload.active_mods_available = false`, with no call to `Global.ActiveMods` or `Dictionary.keys()`.

- [ ] **Step 1: Strengthen the failing runtime-compatibility test**

Replace the current compatibility test body with assertions scoped to known Dictionary receivers so array, string, and byte-array `.size()` calls remain legal:

```csharp
[Fact]
public void Gdscript_AvoidsRuntimeCallsProvenToCrashDungeondraft1201()
{
    var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "mods", "DDAI", "scripts", "ddai_bridge.gd"));
    var processBody = FunctionBody(script, "_process_one_request");
    var advanceBody = FunctionBody(script, "_advance_claim_state");
    var reconcileBody = FunctionBody(script, "_reconcile_request_duplicate");
    var activeModsBody = FunctionBody(script, "_active_mods_payload");

    Assert.DoesNotContain("claim.empty()", processBody, StringComparison.Ordinal);
    Assert.DoesNotContain("claim.size()", processBody, StringComparison.Ordinal);
    Assert.Contains("if not claim.has(\"path\"):", processBody, StringComparison.Ordinal);
    Assert.DoesNotContain("validation_error.empty()", advanceBody, StringComparison.Ordinal);
    Assert.DoesNotContain("validation_error.size()", advanceBody, StringComparison.Ordinal);
    Assert.Contains("if validation_error.has(\"code\"):", advanceBody, StringComparison.Ordinal);
    Assert.DoesNotContain("_validate_request(duplicate, file_name).empty()", reconcileBody, StringComparison.Ordinal);
    Assert.DoesNotContain("_validate_request(duplicate, file_name).size()", reconcileBody, StringComparison.Ordinal);
    Assert.Contains("not _validate_request(duplicate, file_name).has(\"code\")", reconcileBody, StringComparison.Ordinal);
    Assert.DoesNotContain("mods.keys()", activeModsBody, StringComparison.Ordinal);
    Assert.DoesNotContain("Global", activeModsBody, StringComparison.Ordinal);
    Assert.Contains("return {\"available\": false, \"values\": []}", activeModsBody, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Gdscript_AvoidsRuntimeCallsProvenToCrashDungeondraft1201"
```

Expected: FAIL because `_active_mods_payload()` still contains `mods.keys()` and reads `Global`.

- [ ] **Step 3: Implement the minimal status behavior**

Keep the already-tested keyed checks in the three request paths and replace `_active_mods_payload()` with:

```gdscript
func _active_mods_payload():
	return {"available": false, "values": []}
```

Do not replace `mods.keys()` with another Dictionary method or iteration. Do not change array/string/byte-array `.size()` calls.

- [ ] **Step 4: Run focused and core verification**

Run:

```powershell
dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DungeondraftModPackageTests"
dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --no-restore
```

Expected: all package tests pass, then all 225+ core tests pass with zero failures and warnings.

- [ ] **Step 5: Commit only the bridge and its contract test**

```powershell
git add -- mods/DDAI/scripts/ddai_bridge.gd tests/DDAI.Core.Tests/DungeondraftModPackageTests.cs
git diff --cached --check
git commit -m "fix: minimize Dungeondraft runtime introspection"
```

---

### Task 2: Remove Custom Snap from required setup and diagnosis

**Files:**
- Modify: `src/DDAI.App/LocalSetupService.cs:106-209,211-325,690-741`
- Modify: `tests/DDAI.App.Tests/SetupLifecycleTests.cs:30-73,157-192,216-270,389-438`
- Modify: `tests/DDAI.App.Tests/DungeondraftActivationPublishedTests.cs:13-64`
- Modify: `tests/DDAI.App.Tests/PublishedConfigOwnershipTests.cs`
- Test unchanged behavior: `tests/DDAI.App.Tests/DungeondraftConfigEditorTests.cs`
- Retain as unused legacy utility: `src/DDAI.App/DungeondraftModConsolidator.cs`
- Retain its isolated tests: `tests/DDAI.App.Tests/DungeondraftModConsolidatorTests.cs`

**Interfaces:**
- Consumes: `DungeondraftConfigTransaction.PlanSetup(configPath, modsDirectory)`, whose default required ID is `org.ddai.status_bridge` and which preserves unrelated existing active IDs.
- Produces: setup/diagnose results with `mod_consolidation.state` equal to `not_required` for fresh installations or `retained_user_content` for a historical Custom Snap receipt; no Custom Snap copy is planned or applied.

- [ ] **Step 1: Write lifecycle tests for DDAI-only setup**

Change the closed-app lifecycle test to start from an empty active-mod list and assert only DDAI is injected:

```csharp
[Fact]
public void Setup_ClosedActivatesDdaiWithoutRequiringOrCopyingCustomSnap()
{
    using var sandbox = new LifecycleSandbox();
    File.WriteAllText(sandbox.ClaudePath, "{}");
    File.WriteAllText(sandbox.GeminiPath, "{}");
    File.WriteAllText(
        sandbox.ConfigPath,
        "[Mods]\nactive_mods=[ ]\nmods_directory=\"D:\\\\UnrelatedMods\"\n");
    var service = new LocalSetupService(new AdvancingTimeProvider(), new StubProcessProbe(false));

    var first = service.Setup(sandbox.Paths);
    var configured = File.ReadAllText(sandbox.ConfigPath);
    var second = service.Setup(sandbox.Paths);

    Assert.Contains("active_mods=[ \"org.ddai.status_bridge\" ]", configured, StringComparison.Ordinal);
    Assert.DoesNotContain("Lievven.Snappy_Mod", configured, StringComparison.Ordinal);
    Assert.False(Directory.Exists(sandbox.CopiedCustomSnapDirectory));
    Assert.Equal("not_required", first.ModConsolidation.State);
    Assert.Equal("already_current", second.DungeondraftConfig.State);
    Assert.Equal("not_required", second.ModConsolidation.State);
}
```

Replace the old blocking and diagnosis expectations with:

```csharp
[Fact]
public void Setup_ForeignInactiveCustomSnapDirectoryIsPreservedAndDoesNotBlockDdai()
{
    using var sandbox = new LifecycleSandbox();
    File.WriteAllText(sandbox.ClaudePath, "{}");
    File.WriteAllText(sandbox.GeminiPath, "{}");
    Directory.CreateDirectory(sandbox.CopiedCustomSnapDirectory);
    var sentinel = Path.Combine(sandbox.CopiedCustomSnapDirectory, "foreign.txt");
    File.WriteAllText(sentinel, "keep");

    var result = new LocalSetupService(new FixedTimeProvider(), new StubProcessProbe(false)).Setup(sandbox.Paths);

    Assert.Equal("keep", File.ReadAllText(sentinel));
    Assert.True(Directory.Exists(sandbox.Paths.InstalledModDirectory));
    Assert.Equal("not_required", result.ModConsolidation.State);
}

[Fact]
public void Diagnose_DoesNotRequireHistoricalCustomSnapCopy()
{
    using var sandbox = new LifecycleSandbox();
    File.WriteAllText(sandbox.ClaudePath, "{}");
    File.WriteAllText(sandbox.GeminiPath, "{}");
    var service = new LocalSetupService(new FixedTimeProvider(), new StubProcessProbe(false));
    _ = service.Setup(sandbox.Paths);

    var result = service.Diagnose(sandbox.Paths);

    Assert.Equal("configured", result.State);
    Assert.Equal("configured_waiting_for_reload", result.Code);
    Assert.Equal("not_required", result.ModConsolidation.State);
}
```

Update the running-app, uninstall, published-activation, and ownership fixtures so they assert Custom Snap is preserved if present but never newly copied or required. Keep tests that prove unrelated active-mod IDs, comments, encodings, and duplicate non-DDAI IDs are preserved by `DungeondraftConfigEditor`.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SetupLifecycleTests|FullyQualifiedName~DungeondraftActivationPublishedTests|FullyQualifiedName~PublishedConfigOwnershipTests"
```

Expected: failures show setup still plans/copies Custom Snap, injects `Lievven.Snappy_Mod`, and diagnosis still requires a current consolidation receipt.

- [ ] **Step 3: Make setup DDAI-only while preserving historical receipts**

In `LocalSetupService.Setup`:

```csharp
var previousMetadata = TryReadOwnedMetadata(paths.MetadataPath, paths.InstalledExecutable);
var previousConfigReceipt = previousMetadata?.DungeondraftConfig;
var historicalCustomSnapReceipt = GetCustomSnapReceipt(previousMetadata);
var transaction = new DungeondraftConfigTransaction(timeProvider);
DungeondraftConfigTransactionPlan? configPlan = null;
var consolidationUpdate = ConsolidationUpdate(
    historicalCustomSnapReceipt is null ? "not_required" : "retained_user_content",
    historicalCustomSnapReceipt);
```

For a closed app with an existing config, use only the default required ID:

```csharp
configPlan = transaction.PlanSetup(paths.DungeondraftConfigPath, paths.ModsDirectory);
configUpdate = ConfigUpdate("planned", paths, configPlan.Ownership);
```

Delete setup's `DungeondraftModConsolidator`, `consolidationPlan`, source discovery, `PlanCustomSnap`, and `Apply` calls. When persisting a new config ownership receipt, retain `previousMetadata?.ConsolidatedMods` byte-semantically in metadata rather than creating or verifying a new receipt:

```csharp
if (previousMetadata?.DungeondraftConfig != configReceipt)
{
    WriteInstallMetadata(paths, configReceipt, previousMetadata?.ConsolidatedMods);
}
```

Do not delete `DungeondraftModConsolidator`; it remains an isolated legacy migration utility until a later cleanup plan can prove metadata compatibility.

- [ ] **Step 4: Make diagnosis require only DDAI**

Delete the `IsCurrent` consolidation gate from `Diagnose`. Build the informational result from historical metadata:

```csharp
var metadata = TryReadOwnedMetadata(paths.MetadataPath, paths.InstalledExecutable);
var historicalCustomSnapReceipt = GetCustomSnapReceipt(metadata);
var consolidationUpdate = ConsolidationUpdate(
    historicalCustomSnapReceipt is null ? "not_required" : "retained_user_content",
    historicalCustomSnapReceipt);
```

Call the default config predicate:

```csharp
configured = DungeondraftConfigEditor.IsConfigured(
    File.ReadAllBytes(paths.DungeondraftConfigPath),
    paths.ModsDirectory);
```

Return `consolidationUpdate` in the mismatch and configured results. A missing or modified Custom Snap directory must not change diagnosis state.

- [ ] **Step 5: Run focused and full application tests**

Run:

```powershell
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SetupLifecycleTests|FullyQualifiedName~DungeondraftActivationPublishedTests|FullyQualifiedName~PublishedConfigOwnershipTests"
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --no-restore
```

Expected: all focused tests pass, followed by all application tests with no failure or warning. The isolated `DungeondraftModConsolidatorTests` continue to pass even though ordinary setup no longer calls that utility.

- [ ] **Step 6: Commit the DDAI-only lifecycle change**

```powershell
git add -- src/DDAI.App/LocalSetupService.cs tests/DDAI.App.Tests/SetupLifecycleTests.cs tests/DDAI.App.Tests/DungeondraftActivationPublishedTests.cs tests/DDAI.App.Tests/PublishedConfigOwnershipTests.cs
git diff --cached --check
git commit -m "fix: make DDAI activation independent of Custom Snap"
```

---

### Task 3: Add a reusable official-SDK live MCP probe

**Files:**
- Create: `tools/DDAI.McpProbe/DDAI.McpProbe.csproj`
- Create: `tools/DDAI.McpProbe/McpProbeOptions.cs`
- Create: `tools/DDAI.McpProbe/McpStatusProbe.cs`
- Create: `tools/DDAI.McpProbe/Program.cs`
- Modify: `DDAI.slnx`
- Modify: `tests/DDAI.App.Tests/DDAI.App.Tests.csproj`
- Create: `tests/DDAI.App.Tests/McpProbeTests.cs`

**Interfaces:**
- Produces: `McpProbeOptions.Parse(string[] args) -> McpProbeOptions` with `ExecutablePath`, `MailboxRoot`, and `Timeout`.
- Produces: `McpStatusProbe.RunAsync(McpProbeOptions, CancellationToken) -> Task<string>` returning the exact text block from `ddai_status`.
- CLI: `dotnet run --project tools/DDAI.McpProbe -- --exe <absolute-ddai.exe> --mailbox-root <absolute-user-ddai> --timeout-ms 5000`.

- [ ] **Step 1: Add project wiring without implementation types**

Create `tools/DDAI.McpProbe/DDAI.McpProbe.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0-windows</TargetFramework>
    <RootNamespace>DDAI.McpProbe</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ModelContextProtocol" Version="1.4.1" />
  </ItemGroup>
</Project>
```

Add the project to `DDAI.slnx` and add a `ProjectReference` from `tests/DDAI.App.Tests/DDAI.App.Tests.csproj` to the probe project.

- [ ] **Step 2: Write failing parser and real-SDK tests**

Create `tests/DDAI.App.Tests/McpProbeTests.cs`. The parser test uses literal expected paths and milliseconds. The SDK test reuses `PublishedExecutableFixture`, a real `AtomicMailbox`, and `FakeModHarness`, then calls `McpStatusProbe.RunAsync` and asserts `success: true` and payload state `ready`. It must not mock the MCP client, stdio transport, mailbox, or published executable.

Core assertions:

```csharp
var options = McpProbeOptions.Parse([
    "--exe", executable,
    "--mailbox-root", mailbox,
    "--timeout-ms", "2500",
]);
Assert.Equal(Path.GetFullPath(executable), options.ExecutablePath);
Assert.Equal(Path.GetFullPath(mailbox), options.MailboxRoot);
Assert.Equal(TimeSpan.FromMilliseconds(2500), options.Timeout);

var text = await new McpStatusProbe().RunAsync(options, deadline.Token);
using var document = JsonDocument.Parse(text);
Assert.True(document.RootElement.GetProperty("success").GetBoolean());
Assert.Equal("ready", document.RootElement.GetProperty("payload").GetProperty("state").GetString());
```

- [ ] **Step 3: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --filter "FullyQualifiedName~McpProbeTests"
```

Expected: compile failure because `McpProbeOptions` and `McpStatusProbe` do not exist.

- [ ] **Step 4: Implement strict options and official SDK transport**

`McpProbeOptions.Parse` accepts each required option exactly once, rejects unknown/duplicate/missing values, requires absolute regular-file `--exe`, normalizes an absolute mailbox root, and requires a positive integer timeout.

`McpStatusProbe.RunAsync` creates:

```csharp
using System.Globalization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "ddai-live-probe",
    Command = options.ExecutablePath,
    Arguments = [
        "serve", "--stdio",
        "--mailbox-root", options.MailboxRoot,
        "--timeout-ms", ((int)options.Timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
    ],
    WorkingDirectory = Path.GetDirectoryName(options.ExecutablePath)!,
    ShutdownTimeout = TimeSpan.FromSeconds(5),
    StandardErrorLines = line => Console.Error.WriteLine(line),
});
await using var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
if (!tools.Any(tool => tool.Name == "ddai_status"))
{
    throw new InvalidOperationException("Installed DDAI MCP server did not list ddai_status.");
}
var result = await client.CallToolAsync(
    "ddai_status",
    new Dictionary<string, object?>(),
    cancellationToken: cancellationToken);
if (result.Content.Count != 1 || result.Content[0] is not TextContentBlock textBlock)
{
    throw new InvalidOperationException(
        "Installed DDAI MCP server did not return exactly one text content block.");
}

return textBlock.Text;
```

`Program.cs` uses the following protocol-clean entry point:

```csharp
namespace DDAI.McpProbe;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = McpProbeOptions.Parse(args);
            using var timeout = new CancellationTokenSource(options.Timeout);
            var text = await new McpStatusProbe().RunAsync(options, timeout.Token);
            await Console.Out.WriteLineAsync(text);
            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or InvalidOperationException or OperationCanceledException)
        {
            await Console.Error.WriteLineAsync($"DDAI MCP probe failed: {exception.Message}");
            return 1;
        }
    }
}
```

`McpProbeOptions.Parse` must use an ordinal option-name switch and a `HashSet<string>` to reject duplicate names before consuming values. After parsing, validate with these exact checks:

```csharp
if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable))
    throw new ArgumentException("--exe must be an absolute path.");
executable = Path.GetFullPath(executable);
if (!File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.Directory) != 0)
    throw new ArgumentException("--exe must identify an existing regular file.");
if (string.IsNullOrWhiteSpace(mailboxRoot) || !Path.IsPathFullyQualified(mailboxRoot))
    throw new ArgumentException("--mailbox-root must be an absolute path.");
if (!int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out var timeoutMs) || timeoutMs <= 0)
    throw new ArgumentException("--timeout-ms must be a positive integer.");
return new McpProbeOptions(executable, Path.GetFullPath(mailboxRoot), TimeSpan.FromMilliseconds(timeoutMs));
```

- [ ] **Step 5: Verify the probe and full solution**

Run:

```powershell
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --filter "FullyQualifiedName~McpProbeTests"
dotnet test DDAI.slnx -c Release --no-restore
dotnet build DDAI.slnx -c Release --no-restore
```

Expected: probe tests pass, the full suite passes, and the Release build reports zero warnings and zero errors.

- [ ] **Step 6: Commit the live probe**

```powershell
git add -- DDAI.slnx tools/DDAI.McpProbe tests/DDAI.App.Tests/DDAI.App.Tests.csproj tests/DDAI.App.Tests/McpProbeTests.cs
git diff --cached --check
git commit -m "test: add live MCP status probe"
```

---

### Task 4: Publish, install, and prove the live bridge

**Files:**
- Create: `docs/superpowers/reports/2026-08-10-ddai-minimal-runtime-bridge-report.md`
- Produce untracked build artifact: `artifacts/minimal-runtime-bridge/win-x64/ddai.exe`
- Mutate only after user closes Dungeondraft: `%LOCALAPPDATA%\DDAI\ddai.exe`, `%LOCALAPPDATA%\DDAI\DungeondraftMods\DDAI`, `%APPDATA%\Dungeondraft\config.ini`, owned Claude/Gemini `mcpServers.ddai` entries.

**Interfaces:**
- Consumes: published `ddai.exe`, repository `mods/DDAI`, current per-user Dungeondraft config, and the real mailbox at `%APPDATA%\Dungeondraft\ddai`.
- Produces: fresh heartbeat, recovered stranded claim, correlated CLI status response, correlated official-SDK MCP response, and a live report with exact hashes/timestamps/exit codes.

- [ ] **Step 1: Run the final offline gate while Dungeondraft remains open and untouched**

Run:

```powershell
dotnet test DDAI.slnx -c Release --no-restore
dotnet build DDAI.slnx -c Release --no-restore
dotnet publish src/DDAI.App/DDAI.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false --no-restore -o artifacts/minimal-runtime-bridge/win-x64
Get-FileHash -Algorithm SHA256 artifacts/minimal-runtime-bridge/win-x64/ddai.exe
```

Expected: all tests pass, build has zero warnings/errors, publish contains one `ddai.exe`, and its SHA-256 is recorded.

- [ ] **Step 2: Ask the user to close Dungeondraft normally**

Do not continue until `Get-Process -Name Dungeondraft -ErrorAction SilentlyContinue` returns no process. Do not terminate it from automation.

- [ ] **Step 3: Run owned setup with explicit source paths**

Run the published executable with:

```powershell
artifacts/minimal-runtime-bridge/win-x64/ddai.exe setup `
  --source-exe artifacts/minimal-runtime-bridge/win-x64/ddai.exe `
  --source-mod-directory mods/DDAI `
  --install-root "$env:LOCALAPPDATA\DDAI" `
  --mods-directory "$env:LOCALAPPDATA\DDAI\DungeondraftMods" `
  --dungeondraft-user-data "$env:APPDATA\Dungeondraft"
```

Expected: exit `0`; installed executable and GDScript hashes equal their sources; config contains exactly one `org.ddai.status_bridge`; setup does not add `Lievven.Snappy_Mod`; the existing Custom Snap files retain their pre-setup hashes; unrelated Claude/Gemini MCP nodes compare byte-semantically with their backups.

- [ ] **Step 4: Ask the user to launch Dungeondraft normally and keep the blank map open**

Do not drive the Dungeondraft UI. Record the new process ID and start time, then poll for a fresh heartbeat rather than sleeping blindly. Require the process to remain alive through the status and MCP probes.

- [ ] **Step 5: Prove diagnosis and direct installed status**

Run:

```powershell
& "$env:LOCALAPPDATA\DDAI\ddai.exe" diagnose --json
& "$env:LOCALAPPDATA\DDAI\ddai.exe" status --json --timeout-ms 5000
```

Expected: diagnosis exit `0` with `runtime_heartbeat_fresh`; status exit `0`, `success: true`, a correlated request ID/command, `active_mods_available: false`, and no new crash dump or Application Error event. Verify the previously stranded processing request is either completed by a correlated response or retained according to the durable state machine; never delete it manually.

- [ ] **Step 6: Prove the installed server through the official MCP SDK**

Run:

```powershell
dotnet run --project tools/DDAI.McpProbe -c Release --no-build -- `
  --exe "$env:LOCALAPPDATA\DDAI\ddai.exe" `
  --mailbox-root "$env:APPDATA\Dungeondraft\ddai" `
  --timeout-ms 5000
```

Expected: exit `0`; stdout is one JSON tool result with `success: true`; stderr contains no protocol contamination; Dungeondraft remains running.

- [ ] **Step 7: Write the evidence report**

Record exact test counts, build warnings/errors, artifact and installed hashes, config active IDs, preserved third-party hashes, Dungeondraft PID/start time, heartbeat/receipt timestamps, mailbox transition results, CLI JSON/exit codes, MCP probe JSON/exit code, crash-dump directory delta, and remaining gap: native rectangular-room map application is not part of this stabilization plan.

- [ ] **Step 8: Commit only the live report**

```powershell
git add -- docs/superpowers/reports/2026-08-10-ddai-minimal-runtime-bridge-report.md
git diff --cached --check
git commit -m "docs: record live DDAI bridge acceptance"
```

After this commit, begin a separate Superpowers design/plan cycle for the rectangular-room `ddai_apply_plan` slice. That follow-on must inspect and select documented native Dungeondraft map APIs before any map-mutation code is written.
