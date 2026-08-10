using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DDAI.Core.Assets;

namespace DDAI.Core.Tests.Assets;

public sealed class AssetCatalogContractTests
{
    [Fact]
    public void All_ContainsEveryOfficialCategoryInProtocolOrder() =>
        Assert.Equal(
        [
            "Terrain", "Patterns", "Patterns Colorable", "Caves", "Roofs",
            "Objects", "Walls", "Materials", "Portals", "Paths", "Lights",
            "Simple Tiles", "Smart Tiles", "Smart Tiles Double",
        ],
        AssetCategory.All);

    [Fact]
    public void Create_IsStableAndOpaque()
    {
        var value = AssetReference.Create("official-pack", "Objects", "res://packs/secret/chair.png");

        Assert.Matches("^sha256:[0-9a-f]{64}$", value);
        Assert.DoesNotContain("secret", value, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(value, AssetReference.Create("official-pack", "Objects", "res://packs/secret/chair.png"));
    }

    [Fact]
    public void Create_NormalizesOnlyThePackIdentifier()
    {
        var normalized = AssetReference.Create("Pack Café", "Objects", "res://chair.png");

        Assert.Equal(
            normalized,
            AssetReference.Create("  PＡＣＫ Café  ", "Objects", "res://chair.png"));
        Assert.Throws<ArgumentException>(
            () => AssetReference.Create("pack café", "objects", "res://chair.png"));
        Assert.Equal(
            AssetReference.Create(null, "Objects", "res://chair.png"),
            AssetReference.Create(string.Empty, "Objects", "res://chair.png"));
    }

    [Fact]
    public void SerializeManifest_UsesStableSnakeCaseAndRoundTrips()
    {
        var manifest = ValidManifest();

        var json = AssetCatalogJson.SerializeManifest(manifest);
        var parsed = AssetCatalogJson.DeserializeManifest(json);

        Assert.Contains("\"schema_version\"", json, StringComparison.Ordinal);
        Assert.Contains("\"catalog_fingerprint\"", json, StringComparison.Ordinal);
        Assert.Equal(manifest.SchemaVersion, parsed.SchemaVersion);
        Assert.Equal(manifest.CatalogFingerprint, parsed.CatalogFingerprint);
        Assert.Equal(manifest.CategoryCounts, parsed.CategoryCounts);
        Assert.Equal(manifest.Chunks, parsed.Chunks);
    }

    [Fact]
    public void DeserializeManifest_RejectsCategoryCountMismatch()
    {
        var counts = Counts();
        counts["Objects"] = 2;
        var manifest = ValidManifest() with
        {
            CategoryCounts = counts,
        };

        var exception = Assert.Throws<JsonException>(
            () => AssetCatalogJson.DeserializeManifest(SerializeWithoutValidation(manifest)));

        Assert.Contains("category counts", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeEntries_RejectsDuplicateAssetReference()
    {
        var entry = ValidEntry();
        var json = JsonSerializer.Serialize(new[] { entry, entry });

        var exception = Assert.Throws<JsonException>(() => AssetCatalogJson.DeserializeEntries(json));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeEntries_RejectsMissingCategory()
    {
        const string Json =
            "[{\"asset_ref\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"display_name\":\"Chair\",\"resource_fingerprint\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"pack_id\":null,\"pack_name\":null,\"search_terms\":[],\"tags\":[],\"preview_hash\":null,\"allow_third_party_use\":true,\"generated\":false}]";

        Assert.Throws<JsonException>(() => AssetCatalogJson.DeserializeEntries(Json));
    }

    [Fact]
    public void DeserializeManifest_RejectsTraversalChunkName()
    {
        var manifest = ValidManifest() with
        {
            Chunks = [new AssetCatalogChunk("../entries-0001.json", Hash("chunk"), 1, 5)],
        };

        Assert.Throws<JsonException>(() => AssetCatalogJson.DeserializeManifest(SerializeWithoutValidation(manifest)));
    }

    [Fact]
    public void DeserializeManifest_RejectsUppercaseHash()
    {
        var manifest = ValidManifest() with
        {
            Chunks = [new AssetCatalogChunk("entries-0001.json", Hash("chunk").ToUpperInvariant(), 1, 5)],
        };

        Assert.Throws<JsonException>(() => AssetCatalogJson.DeserializeManifest(SerializeWithoutValidation(manifest)));
    }

    [Fact]
    public void DeserializeManifest_RejectsNonUtcTimestamp()
    {
        var manifest = ValidManifest() with { SnapshotAt = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.FromHours(-4)) };

        Assert.Throws<JsonException>(() => AssetCatalogJson.DeserializeManifest(SerializeWithoutValidation(manifest)));
    }

    [Fact]
    public void DeserializeManifest_RejectsJsonOverOneMiB()
    {
        var oversized = "{" + new string(' ', (1024 * 1024) + 1) + "}";

        Assert.Throws<JsonException>(() => AssetCatalogJson.DeserializeManifest(oversized));
    }

    [Fact]
    public void ComputeCatalogFingerprint_ChangesWhenCatalogContentsChange()
    {
        var original = ValidManifest();
        var changed = original with { CatalogRevision = original.CatalogRevision + 1 };

        Assert.NotEqual(
            AssetCatalogJson.ComputeCatalogFingerprint(original),
            AssetCatalogJson.ComputeCatalogFingerprint(changed));
    }

    private static AssetCatalogManifest ValidManifest()
    {
        var seed = new AssetCatalogManifest(
            SchemaVersion: AssetCatalogManifest.CurrentSchemaVersion,
            SessionId: "session-001",
            CatalogRevision: 3,
            CatalogFingerprint: new string('0', 64),
            SnapshotAt: new DateTimeOffset(2026, 8, 10, 16, 0, 0, TimeSpan.Zero),
            Complete: true,
            CategoryCounts: Counts(),
            Chunks: [new AssetCatalogChunk("entries-0001.json", Hash("chunk"), 1, 5)],
            Errors: []);

        return seed with { CatalogFingerprint = AssetCatalogJson.ComputeCatalogFingerprint(seed) };
    }

    private static AssetCatalogEntry ValidEntry() => new(
        AssetReference.Create("official-pack", "Objects", "res://chair.png"),
        "Objects",
        "Chair",
        Hash("resource"),
        "official-pack",
        "Official Pack",
        ["chair"],
        ["furniture"],
        null,
        true,
        false);

    private static Dictionary<string, int> Counts()
    {
        var counts = AssetCategory.All.ToDictionary(category => category, _ => 0, StringComparer.Ordinal);
        counts["Objects"] = 1;
        return counts;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string SerializeWithoutValidation(AssetCatalogManifest manifest) =>
        JsonSerializer.Serialize(manifest, AssetCatalogJson.SerializerOptions);
}
