using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Theory]
    [InlineData("arbitrary-id")]
    [InlineData("content-hash")]
    [InlineData("category")]
    [InlineData("grid-width")]
    [InlineData("path-name")]
    [InlineData("controlled-name")]
    [InlineData("oversize-name")]
    [InlineData("oversize-tags")]
    [InlineData("oversize-tag")]
    [InlineData("noncanonical-tag")]
    [InlineData("traversal-id")]
    public async Task Search_RejectsStagedManifestThatDoesNotMatchTheGeneratedStoreCanonicalAuthority(string mutation)
    {
        using var sandbox = new GeneratedCatalogSandbox();
        var imported = await sandbox.ImportAsync("hostile-authority");
        sandbox.PublishCatalog(revision: 51, additionalEntries: []);
        var repository = new AssetCatalogRepository(sandbox.CatalogRoot, sandbox.DataRoot, sandbox.TimeProvider);
        Assert.True(repository.TryRefresh());
        var accepted = repository.GetCurrent()!.Manifest;
        var leakedPath = @"C:\Users\Someone\Purchased Pack\secret.png";

        sandbox.MutateManifest(imported.GeneratedAssetId, mutation, leakedPath);

        var result = new AssetSearchService(repository).Search(new AssetSearchQuery(
            Generated: true,
            IncludeStagedGenerated: true));

        Assert.Empty(result.Items);
        Assert.Equal(51, result.CatalogRevision);
        Assert.Equal(accepted.CatalogFingerprint, result.CatalogFingerprint);
        Assert.Equal(accepted.SnapshotAt, result.SnapshotAt);
        Assert.DoesNotContain(leakedPath, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_MalformedAndOversizeStagedManifestsDoNotContaminateAcceptedLiveState()
    {
        using var sandbox = new GeneratedCatalogSandbox();
        var imported = await sandbox.ImportAsync("malformed-bounds");
        sandbox.PublishCatalog(revision: 61, additionalEntries: []);
        var repository = new AssetCatalogRepository(sandbox.CatalogRoot, sandbox.DataRoot, sandbox.TimeProvider);
        Assert.True(repository.TryRefresh());
        var accepted = repository.GetCurrent()!.Manifest;
        var service = new AssetSearchService(repository);

        File.WriteAllText(sandbox.ManifestPath(imported.GeneratedAssetId), "{\"schema_version\":1}");
        var malformed = service.Search(new AssetSearchQuery(Generated: true, IncludeStagedGenerated: true));
        File.WriteAllText(sandbox.ManifestPath(imported.GeneratedAssetId), new string('x', 65_537));
        var oversize = service.Search(new AssetSearchQuery(Generated: true, IncludeStagedGenerated: true));

        Assert.Empty(malformed.Items);
        Assert.Empty(oversize.Items);
        Assert.Equal(accepted.CatalogFingerprint, repository.GetCurrent()!.Manifest.CatalogFingerprint);
        Assert.Equal(61, repository.GetCurrent()!.Manifest.CatalogRevision);
    }

    [Fact]
    public async Task Search_ReparseBackedGeneratedManifestDirectoryDoesNotContaminateAcceptedLiveState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new GeneratedCatalogSandbox();
        _ = await sandbox.ImportAsync("reparse-root");
        sandbox.PublishCatalog(revision: 71, additionalEntries: []);
        var repository = new AssetCatalogRepository(sandbox.CatalogRoot, sandbox.DataRoot, sandbox.TimeProvider);
        Assert.True(repository.TryRefresh());
        var accepted = repository.GetCurrent()!.Manifest;

        sandbox.ReplaceManifestDirectoryWithJunction();
        var result = new AssetSearchService(repository).Search(new AssetSearchQuery(
            Generated: true,
            IncludeStagedGenerated: true));

        Assert.Empty(result.Items);
        Assert.Equal(71, result.CatalogRevision);
        Assert.Equal(accepted.CatalogFingerprint, result.CatalogFingerprint);
    }

    [Fact]
    public async Task Search_SameReferenceVisibleMetadataMutationInvalidatesAnExistingOverlayCursor()
    {
        using var sandbox = new GeneratedCatalogSandbox();
        var first = await sandbox.ImportAsync("cursor-first", "Alpha Statue");
        _ = await sandbox.ImportAsync("cursor-second", "Beta Statue");
        sandbox.PublishCatalog(revision: 81, additionalEntries: []);
        var repository = new AssetCatalogRepository(sandbox.CatalogRoot, sandbox.DataRoot, sandbox.TimeProvider);
        Assert.True(repository.TryRefresh());
        var service = new AssetSearchService(repository);
        var firstPage = service.Search(new AssetSearchQuery(
            Generated: true,
            Limit: 1,
            IncludeStagedGenerated: true));
        Assert.NotNull(firstPage.NextCursor);

        sandbox.SetPreviewHash(first.GeneratedAssetId, new string('f', 64));

        Assert.Throws<ArgumentException>(() => service.Search(new AssetSearchQuery(
            Generated: true,
            Limit: 1,
            Cursor: firstPage.NextCursor,
            IncludeStagedGenerated: true)));
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
        private string? manifestJunctionPath;

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

        public Task<GeneratedAssetImportResult> ImportAsync(string key, string name = "Ancient Statue") =>
            Store.ImportAsync(new GeneratedAssetImportRequest(
                key,
                "Objects",
                name,
                ["stone", "ruin"],
                1,
                1,
                Convert.ToBase64String(CreatePng()),
                null));

        public string ManifestPath(string generatedAssetId) =>
            Path.Combine(DataRoot, "generated-assets", "manifest", generatedAssetId + ".json");

        public void MutateManifest(string generatedAssetId, string mutation, string leakedPath)
        {
            var originalPath = ManifestPath(generatedAssetId);
            var manifest = JsonNode.Parse(File.ReadAllText(originalPath))!.AsObject();
            switch (mutation)
            {
                case "arbitrary-id":
                    var arbitraryId = generatedAssetId[0] == 'a' ? new string('b', 64) : new string('a', 64);
                    manifest["generated_asset_id"] = arbitraryId;
                    File.WriteAllText(ManifestPath(arbitraryId), manifest.ToJsonString());
                    File.Delete(originalPath);
                    return;
                case "content-hash":
                    manifest["content_hash"] = new string('c', 64);
                    break;
                case "category":
                    manifest["category"] = "Walls";
                    break;
                case "grid-width":
                    manifest["grid_width"] = 2;
                    break;
                case "path-name":
                    manifest["name"] = leakedPath;
                    break;
                case "controlled-name":
                    manifest["name"] = "safe\u202ename";
                    break;
                case "oversize-name":
                    manifest["name"] = new string('n', 121);
                    break;
                case "oversize-tags":
                    manifest["tags"] = new JsonArray(
                        Enumerable.Range(0, 33).Select(index => JsonValue.Create("tag-" + index)).ToArray());
                    break;
                case "oversize-tag":
                    manifest["tags"] = new JsonArray(new string('t', 65));
                    break;
                case "noncanonical-tag":
                    manifest["tags"] = new JsonArray(" Stone ");
                    break;
                case "traversal-id":
                    manifest["generated_asset_id"] = "../secret";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }

            File.WriteAllText(originalPath, manifest.ToJsonString());
        }

        public void SetPreviewHash(string generatedAssetId, string previewHash)
        {
            var path = ManifestPath(generatedAssetId);
            var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            manifest["preview_hash"] = previewHash;
            File.WriteAllText(path, manifest.ToJsonString());
        }

        public void ReplaceManifestDirectoryWithJunction()
        {
            var manifestRoot = Path.Combine(DataRoot, "generated-assets", "manifest");
            var targetRoot = Path.Combine(DataRoot, "generated-assets", "manifest-target");
            Directory.Move(manifestRoot, targetRoot);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{manifestRoot}\" \"{targetRoot}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("cmd.exe could not create the junction fixture.");
            process.WaitForExit();
            Assert.True(process.ExitCode == 0 && Directory.Exists(manifestRoot), "The Windows junction fixture could not be created.");
            manifestJunctionPath = manifestRoot;
        }

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
            if (manifestJunctionPath is not null && Directory.Exists(manifestJunctionPath))
            {
                Directory.Delete(manifestJunctionPath);
            }

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
