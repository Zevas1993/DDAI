using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DDAI.App.Assets;
using DDAI.Core.Assets;

namespace DDAI.App.Tests.Assets;

public sealed class AssetCatalogRepositoryTests
{
    [Fact]
    public void OptInIsolatedLiveCatalogCopy_PreservesExactEntriesAndPreviewCoverage()
    {
        var root = Environment.GetEnvironmentVariable("DDAI_ISOLATED_LIVE_CATALOG_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        NormalizeIsolatedTask7Metadata(root);
        var repository = new AssetCatalogRepository(root, TimeProvider.System);
        Assert.True(repository.TryRefresh());
        var catalog = Assert.IsType<AcceptedAssetCatalog>(repository.GetCurrent());
        Assert.Equal(1_947, catalog.Entries.Count);
        Assert.Equal(1_947, catalog.Entries.Count(entry =>
            entry.PreviewHash is not null && repository.OpenPreview(entry.PreviewHash) is not null));
    }

    private static void NormalizeIsolatedTask7Metadata(string root)
    {
        using var pointer = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "current.json")));
        var manifestRelativePath = pointer.RootElement.GetProperty("manifest").GetString()
            ?? throw new InvalidDataException("Isolated catalog pointer has no manifest.");
        var manifestPath = Path.Combine(root, "snapshots", manifestRelativePath);
        var manifest = AssetCatalogJson.DeserializeManifest(File.ReadAllText(manifestPath));
        var manifestDirectory = Path.GetDirectoryName(manifestPath)!;
        var receipts = new List<AssetCatalogChunk>();
        foreach (var chunk in manifest.Chunks)
        {
            var path = Path.Combine(manifestDirectory, chunk.FileName);
            var entries = JsonSerializer.Deserialize<IReadOnlyList<AssetCatalogEntry>>(
                File.ReadAllText(path), AssetCatalogJson.SerializerOptions)!;
            var normalized = entries.Select(entry => entry with
            {
                SearchTerms = entry.SearchTerms.Select(CanonicalizeTask7Metadata)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                Tags = entry.Tags.Select(CanonicalizeTask7Metadata)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            }).ToArray();
            var bytes = Encoding.UTF8.GetBytes(AssetCatalogJson.SerializeChunk(normalized));
            File.WriteAllBytes(path, bytes);
            receipts.Add(new AssetCatalogChunk(chunk.FileName, Task7Hash(bytes), normalized.Length, bytes.LongLength));
        }

        var updated = manifest with { Chunks = receipts, CatalogFingerprint = new string('0', 64) };
        updated = updated with { CatalogFingerprint = AssetCatalogJson.ComputeCatalogFingerprint(updated) };
        File.WriteAllText(manifestPath, AssetCatalogJson.SerializeManifest(updated));
    }

    private static string CanonicalizeTask7Metadata(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        while (normalized.Contains("  ", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        }
        return normalized;
    }

    private static string Task7Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public void PublicationAdvice_OverwritesInvalidHighRevisionSlotInsteadOfSoleValidSlot()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        File.Copy(sandbox.CurrentPath, Path.Combine(sandbox.Root, "current-slot-0.json"));
        File.WriteAllText(
            Path.Combine(sandbox.Root, "current-slot-1.json"),
            JsonSerializer.Serialize(new
            {
                session_id = "attacker",
                manifest = "../escape/manifest.json",
                catalog_revision = long.MaxValue,
            }));

        var advice = new AssetCatalogPublicationAdvisor(sandbox.Root, sandbox.TimeProvider)
            .InspectForPublication(0);

        Assert.True(advice.Success);
        Assert.Equal(1, advice.SlotIndex);
        Assert.Equal(2, advice.CatalogRevision);
    }

    [Fact]
    public void PublicationAdvice_IsMonotonicWhenWallClockMovesBackward()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        File.Copy(sandbox.CurrentPath, Path.Combine(sandbox.Root, "current-slot-0.json"));

        var advice = new AssetCatalogPublicationAdvisor(sandbox.Root, sandbox.TimeProvider)
            .InspectForPublication(0);

        Assert.True(advice.Success);
        Assert.Equal(2, advice.CatalogRevision);
        Assert.Equal(1, advice.SlotIndex);
    }

    [Fact]
    public void PublicationAdvice_UsesDeterministicSlotForIdenticalValidTies()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        File.Copy(sandbox.CurrentPath, Path.Combine(sandbox.Root, "current-slot-0.json"));
        File.Copy(sandbox.CurrentPath, Path.Combine(sandbox.Root, "current-slot-1.json"));

        var advice = new AssetCatalogPublicationAdvisor(sandbox.Root, sandbox.TimeProvider)
            .InspectForPublication(1);

        Assert.True(advice.Success);
        Assert.Equal(0, advice.SlotIndex);
        Assert.Equal(2, advice.CatalogRevision);
    }

    [Fact]
    public void PublicationAdvice_RejectsValidFingerprintConflictAtSameRevision()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        File.Copy(sandbox.CurrentPath, Path.Combine(sandbox.Root, "current-slot-0.json"));
        sandbox.PublishConflictingSlotAtSameRevision();

        var advice = new AssetCatalogPublicationAdvisor(sandbox.Root, sandbox.TimeProvider)
            .InspectForPublication(2);

        Assert.False(advice.Success);
        Assert.Equal("catalog_pointer_conflict", advice.ErrorCode);
    }

    [Fact]
    public void PublicationAdvice_RejectsRevisionExhaustionInsteadOfWrapping()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        sandbox.PublishCompleteRevision(revision: 9_007_199_254_740_991L);
        File.Copy(sandbox.CurrentPath, Path.Combine(sandbox.Root, "current-slot-0.json"));

        var advice = new AssetCatalogPublicationAdvisor(sandbox.Root, sandbox.TimeProvider)
            .InspectForPublication(0);

        Assert.False(advice.Success);
        Assert.Equal("catalog_revision_exhausted", advice.ErrorCode);
    }

    [Fact]
    public void TryRefresh_PromotesOnlyACompleteHashVerifiedSnapshot()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        var repository = new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider);

        Assert.True(repository.TryRefresh());

        var current = repository.GetCurrent();
        Assert.NotNull(current);
        Assert.Equal(14, current.Manifest.CategoryCounts.Count);
        Assert.Equal(14, current.Entries.Count);
        Assert.True(current.Live);
        Assert.Equal(TimeSpan.Zero, current.Age);
        var objects = Assert.Single(current.Entries, entry => entry.Category == "Objects");
        Assert.Contains("objects", objects.SearchTerms);
        Assert.Contains("fixture", objects.Tags);
    }

    [Fact]
    public void TryRefresh_KeepsLastGoodWhenNextChunkIsTruncated()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        var repository = new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider);
        Assert.True(repository.TryRefresh());
        var original = repository.GetCurrent()!.Manifest.CatalogFingerprint;

        sandbox.PublishTruncatedNextRevision();

        Assert.False(repository.TryRefresh());
        Assert.Equal(original, repository.GetCurrent()!.Manifest.CatalogFingerprint);
    }

    [Fact]
    public void TryRefresh_RecoversValidSlotAtEveryMutablePointerFaultBoundary()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        var slot = Path.Combine(sandbox.Root, "current-slot-0.json");
        File.Copy(sandbox.CurrentPath, slot);

        foreach (var currentState in new[] { "missing", "truncated", "deleted_after_stage" })
        {
            if (File.Exists(sandbox.CurrentPath))
            {
                File.Delete(sandbox.CurrentPath);
            }

            if (currentState == "truncated")
            {
                File.WriteAllText(sandbox.CurrentPath, "{");
            }
            else if (currentState == "deleted_after_stage")
            {
                File.WriteAllText(sandbox.CurrentPath + ".next", "{\"staged\":true}");
            }

            var repository = new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider);
            Assert.True(repository.TryRefresh(), currentState);
            Assert.Equal(1, repository.GetCurrent()!.Manifest.CatalogRevision);
        }
    }

    [Fact]
    public void TryRefresh_IgnoresMalformedSlotWhenAnotherSlotIsRecoverable()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        File.Copy(sandbox.CurrentPath, Path.Combine(sandbox.Root, "current-slot-0.json"));
        File.WriteAllText(Path.Combine(sandbox.Root, "current-slot-1.json"), "{\"manifest\":\"../escape.json\",\"session_id\":\"attacker\"}");
        File.Delete(sandbox.CurrentPath);

        var repository = new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider);

        Assert.True(repository.TryRefresh());
        Assert.Equal("catalog-session", repository.GetCurrent()!.Manifest.SessionId);
    }

    [Fact]
    public void TryRefresh_RejectsConflictingSnapshotsAtTheSameRevision()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        File.Copy(sandbox.CurrentPath, Path.Combine(sandbox.Root, "current-slot-0.json"));
        sandbox.PublishConflictingSlotAtSameRevision();

        var repository = new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider);

        Assert.False(repository.TryRefresh());
        Assert.Null(repository.GetCurrent());
    }

    [Fact]
    public void TryRefresh_RejectsPointerTraversalWithoutPromoting()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        var repository = new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider);
        Assert.True(repository.TryRefresh());
        var original = repository.GetCurrent()!;
        var outside = Path.Combine(sandbox.Root, "outside");
        Directory.CreateDirectory(outside);
        File.Copy(sandbox.ManifestPath, Path.Combine(outside, "manifest.json"));
        File.Copy(sandbox.ChunkPath, Path.Combine(outside, "assets-000.json"));
        sandbox.WriteCurrent("../outside/manifest.json", sandbox.SessionId);

        Assert.False(repository.TryRefresh());
        Assert.Same(original, repository.GetCurrent());
    }

    [Fact]
    public void TryRefresh_RejectsReparsePointInSnapshotPathWhenSupported()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        var external = Path.Combine(sandbox.Root, "external");
        Directory.CreateDirectory(external);
        File.Copy(sandbox.ManifestPath, Path.Combine(external, "manifest.json"));
        File.Copy(sandbox.ChunkPath, Path.Combine(external, "assets-000.json"));
        var link = Path.Combine(sandbox.SnapshotsPath, "linked");
        CreateDirectoryJunction(link, external);
        try
        {
            sandbox.WriteCurrent("linked/manifest.json", sandbox.SessionId);

            Assert.False(new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider).TryRefresh());
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void TryRefresh_RejectsOversizePointerManifestAndChunk()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        var repository = new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider);
        File.WriteAllText(sandbox.CurrentPath, new string('x', AssetCatalogJson.MaximumJsonBytes + 1));
        Assert.False(repository.TryRefresh());

        sandbox.PublishCompleteRevision();
        File.WriteAllText(sandbox.ManifestPath, new string('x', AssetCatalogJson.MaximumJsonBytes + 1));
        Assert.False(repository.TryRefresh());

        sandbox.PublishCompleteRevision();
        File.WriteAllBytes(sandbox.ChunkPath, new byte[AssetCatalogJson.MaximumJsonBytes + 1]);
        Assert.False(repository.TryRefresh());
    }

    [Fact]
    public void TryRefresh_RejectsChunkWithBadHashAndKeepsLastGood()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        var repository = new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider);
        Assert.True(repository.TryRefresh());
        var original = repository.GetCurrent()!;
        sandbox.MutateChunkWithoutUpdatingManifest();

        Assert.False(repository.TryRefresh());
        Assert.Same(original, repository.GetCurrent());
    }

    [Fact]
    public void TryRefresh_RejectsMissingAndDuplicateChunks()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        var repository = new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider);
        sandbox.DeleteChunk();
        Assert.False(repository.TryRefresh());

        sandbox.PublishCompleteRevision();
        sandbox.PublishManifestWithDuplicateChunk();
        Assert.False(repository.TryRefresh());
    }

    [Fact]
    public void TryRefresh_RejectsCategoryAndPointerSessionMismatches()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        var repository = new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider);
        sandbox.PublishManifestWithCategoryMismatch();
        Assert.False(repository.TryRefresh());

        sandbox.PublishCompleteRevision();
        sandbox.WriteCurrent(sandbox.ManifestRelativePath, "another-session");
        Assert.False(repository.TryRefresh());
    }

    [Fact]
    public void TryRefresh_RejectsIncompleteAndMismatchedDeclaredChunkCounts()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        var repository = new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider);
        sandbox.PublishIncompleteManifest();
        Assert.False(repository.TryRefresh());

        sandbox.PublishCompleteRevision();
        sandbox.PublishManifestWithWrongChunkByteAndRecordCounts();
        Assert.False(repository.TryRefresh());
    }

    [Fact]
    public void GetCurrent_ReturnsOneCompleteSnapshotDuringConcurrentRefreshes()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        var repository = new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider);
        Assert.True(repository.TryRefresh());
        var failures = 0;

        Parallel.For(0, 200, index =>
        {
            if (index % 2 == 0)
            {
                repository.TryRefresh();
                return;
            }

            var current = repository.GetCurrent();
            if (current is null || current.Entries.Count != 14 || current.Manifest.CategoryCounts.Count != 14)
            {
                Interlocked.Increment(ref failures);
            }
        });

        Assert.Equal(0, failures);
    }

    [Fact]
    public void GetCurrent_ReportsAgeAndStaleLiveState()
    {
        using var sandbox = CatalogSandbox.CreateComplete(snapshotAt: CatalogSandbox.Now - TimeSpan.FromSeconds(46));
        var repository = new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider);

        Assert.True(repository.TryRefresh());

        var current = repository.GetCurrent()!;
        Assert.Equal(TimeSpan.FromSeconds(46), current.Age);
        Assert.False(current.Live);
    }

    [Fact]
    public void OpenPreview_RejectsInvalidIdentifierAndOversizeContent()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        var repository = new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider);

        Assert.Null(repository.OpenPreview("ABC"));
        var hash = new string('a', 64);
        sandbox.WritePreview(hash, new byte[262_145]);
        Assert.Null(repository.OpenPreview(hash));
    }

    [Fact]
    public void OpenPreview_ReturnsNullWhenPreviewIsAbsent()
    {
        using var sandbox = CatalogSandbox.CreateComplete();

        Assert.Null(new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider).OpenPreview(new string('a', 64)));
    }

    [Fact]
    public void TryRefresh_ReturnsFalseWhenPointerIsAbsent()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        File.Delete(sandbox.CurrentPath);

        Assert.False(new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider).TryRefresh());
    }

    [Fact]
    public void OpenPreview_ReturnsVerifiedOneByOnePngAndRejectsHashMismatch()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        var repository = new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider);
        var valid = PngFixture.OneByOne;
        var validHash = sandbox.WritePreviewForContent(valid);

        Assert.Equal(valid, repository.OpenPreview(validHash));

        sandbox.WritePreview(validHash, Encoding.UTF8.GetBytes("tampered"));
        Assert.Null(repository.OpenPreview(validHash));
    }

    [Fact]
    public void OpenPreview_RejectsNonPngAndInvalidPngDimensions()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        var repository = new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider);

        var notPngHash = sandbox.WritePreviewForContent(Encoding.ASCII.GetBytes("GIF89a"));
        var zeroWidthHash = sandbox.WritePreviewForContent(PngFixture.WithDimensions(0, 1));
        var oversizedHeightHash = sandbox.WritePreviewForContent(PngFixture.WithDimensions(1, 257));

        Assert.Null(repository.OpenPreview(notPngHash));
        Assert.Null(repository.OpenPreview(zeroWidthHash));
        Assert.Null(repository.OpenPreview(oversizedHeightHash));
    }

    [Fact]
    public void OpenPreview_RejectsMalformedPng()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        var repository = new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider);
        var malformed = PngFixture.OneByOne[..24];
        var hash = sandbox.WritePreviewForContent(malformed);

        Assert.Null(repository.OpenPreview(hash));
    }

    [Fact]
    public void OpenPreview_RejectsCrcCorrectPngWithUndecodableImageData()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        var repository = new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider);
        var corrupted = PngFixture.WithUndecodableIdat();
        var hash = sandbox.WritePreviewForContent(corrupted);

        Assert.Null(repository.OpenPreview(hash));
    }

    [Fact]
    public void OpenPreview_RejectsPreviewReparsePointThroughOpenedHandle()
    {
        using var sandbox = CatalogSandbox.CreateComplete();
        var repository = new AssetCatalogRepository(sandbox.Root, sandbox.TimeProvider);
        var content = PngFixture.OneByOne;
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var external = Path.Combine(sandbox.Root, "outside.png");
        Directory.CreateDirectory(external);
        var preview = Path.Combine(sandbox.PreviewsPath, hash + ".png");
        File.WriteAllBytes(Path.Combine(external, hash + ".png"), content);
        Directory.Delete(sandbox.PreviewsPath);
        CreateDirectoryJunction(sandbox.PreviewsPath, external);
        try
        {
            Assert.Null(repository.OpenPreview(hash));
        }
        finally
        {
            Directory.Delete(sandbox.PreviewsPath);
        }
    }

    [Fact]
    public void FinalPathBufferCapacity_AcceptsAnExact511CharacterFinalPath()
    {
        Assert.True(AssetCatalogRepository.FinalPathFitsBuffer(511, 512));
        Assert.False(AssetCatalogRepository.FinalPathFitsBuffer(512, 512));
    }

    private sealed class CatalogSandbox : IDisposable
    {
        public static readonly DateTimeOffset Now = new(2026, 8, 10, 18, 0, 0, TimeSpan.Zero);

        private CatalogSandbox(DateTimeOffset snapshotAt)
        {
            MailboxRoot = Path.Combine(Path.GetTempPath(), "ddai-asset-catalog-tests", Guid.NewGuid().ToString("N"));
            Root = Path.Combine(MailboxRoot, "catalog");
            SnapshotsPath = Path.Combine(Root, "snapshots");
            PreviewsPath = Path.Combine(Root, "previews");
            Directory.CreateDirectory(SnapshotsPath);
            Directory.CreateDirectory(PreviewsPath);
            TimeProvider = new FixedTimeProvider();
            PublishCompleteRevision(snapshotAt);
        }

        public string Root { get; }
        public string MailboxRoot { get; }
        public string SnapshotsPath { get; }
        public string PreviewsPath { get; }
        public FixedTimeProvider TimeProvider { get; }
        public string SessionId => "catalog-session";
        public string SnapshotPath => Path.Combine(SnapshotsPath, "snapshot-1");
        public string ManifestPath => Path.Combine(SnapshotPath, "manifest.json");
        public string ChunkPath => Path.Combine(SnapshotPath, "assets-000.json");
        public string CurrentPath => Path.Combine(Root, "current.json");
        public string ManifestRelativePath => "snapshot-1/manifest.json";

        public static CatalogSandbox CreateComplete(DateTimeOffset? snapshotAt = null) => new(snapshotAt ?? Now);

        public void PublishTruncatedNextRevision()
        {
            PublishCompleteRevision(Now, revision: 2);
            File.WriteAllText(ChunkPath, "[");
        }

        public void PublishCompleteRevision(DateTimeOffset? snapshotAt = null, long revision = 1)
        {
            Directory.CreateDirectory(SnapshotPath);
            var entries = AssetCategory.All.Select(category => EntryFor(category)).ToArray();
            var chunkBytes = Encoding.UTF8.GetBytes(AssetCatalogJson.SerializeChunk(entries));
            File.WriteAllBytes(ChunkPath, chunkBytes);
            var chunk = new AssetCatalogChunk(
                "assets-000.json",
                Hash(chunkBytes),
                entries.Length,
                chunkBytes.LongLength);
            var manifest = CreateManifest(
                snapshotAt ?? Now,
                revision,
                AssetCategory.All.ToDictionary(category => category, _ => 1, StringComparer.Ordinal),
                [chunk]);
            File.WriteAllText(ManifestPath, AssetCatalogJson.SerializeManifest(manifest));
            WriteCurrent(ManifestRelativePath, SessionId);
        }

        public void WriteCurrent(string manifest, string sessionId) =>
            File.WriteAllText(
                CurrentPath,
                JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["manifest"] = manifest,
                    ["session_id"] = sessionId,
                }));

        public void MutateChunkWithoutUpdatingManifest()
        {
            var bytes = File.ReadAllBytes(ChunkPath);
            bytes[^1] ^= 1;
            File.WriteAllBytes(ChunkPath, bytes);
        }

        public void DeleteChunk() => File.Delete(ChunkPath);

        public void PublishManifestWithDuplicateChunk()
        {
            var bytes = File.ReadAllBytes(ChunkPath);
            var chunk = new AssetCatalogChunk("assets-000.json", Hash(bytes), 7, bytes.LongLength);
            WriteUncheckedManifest(CreateManifest(
                Now,
                1,
                AssetCategory.All.ToDictionary(category => category, _ => 1, StringComparer.Ordinal),
                [chunk, chunk]));
        }

        public void PublishManifestWithCategoryMismatch()
        {
            var bytes = File.ReadAllBytes(ChunkPath);
            var chunk = new AssetCatalogChunk("assets-000.json", Hash(bytes), 14, bytes.LongLength);
            var counts = AssetCategory.All.ToDictionary(category => category, _ => 0, StringComparer.Ordinal);
            counts[AssetCategory.All[0]] = 14;
            WriteUncheckedManifest(CreateManifest(Now, 1, counts, [chunk]));
        }

        public void PublishIncompleteManifest()
        {
            var bytes = File.ReadAllBytes(ChunkPath);
            var chunk = new AssetCatalogChunk("assets-000.json", Hash(bytes), 14, bytes.LongLength);
            var manifest = CreateManifest(
                Now,
                1,
                AssetCategory.All.ToDictionary(category => category, _ => 1, StringComparer.Ordinal),
                [chunk]) with
            { Complete = false };
            WriteUncheckedManifest(manifest with { CatalogFingerprint = AssetCatalogJson.ComputeCatalogFingerprint(manifest) });
        }

        public void PublishManifestWithWrongChunkByteAndRecordCounts()
        {
            var bytes = File.ReadAllBytes(ChunkPath);
            var chunk = new AssetCatalogChunk("assets-000.json", Hash(bytes), 13, bytes.LongLength - 1);
            var manifest = CreateManifest(
                Now,
                1,
                AssetCategory.All.ToDictionary(category => category, _ => 1, StringComparer.Ordinal),
                [chunk]);
            WriteUncheckedManifest(manifest with { CatalogFingerprint = AssetCatalogJson.ComputeCatalogFingerprint(manifest) });
        }

        public void PublishConflictingSlotAtSameRevision()
        {
            var conflictSnapshot = Path.Combine(SnapshotsPath, "snapshot-conflict");
            Directory.CreateDirectory(conflictSnapshot);
            File.Copy(ChunkPath, Path.Combine(conflictSnapshot, "assets-000.json"));
            var manifest = AssetCatalogJson.DeserializeManifest(File.ReadAllText(ManifestPath)) with
            {
                Errors = [new AssetCatalogError("conflict", "conflict", "Objects")],
            };
            manifest = manifest with { CatalogFingerprint = AssetCatalogJson.ComputeCatalogFingerprint(manifest) };
            File.WriteAllText(Path.Combine(conflictSnapshot, "manifest.json"), AssetCatalogJson.SerializeManifest(manifest));
            File.WriteAllText(
                Path.Combine(Root, "current-slot-1.json"),
                JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["manifest"] = "snapshot-conflict/manifest.json",
                    ["session_id"] = SessionId,
                }));
        }

        public void WritePreview(string hash, byte[] bytes) =>
            File.WriteAllBytes(Path.Combine(PreviewsPath, hash + ".png"), bytes);

        public string WritePreviewForContent(byte[] bytes)
        {
            var hash = Hash(bytes);
            WritePreview(hash, bytes);
            return hash;
        }

        public void Dispose()
        {
            if (Directory.Exists(MailboxRoot))
            {
                Directory.Delete(MailboxRoot, recursive: true);
            }
        }

        private AssetCatalogManifest CreateManifest(
            DateTimeOffset snapshotAt,
            long revision,
            IReadOnlyDictionary<string, int> categoryCounts,
            IReadOnlyList<AssetCatalogChunk> chunks)
        {
            var incomplete = new AssetCatalogManifest(
                AssetCatalogManifest.CurrentSchemaVersion,
                SessionId,
                revision,
                new string('0', 64),
                snapshotAt,
                true,
                categoryCounts,
                chunks,
                []);
            return incomplete with { CatalogFingerprint = AssetCatalogJson.ComputeCatalogFingerprint(incomplete) };
        }

        private void WriteUncheckedManifest(AssetCatalogManifest manifest) =>
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, AssetCatalogJson.SerializerOptions));

        private static AssetCatalogEntry EntryFor(string category) => new(
            AssetReference.Create("official-pack", category, "resource/" + category),
            category,
            category + " asset",
            Hash(Encoding.UTF8.GetBytes("resource/" + category)),
            "official-pack",
            "Official Pack",
            [category.ToLowerInvariant()],
            ["fixture"],
            null,
            true,
            false);

        private static string Hash(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => CatalogSandbox.Now;
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("cmd.exe could not create the junction fixture.");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && Directory.Exists(linkPath), "The Windows junction fixture could not be created.");
    }

    private static class PngFixture
    {
        // Hand-checked PNG: signature, 13-byte IHDR for 1x1, one IDAT, and IEND.
        public static readonly byte[] OneByOne = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP4z8DwHwAFAAH/VscvDQAAAABJRU5ErkJggg==");

        public static byte[] WithDimensions(uint width, uint height)
        {
            var bytes = OneByOne.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), width);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), height);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(29, 4), ComputeCrc(bytes.AsSpan(12, 17)));
            return bytes;
        }

        public static byte[] WithUndecodableIdat()
        {
            var bytes = OneByOne.ToArray();
            bytes[41] = 0;
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(54, 4), ComputeCrc(bytes.AsSpan(37, 17)));
            return bytes;
        }

        private static uint ComputeCrc(ReadOnlySpan<byte> bytes)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var value in bytes)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320u;
                }
            }

            return ~crc;
        }
    }
}
