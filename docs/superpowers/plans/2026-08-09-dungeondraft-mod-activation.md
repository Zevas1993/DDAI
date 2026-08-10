# Dungeondraft Mod Activation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `ddai setup` safely and transactionally activate the DDAI bridge in Dungeondraft 1.2.0.1 while preserving Custom Snap and every unrelated setting.

**Architecture:** A pure byte-preserving editor owns the two supported `[Mods]` values, an atomic transaction layer owns staging/backups/concurrency checks, and `LocalSetupService` coordinates process detection, ownership metadata, diagnosis, setup, and uninstall. Dungeondraft is never closed or restarted by DDAI; configuration writes occur only while no Dungeondraft process is running.

**Tech Stack:** C# 13, .NET 9 `net9.0-windows`, xUnit 2.9.2, `System.Text`, `System.Security.Cryptography`, existing self-contained win-x64 CLI and MCP integration tests.

## Global Constraints

- Certify Dungeondraft `1.2.0.1` on Windows first.
- Never close, restart, inject input into, patch, unpack, or redistribute Dungeondraft.
- Never modify `config.ini` while any `Dungeondraft` process is running.
- Modify only `[Mods].mods_directory` and `[Mods].active_mods`.
- Preserve `Lievven.Snappy_Mod`, all unrelated mod IDs, sections, keys, comments, encoding, BOM, and newline convention.
- Add `org.ddai.status_bridge` exactly once and install the bridge only under the DDAI-managed custom Mods root.
- Unsupported or ambiguous syntax, concurrent changes, unproven ownership, and write failures fail closed.
- A fresh runtime heartbeat, not configuration presence, is the only proof that the running application loaded the bridge.
- Preserve unrelated dirty `.gitignore`, Task 3 report, `.claude/`, `AGENTS.md`, and `CLAUDE.md` changes.

---

### Task 1: Byte-Preserving Dungeondraft Mods Editor

**Files:**
- Create: `src/DDAI.App/DungeondraftConfigEditor.cs`
- Create: `tests/DDAI.App.Tests/DungeondraftConfigEditorTests.cs`

**Interfaces:**
- Consumes: exact `config.ini` bytes and an absolute managed Mods directory.
- Produces: `DungeondraftConfigOwnership`, `DungeondraftConfigEdit`, `DungeondraftConfigEditor.PlanSetup`, `PlanUninstall`, and `IsConfigured`.

- [ ] **Step 1: Write preservation and idempotence RED tests**

```csharp
[Fact]
public void PlanSetup_PreservesCustomSnapCommentsUtf8BomAndCrLf()
{
    var text = "; keep\r\n[Display]\r\nwindow_width=1920\r\n\r\n[Mods]\r\nactive_mods=[ \"Lievven.Snappy_Mod\", \"Other.Mod\" ]\r\nmods_directory=\"D:\\\\OldMods\"\r\n";
    var original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();

    var edit = DungeondraftConfigEditor.PlanSetup(original, @"C:\Users\Chris\AppData\Local\DDAI\DungeondraftMods");
    var result = Encoding.UTF8.GetString(edit.ReplacementBytes.AsSpan(Encoding.UTF8.GetPreamble().Length));

    Assert.True(edit.ReplacementBytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
    Assert.Contains("; keep\r\n[Display]\r\nwindow_width=1920", result);
    Assert.Contains("active_mods=[ \"Lievven.Snappy_Mod\", \"Other.Mod\", \"org.ddai.status_bridge\" ]\r\n", result);
    Assert.Contains("mods_directory=\"C:\\\\Users\\\\Chris\\\\AppData\\\\Local\\\\DDAI\\\\DungeondraftMods\"\r\n", result);
    Assert.True(DungeondraftConfigEditor.IsConfigured(edit.ReplacementBytes, @"C:\Users\Chris\AppData\Local\DDAI\DungeondraftMods"));
    Assert.False(DungeondraftConfigEditor.PlanSetup(edit.ReplacementBytes, @"C:\Users\Chris\AppData\Local\DDAI\DungeondraftMods").Changed);
}

[Fact]
public void PlanSetup_InsertsMissingModsSectionWithoutChangingLfConvention()
{
    var original = Encoding.UTF8.GetBytes("[Display]\nwindow_width=1920\n");
    var edit = DungeondraftConfigEditor.PlanSetup(original, @"C:\DDAI\Mods");
    Assert.EndsWith("\n[Mods]\nactive_mods=[ \"org.ddai.status_bridge\" ]\nmods_directory=\"C:\\\\DDAI\\\\Mods\"\n", Encoding.UTF8.GetString(edit.ReplacementBytes));
}
```

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --filter "FullyQualifiedName~DungeondraftConfigEditorTests" --no-restore
```

Expected: compilation fails because `DungeondraftConfigEditor` and its records do not exist.

- [ ] **Step 3: Add the edit and ownership records**

```csharp
public sealed record DungeondraftConfigOwnership(
    string ManagedModsDirectory,
    bool PreviousModsDirectoryPresent,
    string? PreviousModsDirectoryLiteral,
    string ModId);

