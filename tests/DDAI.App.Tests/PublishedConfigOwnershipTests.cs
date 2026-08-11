using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DDAI.App.Assets;
using DDAI.Core.Assets;

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

[Collection("Published executable")]
public sealed class PublishedAssetHelperSecurityTests : IClassFixture<PublishedExecutableFixture>
{
    private readonly PublishedExecutableFixture _fixture;

    public PublishedAssetHelperSecurityTests(PublishedExecutableFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void AssetHelper_ReplacementBetweenOuterCheckAndProcessStartFailsBeforeMailboxMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), "ddai-helper-start-race", Guid.NewGuid().ToString("N"));
        var helperRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DDAI",
            "helpers");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(helperRoot);
        string? helperPath = null;
        string? replacementPath = null;
        try
        {
            var original = File.ReadAllBytes(_fixture.ExecutablePath).Concat("\nDDAI benign outer-check fixture A"u8.ToArray()).ToArray();
            var replacement = File.ReadAllBytes(_fixture.ExecutablePath).Concat("\nDDAI benign self-check fixture B"u8.ToArray()).ToArray();
            var expectedHash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
            helperPath = Path.Combine(helperRoot, "ddai-" + expectedHash + ".exe");
            replacementPath = helperPath + ".replacement-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(helperPath, original);

            var mailbox = Path.Combine(root, "mailbox");
            var requestId = Convert.ToHexString(SHA256.HashData(" Pack Café "u8)).ToLowerInvariant();
            var requestPath = Path.Combine(mailbox, "private", "pack-normalization", "requests", requestId + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(requestPath)!);
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema_version = "1.0",
                request_id = requestId,
                request_content_hash = AssetPackNormalizationService.ComputeRequestContentHash(requestId, " Pack Café "),
                value = " Pack Café ",
            });
            File.WriteAllBytes(requestPath, requestBytes);
            var receiptPath = Path.Combine(mailbox, "private", "asset-helper.json");
            File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
            {
                schema_version = "1.0",
                owner = "org.ddai.connector",
                executable_path = helperPath,
                sha256 = expectedHash,
            }));

            // Model the outer launcher's successful content-address check, followed by a
            // replacement before CreateProcess. Both files are runnable DDAI PE images.
            Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(helperPath))).ToLowerInvariant());
            File.WriteAllBytes(replacementPath, replacement);
            File.Move(replacementPath, helperPath, overwrite: true);

            var result = PublishedExecutableFixture.RunProcess(
                helperPath,
                root,
                ["asset-helper", "--mailbox-root", mailbox]);

            Assert.Equal(1, result.ExitCode);
            Assert.Equal(requestBytes, File.ReadAllBytes(requestPath));
            Assert.False(Directory.Exists(Path.Combine(mailbox, "private", "pack-normalization", "responses")));
            Assert.False(File.Exists(Path.Combine(mailbox, "catalog", "current.json")));
        }
        finally
        {
            if (replacementPath is not null && File.Exists(replacementPath)) File.Delete(replacementPath);
            if (helperPath is not null && File.Exists(helperPath)) File.Delete(helperPath);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AssetHelper_SeparateProcessesSerializeCatalogRevisionsThroughTheGlobalMutex()
    {
        var root = Path.Combine(Path.GetTempPath(), "ddai-helper-cross-process", Guid.NewGuid().ToString("N"));
        var helperRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DDAI",
            "helpers");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(helperRoot);
        string? helperPath = null;
        try
        {
            var helperBytes = File.ReadAllBytes(_fixture.ExecutablePath).Concat("\nDDAI benign cross-process fixture"u8.ToArray()).ToArray();
            var helperHash = Convert.ToHexString(SHA256.HashData(helperBytes)).ToLowerInvariant();
            helperPath = Path.Combine(helperRoot, "ddai-" + helperHash + ".exe");
            File.WriteAllBytes(helperPath, helperBytes);
            var mailbox = Path.Combine(root, "mailbox");
            Directory.CreateDirectory(Path.Combine(mailbox, "private"));
            File.WriteAllText(Path.Combine(mailbox, "private", "asset-helper.json"), JsonSerializer.Serialize(new
            {
                schema_version = "1.0",
                owner = "org.ddai.connector",
                executable_path = helperPath,
                sha256 = helperHash,
            }));
            var first = StageCatalogCandidate(mailbox, "cross-process-a", new string('1', 64));
            var second = StageCatalogCandidate(mailbox, "cross-process-b", new string('2', 64));

            using var start = new ManualResetEventSlim();
            var firstProcess = Task.Run(() =>
            {
                start.Wait();
                return PublishedExecutableFixture.RunProcess(helperPath, root, ["asset-helper", "--mailbox-root", mailbox]);
            });
            var secondProcess = Task.Run(() =>
            {
                start.Wait();
                return PublishedExecutableFixture.RunProcess(helperPath, root, ["asset-helper", "--mailbox-root", mailbox]);
            });
            start.Set();
            var results = await Task.WhenAll(firstProcess, secondProcess);

            Assert.All(results, result => Assert.Equal(0, result.ExitCode));
            var revisions = new[] { ReadResponseRevision(mailbox, first), ReadResponseRevision(mailbox, second) }.Order().ToArray();
            Assert.True(revisions[1] > revisions[0]);
            var repository = new AssetCatalogRepository(Path.Combine(mailbox, "catalog"), TimeProvider.System);
            Assert.True(repository.TryRefresh());
            Assert.Equal(revisions[1], repository.GetCurrent()!.Manifest.CatalogRevision);
        }
        finally
        {
            if (helperPath is not null && File.Exists(helperPath)) File.Delete(helperPath);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string StageCatalogCandidate(string mailbox, string sessionId, string assetHash)
    {
        var entry = new AssetCatalogEntry(
            "sha256:" + assetHash, "Objects", "Fixture", assetHash, "pack", "Pack", ["fixture"], [], null, true, false);
        var chunkBytes = Encoding.UTF8.GetBytes(AssetCatalogJson.SerializeChunk([entry]));
        var counts = AssetCategory.All.ToDictionary(
            category => category,
            category => category == "Objects" ? 1 : 0,
            StringComparer.Ordinal);
        var manifest = new AssetCatalogManifest(
            "1.0",
            sessionId,
            0,
            new string('0', 64),
            DateTimeOffset.Parse("2026-08-10T12:00:00Z"),
            true,
            counts,
            [new AssetCatalogChunk("chunk-0000.json", Hash(chunkBytes), 1, chunkBytes.Length)],
            []);
        manifest = manifest with { CatalogFingerprint = AssetCatalogJson.ComputeCatalogFingerprint(manifest) };
        var manifestBytes = Encoding.UTF8.GetBytes(AssetCatalogJson.SerializeManifest(manifest));
        var candidateFingerprint = Hash(manifestBytes);
        var candidateRoot = Path.Combine(mailbox, "private", "catalog-commit", "candidates", candidateFingerprint);
        Directory.CreateDirectory(candidateRoot);
        File.WriteAllBytes(Path.Combine(candidateRoot, "chunk-0000.json"), chunkBytes);
        File.WriteAllBytes(Path.Combine(candidateRoot, "manifest.json"), manifestBytes);
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = "1.0",
            candidate_fingerprint = candidateFingerprint,
            session_id = sessionId,
            snapshot_at = manifest.SnapshotAt,
        }, AssetCatalogJson.SerializerOptions);
        var requestId = Hash(requestBytes);
        var requests = Path.Combine(mailbox, "private", "catalog-commit", "requests");
        Directory.CreateDirectory(requests);
        File.WriteAllBytes(Path.Combine(requests, requestId + ".json"), requestBytes);
        return requestId;
    }

    private static long ReadResponseRevision(string mailbox, string requestId)
    {
        using var response = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            mailbox, "private", "catalog-commit", "responses", requestId + ".json")));
        return response.RootElement.GetProperty("catalog_revision").GetInt64();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
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
        OriginalModsDirectory = Path.Combine(Root, "original-mods", "custom_snap");
        ClaudePath = Path.Combine(Root, "Claude", "claude_desktop_config.json");
        GeminiPath = Path.Combine(Root, ".gemini", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(ClaudePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(GeminiPath)!);
        Directory.CreateDirectory(UserDataDirectory);
        Directory.CreateDirectory(Path.Combine(OriginalModsDirectory, "scripts"));
        File.WriteAllText(
            Path.Combine(OriginalModsDirectory, "snappy_mod.ddmod"),
            "{\"name\":\"Custom Snap Mod\",\"unique_id\":\"Lievven.Snappy_Mod\",\"dd_version\":\"1.1.0.6\"}");
        File.WriteAllText(
            Path.Combine(OriginalModsDirectory, "scripts", "snappy_mod.gd"),
            "func load_local_settings():\n    var data = Global.ModMapData[TOOL_ID]\n    if data == null or data.empty():\n        return\n");
        File.WriteAllText(
            Path.Combine(UserDataDirectory, "config.ini"),
            "[Mods]\r\nactive_mods=[ ]\r\nmods_directory=\"" +
            OriginalModsDirectory.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"\r\n");
        SourceExecutable = sourceExecutable;
        SourceModDirectory = Path.Combine(PublishedExecutableFixture.FindRepositoryRoot(), "mods", "DDAI");
    }

    public string Root { get; }
    public string SourceExecutable { get; }
    public string SourceModDirectory { get; }
    public string InstallRoot { get; }
    public string ModsDirectory { get; }
    public string UserDataDirectory { get; }
    public string OriginalModsDirectory { get; }
    public string ClaudePath { get; }
    public string GeminiPath { get; }
    public string InstalledExecutable => Path.Combine(InstallRoot, "ddai.exe");
    public string MetadataPath => Path.Combine(InstallRoot, "install-metadata.json");
    public string InstalledModDirectory => Path.Combine(ModsDirectory, "DDAI");
    public string ConfigPath => Path.Combine(UserDataDirectory, "config.ini");
    public string CopiedCustomSnapDirectory => Path.Combine(ModsDirectory, "custom_snap");

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
