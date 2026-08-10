using System.Diagnostics;
using System.Text.Json.Nodes;

namespace DDAI.App.Tests;

[CollectionDefinition("Published executable", DisableParallelization = true)]
public sealed class PublishedExecutableCollection;

[Collection("Published executable")]
public sealed class PublishedConfigOwnershipTests : IClassFixture<PublishedExecutableFixture>
{
    private readonly PublishedExecutableFixture _fixture;

    public PublishedConfigOwnershipTests(PublishedExecutableFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Setup_ForeignDdaiEntriesFailBeforeInstallingOrChangingEitherConfig()
    {
        using var sandbox = new PublishedLifecycleSandbox(_fixture.ExecutablePath);
        const string foreign = "{\"mcpServers\":{\"ddai\":{\"command\":\"C:\\\\foreign.exe\",\"args\":[\"foreign\"]},\"other\":{\"command\":\"keep.exe\"}}}";
        File.WriteAllText(sandbox.ClaudePath, foreign);
        File.WriteAllText(sandbox.GeminiPath, foreign);

        var result = sandbox.Run("setup");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(foreign, File.ReadAllText(sandbox.ClaudePath));
        Assert.Equal(foreign, File.ReadAllText(sandbox.GeminiPath));
        Assert.False(File.Exists(sandbox.InstalledExecutable));
        Assert.False(Directory.Exists(sandbox.InstalledModDirectory));
    }

    [Fact]
    public void Uninstall_PreservesForeignReplacementsWhileRemovingOwnedLocalFiles()
    {
        using var sandbox = new PublishedLifecycleSandbox(_fixture.ExecutablePath);
        const string original = "{\"mcpServers\":{\"other\":{\"command\":\"keep.exe\"}}}";
        File.WriteAllText(sandbox.ClaudePath, original);
        File.WriteAllText(sandbox.GeminiPath, original);
        var setup = sandbox.Run("setup");
        if (setup.ExitCode == 2)
        {
            Assert.Equal(
                "activation_pending_dungeondraft_running",
                JsonNode.Parse(setup.StandardOutput)!["code"]!.GetValue<string>());
            Assert.True(File.Exists(sandbox.InstalledExecutable));
            Assert.True(Directory.Exists(sandbox.InstalledModDirectory));
            return;
        }

        Assert.Equal(0, setup.ExitCode);
        var foreignClaude = JsonNode.Parse(File.ReadAllText(sandbox.ClaudePath))!;
        foreignClaude["mcpServers"]!["ddai"] = JsonNode.Parse("{\"command\":\"C:\\\\foreign-claude.exe\",\"args\":[\"foreign\"]}");
        File.WriteAllText(sandbox.ClaudePath, foreignClaude.ToJsonString());
        var foreignGemini = JsonNode.Parse(File.ReadAllText(sandbox.GeminiPath))!;
        foreignGemini["mcpServers"]!["ddai"] = JsonNode.Parse("{\"command\":\"C:\\\\foreign-gemini.exe\",\"args\":[\"foreign\"],\"trust\":true}");
        File.WriteAllText(sandbox.GeminiPath, foreignGemini.ToJsonString());

        var result = sandbox.Run("uninstall");

        Assert.Equal(0, result.ExitCode);
        var preservedClaude = JsonNode.Parse(File.ReadAllText(sandbox.ClaudePath))!["mcpServers"]!["ddai"];
        var preservedGemini = JsonNode.Parse(File.ReadAllText(sandbox.GeminiPath))!["mcpServers"]!["ddai"];
        Assert.NotNull(preservedClaude);
        Assert.NotNull(preservedGemini);
        Assert.Equal("C:\\foreign-claude.exe", preservedClaude["command"]!.GetValue<string>());
        Assert.Equal("C:\\foreign-gemini.exe", preservedGemini["command"]!.GetValue<string>());
        Assert.False(File.Exists(sandbox.InstalledExecutable));
        Assert.False(File.Exists(sandbox.MetadataPath));
        Assert.False(Directory.Exists(sandbox.InstalledModDirectory));
    }
}

[Collection("Published executable")]
public sealed class PublishedProtocolFailureTests : IClassFixture<PublishedExecutableFixture>
{
    private readonly PublishedExecutableFixture _fixture;