public sealed record DungeondraftConfigEdit(
    byte[] OriginalBytes,
    byte[] ReplacementBytes,
    DungeondraftConfigOwnership Ownership)
{
    public bool Changed => !OriginalBytes.AsSpan().SequenceEqual(ReplacementBytes);
}

public sealed class DungeondraftConfigException(string message, Exception? inner = null) : Exception(message, inner);
```

- [ ] **Step 4: Implement strict parsing and byte-preserving replacement**

Create these exact entry points:

```csharp
public static class DungeondraftConfigEditor
{
    public const string DdaiModId = "org.ddai.status_bridge";

    public static DungeondraftConfigEdit PlanSetup(byte[] originalBytes, string managedModsDirectory);
    public static DungeondraftConfigEdit PlanUninstall(byte[] originalBytes, DungeondraftConfigOwnership ownership);
    public static bool IsConfigured(byte[] bytes, string managedModsDirectory);
}
```

Decode only strict UTF-8 with or without BOM, UTF-16 LE with BOM, and UTF-16 BE with BOM; re-encode with the same encoding and BOM. Detect one newline convention. Reject mixed newlines, multiple `[Mods]` sections, duplicate owned keys, invalid encodings, NULs, unterminated strings, invalid escapes, non-string array items, and trailing array data.

Use Godot string escaping:

```csharp
private static string EncodeGodotString(string value) =>
    "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
```

Preserve all text outside the two owned value spans. Preserve non-DDAI duplicate IDs; collapse only duplicate `org.ddai.status_bridge` values.

- [ ] **Step 5: Add rejection and uninstall tests**

```csharp
[Theory]
[InlineData("[Mods]\nactive_mods=[ bare ]\nmods_directory=\"C:\\\\Mods\"\n")]
[InlineData("[Mods]\nactive_mods=[ \"A\" ]\nactive_mods=[ \"B\" ]\nmods_directory=\"C:\\\\Mods\"\n")]
[InlineData("[Mods]\nmods_directory=\"C:\\\\Mods\"\n[Mods]\nactive_mods=[ ]\n")]
public void PlanSetup_RejectsAmbiguousOwnedSyntax(string text) =>
    Assert.Throws<DungeondraftConfigException>(() =>
        DungeondraftConfigEditor.PlanSetup(Encoding.UTF8.GetBytes(text), @"C:\DDAI\Mods"));

