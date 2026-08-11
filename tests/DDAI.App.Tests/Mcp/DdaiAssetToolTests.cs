using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DDAI.App.Assets;
using DDAI.App.Mcp;
using DDAI.Core.Assets;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DDAI.App.Tests.Mcp;

public sealed class DdaiAssetToolTests
{
    [Fact]
    public void SearchAssets_DeclaresClosedReadOnlySafetyMetadata()
    {
        var method = typeof(DdaiAssetTools).GetMethod(nameof(DdaiAssetTools.SearchAssets));
        var attribute = Assert.Single(method!.GetCustomAttributes<McpServerToolAttribute>());

        Assert.Equal("ddai_search_assets", attribute.Name);
        Assert.True(attribute.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
    }

    [Fact]
    public void GetAssetPreview_DeclaresClosedReadOnlySafetyMetadata()
    {
        var method = typeof(DdaiAssetTools).GetMethod(nameof(DdaiAssetTools.GetAssetPreview));
        var attribute = Assert.Single(method!.GetCustomAttributes<McpServerToolAttribute>());

        Assert.Equal("ddai_get_asset_preview", attribute.Name);
        Assert.True(attribute.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
    }

    [Fact]
    public void ImportAsset_DeclaresClosedStateChangingIdempotentSafetyMetadata()
    {
        var method = typeof(DdaiAssetTools).GetMethod(nameof(DdaiAssetTools.ImportAssetAsync));
        var attribute = Assert.Single(method!.GetCustomAttributes<McpServerToolAttribute>());

        Assert.Equal("ddai_import_asset", attribute.Name);
        Assert.False(attribute.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
    }

    [Fact]
    public async Task ImportAsset_ImportsInlinePngAndReturnsBoundedStructuredMetadataAndPreview()
    {
        using var sandbox = new GeneratedAssetToolSandbox();
        var request = GeneratedAssetRequest("tool-import-001", sandbox.PngBase64);

        var result = await DdaiAssetTools.ImportAssetAsync(request, sandbox.Store, CancellationToken.None);

        Assert.Null(result.IsError);
        Assert.Equal(2, result.Content.Count);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content.OfType<TextContentBlock>())).Text;
        var image = Assert.IsType<ImageContentBlock>(Assert.Single(result.Content.OfType<ImageContentBlock>()));
        Assert.Equal("image/png", image.MimeType);
        Assert.True(image.DecodedData.Length <= GeneratedAssetStore.MaximumPreviewBytes);
        using var metadata = JsonDocument.Parse(text);
        Assert.Equal("staged", metadata.RootElement.GetProperty("activation_state").GetString());
        Assert.Equal("Objects", metadata.RootElement.GetProperty("category").GetString());
        Assert.False(metadata.RootElement.GetProperty("duplicate").GetBoolean());
        Assert.True(result.StructuredContent.HasValue);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), JsonNode.Parse(result.StructuredContent.Value.GetRawText())));
    }

    [Fact]
    public async Task ImportAsset_ReplaysExactRequestAndBoundsConflictAndValidationErrors()
    {
        using var sandbox = new GeneratedAssetToolSandbox();
        var request = GeneratedAssetRequest("tool-replay-001", sandbox.PngBase64);

        var first = await DdaiAssetTools.ImportAssetAsync(request, sandbox.Store, CancellationToken.None);
        var replay = await DdaiAssetTools.ImportAssetAsync(request, sandbox.Store, CancellationToken.None);
        var conflict = await DdaiAssetTools.ImportAssetAsync(request with { Name = "Changed Name" }, sandbox.Store, CancellationToken.None);
        var invalid = await DdaiAssetTools.ImportAssetAsync(request with { IdempotencyKey = "tool-invalid-001", ContentBase64 = "not-base64" }, sandbox.Store, CancellationToken.None);

        using var firstJson = StructuredJson(first);
        using var replayJson = StructuredJson(replay);
        Assert.Equal(firstJson.RootElement.GetProperty("generated_asset_id").GetString(), replayJson.RootElement.GetProperty("generated_asset_id").GetString());
        Assert.False(firstJson.RootElement.GetProperty("duplicate").GetBoolean());
        Assert.True(replayJson.RootElement.GetProperty("duplicate").GetBoolean());
        Assert.True(conflict.IsError);
        Assert.Equal("request_conflict", ErrorCode(conflict));
        Assert.True(invalid.IsError);
        Assert.Equal("invalid_base64", ErrorCode(invalid));
        Assert.DoesNotContain("exception", ErrorText(invalid), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sandbox.Root, ErrorText(invalid), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportAsset_PropagatesCancellationWithoutCreatingAnIdempotencyReceipt()
    {
        using var sandbox = new GeneratedAssetToolSandbox();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DdaiAssetTools.ImportAssetAsync(
            GeneratedAssetRequest("tool-cancel-001", sandbox.PngBase64), sandbox.Store, cancellation.Token));

        var receiptRoot = Path.Combine(sandbox.Root, "generated-assets", "idempotency");
        Assert.Empty(Directory.Exists(receiptRoot) ? Directory.GetFiles(receiptRoot) : []);
    }

    [Fact]
    public void SearchAssets_DelegatesEveryFilterAndReturnsSnakeCaseStructuredPagination()
    {
        var service = CreateService(
            Entry("match-a", "Ancient Gate", category: "Objects", packId: "pack-a", tags: ["featured"], generated: true, previewHash: "preview-a"),
            Entry("match-b", "Ancient Gateway", category: "Walls", packId: "pack-b", tags: ["rare"], generated: true, previewHash: "preview-b"),
            Entry("wrong-pack", "Ancient Gate", category: "Objects", packId: "pack-c", tags: ["featured"], generated: true, previewHash: "preview-c"),
            Entry("wrong-tag", "Ancient Gate", category: "Objects", packId: "pack-a", tags: ["other"], generated: true, previewHash: "preview-d"),
            Entry("wrong-generated", "Ancient Gate", category: "Objects", packId: "pack-a", tags: ["featured"], generated: false, previewHash: "preview-e"),
            Entry("wrong-preview", "Ancient Gate", category: "Objects", packId: "pack-a", tags: ["featured"], generated: true));

        var first = DdaiAssetTools.SearchAssets(new AssetSearchQuery(
            Query: "ancient gate",
            Categories: ["Objects", "Walls"],
            PackIds: ["pack-a", "pack-b"],
            Generated: true,
            PreviewRequired: true,
            Tags: ["featured", "rare"],
            Limit: 1), service);
        var firstJson = StructuredJson(first);

        Assert.Null(first.IsError);
        Assert.Equal("match-a", firstJson.RootElement.GetProperty("items")[0].GetProperty("asset_ref").GetString());
        Assert.True(firstJson.RootElement.GetProperty("items")[0].TryGetProperty("allow_third_party_use", out _));
        Assert.Equal(42, firstJson.RootElement.GetProperty("catalog_revision").GetInt64());
        Assert.Equal("catalog-fingerprint", firstJson.RootElement.GetProperty("catalog_fingerprint").GetString());
        Assert.True(firstJson.RootElement.GetProperty("live").GetBoolean());
        Assert.Equal("2026-08-10T18:00:00+00:00", firstJson.RootElement.GetProperty("snapshot_at").GetString());
        var cursor = firstJson.RootElement.GetProperty("next_cursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        var second = DdaiAssetTools.SearchAssets(new AssetSearchQuery(
            Query: "ancient gate",
            Categories: ["Objects", "Walls"],
            PackIds: ["pack-a", "pack-b"],
            Generated: true,
            PreviewRequired: true,
            Tags: ["featured", "rare"],
            Limit: 1,
            Cursor: cursor), service);
        var secondJson = StructuredJson(second);

        Assert.Equal("match-b", secondJson.RootElement.GetProperty("items")[0].GetProperty("asset_ref").GetString());
        Assert.Equal(JsonValueKind.Null, secondJson.RootElement.GetProperty("next_cursor").ValueKind);
    }

    [Fact]
    public void SearchAssets_PreservesThirdPartyUseMetadataWithoutFiltering()
    {
        var service = CreateService(
            Entry("permitted", "Permitted", allowThirdPartyUse: true),
            Entry("restricted", "Restricted", allowThirdPartyUse: false));

        var result = DdaiAssetTools.SearchAssets(new AssetSearchQuery(), service);
        var json = StructuredJson(result);

        Assert.Null(result.IsError);
        Assert.Equal(2, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.True(json.RootElement.GetProperty("items")[0].GetProperty("allow_third_party_use").GetBoolean());
        Assert.False(json.RootElement.GetProperty("items")[1].GetProperty("allow_third_party_use").GetBoolean());
    }

    [Fact]
    public void SearchAssets_FindsNonFilenameLibraryTermAndExactPublicTag()
    {
        var service = CreateService(Entry(
            "semantic-match",
            "Opaque Fixture",
            searchTerms: ["sanctuary", "stone altar"],
            tags: ["sacred"]));

        using var result = StructuredJson(DdaiAssetTools.SearchAssets(
            new AssetSearchQuery(Query: "sanctuary", Tags: ["sacred"]),
            service));

        Assert.Equal("semantic-match", result.RootElement.GetProperty("items")[0].GetProperty("asset_ref").GetString());
        Assert.DoesNotContain("resource_fingerprint", result.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void SearchAssets_FindsPackKeywordAsSearchTermWithoutClaimingExactTagMembership()
    {
        var service = CreateService(Entry(
            "pack-keyword-match",
            "Opaque Fixture",
            searchTerms: ["ceremonial"],
            tags: []));

        using var keywordResult = StructuredJson(DdaiAssetTools.SearchAssets(
            new AssetSearchQuery(Query: "ceremonial"),
            service));
        using var tagResult = StructuredJson(DdaiAssetTools.SearchAssets(
            new AssetSearchQuery(Tags: ["ceremonial"]),
            service));

        Assert.Equal("pack-keyword-match", keywordResult.RootElement.GetProperty("items")[0].GetProperty("asset_ref").GetString());
        Assert.Empty(tagResult.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public void SearchAssets_PreservesStaleCatalogMetadata()
    {
        var result = DdaiAssetTools.SearchAssets(
            new AssetSearchQuery(),
            CreateServiceWithLiveStatus(false, Entry("asset", "Asset")));
        var json = StructuredJson(result);

        Assert.Null(result.IsError);
        Assert.False(json.RootElement.GetProperty("live").GetBoolean());
        Assert.Equal(42, json.RootElement.GetProperty("catalog_revision").GetInt64());
        Assert.Equal("catalog-fingerprint", json.RootElement.GetProperty("catalog_fingerprint").GetString());
        Assert.Equal("2026-08-10T18:00:00+00:00", json.RootElement.GetProperty("snapshot_at").GetString());
    }

    [Fact]
    public void SearchAssets_ExposesStagedGeneratedAssetsOnlyWhenRequested()
    {
        var service = CreateServiceWithStagedGenerated(
            Entry("live", "Live", generated: true),
            Entry("staged", "Staged", generated: true));

        var ordinary = StructuredJson(DdaiAssetTools.SearchAssets(new AssetSearchQuery(Generated: true), service));
        var withStaged = StructuredJson(DdaiAssetTools.SearchAssets(
            new AssetSearchQuery(Generated: true, IncludeStagedGenerated: true), service));

        Assert.Equal(["live"], ordinary.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("asset_ref").GetString()));
        Assert.Equal(["live", "staged"], withStaged.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("asset_ref").GetString()));
        Assert.False(withStaged.RootElement.GetProperty("items")[1].GetProperty("placeable").GetBoolean());
    }

    [Fact]
    public void SearchAssets_ReturnsBoundedToolErrorsWithoutExceptionOrSourcePathLeakage()
    {
        var unavailable = DdaiAssetTools.SearchAssets(new AssetSearchQuery(), new AssetSearchService(() => null));
        var invalid = DdaiAssetTools.SearchAssets(new AssetSearchQuery(Limit: 101), CreateService(Entry("asset", "Asset")));

        Assert.True(unavailable.IsError);
        Assert.Equal("catalog_unavailable", ErrorCode(unavailable));
        Assert.DoesNotContain("exception", ErrorText(unavailable), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", ErrorText(unavailable), StringComparison.OrdinalIgnoreCase);
        Assert.True(invalid.IsError);
        Assert.Equal("invalid_request", ErrorCode(invalid));
        Assert.DoesNotContain("101", ErrorText(invalid), StringComparison.Ordinal);
    }

    [Fact]
    public void GetAssetPreview_ReturnsVerifiedPngWithStructuredMetadataAndExactlyOneTextAndImageBlock()
    {
        using var sandbox = new PreviewCatalogSandbox();
        var asset = sandbox.PublishAssetWithPreview();
        var repository = new AssetCatalogRepository(sandbox.CatalogRoot, sandbox.TimeProvider);

        var result = DdaiAssetTools.GetAssetPreview(asset.AssetRef, repository);

        Assert.Null(result.IsError);
        var image = Assert.Single(result.Content.OfType<ImageContentBlock>());
        var text = Assert.Single(result.Content.OfType<TextContentBlock>());
        Assert.Equal(2, result.Content.Count);
        Assert.NotNull(result.StructuredContent);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(sandbox.PreviewBytes, image.DecodedData.ToArray());
        using var structured = StructuredJson(result);
        using var textJson = JsonDocument.Parse(text.Text);
        Assert.Equal(asset.AssetRef, structured.RootElement.GetProperty("asset_ref").GetString());
        Assert.Equal("image/png", structured.RootElement.GetProperty("mime_type").GetString());
        Assert.Equal(sandbox.PreviewBytes.Length, structured.RootElement.GetProperty("byte_count").GetInt32());
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(structured.RootElement.GetRawText()),
            JsonNode.Parse(textJson.RootElement.GetRawText())));
    }

    [Fact]
    public void GetAssetPreview_OmitsOversizedDisplayNameFromBoundedConsistentMetadata()
    {
        using var sandbox = new PreviewCatalogSandbox();
        const string pathMarker = "file:///not-a-response-path/";
        var asset = sandbox.PublishAssetWithPreview(pathMarker + new string('x', 900_000));
        var result = DdaiAssetTools.GetAssetPreview(asset.AssetRef, new AssetCatalogRepository(
            sandbox.CatalogRoot, sandbox.TimeProvider));

        Assert.Null(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content.OfType<TextContentBlock>())).Text;
        Assert.True(Encoding.UTF8.GetByteCount(text) <= 1024);
        Assert.DoesNotContain(pathMarker, text, StringComparison.OrdinalIgnoreCase);
        using var structured = StructuredJson(result);
        Assert.False(structured.RootElement.TryGetProperty("display_name", out _));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(text),
            JsonNode.Parse(structured.RootElement.GetRawText())));
    }

    [Fact]
    public void GetAssetPreview_ReturnsStructuredBoundedErrorsWithoutPathOrUriLeakage()
    {
        using var sandbox = new PreviewCatalogSandbox();
        var asset = sandbox.PublishAssetWithPreview();
        var repository = new AssetCatalogRepository(sandbox.CatalogRoot, sandbox.TimeProvider);

        var absent = DdaiAssetTools.GetAssetPreview("sha256:" + new string('f', 64), repository);
        var unavailable = DdaiAssetTools.GetAssetPreview(asset.AssetRef, new AssetCatalogRepository(
            Path.Combine(sandbox.Root, "missing-catalog"), sandbox.TimeProvider));

        Assert.True(absent.IsError);
        Assert.Equal("asset_not_found", ErrorCode(absent));
        Assert.True(unavailable.IsError);
        Assert.Equal("catalog_unavailable", ErrorCode(unavailable));
        Assert.NotNull(absent.StructuredContent);
        Assert.NotNull(unavailable.StructuredContent);
        Assert.DoesNotContain(sandbox.CatalogRoot, ErrorText(absent), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file:", ErrorText(unavailable), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", ErrorText(unavailable), StringComparison.OrdinalIgnoreCase);
        using var unavailableStructured = StructuredJson(unavailable);
        Assert.Equal("catalog_unavailable", unavailableStructured.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void GetAssetPreview_ReturnsPreviewUnavailableForNoHashAndRejectedRepositoryPreviews()
    {
        using var noHashSandbox = new PreviewCatalogSandbox();
        var noHashAsset = noHashSandbox.PublishAssetWithPreview(includePreview: false);
        AssertPreviewUnavailable(DdaiAssetTools.GetAssetPreview(noHashAsset.AssetRef, new AssetCatalogRepository(
            noHashSandbox.CatalogRoot, noHashSandbox.TimeProvider)));

        using var missingSandbox = new PreviewCatalogSandbox();
        var missingAsset = missingSandbox.PublishAssetWithPreview();
        File.Delete(missingSandbox.PreviewPath(missingAsset.PreviewHash!));
        AssertPreviewUnavailable(DdaiAssetTools.GetAssetPreview(missingAsset.AssetRef, new AssetCatalogRepository(
            missingSandbox.CatalogRoot, missingSandbox.TimeProvider)));

        using var mismatchSandbox = new PreviewCatalogSandbox();
        var mismatchAsset = mismatchSandbox.PublishAssetWithPreview();
        var mismatched = mismatchSandbox.PreviewBytes.ToArray();
        mismatched[^1] ^= 1;
        File.WriteAllBytes(mismatchSandbox.PreviewPath(mismatchAsset.PreviewHash!), mismatched);
        AssertPreviewUnavailable(DdaiAssetTools.GetAssetPreview(mismatchAsset.AssetRef, new AssetCatalogRepository(
            mismatchSandbox.CatalogRoot, mismatchSandbox.TimeProvider)));

        using var oversizeSandbox = new PreviewCatalogSandbox();
        var oversizeAsset = oversizeSandbox.PublishAssetWithPreview();
        File.WriteAllBytes(
            oversizeSandbox.PreviewPath(oversizeAsset.PreviewHash!),
            new byte[AssetCatalogRepository.MaximumPreviewBytes + 1]);
        AssertPreviewUnavailable(DdaiAssetTools.GetAssetPreview(oversizeAsset.AssetRef, new AssetCatalogRepository(
            oversizeSandbox.CatalogRoot, oversizeSandbox.TimeProvider)));
    }

    private static JsonDocument StructuredJson(CallToolResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result.StructuredContent));

    private static string ErrorCode(CallToolResult result) =>
        JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text)
            .RootElement.GetProperty("error").GetString()!;

    private static string ErrorText(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private static void AssertPreviewUnavailable(CallToolResult result)
    {
        Assert.True(result.IsError);
        Assert.Equal("preview_unavailable", ErrorCode(result));
        Assert.Single(result.Content.OfType<TextContentBlock>());
        Assert.Empty(result.Content.OfType<ImageContentBlock>());
        Assert.Single(result.Content);
        Assert.NotNull(result.StructuredContent);
        var text = ErrorText(result);
        Assert.DoesNotContain("file:", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exception", text, StringComparison.OrdinalIgnoreCase);
        using var structured = StructuredJson(result);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), JsonNode.Parse(structured.RootElement.GetRawText())));
    }

    private static readonly DateTimeOffset SnapshotAt = new(2026, 8, 10, 18, 0, 0, TimeSpan.Zero);

    private static AssetSearchService CreateService(params AssetCatalogEntry[] entries) =>
        CreateServiceWithLiveStatus(true, entries);

    private static AssetSearchService CreateServiceWithLiveStatus(bool live, params AssetCatalogEntry[] entries)
    {
        var catalog = CreateCatalog(live, entries);
        return new AssetSearchService(() => catalog);
    }

    private static AssetSearchService CreateServiceWithStagedGenerated(
        AssetCatalogEntry liveEntry,
        AssetCatalogEntry stagedEntry)
    {
        var catalog = CreateCatalog(true, [liveEntry]);
        return new AssetSearchService(() => catalog, () => [stagedEntry]);
    }

    private static AcceptedAssetCatalog CreateCatalog(bool live, params AssetCatalogEntry[] entries)
    {
        var manifest = new AssetCatalogManifest(
            AssetCatalogManifest.CurrentSchemaVersion,
            "catalog-session",
            42,
            "catalog-fingerprint",
            SnapshotAt,
            live,
            entries.GroupBy(entry => entry.Category).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            [],
            []);
        var now = live
            ? SnapshotAt
            : SnapshotAt.Add(AssetCatalogRepository.MaximumLiveAge).AddTicks(1);
        return new AcceptedAssetCatalog(manifest, entries.ToImmutableArray(), new FixedTimeProvider(now));
    }

    private static AssetCatalogEntry Entry(
        string assetRef,
        string displayName,
        string category = "Objects",
        string? packId = "pack-a",
        IReadOnlyList<string>? searchTerms = null,
        IReadOnlyList<string>? tags = null,
        string? previewHash = null,
        bool allowThirdPartyUse = true,
        bool generated = false) => new(
        assetRef,
        category,
        displayName,
        "resource-fingerprint",
        packId,
        "Pack",
        searchTerms ?? [],
        tags ?? [],
        previewHash,
        allowThirdPartyUse,
        generated);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class PreviewCatalogSandbox : IDisposable
    {
        private static readonly DateTimeOffset SnapshotAt = new(2026, 8, 10, 18, 0, 0, TimeSpan.Zero);

        public PreviewCatalogSandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "ddai-asset-preview-tool-tests", Guid.NewGuid().ToString("N"));
            CatalogRoot = Path.Combine(Root, "catalog");
            Directory.CreateDirectory(Path.Combine(CatalogRoot, "snapshots", "snapshot-1"));
            Directory.CreateDirectory(Path.Combine(CatalogRoot, "previews"));
            TimeProvider = new FixedTimeProvider(SnapshotAt);
        }

        public string Root { get; }
        public string CatalogRoot { get; }
        public TimeProvider TimeProvider { get; }
        public byte[] PreviewBytes { get; } = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP4z8DwHwAFAAH/VscvDQAAAABJRU5ErkJggg==");

        public string PreviewPath(string previewHash) => Path.Combine(CatalogRoot, "previews", previewHash + ".png");

        public AssetCatalogEntry PublishAssetWithPreview(string? displayName = null, bool includePreview = true)
        {
            var previewHash = includePreview ? Hash(PreviewBytes) : null;
            if (previewHash is not null)
            {
                File.WriteAllBytes(PreviewPath(previewHash), PreviewBytes);
            }
            var entry = new AssetCatalogEntry(
                AssetReference.Create("official-pack", "Objects", "resource/ancient-gate"),
                "Objects",
                displayName ?? "Ancient Gate",
                Hash(Encoding.UTF8.GetBytes("resource/ancient-gate")),
                "official-pack",
                "Official Pack",
                ["ancient"],
                ["fixture"],
                previewHash,
                true,
                false);
            var chunkBytes = Encoding.UTF8.GetBytes(AssetCatalogJson.SerializeChunk([entry]));
            var manifest = new AssetCatalogManifest(
                AssetCatalogManifest.CurrentSchemaVersion,
                "preview-session",
                1,
                new string('0', 64),
                SnapshotAt,
                true,
                AssetCategory.All.ToDictionary(category => category, category => category == "Objects" ? 1 : 0, StringComparer.Ordinal),
                [new AssetCatalogChunk("assets-000.json", Hash(chunkBytes), 1, chunkBytes.LongLength)],
                []);
            manifest = manifest with { CatalogFingerprint = AssetCatalogJson.ComputeCatalogFingerprint(manifest) };
            var snapshotRoot = Path.Combine(CatalogRoot, "snapshots", "snapshot-1");
            File.WriteAllBytes(Path.Combine(snapshotRoot, "assets-000.json"), chunkBytes);
            File.WriteAllText(Path.Combine(snapshotRoot, "manifest.json"), AssetCatalogJson.SerializeManifest(manifest));
            File.WriteAllText(
                Path.Combine(CatalogRoot, "current.json"),
                JsonSerializer.Serialize(new { manifest = "snapshot-1/manifest.json", session_id = "preview-session" }));
            return entry;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static string Hash(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static GeneratedAssetImportRequest GeneratedAssetRequest(string idempotencyKey, string contentBase64) => new(
        idempotencyKey,
        "Objects",
        "Generated Torch",
        ["generated", "fixture"],
        1,
        1,
        contentBase64,
        null);

    private sealed class GeneratedAssetToolSandbox : IDisposable
    {
        public GeneratedAssetToolSandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "ddai-generated-asset-tool-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Store = new GeneratedAssetStore(Root);
        }

        public string Root { get; }
        public GeneratedAssetStore Store { get; }
        public string PngBase64 { get; } = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP4z8DwHwAFAAH/VscvDQAAAABJRU5ErkJggg==";

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
