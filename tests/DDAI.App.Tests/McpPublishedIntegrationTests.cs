using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DDAI.App.Assets;
using DDAI.Core.Assets;
using DDAI.Core.Mailbox;
using DDAI.Core.MapPlans;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SkiaSharp;

namespace DDAI.App.Tests;

[Collection("Published executable")]
public sealed class McpPublishedIntegrationTests
{
    [Fact(Timeout = 120_000)]
    public async Task PublishedAssetPreview_EmitsOnlyJsonRpcFramesOnStdout()
    {
        using var sandbox = new TestDirectory();
        var asset = sandbox.PublishAcceptedAssetCatalog(includePreview: true);
        var executable = PublishSingleFile(sandbox.PublishDirectory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var process = StartCapturedServer(executable, sandbox);

        try
        {
            await WriteFrameAsync(process.StandardInput, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-11-25",
                    capabilities = new { },
                    clientInfo = new { name = "ddai-stdout-capture", version = "1.0" },
                },
            });
            AssertJsonRpcResponse(await ReadFrameAsync(process.StandardOutput, 1, deadline.Token), 1);

            await WriteFrameAsync(process.StandardInput, new { jsonrpc = "2.0", method = "notifications/initialized" });
            await WriteFrameAsync(process.StandardInput, new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new
                {
                    name = "ddai_get_asset_preview",
                    arguments = new Dictionary<string, object?> { ["assetRef"] = asset.AssetRef },
                },
            });
            AssertJsonRpcResponse(await ReadFrameAsync(process.StandardOutput, 2, deadline.Token), 2);

            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            var trailing = await process.StandardOutput.ReadToEndAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, process.StandardError.ReadToEnd());
            Assert.All(trailing.Split('\n', StringSplitOptions.None).Where(line => line.Length > 0),
                line => AssertJsonRpcResponse(line, expectedId: null));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task SdkClient_DiscoversAndCallsPublishedAssetPreviewWithBoundedPngContent()
    {
        using var sandbox = new TestDirectory();
        var asset = sandbox.PublishAcceptedAssetCatalog(includePreview: true);
        var executable = PublishSingleFile(sandbox.PublishDirectory);
        var stderr = new ConcurrentQueue<string>();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ddai-published-asset-preview-integration",
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
            candidate => candidate.Name == "ddai_get_asset_preview");
        Assert.True(tool.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("assetRef", out _));

        var result = await client.CallToolAsync(
            "ddai_get_asset_preview",
            new Dictionary<string, object?> { ["assetRef"] = asset.AssetRef },
            cancellationToken: deadline.Token);

        Assert.Null(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content.OfType<TextContentBlock>())).Text;
        var image = Assert.IsType<ImageContentBlock>(Assert.Single(result.Content.OfType<ImageContentBlock>()));
        Assert.Equal(2, result.Content.Count);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(sandbox.PreviewBytes, image.DecodedData.ToArray());
        Assert.True(image.DecodedData.Length <= AssetCatalogRepository.MaximumPreviewBytes);
        using var bitmap = SKBitmap.Decode(image.DecodedData.ToArray());
        Assert.NotNull(bitmap);
        Assert.Equal(1, bitmap.Width);
        Assert.Equal(1, bitmap.Height);
        Assert.True(result.StructuredContent.HasValue);
        using var textDocument = JsonDocument.Parse(text);
        Assert.Equal(asset.AssetRef, textDocument.RootElement.GetProperty("asset_ref").GetString());
        Assert.Equal("image/png", textDocument.RootElement.GetProperty("mime_type").GetString());
        Assert.Equal(image.DecodedData.Length, textDocument.RootElement.GetProperty("byte_count").GetInt32());
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(text),
            JsonNode.Parse(result.StructuredContent.Value.GetRawText())));
        Assert.Empty(stderr);
    }

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
    public async Task SdkClient_ImportsGeneratedPngIdempotentlyAndFindsStagedNonPlaceableAsset()
    {
        using var sandbox = new TestDirectory();
        sandbox.PublishAcceptedAssetCatalog();
        var executable = PublishSingleFile(sandbox.PublishDirectory);
        var stderr = new ConcurrentQueue<string>();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ddai-published-generated-import-integration",
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
            candidate => candidate.Name == "ddai_import_asset");
        var requestSchema = tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("request").GetProperty("properties");
        foreach (var field in new[] { "idempotencyKey", "category", "name", "tags", "gridWidth", "gridHeight", "contentBase64", "inboxToken" })
        {
            Assert.True(requestSchema.TryGetProperty(field, out _), $"Missing generated-import request field '{field}'.");
        }

        var request = new
        {
            idempotencyKey = "published-generated-import-001",
            category = "Objects",
            name = "Generated Torch",
            tags = new[] { "generated", "fixture" },
            gridWidth = 1,
            gridHeight = 1,
            contentBase64 = Convert.ToBase64String(sandbox.PreviewBytes),
        };
        var first = await client.CallToolAsync(
            "ddai_import_asset",
            new Dictionary<string, object?> { ["request"] = request },
            cancellationToken: deadline.Token);
        var replay = await client.CallToolAsync(
            "ddai_import_asset",
            new Dictionary<string, object?> { ["request"] = request },
            cancellationToken: deadline.Token);
        var conflict = await client.CallToolAsync(
            "ddai_import_asset",
            new Dictionary<string, object?>
            {
                ["request"] = new
                {
                    idempotencyKey = request.idempotencyKey,
                    category = request.category,
                    name = "Changed Torch",
                    tags = request.tags,
                    gridWidth = request.gridWidth,
                    gridHeight = request.gridHeight,
                    contentBase64 = request.contentBase64,
                },
            },
            cancellationToken: deadline.Token);

        Assert.Null(first.IsError);
        var firstText = Assert.IsType<TextContentBlock>(Assert.Single(first.Content.OfType<TextContentBlock>())).Text;
        var firstImage = Assert.IsType<ImageContentBlock>(Assert.Single(first.Content.OfType<ImageContentBlock>()));
        Assert.Equal("image/png", firstImage.MimeType);
        Assert.True(firstImage.DecodedData.Length <= GeneratedAssetStore.MaximumPreviewBytes);
        using var firstJson = JsonDocument.Parse(firstText);
        var generatedAssetId = firstJson.RootElement.GetProperty("generated_asset_id").GetString();
        Assert.False(firstJson.RootElement.GetProperty("duplicate").GetBoolean());
        Assert.True(replay.StructuredContent.HasValue);
        using var replayJson = JsonDocument.Parse(replay.StructuredContent.Value.GetRawText());
        Assert.Equal(generatedAssetId, replayJson.RootElement.GetProperty("generated_asset_id").GetString());
        Assert.True(replayJson.RootElement.GetProperty("duplicate").GetBoolean());
        Assert.True(conflict.IsError);
        Assert.Contains("request_conflict", Assert.IsType<TextContentBlock>(Assert.Single(conflict.Content)).Text, StringComparison.Ordinal);

        var found = await client.CallToolAsync(
            "ddai_search_assets",
            new Dictionary<string, object?>
            {
                ["query"] = new { generated = true, includeStagedGenerated = true, limit = 10 },
            },
            cancellationToken: deadline.Token);
        Assert.Null(found.IsError);
        using var foundJson = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(found.Content)).Text);
        Assert.Matches("^[0-9a-f]{64}$", generatedAssetId);
        var staged = Assert.Single(foundJson.RootElement.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("display_name").GetString() == "Generated Torch");
        Assert.True(staged.GetProperty("generated").GetBoolean());
        Assert.False(staged.GetProperty("placeable").GetBoolean());
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

    private static Process StartCapturedServer(string executable, TestDirectory sandbox)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = sandbox.PublishDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "serve", "--stdio", "--mailbox-root", sandbox.MailboxDirectory, "--timeout-ms", "2000" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start published DDAI server.");
    }

    private static async Task WriteFrameAsync(StreamWriter input, object frame)
    {
        await input.WriteLineAsync(JsonSerializer.Serialize(frame));
        await input.FlushAsync();
    }

    private static async Task<string> ReadFrameAsync(StreamReader output, int expectedId, CancellationToken cancellationToken)
    {
        var line = await output.ReadLineAsync(cancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(line), "Published server closed stdout before its JSON-RPC response.");
        AssertJsonRpcResponse(line!, expectedId);
        return line;
    }

    private static void AssertJsonRpcResponse(string line, int? expectedId)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.True(root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _));
        if (expectedId is not null)
        {
            Assert.Equal(expectedId.Value, root.GetProperty("id").GetInt32());
        }
    }

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

        public byte[] PreviewBytes { get; } = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP4z8DwHwAFAAH/VscvDQAAAABJRU5ErkJggg==");

        public AssetCatalogEntry PublishAcceptedAssetCatalog(bool includePreview = false)
        {
            var catalogRoot = Path.Combine(MailboxDirectory, "catalog");
            var snapshotRoot = Path.Combine(catalogRoot, "snapshots", "fixture-snapshot");
            Directory.CreateDirectory(snapshotRoot);
            var previewHash = includePreview ? Hash(PreviewBytes) : null;
            if (previewHash is not null)
            {
                Directory.CreateDirectory(Path.Combine(catalogRoot, "previews"));
                File.WriteAllBytes(Path.Combine(catalogRoot, "previews", previewHash + ".png"), PreviewBytes);
            }
            var entry = new AssetCatalogEntry(
                AssetReference.Create("official-pack", "Objects", "resource/ancient-gate"),
                "Objects",
                "Ancient Gate",
                Hash("resource/ancient-gate"),
                "official-pack",
                "Official Pack",
                ["ancient"],
                ["fixture"],
                previewHash,
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
            return entry;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

        private static string Hash(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