[Fact]
public void PlanUninstall_RemovesOnlyDdaiAndRestoresProvenPriorDirectory()
{
    var original = Encoding.UTF8.GetBytes("[Mods]\nactive_mods=[ \"Lievven.Snappy_Mod\", \"org.ddai.status_bridge\", \"Other.Mod\" ]\nmods_directory=\"C:\\\\DDAI\\\\Mods\"\n");
    var ownership = new DungeondraftConfigOwnership(@"C:\DDAI\Mods", true, "\"D:\\\\PreviousMods\"", DungeondraftConfigEditor.DdaiModId);
    var edit = DungeondraftConfigEditor.PlanUninstall(original, ownership);
    var text = Encoding.UTF8.GetString(edit.ReplacementBytes);
    Assert.Contains("active_mods=[ \"Lievven.Snappy_Mod\", \"Other.Mod\" ]", text);
    Assert.Contains("mods_directory=\"D:\\\\PreviousMods\"", text);
}
```

Also prove duplicate DDAI IDs collapse to one, non-DDAI duplicates remain, user-changed directories are preserved, UTF-16 LE/BE BOMs round-trip, mixed newlines fail closed, missing owned keys insert correctly, and uninstall removes no unrelated ID.

- [ ] **Step 6: Run focused and app tests and verify GREEN**

```powershell
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --filter "FullyQualifiedName~DungeondraftConfigEditorTests" --no-restore
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --no-restore
```

Expected: both commands pass with zero failures.

- [ ] **Step 7: Commit Task 1**

```powershell
git add -- src/DDAI.App/DungeondraftConfigEditor.cs tests/DDAI.App.Tests/DungeondraftConfigEditorTests.cs
git commit -m "feat: edit Dungeondraft mod configuration safely"
```

---

### Task 2: Atomic Config Transaction

**Files:**
- Create: `src/DDAI.App/DungeondraftConfigTransaction.cs`
- Create: `tests/DDAI.App.Tests/DungeondraftConfigTransactionTests.cs`

**Interfaces:**
- Consumes: `DungeondraftConfigEdit` from Task 1, an absolute `config.ini` path, and `TimeProvider`.
- Produces: `DungeondraftConfigTransactionPlan`, `DungeondraftConfigUpdate`, `PlanSetup`, `PlanUninstall`, `Apply`, and `Restore`.

- [ ] **Step 1: Write exact-backup, idempotence, and concurrency RED tests**

```csharp
[Fact]
public void Apply_CreatesExactSiblingBackupAndIsIdempotent()
{
    using var sandbox = new ConfigTransactionSandbox();
    var original = Encoding.UTF8.GetBytes("[Mods]\r\nactive_mods=[ \"Lievven.Snappy_Mod\" ]\r\nmods_directory=\"D:\\\\Old\"\r\n");
    File.WriteAllBytes(sandbox.ConfigPath, original);
    var transaction = new DungeondraftConfigTransaction(new FixedTimeProvider());

    var update = transaction.Apply(transaction.PlanSetup(sandbox.ConfigPath, sandbox.ManagedModsDirectory));

    Assert.True(update.Changed);
    Assert.NotNull(update.BackupPath);
    Assert.Equal(original, File.ReadAllBytes(update.BackupPath!));
    Assert.Equal(Path.GetDirectoryName(sandbox.ConfigPath), Path.GetDirectoryName(update.BackupPath));
    var second = transaction.Apply(transaction.PlanSetup(sandbox.ConfigPath, sandbox.ManagedModsDirectory));
    Assert.False(second.Changed);
    Assert.Null(second.BackupPath);
}

[Fact]
public void Apply_RejectsConcurrentChangeWithoutReplacingIt()
{
    using var sandbox = new ConfigTransactionSandbox();
    File.WriteAllText(sandbox.ConfigPath, "[Mods]\nactive_mods=[ ]\n");
    var transaction = new DungeondraftConfigTransaction(new FixedTimeProvider());
    var plan = transaction.PlanSetup(sandbox.ConfigPath, sandbox.ManagedModsDirectory);
    File.WriteAllText(sandbox.ConfigPath, "user changed this");

    Assert.Throws<DungeondraftConfigException>(() => transaction.Apply(plan));
    Assert.Equal("user changed this", File.ReadAllText(sandbox.ConfigPath));
    Assert.Empty(Directory.GetFiles(sandbox.Root, "*.ddai-backup-*.ini"));
}

private sealed class FixedTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 8, 9, 20, 0, 0, TimeSpan.Zero);
}