    public PublishedProtocolFailureTests(PublishedExecutableFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Serve_MailboxInitializationFailureWritesNoNonProtocolBytesToStdout()
    {
        var root = Path.Combine(Path.GetTempPath(), "ddai-published-protocol-fault", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var mailboxRoot = Path.Combine(root, "mailbox-is-a-file");
            File.WriteAllText(mailboxRoot, "not a directory");

            var result = PublishedExecutableFixture.RunProcess(
                _fixture.ExecutablePath,
                root,
                ["serve", "--stdio", "--mailbox-root", mailboxRoot, "--timeout-ms", "100"]);

            Assert.Equal(1, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.NotEmpty(result.StandardError);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class PublishedExecutableFixture : IDisposable
{
    public PublishedExecutableFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "ddai-published-fixture", Guid.NewGuid().ToString("N"));
        var publishDirectory = Path.Combine(Root, "publish");
        Directory.CreateDirectory(publishDirectory);
        var repositoryRoot = FindRepositoryRoot();
        var project = Path.Combine(repositoryRoot, "src", "DDAI.App", "DDAI.App.csproj");
        var result = RunProcess(
            "dotnet",
            repositoryRoot,
            [
                "publish", project, "-c", "Release", "-r", "win-x64", "--self-contained", "true",
                "-p:PublishSingleFile=true", "-p:DebugType=None", "--no-restore", "-o", publishDirectory,
            ]);
        Assert.True(result.ExitCode == 0, $"dotnet publish failed:\n{result.StandardOutput}\n{result.StandardError}");
        ExecutablePath = Path.Combine(publishDirectory, "ddai.exe");
        Assert.True(File.Exists(ExecutablePath));
    }

    public string Root { get; }
    public string ExecutablePath { get; }

    public void Dispose() => Directory.Delete(Root, recursive: true);

    internal static ProcessResult RunProcess(string executable, string workingDirectory, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    internal static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DDAI.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate DDAI repository root.");
    }
}

internal sealed class PublishedLifecycleSandbox : IDisposable
{
    public PublishedLifecycleSandbox(string sourceExecutable)
    {
        Root = Path.Combine(Path.GetTempPath(), "ddai-published-lifecycle", Guid.NewGuid().ToString("N"));
        InstallRoot = Path.Combine(Root, "install");
        ModsDirectory = Path.Combine(InstallRoot, "DungeondraftMods");
        UserDataDirectory = Path.Combine(Root, "DungeondraftUserData");
        ClaudePath = Path.Combine(Root, "Claude", "claude_desktop_config.json");
        GeminiPath = Path.Combine(Root, ".gemini", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(ClaudePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(GeminiPath)!);
        Directory.CreateDirectory(UserDataDirectory);
        File.WriteAllText(
            Path.Combine(UserDataDirectory, "config.ini"),
            "[Mods]\r\nactive_mods=[ \"Lievven.Snappy_Mod\" ]\r\nmods_directory=\"D:\\\\DungeonDraft\\\\Dungeondraft\\\\mods\\\\custom_snap\"\r\n");
        SourceExecutable = sourceExecutable;
        SourceModDirectory = Path.Combine(PublishedExecutableFixture.FindRepositoryRoot(), "mods", "DDAI");
    }

    public string Root { get; }
    public string SourceExecutable { get; }
    public string SourceModDirectory { get; }
    public string InstallRoot { get; }
    public string ModsDirectory { get; }
    public string UserDataDirectory { get; }
    public string ClaudePath { get; }
    public string GeminiPath { get; }
    public string InstalledExecutable => Path.Combine(InstallRoot, "ddai.exe");
    public string MetadataPath => Path.Combine(InstallRoot, "install-metadata.json");
    public string InstalledModDirectory => Path.Combine(ModsDirectory, "DDAI");
    public string ConfigPath => Path.Combine(UserDataDirectory, "config.ini");

    public ProcessResult Run(string command) => PublishedExecutableFixture.RunProcess(
        SourceExecutable,
        Root,
        [
            command,
            "--source-exe", SourceExecutable,
            "--source-mod-directory", SourceModDirectory,
            "--install-root", InstallRoot,
            "--mods-directory", ModsDirectory,
            "--dungeondraft-user-data", UserDataDirectory,
            "--claude-config", ClaudePath,
            "--gemini-config", GeminiPath,
        ]);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
