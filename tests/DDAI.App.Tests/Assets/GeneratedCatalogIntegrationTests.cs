using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DDAI.App.Assets;
using DDAI.Core.Assets;
using SkiaSharp;

namespace DDAI.App.Tests.Assets;

public sealed class GeneratedCatalogIntegrationTests
{
    [Fact]
    public async Task Search_StagedGeneratedAssetIsOptInUntilAnAcceptedLiveSnapshotActivatesTheSameOpaqueReference()
    {
        using var sandbox = new GeneratedCatalogSandbox();
        var imported = await sandbox.Store.ImportAsync(new GeneratedAssetImportRequest(
            "integration-import",
            "Objects",
            "Ancient Statue",
            ["stone", "ruin"],
            1,
            1,
            Convert.ToBase64String(CreatePng()),
            null));
        var stagedAssetRef = "sha256:" + imported.GeneratedAssetId;
        var otherLiveGeneratedRef = "sha256:" + new string('1', 64);
        sandbox.PublishCatalog(
            revision: 41,
            additionalEntries:
            [
                Entry(otherLiveGeneratedRef, "Other Generated Asset", generated: true),
            ]);

        var repository = new AssetCatalogRepository(sandbox.CatalogRoot, sandbox.DataRoot, sandbox.TimeProvider);
        Assert.True(repository.TryRefresh());
        var beforeManifest = repository.GetCurrent()!.Manifest;
        var service = new AssetSearchService(repository);

        var ordinaryBeforeActivation = service.Search(new AssetSearchQuery(
            Query: "Ancient Statue",
            Generated: true));
        var stagedBeforeActivation = service.Search(new AssetSearchQuery(
            Query: "Ancient Statue",
            Generated: true,
            IncludeStagedGenerated: true));

        Assert.Empty(ordinaryBeforeActivation.Items);
        var staged = Assert.Single(stagedBeforeActivation.Items);
        Assert.Equal(stagedAssetRef, staged.AssetRef);
        Assert.False(staged.Placeable);
        Assert.Equal(41, stagedBeforeActivation.CatalogRevision);
        Assert.Equal(beforeManifest.CatalogFingerprint, stagedBeforeActivation.CatalogFingerprint);
        Assert.Equal(beforeManifest.SnapshotAt, stagedBeforeActivation.SnapshotAt);

        sandbox.PublishCatalog(
            revision: 42,
            additionalEntries:
            [
                Entry(otherLiveGeneratedRef, "Other Generated Asset", generated: true),
                Entry(stagedAssetRef, "Activated Ancient Statue", generated: true),
            ]);
        Assert.True(repository.TryRefresh());
        var afterManifest = repository.GetCurrent()!.Manifest;

        var ordinaryAfterActivation = service.Search(new AssetSearchQuery(
            Query: "Activated Ancient Statue",
            Generated: true));
        var stagedAfterActivation = service.Search(new AssetSearchQuery(
            Query: "Activated Ancient Statue",
            Generated: true,
            IncludeStagedGenerated: true));

        var active = Assert.Single(ordinaryAfterActivation.Items);
        Assert.True(active.Placeable);
        Assert.Equal(stagedAssetRef, active.AssetRef);
        Assert.Equal("Activated Ancient Statue", active.DisplayName);
        Assert.Single(stagedAfterActivation.Items);
        Assert.Equal(active, stagedAfterActivation.Items[0]);
        Assert.Equal(42, stagedAfterActivation.CatalogRevision);
        Assert.Equal(afterManifest.CatalogFingerprint, stagedAfterActivation.CatalogFingerprint);
    }

    private static AssetCatalogEntry Entry(string assetRef, string displayName, bool generated = false) => new(
        assetRef,
        "Objects",
        displayName,
        Hash(Encoding.UTF8.GetBytes(displayName)),
        generated ? "ddai-generated" : "official-pack",
        generated ? "DDAI Generated Assets" : "Official Pack",
        [],
        generated ? ["generated"] : [],
        null,
        true,
        generated);

    private static byte[] CreatePng()
    {
        using var bitmap = new SKBitmap(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(new SKColor(18, 52, 86, 255));
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class GeneratedCatalogSandbox : IDisposable
    {
        private static readonly DateTimeOffset SnapshotAt = new(2026, 8, 10, 18, 0, 0, TimeSpan.Zero);

        public GeneratedCatalogSandbox()
        {
            DataRoot = Path.Combine(Path.GetTempPath(), "ddai-generated-catalog-tests", Guid.NewGuid().ToString("N"));
            CatalogRoot = Path.Combine(DataRoot, "mailbox", "catalog");
            Directory.CreateDirectory(Path.Combine(CatalogRoot, "snapshots"));
            Directory.CreateDirectory(Path.Combine(CatalogRoot, "previews"));
            Store = new GeneratedAssetStore(DataRoot);
            TimeProvider = new FixedTimeProvider();
        }

        public string DataRoot { get; }
        public string CatalogRoot { get; }
        public GeneratedAssetStore Store { get; }
        public TimeProvider TimeProvider { get; }

        public void PublishCatalog(long revision, IReadOnlyList<AssetCatalogEntry> additionalEntries)
        {
            var entries = AssetCategory.All
                .Select(category => category == "Objects"
                    ? Entry("sha256:" + new string('2', 64), "Official Objects Asset")
                    : new AssetCatalogEntry(
                        AssetReference.Create("official-pack", category, "resource/" + category),
                        category,
                        category + " Asset",
                        Hash(Encoding.UTF8.GetBytes("resource/" + category)),
                        "official-pack",
                        "Official Pack",
                        [],
                        [],
                        null,
                        true,
                        false))
                .Concat(additionalEntries)
                .ToArray();
            var snapshotName = $"snapshot-{revision}";
            var snapshotRoot = Path.Combine(CatalogRoot, "snapshots", snapshotName);
            Directory.CreateDirectory(snapshotRoot);
            var chunkBytes = Encoding.UTF8.GetBytes(AssetCatalogJson.SerializeChunk(entries));
            File.WriteAllBytes(Path.Combine(snapshotRoot, "assets-000.json"), chunkBytes);
            var incomplete = new AssetCatalogManifest(
                AssetCatalogManifest.CurrentSchemaVersion,
                "catalog-session",
                revision,
                new string('0', 64),
                SnapshotAt,
                true,
                entries.GroupBy(entry => entry.Category)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                [new AssetCatalogChunk("assets-000.json", Hash(chunkBytes), entries.Length, chunkBytes.LongLength)],
                []);
            var manifest = incomplete with
            {
                CatalogFingerprint = AssetCatalogJson.ComputeCatalogFingerprint(incomplete),
            };
            File.WriteAllText(
                Path.Combine(snapshotRoot, "manifest.json"),
                AssetCatalogJson.SerializeManifest(manifest));
            File.WriteAllText(
                Path.Combine(CatalogRoot, "current.json"),
                JsonSerializer.Serialize(new
                {
                    session_id = manifest.SessionId,
                    manifest = $"{snapshotName}/manifest.json",
                    catalog_revision = revision,
                }));
        }

        public void Dispose()
        {
            if (Directory.Exists(DataRoot))
            {
                Directory.Delete(DataRoot, recursive: true);
            }
        }

        private sealed class FixedTimeProvider : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => SnapshotAt;
        }
    }
}