private sealed class ConfigTransactionSandbox : IDisposable
{
    public ConfigTransactionSandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "ddai-config-transaction-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        ConfigPath = Path.Combine(Root, "config.ini");
        ManagedModsDirectory = Path.Combine(Root, "DungeondraftMods");
    }

    public string Root { get; }
    public string ConfigPath { get; }
    public string ManagedModsDirectory { get; }
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
```

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --filter "FullyQualifiedName~DungeondraftConfigTransactionTests" --no-restore
```

Expected: compilation fails because the transaction types do not exist.

- [ ] **Step 3: Add transaction records and planning APIs**

```csharp
public sealed record DungeondraftConfigTransactionPlan(
    string Path,
    byte[] OriginalBytes,
    byte[] ReplacementBytes,
    DungeondraftConfigOwnership Ownership);

public sealed record DungeondraftConfigUpdate(
    string State,
    string Path,
    bool Changed,
    string? BackupPath,
    DungeondraftConfigOwnership? Ownership);

public sealed class DungeondraftConfigTransaction(TimeProvider timeProvider)
{
    public DungeondraftConfigTransactionPlan PlanSetup(string configPath, string managedModsDirectory);
    public DungeondraftConfigTransactionPlan PlanUninstall(string configPath, DungeondraftConfigOwnership ownership);
    public DungeondraftConfigUpdate Apply(DungeondraftConfigTransactionPlan plan);
    public void Restore(DungeondraftConfigTransactionPlan plan);
}
```

Planning requires an existing regular file, reads exact bytes once, and calls the Task 1 editor. `Apply` returns state `already_current` without creating files when the byte arrays are identical.

- [ ] **Step 4: Implement same-directory staging, backup, verify, replace, and cleanup**

Use these exact path shapes:

```csharp
var stage = Path.Combine(directory, $".{fileName}.ddai-stage-{Guid.NewGuid():N}.tmp");
var backup = $"{plan.Path}.ddai-backup-{timeProvider.GetUtcNow().UtcDateTime:yyyyMMdd'T'HHmmssfffffff'Z'}-{Guid.NewGuid():N}.ini";
```

Durably create stage and backup files with `FileMode.CreateNew`, `FileShare.None`, and `Flush(flushToDisk: true)`. Immediately before replacement, require current bytes to equal `OriginalBytes`. Replace with `File.Move(stage, plan.Path, overwrite: true)` and verify the destination equals `ReplacementBytes`.

On `IOException` or `UnauthorizedAccessException`, restore `OriginalBytes` through a new same-directory rollback stage, delete the failed backup, clean stages, and throw `DungeondraftConfigException`. `Restore(plan)` uses the same durable rollback path and refuses to overwrite a destination that is neither the planned original nor planned replacement.

- [ ] **Step 5: Add locked-file, restore, and no-orphan tests**

Open `config.ini` using `new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)` to force a Windows write failure. Assert original bytes remain, the failed backup is removed, and no `.ddai-stage-` or `.ddai-rollback-` files remain. After a successful apply, call `Restore(plan)` and assert exact original bytes return. Add an uninstall transaction test proving the exact backup and unrelated IDs are preserved.

- [ ] **Step 6: Run focused and app tests and verify GREEN**

```powershell
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --filter "FullyQualifiedName~DungeondraftConfigTransactionTests" --no-restore
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --no-restore
```

Expected: both commands pass with zero failures.

- [ ] **Step 7: Commit Task 2**

```powershell
git add -- src/DDAI.App/DungeondraftConfigTransaction.cs tests/DDAI.App.Tests/DungeondraftConfigTransactionTests.cs
git commit -m "feat: transact Dungeondraft mod activation"
```

---

### Task 3: Setup, Ownership, Diagnosis, and Live Activation

**Files:**
- Modify: `src/DDAI.App/LocalSetupService.cs`
- Modify: `src/DDAI.App/CliApplication.cs`
- Modify: `tests/DDAI.App.Tests/SetupLifecycleTests.cs`
- Modify: `tests/DDAI.App.Tests/McpPublishedIntegrationTests.cs`
- Create: `tests/DDAI.App.Tests/DungeondraftActivationPublishedTests.cs`
- Modify: `.superpowers/sdd/2026-08-09-ddai-windows-connector/task-4-report.md`

