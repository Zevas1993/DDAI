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
using DDAI.Core.Maps;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SkiaSharp;

namespace DDAI.App.Tests;

[Collection("Published executable")]
public sealed class McpPublishedIntegrationTests
{
    [Fact]
    public async Task Status_EnrichesTheUnchangedBridgeResponseWithBoundedDiscoveryState()
    {
        using var sandbox = new TestDirectory();
        var now = DateTimeOffset.Parse("2026-08-10T20:00:00Z");
        sandbox.PublishAcceptedAssetCatalog(
            snapshotAt: now - TimeSpan.FromSeconds(12),
            revision: 73,
            complete: true,
            errors: [new AssetCatalogError("category_partial", "One fixture category is incomplete.", "Objects")]);
        var mailbox = new AtomicMailbox(sandbox.MailboxDirectory);
        var service = new DdaiStatusService(mailbox, new FixedTimeProvider(now));
        var mapRevision = new string('b', 64);
        string[] bridgeCapabilities = ["status", "apply_plan", "inspect_map"];
        var worker = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var claim = mailbox.ClaimNextRequest();
                if (claim is null)
                {
                    await Task.Delay(10);
                    continue;
                }

                Assert.Equal("status", claim.Request.Command);
                mailbox.PublishResponse(claim, new MailboxResponse
                {
                    SchemaVersion = MailboxRequest.CurrentSchemaVersion,
                    RequestId = claim.Request.RequestId,
                    Command = claim.Request.Command,
                    Timestamp = now,
                    Success = true,
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        state = "ready",
                        revision = mapRevision,
                        supported_commands = bridgeCapabilities,
                    }),
                });
                return;
            }

            throw new TimeoutException("The status request was not published to the real atomic mailbox.");
        });

        var result = await service.GetStatusAsync(TimeSpan.FromSeconds(2));
        await worker;
        var wire = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        });

        Assert.True(wire.GetProperty("success").GetBoolean());
        Assert.Equal("status", wire.GetProperty("command").GetString());
        Assert.Null(wire.GetProperty("error").Deserialize<MailboxErrorDetails>());
        Assert.Equal("ready", wire.GetProperty("payload").GetProperty("state").GetString());
        Assert.Equal(mapRevision, wire.GetProperty("payload").GetProperty("revision").GetString());
        Assert.Equal(bridgeCapabilities, wire.GetProperty("payload").GetProperty("supported_commands").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal(mapRevision, wire.GetProperty("map_revision").GetString());
        Assert.Equal(73, wire.GetProperty("catalog_revision").GetInt64());
        Assert.Equal(12_000, wire.GetProperty("catalog_cache_age_milliseconds").GetInt64());
        Assert.True(wire.GetProperty("catalog_live").GetBoolean());
        Assert.True(wire.GetProperty("catalog_complete").GetBoolean());
        Assert.Equal(1, wire.GetProperty("catalog_entry_count").GetInt32());
        var catalogError = Assert.Single(wire.GetProperty("catalog_errors").EnumerateArray());
        Assert.Equal("category_partial", catalogError.GetProperty("code").GetString());
        Assert.Equal("Objects", catalogError.GetProperty("category").GetString());
        Assert.Equal(bridgeCapabilities, wire.GetProperty("bridge_capabilities").EnumerateArray().Select(item => item.GetString()).ToArray());
    }

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
        var asset = sandbox.PublishAcceptedAssetCatalog(includePreview: true, revision: 73);
        sandbox.WriteRuntimeReceipt();
        var executable = PublishSingleFile(sandbox.PublishDirectory);
        var mailbox = new AtomicMailbox(sandbox.MailboxDirectory);
        var fakeBridge = new PublishedDiscoveryBridgeHarness(mailbox, asset.AssetRef);
        using var workerCancellation = new CancellationTokenSource();
        var worker = Task.Run(async () =>
        {
            while (!workerCancellation.IsCancellationRequested)
            {
                if (!fakeBridge.HandleOne())
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

        try
        {
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
            var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
            var expectedTools = new Dictionary<string, (bool ReadOnly, bool Destructive, bool Idempotent, string[] Parameters)>(StringComparer.Ordinal)
            {
                ["ddai_apply_plan"] = (false, true, true, ["plan"]),
                ["ddai_get_asset_preview"] = (true, false, true, ["assetRef"]),
                ["ddai_get_capabilities"] = (true, false, true, []),
                ["ddai_import_asset"] = (false, false, true, ["request"]),
                ["ddai_inspect_map"] = (true, false, true, ["query"]),
                ["ddai_search_assets"] = (true, false, true, ["query"]),
                ["ddai_status"] = (true, false, false, []),
                ["ddai_validate_plan"] = (true, false, true, ["plan"]),
            };
            Assert.Equal(expectedTools.Keys.Order(StringComparer.Ordinal), tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
            foreach (var tool in tools)
            {
                var expected = expectedTools[tool.Name];
                var annotations = Assert.IsType<ToolAnnotations>(tool.ProtocolTool.Annotations);
                Assert.Equal(expected.ReadOnly, annotations.ReadOnlyHint);
                Assert.Equal(expected.Destructive, annotations.DestructiveHint);
                Assert.Equal(expected.Idempotent, annotations.IdempotentHint);
                Assert.False(annotations.OpenWorldHint);
                Assert.False(string.IsNullOrWhiteSpace(tool.Description));
                var schema = tool.ProtocolTool.InputSchema;
                Assert.Equal("object", schema.GetProperty("type").GetString());
                var parameterNames = schema.TryGetProperty("properties", out var properties)
                    ? properties.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray()
                    : [];
                Assert.Equal(expected.Parameters.Order(StringComparer.Ordinal), parameterNames);
            }

            var searchFields = tools.Single(tool => tool.Name == "ddai_search_assets").ProtocolTool.InputSchema
                .GetProperty("properties").GetProperty("query").GetProperty("properties");
            Assert.Equal(
                ["categories", "cursor", "generated", "includeStagedGenerated", "limit", "packIds", "previewRequired", "query", "tags"],
                searchFields.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
            var inspectionFields = tools.Single(tool => tool.Name == "ddai_inspect_map").ProtocolTool.InputSchema
                .GetProperty("properties").GetProperty("query").GetProperty("properties");
            Assert.Equal(
                ["cursor", "level", "limit", "region"],
                inspectionFields.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());

            var status = await client.CallToolAsync(
                "ddai_status",
                new Dictionary<string, object?>(),
                cancellationToken: deadline.Token);
            Assert.Null(status.IsError);
            var statusText = Assert.IsType<TextContentBlock>(Assert.Single(status.Content)).Text;
            using var statusDocument = JsonDocument.Parse(statusText);
            Assert.True(statusDocument.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("ready", statusDocument.RootElement.GetProperty("payload").GetProperty("state").GetString());
            Assert.Equal(PublishedDiscoveryBridgeHarness.MapRevision, statusDocument.RootElement.GetProperty("map_revision").GetString());
            Assert.Equal(73, statusDocument.RootElement.GetProperty("catalog_revision").GetInt64());
            Assert.Equal(PublishedDiscoveryBridgeHarness.Capabilities, statusDocument.RootElement.GetProperty("bridge_capabilities").EnumerateArray().Select(item => item.GetString()).ToArray());

            var capabilities = await client.CallToolAsync(
                "ddai_get_capabilities",
                new Dictionary<string, object?>(),
                cancellationToken: deadline.Token);
            Assert.Null(capabilities.IsError);
            using var capabilitiesDocument = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(capabilities.Content)).Text);
            Assert.Equal("live", capabilitiesDocument.RootElement.GetProperty("runtime_state").GetString());
            Assert.Equal(73, capabilitiesDocument.RootElement.GetProperty("catalog_revision").GetInt64());
            Assert.Equal(AssetCategory.All.Count, capabilitiesDocument.RootElement.GetProperty("asset_categories").GetArrayLength());
            Assert.Equal(AssetCatalogRepository.MaximumPreviewBytes, capabilitiesDocument.RootElement.GetProperty("preview").GetProperty("maximum_bytes").GetInt32());
            Assert.Equal(GeneratedAssetStore.MaximumDecodedBytes, capabilitiesDocument.RootElement.GetProperty("import").GetProperty("maximum_decoded_bytes").GetInt32());

            var search = await client.CallToolAsync(
                "ddai_search_assets",
                new Dictionary<string, object?> { ["query"] = new { query = "ancient", limit = 1 } },
                cancellationToken: deadline.Token);
            AssertTextMatchesStructured(search);
            using var searchDocument = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(search.Content)).Text);
            Assert.Equal(asset.AssetRef, searchDocument.RootElement.GetProperty("items")[0].GetProperty("asset_ref").GetString());

            var preview = await client.CallToolAsync(
                "ddai_get_asset_preview",
                new Dictionary<string, object?> { ["assetRef"] = asset.AssetRef },
                cancellationToken: deadline.Token);
            AssertTextMatchesStructured(preview);
            Assert.Equal(2, preview.Content.Count);
            var previewImage = Assert.IsType<ImageContentBlock>(Assert.Single(preview.Content.OfType<ImageContentBlock>()));
            Assert.Equal("image/png", previewImage.MimeType);
            Assert.Equal(sandbox.PreviewBytes, previewImage.DecodedData.ToArray());
            Assert.True(previewImage.DecodedData.Length <= AssetCatalogRepository.MaximumPreviewBytes);

            var importRequest = new
            {
                idempotencyKey = "published-complete-discovery-import-001",
                category = "Objects",
                name = "Cross Tool Lantern",
                tags = new[] { "generated", "cross-tool" },
                gridWidth = 1,
                gridHeight = 1,
                contentBase64 = Convert.ToBase64String(sandbox.PreviewBytes),
            };
            var import = await client.CallToolAsync(
                "ddai_import_asset",
                new Dictionary<string, object?> { ["request"] = importRequest },
                cancellationToken: deadline.Token);
            AssertTextMatchesStructured(import);
            Assert.Equal(2, import.Content.Count);
            var importImage = Assert.IsType<ImageContentBlock>(Assert.Single(import.Content.OfType<ImageContentBlock>()));
            Assert.True(importImage.DecodedData.Length <= GeneratedAssetStore.MaximumPreviewBytes);
            using var importDocument = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(import.Content.OfType<TextContentBlock>())).Text);
            var generatedAssetId = importDocument.RootElement.GetProperty("generated_asset_id").GetString();

            var importedSearch = await client.CallToolAsync(
                "ddai_search_assets",
                new Dictionary<string, object?>
                {
                    ["query"] = new { query = "Cross Tool Lantern", generated = true, includeStagedGenerated = true, limit = 100 },
                },
                cancellationToken: deadline.Token);
            AssertTextMatchesStructured(importedSearch);
            using var importedSearchDocument = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(importedSearch.Content)).Text);
            var staged = Assert.Single(importedSearchDocument.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("sha256:" + generatedAssetId, staged.GetProperty("asset_ref").GetString());
            Assert.True(staged.GetProperty("generated").GetBoolean());
            Assert.False(staged.GetProperty("placeable").GetBoolean());

            var inspection = await client.CallToolAsync(
                "ddai_inspect_map",
                new Dictionary<string, object?> { ["query"] = new { level = 0, limit = 1 } },
                cancellationToken: deadline.Token);
            AssertTextMatchesStructured(inspection);
            using var inspectionDocument = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(inspection.Content)).Text);
            Assert.Equal(PublishedDiscoveryBridgeHarness.MapRevision, inspectionDocument.RootElement.GetProperty("map_revision").GetString());
            Assert.Equal(73, inspectionDocument.RootElement.GetProperty("catalog_revision").GetInt64());
            Assert.Equal(asset.AssetRef, inspectionDocument.RootElement.GetProperty("items")[0].GetProperty("asset_ref").GetString());

            var plan = ValidPlan("published-plan-001");
            var arguments = new Dictionary<string, object?> { ["plan"] = plan };
            var validation = await client.CallToolAsync("ddai_validate_plan", arguments, cancellationToken: deadline.Token);
            using var validationDocument = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(validation.Content)).Text);
            Assert.True(validationDocument.RootElement.GetProperty("valid").GetBoolean());
            var apply = await client.CallToolAsync("ddai_apply_plan", arguments, cancellationToken: deadline.Token);
            using var applyDocument = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(apply.Content)).Text);
            Assert.True(applyDocument.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("room-entrance", applyDocument.RootElement.GetProperty("payload").GetProperty("room_id").GetString());

            await AssertStableToolErrorAsync(
                client.CallToolAsync(
                    "ddai_search_assets",
                    new Dictionary<string, object?> { ["query"] = new { limit = AssetSearchService.MaximumLimit + 1 } },
                    cancellationToken: deadline.Token),
                "invalid_request");
            await AssertStableToolErrorAsync(
                client.CallToolAsync(
                    "ddai_get_asset_preview",
                    new Dictionary<string, object?> { ["assetRef"] = "sha256:" + new string('f', 64) },
                    cancellationToken: deadline.Token),
                "asset_not_found");
            await AssertStableToolErrorAsync(
                client.CallToolAsync(
                    "ddai_inspect_map",
                    new Dictionary<string, object?> { ["query"] = new { limit = MapSnapshotJson.MaximumLimit + 1 } },
                    cancellationToken: deadline.Token),
                "invalid_request");

            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await client.CallToolAsync(
                "ddai_inspect_map",
                new Dictionary<string, object?> { ["query"] = new { level = PublishedDiscoveryBridgeHarness.UnansweredLevel, limit = 1 } },
                cancellationToken: cancelled.Token));
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

    private static void AssertTextMatchesStructured(CallToolResult result)
    {
        Assert.Null(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content.OfType<TextContentBlock>())).Text;
        Assert.True(result.StructuredContent.HasValue);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), JsonNode.Parse(result.StructuredContent.Value.GetRawText())));
    }

    private static async Task AssertStableToolErrorAsync(ValueTask<CallToolResult> operation, string errorCode)
    {
        var result = await operation;
        Assert.True(result.IsError);
        AssertTextMatchesStructuredError(result);
        using var document = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(errorCode, document.RootElement.GetProperty("error").GetString());
    }

    private static void AssertTextMatchesStructuredError(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.StructuredContent.HasValue);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), JsonNode.Parse(result.StructuredContent.Value.GetRawText())));
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

        public AssetCatalogEntry PublishAcceptedAssetCatalog(
            bool includePreview = false,
            DateTimeOffset? snapshotAt = null,
            long revision = 1,
            bool complete = true,
            IReadOnlyList<AssetCatalogError>? errors = null)
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
                revision,
                new string('0', 64),
                snapshotAt ?? DateTimeOffset.UtcNow,
                complete,
                AssetCategory.All.ToDictionary(category => category, category => category == "Objects" ? 1 : 0, StringComparer.Ordinal),
                [new AssetCatalogChunk("assets-000.json", Hash(chunkBytes), 1, chunkBytes.LongLength)],
                errors ?? []);
            manifest = manifest with { CatalogFingerprint = AssetCatalogJson.ComputeCatalogFingerprint(manifest) };
            File.WriteAllText(Path.Combine(snapshotRoot, "manifest.json"), AssetCatalogJson.SerializeManifest(manifest));
            File.WriteAllText(
                Path.Combine(catalogRoot, "current.json"),
                JsonSerializer.Serialize(new { manifest = "fixture-snapshot/manifest.json", session_id = "published-fixture" }));
            return entry;
        }

        public void WriteRuntimeReceipt()
        {
            File.WriteAllText(
                Path.Combine(MailboxDirectory, "runtime-receipt.json"),
                JsonSerializer.Serialize(new
                {
                    schema_version = "1.0",
                    @event = "started",
                    mod_version = DdaiCapabilityService.ExpectedModVersion,
                    target_dungeondraft_version = DdaiCapabilityService.ExpectedDungeondraftVersion,
                    timestamp = DateTimeOffset.UtcNow.ToString("O"),
                    session_id = "1-1",
                    supported_commands = PublishedDiscoveryBridgeHarness.Capabilities,
                }));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

        private static string Hash(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class PublishedDiscoveryBridgeHarness(AtomicMailbox mailbox, string assetRef)
    {
        public const int UnansweredLevel = 99;
        public static readonly string MapRevision = new('b', 64);
        public static readonly string[] Capabilities = ["status", "apply_plan", "inspect_map"];

        public bool HandleOne()
        {
            var claim = mailbox.ClaimNextRequest();
            if (claim is null)
            {
                return false;
            }

            if (claim.Request.Command == "inspect_map" &&
                claim.Request.Payload.TryGetProperty("level", out var level) &&
                level.GetInt32() == UnansweredLevel)
            {
                return true;
            }

            var response = claim.Request.Command switch
            {
                "status" => Success(claim.Request, JsonSerializer.SerializeToElement(new
                {
                    state = "ready",
                    revision = MapRevision,
                    supported_commands = Capabilities,
                })),
                "inspect_map" => Inspect(claim.Request),
                "apply_plan" => Apply(claim.Request),
                _ => Failure(claim.Request, "unsupported_command"),
            };
            mailbox.PublishResponse(claim, response);
            return true;
        }

        private MailboxResponse Inspect(MailboxRequest request)
        {
            var query = JsonSerializer.Deserialize<MapInspectionQuery>(request.Payload.GetRawText())
                ?? throw new JsonException("The published inspection query was null.");
            var page = new MapSnapshotPage(
                new string('a', 64),
                MapRevision,
                new MapCanvas(40, 30),
                256,
                [new MapSnapshotLevel(0, "Ground", true)],
                [new MapSnapshotItem(7, "wall", new MapSnapshotBounds(1, 2, 3, 4), 0, assetRef)],
                null,
                false,
                ["object", "portal", "light", "text", "material", "floor_shape"]);
            Assert.Equal(0, query.Level);
            Assert.Equal(1, query.Limit);
            return Success(request, JsonSerializer.Deserialize<JsonElement>(MapSnapshotJson.SerializePage(page)));
        }

        private static MailboxResponse Apply(MailboxRequest request)
        {
            var plan = MapPlanJson.Deserialize(request.Payload.GetRawText());
            return Success(request, JsonSerializer.SerializeToElement(new
            {
                applied = true,
                created_walls = 1,
                room_id = plan.Rooms[0].Id,
                undo_available = true,
                undo_instruction = "Use Dungeondraft Undo once",
                plan_fingerprint = MapPlanJson.Fingerprint(plan),
            }));
        }

        private static MailboxResponse Success(MailboxRequest request, JsonElement payload) => new()
        {
            SchemaVersion = MailboxRequest.CurrentSchemaVersion,
            RequestId = request.RequestId,
            Command = request.Command,
            Timestamp = DateTimeOffset.UtcNow,
            Success = true,
            Payload = payload,
        };

        private static MailboxResponse Failure(MailboxRequest request, string code) => new()
        {
            SchemaVersion = MailboxRequest.CurrentSchemaVersion,
            RequestId = request.RequestId,
            Command = request.Command,
            Timestamp = DateTimeOffset.UtcNow,
            Success = false,
            Payload = JsonSerializer.SerializeToElement(new { }),
            Error = new MailboxErrorDetails(code, "The published fake bridge rejected the request."),
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
