using System.Collections.Immutable;
using System.Text;
using DDAI.App.Assets;
using DDAI.Core.Assets;

namespace DDAI.App.Tests.Assets;

public sealed class AssetSearchServiceTests
{
    [Fact]
    public void Search_RanksWholeQueryMatchesThenUsesOrdinalAssetReference()
    {
        var service = CreateService(
            Entry("z-exact", "Ancient Gate"),
            Entry("a-prefix", "Ancient Gateway"),
            Entry("b-tag", "Stone Arch", tags: ["ancient gate"]),
            Entry("c-substring", "Weathered Ancient Gatehouse"),
            Entry("a-tie", "Ancient Gateway"));

        var result = service.Search(new AssetSearchQuery(Query: "ancient gate"));

        Assert.Equal(["z-exact", "a-prefix", "a-tie", "b-tag", "c-substring"], result.Items.Select(item => item.AssetRef));
    }

    [Fact]
    public void Search_UsesTheDefaultLimitAndAcceptsTheMaximumLimit()
    {
        var service = CreateService(Enumerable.Range(0, 101)
            .Select(index => Entry($"asset-{index:D3}", $"Item {index:D3}"))
            .ToArray());

        var defaultResult = service.Search(new AssetSearchQuery());
        var maximumResult = service.Search(new AssetSearchQuery(Limit: 100));

        Assert.Equal(20, defaultResult.Items.Count);
        Assert.NotNull(defaultResult.NextCursor);
        Assert.Equal(100, maximumResult.Items.Count);
        Assert.NotNull(maximumResult.NextCursor);
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Search(new AssetSearchQuery(Limit: 101)));
    }

    [Fact]
    public void Search_EmptyQueryReturnsFilteredEntriesInStableAssetReferenceOrder()
    {
        var service = CreateService(
            Entry("asset-z", "Zulu", category: "Objects", allowThirdPartyUse: false),
            Entry("asset-a", "Alpha", category: "Objects", allowThirdPartyUse: true),
            Entry("asset-b", "Beta", category: "Walls"));

        var result = service.Search(new AssetSearchQuery(Categories: ["Objects"]));

        Assert.Equal(["asset-a", "asset-z"], result.Items.Select(item => item.AssetRef));
        Assert.True(result.Items[0].AllowThirdPartyUse);
        Assert.False(result.Items[1].AllowThirdPartyUse);
    }

    [Fact]
    public void Search_AppliesOrWithinEachFilterAndAndAcrossFilterTypes()
    {
        var service = CreateService(
            Entry("match-a", "A", category: "Objects", packId: "pack-a", tags: ["featured"], generated: true, previewHash: "preview"),
            Entry("match-b", "B", category: "Walls", packId: "pack-b", tags: ["rare"], generated: true, previewHash: "preview"),
            Entry("wrong-pack", "C", category: "Objects", packId: "pack-c", tags: ["featured"], generated: true, previewHash: "preview"),
            Entry("wrong-tag", "D", category: "Objects", packId: "pack-a", tags: ["other"], generated: true, previewHash: "preview"),
            Entry("wrong-generated", "E", category: "Objects", packId: "pack-a", tags: ["featured"], generated: false, previewHash: "preview"),
            Entry("wrong-preview", "F", category: "Objects", packId: "pack-a", tags: ["featured"], generated: true));

        var result = service.Search(new AssetSearchQuery(
            Categories: ["Objects", "Walls"],
            PackIds: ["pack-a", "pack-b"],
            Generated: true,
            PreviewRequired: true,
            Tags: ["featured", "rare"]));

        Assert.Equal(["match-a", "match-b"], result.Items.Select(item => item.AssetRef));
    }

    [Fact]
    public void Search_NormalizesUnicodeAndRequiresEveryFreeTextToken()
    {
        var service = CreateService(
            Entry("unicode", "Café Torch", searchTerms: ["ancient"]),
            Entry("missing-token", "Café Lantern", searchTerms: ["ancient"]));

        var result = service.Search(new AssetSearchQuery(Query: "  ＣＡＦÉ   TORCH  ancient "));

        Assert.Equal(["unicode"], result.Items.Select(item => item.AssetRef));
    }

    [Fact]
    public void Search_RejectsInvalidQueryBounds()
    {
        var service = CreateService(Entry("asset", "Asset"));

        Assert.Throws<ArgumentOutOfRangeException>(() => service.Search(new AssetSearchQuery(Query: string.Concat(Enumerable.Repeat("😀", 257)))));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Search(new AssetSearchQuery(Tags: Enumerable.Range(0, 17).Select(index => $"tag-{index}").ToArray())));
        Assert.Throws<ArgumentException>(() => service.Search(new AssetSearchQuery(Categories: ["Not a category"])));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Search(new AssetSearchQuery(Limit: 0)));
    }

    [Fact]
    public void Search_RejectsMalformedStaleAndOutOfRangeCursors()
    {
        var service = CreateService(Entry("asset", "Asset"));

        Assert.Throws<ArgumentException>(() => service.Search(new AssetSearchQuery(Cursor: "not-a-cursor")));
        Assert.Throws<ArgumentException>(() => service.Search(new AssetSearchQuery(Cursor: EncodeCursor("other-fingerprint", 0))));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Search(new AssetSearchQuery(Cursor: EncodeCursor("catalog-fingerprint", 1))));
    }

    [Fact]
    public void Search_PaginatesWithAnOpaqueCursorAndReturnsCatalogMetadata()
    {
        var service = CreateService(
            Entry("asset-a", "Alpha"),
            Entry("asset-b", "Beta"));

        var first = service.Search(new AssetSearchQuery(Limit: 1));
        var second = service.Search(new AssetSearchQuery(Limit: 1, Cursor: first.NextCursor));

        Assert.Equal(["asset-a"], first.Items.Select(item => item.AssetRef));
        Assert.NotNull(first.NextCursor);
        Assert.Equal(["asset-b"], second.Items.Select(item => item.AssetRef));
        Assert.Null(second.NextCursor);
        Assert.Equal(42, second.CatalogRevision);
        Assert.Equal("catalog-fingerprint", second.CatalogFingerprint);
        Assert.True(second.Live);
        Assert.Equal(SnapshotAt, second.SnapshotAt);
    }

    [Fact]
    public void Search_RejectsAnUnavailableCatalog()
    {
        var service = new AssetSearchService(() => null);

        Assert.Throws<InvalidOperationException>(() => service.Search(new AssetSearchQuery()));
    }

    private static readonly DateTimeOffset SnapshotAt = new(2026, 8, 10, 18, 0, 0, TimeSpan.Zero);

    private static AssetSearchService CreateService(params AssetCatalogEntry[] entries)
    {
        var manifest = new AssetCatalogManifest(
            AssetCatalogManifest.CurrentSchemaVersion,
            "catalog-session",
            42,
            "catalog-fingerprint",
            SnapshotAt,
            true,
            entries.GroupBy(entry => entry.Category).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            [],
            []);
        var catalog = new AcceptedAssetCatalog(manifest, entries.ToImmutableArray(), new FixedTimeProvider());
        return new AssetSearchService(() => catalog);
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

    private static string EncodeCursor(string fingerprint, int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{fingerprint}\n{offset}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => SnapshotAt;
    }
}