**Interfaces:**
- Consumes: Task 2 transaction APIs and existing owned installer, client merger, heartbeat slots, and CLI.
- Produces: process-aware setup/diagnose/uninstall states, persisted activation ownership, published-EXE proof, and a real current-machine status proof.

- [ ] **Step 1: Write process, config, diagnosis, and uninstall RED tests**

Add this injectable boundary:

```csharp
public interface IDungeondraftProcessProbe
{
    bool IsRunning();
}

private sealed class StubProcessProbe(bool isRunning) : IDungeondraftProcessProbe
{
    public bool IsRunning() => isRunning;
}
```

Construct `LocalSetupService(timeProvider, new StubProcessProbe(isRunning: true))` and assert:

```csharp
var result = service.Setup(paths);
Assert.Equal("activation_pending", result.DungeondraftConfig.State);
Assert.Equal("activation_pending_dungeondraft_running", result.Code);
Assert.Equal(originalConfigBytes, File.ReadAllBytes(paths.DungeondraftConfigPath));
```

Add focused cases for:

- closed plus missing `config.ini` => `activation_pending_config_missing`, no config write;
- closed plus valid config => exact backup, Custom Snap and DDAI active, ownership receipt persisted;
- repeated setup => `already_current`, no second backup;
- diagnose configured without heartbeat => `configured_waiting_for_reload`;
- diagnose mismatch => `activation_pending_config_mismatch`;
- fresh heartbeat => `runtime_heartbeat_fresh`;
- running uninstall => config retained and structured partial result;
- closed uninstall with receipt => only DDAI removed and prior directory restored;
- closed uninstall after user directory change => user value preserved;
- legacy metadata without config receipt => config untouched.

- [ ] **Step 2: Run lifecycle tests and verify RED**

```powershell
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --filter "FullyQualifiedName~SetupLifecycleTests" --no-restore
```

Expected: compilation or assertion failures for missing process/config integration.

- [ ] **Step 3: Extend paths, result records, and metadata compatibly**

Add to `LocalSetupPaths`:

```csharp
public string DungeondraftConfigPath =>
    Path.Combine(Path.GetFullPath(DungeondraftUserDataDirectory), "config.ini");
```

Extend each setup, diagnosis, and uninstall result with `DungeondraftConfigUpdate DungeondraftConfig`. Extend private `InstallMetadata` with an optional final `DungeondraftConfigOwnership? DungeondraftConfig` parameter so the currently installed legacy metadata deserializes with `null`. Keep owner `org.ddai.connector`, executable ownership, and version `0.1.0` unchanged.

- [ ] **Step 4: Implement the real Windows process probe and closed-app setup flow**

```csharp
public sealed class WindowsDungeondraftProcessProbe : IDungeondraftProcessProbe
{
    public bool IsRunning()
    {
        var processes = Process.GetProcessesByName("Dungeondraft");
        try { return processes.Any(process => !process.HasExited); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
```

`LocalSetupService(TimeProvider)` delegates to an injectable constructor using this probe. Before installation mutation:

```csharp
if (processProbe.IsRunning())
    configUpdate = new("activation_pending", paths.DungeondraftConfigPath, false, null, null);
else if (!File.Exists(paths.DungeondraftConfigPath))
    configUpdate = new("activation_pending_config_missing", paths.DungeondraftConfigPath, false, null, null);
else
    configPlan = transaction.PlanSetup(paths.DungeondraftConfigPath, paths.ModsDirectory);
```

After connector, mod, and AI-client setup succeeds, apply `configPlan` and persist its receipt. If metadata persistence fails, call `transaction.Restore(configPlan)` and return an error instead of claiming activation. Running setup returns `activation_pending_dungeondraft_running` without reading or writing config; missing config returns `activation_pending_config_missing`.

`CliApplication` returns exit `2` for either activation-pending setup code and exit `0` only when Dungeondraft configuration is already current or was successfully updated.

- [ ] **Step 5: Implement ownership-aware uninstall and diagnosis**

