using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
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

    private static JsonDocument StructuredJson(CallToolResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result.StructuredContent));

    private static string ErrorCode(CallToolResult result) =>
        JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text)
            .RootElement.GetProperty("error").GetString()!;

    private static string ErrorText(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

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
        [],
        tags ?? [],
        previewHash,
        allowThirdPartyUse,
        generated);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
