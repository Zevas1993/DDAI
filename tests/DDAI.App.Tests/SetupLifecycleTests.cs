using System.Text.Json;
using System.Text.Json.Nodes;
using DDAI.App;

namespace DDAI.App.Tests;

public sealed class SetupLifecycleTests
{
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
        var application = new CliApplication(stdout, stderr, new FixedTimeProvider());

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
        var application = new CliApplication(stdout, stderr, new FixedTimeProvider());

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
        var application = new CliApplication(stdout, stderr, new FixedTimeProvider());

        var exitCode = await application.RunAsync(sandbox.SetupArguments);

        Assert.NotEqual(0, exitCode);
        Assert.Equal(foreignJson, File.ReadAllText(foreignManifest));
        Assert.False(File.Exists(Path.Combine(sandbox.InstallRoot, "ddai.exe")));
        Assert.Null(JsonNode.Parse(File.ReadAllText(sandbox.ClaudePath))!["mcpServers"]);
    }

    [Fact]
    public async Task Diagnose_NoHeartbeatNamesCustomModsSelectionOrEnablementGap()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{}");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var application = new CliApplication(stdout, stderr, new FixedTimeProvider());
        Assert.Equal(0, await application.RunAsync(sandbox.SetupArguments));
        stdout.GetStringBuilder().Clear();

        var exitCode = await application.RunAsync(sandbox.CommandArguments("diagnose"));

        Assert.Equal(2, exitCode);
        var diagnosis = JsonNode.Parse(stdout.ToString())!;
        Assert.Equal("installed_not_observed", diagnosis["state"]!.GetValue<string>());
        Assert.Equal("mods_directory_not_selected_or_mod_disabled", diagnosis["code"]!.GetValue<string>());
        Assert.Equal(Path.Combine(sandbox.ModsDirectory, "DDAI"), diagnosis["mod_path"]!.GetValue<string>());
    }

    [Fact]
    public void Uninstall_RunningInstalledExecutableRetainsExecutableAndOwnershipMetadataHonestly()
    {
        using var sandbox = new LifecycleSandbox();
        File.WriteAllText(sandbox.ClaudePath, "{}");
        File.WriteAllText(sandbox.GeminiPath, "{}");
        var paths = sandbox.Paths;
        var service = new LocalSetupService(new FixedTimeProvider());
        Assert.Equal("installed", service.Setup(paths).State);
        var installedExecutable = Path.Combine(sandbox.InstallRoot, "ddai.exe");

        var result = service.Uninstall(paths, installedExecutable);

        Assert.Equal("partial", result.State);
        Assert.Equal("running_executable_retained", result.Code);
        Assert.True(File.Exists(installedExecutable));
        Assert.True(File.Exists(Path.Combine(sandbox.InstallRoot, "install-metadata.json")));
        Assert.False(Directory.Exists(Path.Combine(sandbox.ModsDirectory, "DDAI")));
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
            InstallRoot = Path.Combine(Root, "install");
            ModsDirectory = Path.Combine(InstallRoot, "DungeondraftMods");
            UserDataDirectory = Path.Combine(Root, "DungeondraftUserData");
            ClaudePath = Path.Combine(Root, "Claude", "claude_desktop_config.json");
            GeminiPath = Path.Combine(Root, ".gemini", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(SourceExecutable)!);
            Directory.CreateDirectory(SourceModDirectory);
            Directory.CreateDirectory(InstallRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(ClaudePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(GeminiPath)!);
            File.WriteAllBytes(SourceExecutable, [0x44, 0x44, 0x41, 0x49]);
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
        public string InstallRoot { get; }
        public string ModsDirectory { get; }
        public string UserDataDirectory { get; }
        public string ClaudePath { get; }
        public string GeminiPath { get; }
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
}
