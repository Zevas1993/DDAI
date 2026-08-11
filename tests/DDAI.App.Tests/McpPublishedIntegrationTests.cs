using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DDAI.Core.Assets;
using DDAI.Core.Mailbox;
using DDAI.Core.MapPlans;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DDAI.App.Tests;

[Collection("Published executable")]
public sealed class McpPublishedIntegrationTests
{
    [Fact(Timeout = 120_000)]
    public async Task SdkClient_DiscoversBindsAndCallsPublishedAssetSearchWithStructuredContent()
    {
        using var sandbox = new TestDirectory();
        sandbox.PublishAcceptedAssetCatalog();
        var executable = PublishSingleFile(sandbox.PublishDirectory);
        var stderr = new ConcurrentQueue<string>();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ddai-published-asset-search-integration",
            Command = executable,
            Arguments = ["serve", "--stdio", "--mailbox-root", sandbox.MailboxDirectory, "--timeout-ms", "2000"],
            WorkingDirectory = sandbox.PublishDirectory,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            StandardErrorLines = stderr.Enqueue,
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
        var tool = Assert.Single(
            await client.ListToolsAsync(cancellationToken: deadline.Token),
            candidate => candidate.Name == "ddai_search_assets");
        var querySchema = tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("query");
        var queryFields = querySchema.GetProperty("properties");
        foreach (var field in new[]
                 {
                     "query", "categories", "tags", "packIds", "generated", "previewRequired",
                     "includeStagedGenerated", "limit", "cursor",
                 })
        {
            Assert.True(queryFields.TryGetProperty(field, out _), $"Missing asset-search query field '{field}'.");
        }

        var result = await client.CallToolAsync(
            "ddai_search_assets",
            new Dictionary<string, object?>
            {
                ["query"] = new
                {
                    query = "ancient",
                    categories = new[] { "Objects" },
                    tags = new[] { "fixture" },
                    generated = false,
                    previewRequired = false,
                    limit = 1,
                },
            },
            cancellationToken: deadline.Token);

        Assert.Null(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        using var textDocument = JsonDocument.Parse(text);
        Assert.Equal("Ancient Gate", textDocument.RootElement.GetProperty("items")[0].GetProperty("display_name").GetString());
        Assert.True(result.StructuredContent.HasValue);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(text),
            JsonNode.Parse(result.StructuredContent.Value.GetRawText())));
        Assert.Empty(stderr);
    }

    [Fact(Timeout = 120_000)]
    public async Task SdkClient_InitializesListsAndCallsPublishedStdioServerWithoutStdoutContamination()
    {
        using var sandbox = new TestDirectory();
        var executable = PublishSingleFile(sandbox.PublishDirectory);
        var mailbox = new AtomicMailbox(sandbox.MailboxDirectory);
        var fakeMod = new FakeModHarness(mailbox);
        using var workerCancellation = new CancellationTokenSource();
        var worker = Task.Run(async () =>
        {
            while (!workerCancellation.IsCancellationRequested)
            {
                if (!fakeMod.HandleOne())
                {
                    await Task.Delay(10, workerCancellation.Token);
                }
            }
        }, workerCancellation.Token);
        var stderr = new ConcurrentQueue<string>();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ddai-published-integration",
            Command = executable,
            Arguments = ["serve", "--stdio", "--mailbox-root", sandbox.MailboxDirectory, "--timeout-ms", "2000"],
            WorkingDirectory = sandbox.PublishDirectory,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            StandardErrorLines = stderr.Enqueue,
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
        var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
        Assert.Contains(tools, tool => tool.Name == "ddai_status");
        Assert.Contains(tools, tool => tool.Name == "ddai_validate_plan");
        Assert.Contains(tools, tool => tool.Name == "ddai_apply_plan");

        var result = await client.CallToolAsync(
            "ddai_status",
            new Dictionary<string, object?>(),
            cancellationToken: deadline.Token);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        using var document = JsonDocument.Parse(text);
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("ready", document.RootElement.GetProperty("payload").GetProperty("state").GetString());

        var plan = ValidPlan("published-plan-001");
        var arguments = new Dictionary<string, object?> { ["plan"] = plan };
        var validationResult = await client.CallToolAsync(
            "ddai_validate_plan",
            arguments,
            cancellationToken: deadline.Token);
        var validationText = Assert.IsType<TextContentBlock>(Assert.Single(validationResult.Content)).Text;
        using var validationDocument = JsonDocument.Parse(validationText);
        Assert.True(validationDocument.RootElement.GetProperty("valid").GetBoolean());

        var applyResult = await client.CallToolAsync(
            "ddai_apply_plan",
            arguments,
            cancellationToken: deadline.Token);
        var applyText = Assert.IsType<TextContentBlock>(Assert.Single(applyResult.Content)).Text;
        using var applyDocument = JsonDocument.Parse(applyText);
        Assert.True(applyDocument.RootElement.GetProperty("success").GetBoolean());
        var payload = applyDocument.RootElement.GetProperty("payload");
        Assert.Equal("room-entrance", payload.GetProperty("room_id").GetString());
        Assert.Equal(1, payload.GetProperty("created_walls").GetInt32());
        Assert.Equal("Use Dungeondraft Undo once", payload.GetProperty("undo_instruction").GetString());
        Assert.Equal(MapPlanJson.Fingerprint(plan), payload.GetProperty("plan_fingerprint").GetString());

        workerCancellation.Cancel();
        try
        {
            await worker;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static MapPlan ValidPlan(string requestId) => new()
    {
        SchemaVersion = MapPlan.CurrentSchemaVersion,
        RequestId = requestId,
        BaseRevision = 0,
        Mode = MapOperationMode.Add,
        Canvas = new MapCanvas(40, 30),
        Rooms = [new MapRoom("room-entrance", 8, 7, 10, 8)],
    };

    private static string PublishSingleFile(string outputDirectory)
    {
        var repositoryRoot = FindRepositoryRoot();
        var project = Path.Combine(repositoryRoot, "src", "DDAI.App", "DDAI.App.csproj");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
                 {
                     "publish", project, "-c", "Release", "-r", "win-x64", "--self-contained", "true",
                     "-p:PublishSingleFile=true", "-p:DebugType=None", "--no-restore", "-o", outputDirectory,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start dotnet publish.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"dotnet publish failed:\n{stdout}\n{stderr}");
        var executable = Path.Combine(outputDirectory, "ddai.exe");
        Assert.True(File.Exists(executable), $"Published executable not found: {executable}");
        Assert.Equal(["ddai.exe"], Directory.GetFiles(outputDirectory).Select(path => Path.GetFileName(path)!).Order().ToArray());
        return executable;
    }

    private static string FindRepositoryRoot()
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

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "ddai-mcp-tests", Guid.NewGuid().ToString("N"));
            PublishDirectory = Path.Combine(Root, "publish");
            MailboxDirectory = Path.Combine(Root, "mailbox");
            Directory.CreateDirectory(PublishDirectory);
            Directory.CreateDirectory(MailboxDirectory);
        }

        public string Root { get; }
        public string PublishDirectory { get; }
        public string MailboxDirectory { get; }

        public void PublishAcceptedAssetCatalog()
        {
            var catalogRoot = Path.Combine(MailboxDirectory, "catalog");
            var snapshotRoot = Path.Combine(catalogRoot, "snapshots", "fixture-snapshot");
            Directory.CreateDirectory(snapshotRoot);
            var entry = new AssetCatalogEntry(
                AssetReference.Create("official-pack", "Objects", "resource/ancient-gate"),
                "Objects",
                "Ancient Gate",
                Hash("resource/ancient-gate"),
                "official-pack",
                "Official Pack",
                ["ancient"],
                ["fixture"],
                null,
                false,
                false);
            var chunkBytes = Encoding.UTF8.GetBytes(AssetCatalogJson.SerializeChunk([entry]));
            File.WriteAllBytes(Path.Combine(snapshotRoot, "assets-000.json"), chunkBytes);
            var manifest = new AssetCatalogManifest(
                AssetCatalogManifest.CurrentSchemaVersion,
                "published-fixture",
                1,
                new string('0', 64),
                DateTimeOffset.UtcNow,
                true,
                AssetCategory.All.ToDictionary(category => category, category => category == "Objects" ? 1 : 0, StringComparer.Ordinal),
                [new AssetCatalogChunk("assets-000.json", Hash(chunkBytes), 1, chunkBytes.LongLength)],
                []);
            manifest = manifest with { CatalogFingerprint = AssetCatalogJson.ComputeCatalogFingerprint(manifest) };
            File.WriteAllText(Path.Combine(snapshotRoot, "manifest.json"), AssetCatalogJson.SerializeManifest(manifest));
            File.WriteAllText(
                Path.Combine(catalogRoot, "current.json"),
                JsonSerializer.Serialize(new { manifest = "fixture-snapshot/manifest.json", session_id = "published-fixture" }));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

        private static string Hash(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