When Dungeondraft is running, uninstall does not edit `config.ini` and returns a partial code naming retained configuration. When closed and metadata has a receipt, preflight and apply `PlanUninstall` before deleting metadata. Without a receipt, leave config untouched.

Diagnosis order is:

```text
owned connector -> owned mod -> fresh heartbeat -> process/config presence -> strict parse -> configured values -> waiting for reload
```

Return exactly: `activation_pending_dungeondraft_running`, `activation_pending_config_missing`, `activation_pending_config_mismatch`, `configured_waiting_for_reload`, or `runtime_heartbeat_fresh`.

- [ ] **Step 6: Add a published-EXE activation test**

Publish to an isolated directory, write a real Godot-style config containing Custom Snap, and invoke the executable with sandbox paths:

```csharp
var startInfo = new ProcessStartInfo(executable)
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
};
foreach (var argument in new[]
{
    "setup",
    "--source-exe", executable,
    "--source-mod-directory", sourceModDirectory,
    "--install-root", sandbox.InstallRoot,
    "--mods-directory", sandbox.ModsDirectory,
    "--dungeondraft-user-data", sandbox.UserDataDirectory,
    "--claude-config", sandbox.ClaudeConfigPath,
    "--gemini-config", sandbox.GeminiConfigPath,
})
{
    startInfo.ArgumentList.Add(argument);
}

using var process = Process.Start(startInfo)!;
var stdout = await process.StandardOutput.ReadToEndAsync();
var stderr = await process.StandardError.ReadToEndAsync();
await process.WaitForExitAsync();
```

Assert exit `0`, exactly one JSON document on stdout, empty stderr, exact backup equality, both mod IDs in config, unrelated client entries preserved, and second-run byte idempotence. Re-run the official SDK initialize/list/call test to prove setup result changes do not contaminate MCP stdout.

- [ ] **Step 7: Run full verification and commit**

```powershell
dotnet test DDAI.slnx -c Release --no-restore
dotnet build DDAI.slnx -c Release --no-restore
dotnet publish src/DDAI.App/DDAI.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/dungeondraft-activation/win-x64
git diff --check
git add -- src/DDAI.App/LocalSetupService.cs src/DDAI.App/CliApplication.cs tests/DDAI.App.Tests/SetupLifecycleTests.cs tests/DDAI.App.Tests/McpPublishedIntegrationTests.cs tests/DDAI.App.Tests/DungeondraftActivationPublishedTests.cs .superpowers/sdd/2026-08-09-ddai-windows-connector/task-4-report.md
git commit -m "feat: automate Dungeondraft bridge activation"
```

Expected: full tests pass, build reports zero warnings/errors, publish emits one self-contained `ddai.exe`, and unrelated dirty files remain outside the commit.

- [ ] **Step 8: Run current-machine closed-app acceptance**

With the saved map closed and no `Dungeondraft` process present, install the freshly published executable rather than invoking the older installed binary as its own source:

```powershell
$activationExe = (Resolve-Path 'artifacts\dungeondraft-activation\win-x64\ddai.exe').Path
$activationMod = (Resolve-Path 'mods\DDAI').Path
& $activationExe setup --source-exe $activationExe --source-mod-directory $activationMod
& "$env:LOCALAPPDATA\DDAI\ddai.exe" diagnose --json
```

Verify the backup hashes to the exact pre-setup `config.ini`; confirm `active_mods` contains both `Lievven.Snappy_Mod` and `org.ddai.status_bridge`; confirm `mods_directory` is `%LOCALAPPDATA%\DDAI\DungeondraftMods`. Launch Dungeondraft normally, open the saved map, wait up to 30 seconds, then run:

```powershell
& "$env:LOCALAPPDATA\DDAI\ddai.exe" diagnose --json
& "$env:LOCALAPPDATA\DDAI\ddai.exe" status --json
```

Expected: diagnosis `runtime_heartbeat_fresh`, status success with a correlated response, runtime receipt and one of eight heartbeat slots present, and Custom Snap still active. Record exact commands, hashes, response ID, and remaining gaps in the Task 4 report without claiming native map generation is complete.
