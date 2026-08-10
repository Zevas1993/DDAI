using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using DDAI.App;

namespace DDAI.App.Tests;

public sealed class SetupLifecycleTests
{
    [Fact]
    public void SetupRepairAndUninstall_ManageOwnedAssetHelperReceipt()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{}");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        var service = new LocalSetupService(new FixedTimeProvider(), new StubProcessProbe(false));
        var receiptPath = Path.Combine(sandbox.UserDataDirectory, "ddai", "private", "asset-helper.json");

        _ = service.Setup(sandbox.Paths);

        using (var receipt = JsonDocument.Parse(File.ReadAllText(receiptPath)))
        {
            Assert.Equal("1.0", receipt.RootElement.GetProperty("schema_version").GetString());
            Assert.Equal(Path.GetFullPath(sandbox.Paths.InstalledExecutable), receipt.RootElement.GetProperty("executable_path").GetString());
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sandbox.Paths.InstalledExecutable))).ToLowerInvariant(),
                receipt.RootElement.GetProperty("sha256").GetString());
        }

        File.Delete(receiptPath);
        _ = service.Setup(sandbox.Paths);
        Assert.True(File.Exists(receiptPath));

        _ = service.Uninstall(sandbox.Paths, sandbox.SourceExecutable);
        Assert.False(File.Exists(receiptPath));
    }

    [Fact]
    public void Parse_AcceptsAllIsolatedSetupPathOverridesAndDefaultsToCustomPerUserModsRoot()
    {
        using var sandbox = new LifecycleSandbox();
        var parsed = CliParser.Parse(sandbox.SetupArguments);

        Assert.Equal(Path.GetFullPath(sandbox.SourceExecutable), parsed.SourceExecutable);
        Assert.Equal(Path.GetFullPath(sandbox.SourceModDirectory), parsed.SourceModDirectory);
        Assert.Equal(Path.GetFullPath(sandbox.InstallRoot), parsed.InstallRoot);
        Assert.Equal(Path.GetFullPath(sandbox.ModsDirectory), parsed.ModsDirectory);
        Assert.Equal(Path.GetFullPath(sandbox.UserDataDirectory), parsed.DungeondraftUserDataDirectory);
        Assert.Equal(Path.GetFullPath(sandbox.ClaudePath), parsed.ClaudeConfigPath);
        Assert.Equal(Path.GetFullPath(sandbox.GeminiPath), parsed.GeminiConfigPath);

        var defaults = CliParser.Parse(["setup"]);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDAI", "DungeondraftMods"),
            defaults.ModsDirectory);
    }

    [Fact]
    public async Task SetupAndUninstall_CopyAndRemoveOnlyOwnedFilesWhilePreservingForeignContent()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.InstallSentinel, "keep-install");
        File.WriteAllText(sandbox.ForeignModSentinel, "keep-mod");
        File.WriteAllText(sandbox.ClaudePath, "{\"mcpServers\":{\"other\":{\"command\":\"keep.exe\"}}}");
        File.WriteAllText(sandbox.GeminiPath, "{\"mcpServers\":{\"other\":{\"command\":\"keep.exe\",\"trust\":true}}}");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var application = new CliApplication(stdout, stderr, new FixedTimeProvider(), new StubProcessProbe(false));

        Assert.Equal(0, await application.RunAsync(sandbox.SetupArguments));
        var firstSetup = JsonNode.Parse(stdout.ToString())!;
        Assert.Equal("installed", firstSetup["state"]!.GetValue<string>());
        var installedExecutable = Path.Combine(sandbox.InstallRoot, "ddai.exe");
        Assert.Equal(File.ReadAllBytes(sandbox.SourceExecutable), File.ReadAllBytes(installedExecutable));
        Assert.True(File.Exists(Path.Combine(sandbox.InstallRoot, "install-metadata.json")));
        Assert.Equal("keep-install", File.ReadAllText(sandbox.InstallSentinel));
        Assert.Equal("keep-mod", File.ReadAllText(sandbox.ForeignModSentinel));
        Assert.True(File.Exists(Path.Combine(sandbox.ModsDirectory, "DDAI", "ddai_bridge.ddmod")));
        Assert.Equal(installedExecutable, ReadDdaiCommand(sandbox.ClaudePath));
        Assert.Equal(installedExecutable, ReadDdaiCommand(sandbox.GeminiPath));

        stdout.GetStringBuilder().Clear();
        Assert.Equal(0, await application.RunAsync(sandbox.SetupArguments));
        Assert.Equal("already_current", JsonNode.Parse(stdout.ToString())!["state"]!.GetValue<string>());

        stdout.GetStringBuilder().Clear();
        var uninstallArguments = sandbox.CommandArguments("uninstall");
        Assert.Equal(0, await application.RunAsync(uninstallArguments));
        var uninstall = JsonNode.Parse(stdout.ToString())!;
        Assert.Equal("uninstalled", uninstall["state"]!.GetValue<string>());
        Assert.False(File.Exists(installedExecutable));
        Assert.False(File.Exists(Path.Combine(sandbox.InstallRoot, "install-metadata.json")));
        Assert.False(Directory.Exists(Path.Combine(sandbox.ModsDirectory, "DDAI")));
        Assert.False(Directory.Exists(sandbox.CopiedCustomSnapDirectory));
        Assert.True(Directory.Exists(sandbox.OriginalModsDirectory));
        Assert.Equal("keep-install", File.ReadAllText(sandbox.InstallSentinel));
        Assert.Equal("keep-mod", File.ReadAllText(sandbox.ForeignModSentinel));
        Assert.Null(JsonNode.Parse(File.ReadAllText(sandbox.ClaudePath))!["mcpServers"]!["ddai"]);
        Assert.Equal("keep.exe", JsonNode.Parse(File.ReadAllText(sandbox.ClaudePath))!["mcpServers"]!["other"]!["command"]!.GetValue<string>());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task Setup_InvalidClientJsonDoesNotInstallExecutableOrMod()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{ valid-but-not-really");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var application = new CliApplication(stdout, stderr, new FixedTimeProvider(), new StubProcessProbe(false));

        var exitCode = await application.RunAsync(sandbox.SetupArguments);

        Assert.NotEqual(0, exitCode);
        Assert.False(File.Exists(Path.Combine(sandbox.InstallRoot, "ddai.exe")));
        Assert.False(Directory.Exists(Path.Combine(sandbox.ModsDirectory, "DDAI")));
        Assert.Equal("{ valid-but-not-really", File.ReadAllText(sandbox.ClaudePath));
    }

    [Fact]
    public async Task Setup_ForeignDdaiNamedModIsUntouchedAndBlocksAllInstallationBeforeMutation()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{}");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        var foreignTarget = Path.Combine(sandbox.ModsDirectory, "DDAI");
        Directory.CreateDirectory(foreignTarget);
        var foreignManifest = Path.Combine(foreignTarget, "ddai_bridge.ddmod");
        const string foreignJson = "{\"unique_id\":\"someone.else\",\"dd_version\":\"1.2.0.1\"}";
        File.WriteAllText(foreignManifest, foreignJson);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var application = new CliApplication(stdout, stderr, new FixedTimeProvider(), new StubProcessProbe(false));

        var exitCode = await application.RunAsync(sandbox.SetupArguments);

        Assert.NotEqual(0, exitCode);
        Assert.Equal(foreignJson, File.ReadAllText(foreignManifest));
        Assert.False(File.Exists(Path.Combine(sandbox.InstallRoot, "ddai.exe")));
        Assert.Null(JsonNode.Parse(File.ReadAllText(sandbox.ClaudePath))!["mcpServers"]);
    }

    [Fact]
    public async Task Diagnose_ConfiguredWithoutHeartbeatWaitsForReload()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{}");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var application = new CliApplication(stdout, stderr, new FixedTimeProvider(), new StubProcessProbe(false));
        Assert.Equal(0, await application.RunAsync(sandbox.SetupArguments));
        stdout.GetStringBuilder().Clear();

        var exitCode = await application.RunAsync(sandbox.CommandArguments("diagnose"));

        Assert.Equal(2, exitCode);
        var diagnosis = JsonNode.Parse(stdout.ToString())!;
        Assert.Equal("configured", diagnosis["state"]!.GetValue<string>());
        Assert.Equal("configured_waiting_for_reload", diagnosis["code"]!.GetValue<string>());
        Assert.Equal(Path.Combine(sandbox.ModsDirectory, "DDAI"), diagnosis["mod_path"]!.GetValue<string>());
    }

    [Fact]
    public void Uninstall_RunningInstalledExecutableRetainsExecutableAndOwnershipMetadataHonestly()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{}");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        var paths = sandbox.Paths;
        var service = new LocalSetupService(new FixedTimeProvider(), new StubProcessProbe(false));
        Assert.Equal("installed", service.Setup(paths).State);
        var installedExecutable = Path.Combine(sandbox.InstallRoot, "ddai.exe");

        var result = service.Uninstall(paths, installedExecutable);

        Assert.Equal("partial", result.State);
        Assert.Equal("running_executable_retained", result.Code);
        Assert.True(File.Exists(installedExecutable));
        Assert.True(File.Exists(Path.Combine(sandbox.InstallRoot, "install-metadata.json")));
        Assert.False(Directory.Exists(Path.Combine(sandbox.ModsDirectory, "DDAI")));
    }

    [Fact]
    public void Setup_RunningDungeondraftLeavesConfigBytesUntouchedAndReportsPending()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{}");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        var original = File.ReadAllBytes(sandbox.ConfigPath);
        var service = new LocalSetupService(new FixedTimeProvider(), new StubProcessProbe(true));

        var result = service.Setup(sandbox.Paths);

        Assert.Equal("activation_pending", result.State);
        Assert.Equal("activation_pending_dungeondraft_running", result.Code);
        Assert.Equal("activation_pending", result.DungeondraftConfig.State);
        Assert.Equal("deferred_dungeondraft_running", result.ModConsolidation.State);
        Assert.False(Directory.Exists(sandbox.CopiedCustomSnapDirectory));
        Assert.Equal(original, File.ReadAllBytes(sandbox.ConfigPath));
        Assert.Empty(Directory.GetFiles(sandbox.UserDataDirectory, "config.ini.ddai-backup-*.ini"));
    }

    [Fact]
    public void Setup_RunningDungeondraftDoesNotParseInvalidConfigOrCopyCustomSnap()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{}");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        File.WriteAllText(sandbox.ConfigPath, "[Mods]\nactive_mods=[ bare ]\n");
        var original = File.ReadAllBytes(sandbox.ConfigPath);
        var service = new LocalSetupService(new FixedTimeProvider(), new StubProcessProbe(true));

        var result = service.Setup(sandbox.Paths);

        Assert.Equal("activation_pending_dungeondraft_running", result.Code);
        Assert.Equal(original, File.ReadAllBytes(sandbox.ConfigPath));
        Assert.False(Directory.Exists(sandbox.CopiedCustomSnapDirectory));
    }

    [Fact]
    public async Task SetupCommand_RunningDungeondraftReturnsRetryableExitTwoAndOneJsonDocument()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{}");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var application = new CliApplication(
            stdout,
            stderr,
            new FixedTimeProvider(),
            new StubProcessProbe(true));

        var exitCode = await application.RunAsync(sandbox.SetupArguments);
        var result = JsonNode.Parse(stdout.ToString())!;

        Assert.Equal(2, exitCode);
        Assert.Equal("activation_pending_dungeondraft_running", result["code"]!.GetValue<string>());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Setup_MissingConfigInstallsConnectorButReportsPendingWithoutCreatingConfig()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{}");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        File.Delete(sandbox.ConfigPath);
        var service = new LocalSetupService(new FixedTimeProvider(), new StubProcessProbe(false));

        var result = service.Setup(sandbox.Paths);

        Assert.Equal("activation_pending", result.State);
        Assert.Equal("activation_pending_config_missing", result.Code);
        Assert.Equal("activation_pending_config_missing", result.DungeondraftConfig.State);
        Assert.Equal("not_planned", result.ModConsolidation.State);
        Assert.False(File.Exists(sandbox.ConfigPath));
        Assert.True(File.Exists(sandbox.Paths.InstalledExecutable));
    }

    [Fact]
    public void Setup_ClosedActivatesDdaiWithoutRequiringOrCopyingCustomSnap()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{}");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        File.WriteAllText(
            sandbox.ConfigPath,
            "[Mods]\nactive_mods=[ ]\nmods_directory=\"D:\\\\UnrelatedMods\"\n");
        var original = File.ReadAllBytes(sandbox.ConfigPath);
        var service = new LocalSetupService(new AdvancingTimeProvider(), new StubProcessProbe(false));

        var first = service.Setup(sandbox.Paths);
        var configured = File.ReadAllBytes(sandbox.ConfigPath);
        var metadata = JsonNode.Parse(File.ReadAllText(sandbox.Paths.MetadataPath))!;

        Assert.Equal("updated", first.DungeondraftConfig.State);
        Assert.NotNull(first.DungeondraftConfig.BackupPath);
        Assert.Equal(original, File.ReadAllBytes(first.DungeondraftConfig.BackupPath!));
        Assert.DoesNotContain("Lievven.Snappy_Mod", Encoding.UTF8.GetString(configured));
        Assert.Contains(DungeondraftConfigEditor.DdaiModId, Encoding.UTF8.GetString(configured));
        Assert.NotNull(metadata["dungeondraft_config"]);
        Assert.Null(metadata["consolidated_mods"]);
        Assert.Equal("not_required", first.ModConsolidation.State);
        Assert.False(Directory.Exists(sandbox.CopiedCustomSnapDirectory));
        var metadataBytes = File.ReadAllBytes(sandbox.Paths.MetadataPath);

        var second = service.Setup(sandbox.Paths);

        Assert.Equal("already_current", second.State);
        Assert.Equal("already_current", second.DungeondraftConfig.State);
        Assert.Equal("not_required", second.ModConsolidation.State);
        Assert.Equal(configured, File.ReadAllBytes(sandbox.ConfigPath));
        Assert.Equal(metadataBytes, File.ReadAllBytes(sandbox.Paths.MetadataPath));
        Assert.Single(Directory.GetFiles(sandbox.UserDataDirectory, "config.ini.ddai-backup-*.ini"));
    }

    [Fact]
    public void Diagnose_ReportsRunningMissingMismatchWaitingAndFreshHeartbeatStates()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{}");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        var closedService = new LocalSetupService(new FixedTimeProvider(), new StubProcessProbe(false));
        _ = closedService.Setup(sandbox.Paths);

        var waiting = closedService.Diagnose(sandbox.Paths);
        Assert.Equal("configured_waiting_for_reload", waiting.Code);

        File.WriteAllText(sandbox.ConfigPath, "[Mods]\nactive_mods=[ \"Lievven.Snappy_Mod\" ]\nmods_directory=\"D:\\\\Other\"\n");
        var mismatch = closedService.Diagnose(sandbox.Paths);
        Assert.Equal("activation_pending_config_mismatch", mismatch.Code);

        File.Delete(sandbox.ConfigPath);
        var missing = closedService.Diagnose(sandbox.Paths);
        Assert.Equal("activation_pending_config_missing", missing.Code);

        var running = new LocalSetupService(new FixedTimeProvider(), new StubProcessProbe(true)).Diagnose(sandbox.Paths);
        Assert.Equal("activation_pending_dungeondraft_running", running.Code);

        WriteFreshHeartbeat(sandbox.UserDataDirectory);
        var fresh = new LocalSetupService(new FixedTimeProvider(), new StubProcessProbe(true)).Diagnose(sandbox.Paths);
        Assert.Equal("running", fresh.State);
        Assert.Equal("runtime_heartbeat_fresh", fresh.Code);
    }

    [Fact]
    public void Uninstall_ClosedRestoresPriorDirectoryAndRemovesOnlyDdaiModId()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{}");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        var service = new LocalSetupService(new FixedTimeProvider(), new StubProcessProbe(false));
        _ = service.Setup(sandbox.Paths);

        var result = service.Uninstall(sandbox.Paths, sandbox.SourceExecutable);
        var config = File.ReadAllText(sandbox.ConfigPath);

        Assert.Equal("uninstalled", result.State);
        Assert.Equal("updated", result.DungeondraftConfig.State);
        Assert.Equal("retained_unproven_user_content", result.ModConsolidation.State);
        Assert.Contains("Lievven.Snappy_Mod", config);
        Assert.DoesNotContain(DungeondraftConfigEditor.DdaiModId, config);
        Assert.Contains(
            "mods_directory=\"" + sandbox.OriginalModsDirectory.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"",
            config);
        Assert.True(Directory.Exists(sandbox.OriginalModsDirectory));
        Assert.False(Directory.Exists(sandbox.CopiedCustomSnapDirectory));
    }

    [Fact]
    public void Uninstall_RunningDungeondraftRetainsConfigurationAndOwnershipForRetry()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{}");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        var closedService = new LocalSetupService(new FixedTimeProvider(), new StubProcessProbe(false));
        _ = closedService.Setup(sandbox.Paths);
        var configured = File.ReadAllBytes(sandbox.ConfigPath);

        var result = new LocalSetupService(new FixedTimeProvider(), new StubProcessProbe(true))
            .Uninstall(sandbox.Paths, sandbox.SourceExecutable);

        Assert.Equal("partial", result.State);
        Assert.Equal("dungeondraft_running_configuration_retained", result.Code);
        Assert.Equal(configured, File.ReadAllBytes(sandbox.ConfigPath));
        Assert.True(File.Exists(sandbox.Paths.MetadataPath));
        Assert.True(Directory.Exists(sandbox.Paths.InstalledModDirectory));
        Assert.False(Directory.Exists(sandbox.CopiedCustomSnapDirectory));
    }

    [Fact]
    public void Uninstall_UserChangedModsDirectoryIsPreservedWhileDdaiIdIsRemoved()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{}");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        var service = new LocalSetupService(new FixedTimeProvider(), new StubProcessProbe(false));
        _ = service.Setup(sandbox.Paths);
        var text = File.ReadAllText(sandbox.ConfigPath)
            .Replace(
                "mods_directory=\"" + sandbox.ModsDirectory.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"",
                "mods_directory=\"E:\\\\UserChanged\"",
                StringComparison.Ordinal);
        File.WriteAllText(sandbox.ConfigPath, text);

        var result = service.Uninstall(sandbox.Paths, sandbox.SourceExecutable);
        var uninstalled = File.ReadAllText(sandbox.ConfigPath);

        Assert.Equal("updated", result.DungeondraftConfig.State);
        Assert.Contains("mods_directory=\"E:\\\\UserChanged\"", uninstalled);
        Assert.DoesNotContain(DungeondraftConfigEditor.DdaiModId, uninstalled);
        Assert.Contains("Lievven.Snappy_Mod", uninstalled);
    }

    [Fact]
    public void Uninstall_LegacyMetadataWithoutReceiptLeavesConfigByteExact()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{}");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        var service = new LocalSetupService(new FixedTimeProvider(), new StubProcessProbe(false));
        _ = service.Setup(sandbox.Paths);
        var metadata = JsonNode.Parse(File.ReadAllText(sandbox.Paths.MetadataPath))!.AsObject();
        metadata.Remove("dungeondraft_config");
        File.WriteAllText(sandbox.Paths.MetadataPath, metadata.ToJsonString());
        var configured = File.ReadAllBytes(sandbox.ConfigPath);

        var result = service.Uninstall(sandbox.Paths, sandbox.SourceExecutable);

        Assert.Equal("retained_unproven_ownership", result.DungeondraftConfig.State);
        Assert.Equal(configured, File.ReadAllBytes(sandbox.ConfigPath));
    }

    [Fact]
    public void Setup_InvalidDungeondraftConfigFailsBeforeInstallingAnything()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{}");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        File.WriteAllText(sandbox.ConfigPath, "[Mods]\nactive_mods=[ bare ]\n");
        var service = new LocalSetupService(new FixedTimeProvider(), new StubProcessProbe(false));

        Assert.Throws<DungeondraftConfigException>(() => service.Setup(sandbox.Paths));

        Assert.False(File.Exists(sandbox.Paths.InstalledExecutable));
        Assert.False(Directory.Exists(sandbox.Paths.InstalledModDirectory));
        Assert.Null(JsonNode.Parse(File.ReadAllText(sandbox.ClaudePath))!["mcpServers"]);
    }

    [Fact]
    public void Setup_ForeignInactiveCustomSnapDirectoryIsPreservedAndDoesNotBlockDdai()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{}");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        File.WriteAllText(
            sandbox.ConfigPath,
            "[Mods]\nactive_mods=[ ]\nmods_directory=\"D:\\\\UnrelatedMods\"\n");
        Directory.CreateDirectory(sandbox.CopiedCustomSnapDirectory);
        File.WriteAllText(Path.Combine(sandbox.CopiedCustomSnapDirectory, "foreign.txt"), "keep");
        var service = new LocalSetupService(new FixedTimeProvider(), new StubProcessProbe(false));

        var result = service.Setup(sandbox.Paths);

        Assert.Equal("keep", File.ReadAllText(Path.Combine(sandbox.CopiedCustomSnapDirectory, "foreign.txt")));
        Assert.True(File.Exists(sandbox.Paths.InstalledExecutable));
        Assert.True(Directory.Exists(sandbox.Paths.InstalledModDirectory));
        Assert.Equal("not_required", result.ModConsolidation.State);
        Assert.DoesNotContain("Lievven.Snappy_Mod", File.ReadAllText(sandbox.ConfigPath));
    }

    [Fact]
    public void Diagnose_DoesNotRequireHistoricalCustomSnapCopy()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{}");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        File.WriteAllText(
            sandbox.ConfigPath,
            "[Mods]\nactive_mods=[ ]\nmods_directory=\"D:\\\\UnrelatedMods\"\n");
        var service = new LocalSetupService(new FixedTimeProvider(), new StubProcessProbe(false));
        _ = service.Setup(sandbox.Paths);

        var result = service.Diagnose(sandbox.Paths);

        Assert.False(Directory.Exists(sandbox.CopiedCustomSnapDirectory));
        Assert.Equal("configured", result.State);
        Assert.Equal("configured_waiting_for_reload", result.Code);
        Assert.Equal("not_required", result.ModConsolidation.State);
    }

    private static void WriteFreshHeartbeat(string userDataDirectory)
    {
        var directory = Path.Combine(userDataDirectory, "ddai", "runtime-heartbeats");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "heartbeat-slot-0.json"),
            "{\"schema_version\":\"1.0\",\"mod_version\":\"0.2.1\",\"session_id\":\"session\",\"timestamp\":\"2026-08-09T12:00:00Z\"}");
    }

    private static string ReadDdaiCommand(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!["ddai"]!["command"]!.GetValue<string>();

    private sealed class LifecycleSandbox : IDisposable
    {
        public LifecycleSandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "ddai-lifecycle-tests", Guid.NewGuid().ToString("N"));
            SourceExecutable = Path.Combine(Root, "source", "ddai.exe");
            SourceModDirectory = Path.Combine(Root, "source", "mod");
            OriginalModsDirectory = Path.Combine(Root, "original-mods", "custom_snap");
            InstallRoot = Path.Combine(Root, "install");
            ModsDirectory = Path.Combine(InstallRoot, "DungeondraftMods");
            UserDataDirectory = Path.Combine(Root, "DungeondraftUserData");
            ClaudePath = Path.Combine(Root, "Claude", "claude_desktop_config.json");
            GeminiPath = Path.Combine(Root, ".gemini", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(SourceExecutable)!);
            Directory.CreateDirectory(SourceModDirectory);
            Directory.CreateDirectory(Path.Combine(OriginalModsDirectory, "scripts"));
            Directory.CreateDirectory(InstallRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(ClaudePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(GeminiPath)!);
            Directory.CreateDirectory(UserDataDirectory);
            File.WriteAllBytes(SourceExecutable, [0x44, 0x44, 0x41, 0x49]);
            File.WriteAllText(
                Path.Combine(OriginalModsDirectory, "snappy_mod.ddmod"),
                "{\"name\":\"Custom Snap Mod\",\"unique_id\":\"Lievven.Snappy_Mod\",\"dd_version\":\"1.1.0.6\"}");
            File.WriteAllText(
                Path.Combine(OriginalModsDirectory, "scripts", "snappy_mod.gd"),
                "func load_local_settings():\n    var data = Global.ModMapData[TOOL_ID]\n    if data == null or data.empty():\n        return\n");
            File.WriteAllText(
                Path.Combine(UserDataDirectory, "config.ini"),
                "; keep\r\n[Mods]\r\nactive_mods=[ \"Lievven.Snappy_Mod\" ]\r\nmods_directory=\"" +
                OriginalModsDirectory.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"\r\n");
            CopyDirectory(Path.Combine(FindRepositoryRoot(), "mods", "DDAI"), SourceModDirectory);
            InstallSentinel = Path.Combine(InstallRoot, "foreign-sentinel.txt");
            ForeignModSentinel = Path.Combine(ModsDirectory, "OtherMod", "sentinel.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(ForeignModSentinel)!);
            Paths = new LocalSetupPaths(
                SourceExecutable,
                SourceModDirectory,
                InstallRoot,
                ModsDirectory,
                UserDataDirectory,
                ClaudePath,
                GeminiPath);
            SetupArguments = CommandArguments("setup");
        }

        public string Root { get; }
        public string SourceExecutable { get; }
        public string SourceModDirectory { get; }
        public string OriginalModsDirectory { get; }
        public string InstallRoot { get; }
        public string ModsDirectory { get; }
        public string UserDataDirectory { get; }
        public string ClaudePath { get; }
        public string GeminiPath { get; }
        public string ConfigPath => Path.Combine(UserDataDirectory, "config.ini");
        public string CopiedCustomSnapDirectory => Path.Combine(ModsDirectory, "custom_snap");
        public string InstallSentinel { get; }
        public string ForeignModSentinel { get; }
        public LocalSetupPaths Paths { get; }
        public string[] SetupArguments { get; }

        public string[] CommandArguments(string command) =>
        [
            command,
            "--source-exe", SourceExecutable,
            "--source-mod-directory", SourceModDirectory,
            "--install-root", InstallRoot,
            "--mods-directory", ModsDirectory,
            "--dungeondraft-user-data", UserDataDirectory,
            "--claude-config", ClaudePath,
            "--gemini-config", GeminiPath,
        ];

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static string FindRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "DDAI.slnx")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Could not find DDAI repository root.");
        }

        private static void CopyDirectory(string source, string destination)
        {
            foreach (var sourcePath in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var targetPath = Path.Combine(destination, Path.GetRelativePath(source, sourcePath));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(sourcePath, targetPath);
            }
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private int seconds;

        public override DateTimeOffset GetUtcNow() =>
            new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero).AddSeconds(seconds++);
    }

    private sealed class StubProcessProbe(bool isRunning) : IDungeondraftProcessProbe
    {
        public bool IsRunning() => isRunning;
    }
}
