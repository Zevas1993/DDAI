using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DDAI.App.Assets;
using DDAI.App.Mcp;
using DDAI.Core.Assets;
using DDAI.Core.Mailbox;
using DDAI.Core.MapPlans;
using DDAI.Core.Maps;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DDAI.App.Tests.Mcp;

public sealed class DdaiMapInspectionToolTests
{
    [Fact]
    public void InspectMap_DeclaresClosedReadOnlySafetyMetadata()
    {
        var method = typeof(DdaiMapTools).GetMethod(nameof(DdaiMapTools.InspectMapAsync), BindingFlags.Public | BindingFlags.Static);
        var attribute = Assert.Single(method!.GetCustomAttributes<McpServerToolAttribute>());

        Assert.Equal("ddai_inspect_map", attribute.Name);
        Assert.True(attribute.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
    }

    [Fact]
    public async Task InspectMap_UsesRealMailboxAndReturnsCorrelatedStructuredPage()
    {
        using var sandbox = new InspectionSandbox();
        var asset = sandbox.PublishAcceptedCatalog(73);
        var service = sandbox.CreateService();
        var query = new MapInspectionQuery(new MapInspectionRegion(1, 2, 10, 8), Level: 3, Limit: 1);
        var expectedPage = Page(asset.AssetRef);
        MailboxRequest? observed = null;
        var bridge = Task.Run(() =>
        {
            var claim = WaitForClaim(sandbox.Mailbox);
            observed = claim.Request;
            sandbox.Mailbox.PublishResponse(claim, SuccessResponse(claim.Request, expectedPage));
        });

        var result = await DdaiMapTools.InspectMapAsync(
            query,
            service,
            new DdaiMcpRuntimeOptions(TimeSpan.FromSeconds(2)),
            CancellationToken.None);
        await bridge;

        Assert.Null(result.IsError);
        Assert.NotNull(result.StructuredContent);
        using var structured = StructuredJson(result);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), JsonNode.Parse(structured.RootElement.GetRawText())));
        Assert.Equal(new string('b', 64), structured.RootElement.GetProperty("map_revision").GetString());
        Assert.Equal(73, structured.RootElement.GetProperty("catalog_revision").GetInt64());
        Assert.Equal(asset.AssetRef, structured.RootElement.GetProperty("items")[0].GetProperty("asset_ref").GetString());
        Assert.Equal("inspect_map", observed!.Command);
        Assert.Equal(3, observed.Payload.GetProperty("level").GetInt32());
        Assert.Equal(1, observed.Payload.GetProperty("limit").GetInt32());
        Assert.Equal(1, observed.Payload.GetProperty("region").GetProperty("x").GetDouble());
    }

    [Fact]
    public async Task InspectMap_RejectsInvalidInputWithoutPublishingAndBoundsTheToolError()
    {
        using var sandbox = new InspectionSandbox();

        var result = await DdaiMapTools.InspectMapAsync(
            new MapInspectionQuery(Limit: 0),
            sandbox.CreateService(),
            new DdaiMcpRuntimeOptions(TimeSpan.FromMilliseconds(100)),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("invalid_request", ErrorCode(result));
        Assert.Empty(Directory.GetFiles(Path.Combine(sandbox.Root, "requests")));
        Assert.DoesNotContain(sandbox.Root, ErrorText(result), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exception", ErrorText(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InspectMap_FailsClosedForWrongCommandAndUncorrelatedCatalogReference()
    {
        using var wrongCommandSandbox = new InspectionSandbox();
        var wrongCommandBridge = Task.Run(() =>
        {
            var claim = WaitForClaim(wrongCommandSandbox.Mailbox);
            var forged = SuccessResponse(claim.Request, Page()) with { Command = "status" };
            WriteForgedResponse(wrongCommandSandbox.Root, claim.Request.RequestId, forged);
        });
        var wrongCommand = await DdaiMapTools.InspectMapAsync(
            new MapInspectionQuery(Limit: 1),
            wrongCommandSandbox.CreateService(),
            new DdaiMcpRuntimeOptions(TimeSpan.FromSeconds(2)),
            CancellationToken.None);
        await wrongCommandBridge;

        using var wrongRequestSandbox = new InspectionSandbox();
        var wrongRequestBridge = Task.Run(() =>
        {
            var claim = WaitForClaim(wrongRequestSandbox.Mailbox);
            var forged = SuccessResponse(claim.Request, Page()) with { RequestId = "different-request" };
            WriteForgedResponse(wrongRequestSandbox.Root, claim.Request.RequestId, forged);
        });
        var wrongRequest = await DdaiMapTools.InspectMapAsync(
            new MapInspectionQuery(Limit: 1),
            wrongRequestSandbox.CreateService(),
            new DdaiMcpRuntimeOptions(TimeSpan.FromSeconds(2)),
            CancellationToken.None);
        await wrongRequestBridge;

        using var catalogSandbox = new InspectionSandbox();
        _ = catalogSandbox.PublishAcceptedCatalog(73);
        var catalogBridge = Task.Run(() =>
        {
            var claim = WaitForClaim(catalogSandbox.Mailbox);
            catalogSandbox.Mailbox.PublishResponse(claim, SuccessResponse(claim.Request, Page("sha256:" + new string('f', 64))));
        });
        var catalogMismatch = await DdaiMapTools.InspectMapAsync(
            new MapInspectionQuery(Limit: 1),
            catalogSandbox.CreateService(),
            new DdaiMcpRuntimeOptions(TimeSpan.FromSeconds(2)),
            CancellationToken.None);
        await catalogBridge;

        Assert.True(wrongCommand.IsError);
        Assert.Equal("invalid_response", ErrorCode(wrongCommand));
        Assert.True(wrongRequest.IsError);
        Assert.Equal("invalid_response", ErrorCode(wrongRequest));
        Assert.True(catalogMismatch.IsError);
        Assert.Equal("catalog_revision_mismatch", ErrorCode(catalogMismatch));
    }

    [Fact]
    public async Task InspectMap_RejectsPagesOutsideTheSubmittedLimitLevelAndRegion()
    {
        var overLimit = await InvokeWithPageAsync(
            new MapInspectionQuery(Limit: 1),
            Page() with
            {
                Items =
                [
                    new MapSnapshotItem(7, "wall", new MapSnapshotBounds(1, 2, 3, 4), 3, null),
                    new MapSnapshotItem(8, "wall", new MapSnapshotBounds(5, 2, 3, 4), 3, null),
                ],
            });
        var wrongLevel = await InvokeWithPageAsync(
            new MapInspectionQuery(Level: 4, Limit: 1),
            Page());
        var outsideRegion = await InvokeWithPageAsync(
            new MapInspectionQuery(new MapInspectionRegion(20, 20, 2, 2), Level: 3, Limit: 1),
            Page());

        Assert.Equal("invalid_response", ErrorCode(overLimit));
        Assert.Equal("invalid_response", ErrorCode(wrongLevel));
        Assert.Equal("invalid_response", ErrorCode(outsideRegion));
    }

    [Fact]
    public async Task InspectMap_RejectsCursorsNotCorrelatedToReturnedStateAndExactProgression()
    {
        var arbitrarySubmittedCursor = new string('c', 64) + ":0";
        var submitted = await InvokeWithPageAsync(
            new MapInspectionQuery(Level: 3, Limit: 1, Cursor: arbitrarySubmittedCursor),
            Page());
        var arbitraryNext = await InvokeWithPageAsync(
            new MapInspectionQuery(Level: 3, Limit: 1),
            Page() with
            {
                NextCursor = new string('d', 64) + ":1",
                Truncated = true,
            });

        Assert.Equal("invalid_response", ErrorCode(submitted));
        Assert.Equal("invalid_response", ErrorCode(arbitraryNext));
    }

    [Fact]
    public async Task InspectMap_PropagatesCancellationWithoutWaitingForMailboxTimeout()
    {
        using var sandbox = new InspectionSandbox();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sandbox.CreateService().InspectAsync(
            new MapInspectionQuery(Limit: 1),
            TimeSpan.FromSeconds(30),
            cancellation.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact(Timeout = 120_000)]
    public async Task PublishedOfficialSdk_DiscoversBindsAndCallsInspectMapWithoutStdoutContamination()
    {
        using var sandbox = new InspectionSandbox();
        var asset = sandbox.PublishAcceptedCatalog(73);
        var executable = PublishSingleFile(sandbox.PublishRoot);
        using var workerCancellation = new CancellationTokenSource();
        var worker = Task.Run(async () =>
        {
            while (!workerCancellation.IsCancellationRequested)
            {
                var claim = sandbox.Mailbox.ClaimNextRequest();
                if (claim is null)
                {
                    await Task.Delay(10, workerCancellation.Token);
                    continue;
                }

                sandbox.Mailbox.PublishResponse(claim, SuccessResponse(claim.Request, Page(asset.AssetRef)));
            }
        }, workerCancellation.Token);
        var stderr = new ConcurrentQueue<string>();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ddai-map-inspection-published-sdk",
            Command = executable,
            Arguments = ["serve", "--stdio", "--mailbox-root", sandbox.Root, "--timeout-ms", "2000"],
            WorkingDirectory = sandbox.PublishRoot,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            StandardErrorLines = stderr.Enqueue,
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
            var tool = Assert.Single(
                await client.ListToolsAsync(cancellationToken: deadline.Token),
                candidate => candidate.Name == "ddai_inspect_map");
            var queryProperties = tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("query").GetProperty("properties");
            foreach (var field in new[] { "region", "level", "limit", "cursor" })
            {
                Assert.True(queryProperties.TryGetProperty(field, out _), $"Missing inspect-map query field '{field}'.");
            }

            var result = await client.CallToolAsync(
                "ddai_inspect_map",
                new Dictionary<string, object?> { ["query"] = new { level = 3, limit = 1 } },
                cancellationToken: deadline.Token);

            Assert.Null(result.IsError);
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            using var document = JsonDocument.Parse(text);
            Assert.Equal(new string('b', 64), document.RootElement.GetProperty("map_revision").GetString());
            Assert.Equal(73, document.RootElement.GetProperty("catalog_revision").GetInt64());
            Assert.Equal(asset.AssetRef, document.RootElement.GetProperty("items")[0].GetProperty("asset_ref").GetString());
            Assert.True(result.StructuredContent.HasValue);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), JsonNode.Parse(result.StructuredContent.Value.GetRawText())));
            Assert.Empty(stderr);
        }
        finally
        {
            workerCancellation.Cancel();
            try
            {
                await worker;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static MailboxResponse SuccessResponse(MailboxRequest request, MapSnapshotPage page) => new()
    {
        SchemaVersion = MailboxRequest.CurrentSchemaVersion,
        RequestId = request.RequestId,
        Command = request.Command,
        Timestamp = DateTimeOffset.UtcNow,
        Success = true,
        Payload = JsonSerializer.Deserialize<JsonElement>(MapSnapshotJson.SerializePage(page)),
    };

    private static async Task<CallToolResult> InvokeWithPageAsync(
        MapInspectionQuery query,
        MapSnapshotPage page)
    {
        using var sandbox = new InspectionSandbox();
        var bridge = Task.Run(() =>
        {
            var claim = WaitForClaim(sandbox.Mailbox);
            sandbox.Mailbox.PublishResponse(claim, SuccessResponse(claim.Request, page));
        });
        var result = await DdaiMapTools.InspectMapAsync(
            query,
            sandbox.CreateService(),
            new DdaiMcpRuntimeOptions(TimeSpan.FromSeconds(2)),
            CancellationToken.None);
        await bridge;
        return result;
    }

    private static MapSnapshotPage Page(string? assetRef = null) => new(
        new string('a', 64),
        new string('b', 64),
        new MapCanvas(40, 30),
        256,
        [new MapSnapshotLevel(3, "Ground", true)],
        [new MapSnapshotItem(7, "wall", new MapSnapshotBounds(1, 2, 3, 4), 3, assetRef)],
        null,
        false,
        ["object", "portal", "light", "text", "material", "floor_shape"]);

    private static ClaimedMailboxRequest WaitForClaim(AtomicMailbox mailbox)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (mailbox.ClaimNextRequest() is { } claim)
            {
                return claim;
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException("The inspection request was not published.");
    }

    private static void WriteForgedResponse(string root, string requestId, MailboxResponse response)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId))).ToLowerInvariant();
        File.WriteAllText(
            Path.Combine(root, "responses", key + ".json"),
            JsonSerializer.Serialize(response, MailboxWireJson.Options));
    }

    private static JsonDocument StructuredJson(CallToolResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result.StructuredContent));

    private static string ErrorCode(CallToolResult result) =>
        JsonDocument.Parse(ErrorText(result)).RootElement.GetProperty("error").GetString()!;

    private static string ErrorText(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

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
        Assert.True(process.ExitCode == 0, $"dotnet publish failed:{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        var executable = Path.Combine(outputDirectory, "ddai.exe");
        Assert.True(File.Exists(executable), $"Published executable not found: {executable}");
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

    private sealed class InspectionSandbox : IDisposable
    {
        public InspectionSandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "ddai-map-inspection-tests", Guid.NewGuid().ToString("N"));
            PublishRoot = Path.Combine(Root, "publish");
            Directory.CreateDirectory(PublishRoot);
            Mailbox = new AtomicMailbox(Root);
        }

        public string Root { get; }

        public string PublishRoot { get; }

        public AtomicMailbox Mailbox { get; }

        public DdaiMapInspectionService CreateService() => new(
            Mailbox,
            new AssetCatalogRepository(Path.Combine(Root, "catalog"), TimeProvider.System),
            TimeProvider.System);

        public AssetCatalogEntry PublishAcceptedCatalog(long revision)
        {
            var catalogRoot = Path.Combine(Root, "catalog");
            var snapshotRoot = Path.Combine(catalogRoot, "snapshots", "inspection-fixture");
            Directory.CreateDirectory(snapshotRoot);
            var entry = new AssetCatalogEntry(
                AssetReference.Create("official-pack", "Walls", "resource/wall-stone"),
                "Walls",
                "Stone Wall",
                Hash("resource/wall-stone"),
                "official-pack",
                "Official Pack",
                ["stone"],
                ["fixture"],
                null,
                true,
                false);
            var chunkBytes = Encoding.UTF8.GetBytes(AssetCatalogJson.SerializeChunk([entry]));
            File.WriteAllBytes(Path.Combine(snapshotRoot, "assets-000.json"), chunkBytes);
            var manifest = new AssetCatalogManifest(
                AssetCatalogManifest.CurrentSchemaVersion,
                "inspection-fixture-session",
                revision,
                new string('0', 64),
                DateTimeOffset.UtcNow,
                true,
                AssetCategory.All.ToDictionary(category => category, category => category == "Walls" ? 1 : 0, StringComparer.Ordinal),
                [new AssetCatalogChunk("assets-000.json", Hash(chunkBytes), 1, chunkBytes.LongLength)],
                []);
            manifest = manifest with { CatalogFingerprint = AssetCatalogJson.ComputeCatalogFingerprint(manifest) };
            File.WriteAllText(Path.Combine(snapshotRoot, "manifest.json"), AssetCatalogJson.SerializeManifest(manifest));
            File.WriteAllText(
                Path.Combine(catalogRoot, "current.json"),
                JsonSerializer.Serialize(new
                {
                    manifest = "inspection-fixture/manifest.json",
                    session_id = "inspection-fixture-session",
                    catalog_revision = revision,
                }));
            return entry;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));

        private static string Hash(byte[] value) =>
            Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    }
}
