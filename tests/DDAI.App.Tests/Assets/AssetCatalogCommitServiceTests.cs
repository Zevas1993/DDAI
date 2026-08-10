using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DDAI.App.Assets;
using DDAI.Core.Assets;

namespace DDAI.App.Tests.Assets;

public sealed class AssetCatalogCommitServiceTests
{
    [Fact]
    public async Task ConcurrentPublishers_CommitDistinctMonotonicRevisionsAndValidFinalCatalog()
    {
        using var sandbox = new CommitSandbox();
        var first = sandbox.Stage("session-a", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var second = sandbox.Stage("session-b", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        using var gate = new ManualResetEventSlim(false);

        var workers = new[] { first, second }.Select(_ => Task.Run(() =>
        {
            gate.Wait();
            return new AssetCatalogCommitService(sandbox.Root, sandbox.TimeProvider).ProcessPending();
        })).ToArray();
        gate.Set();
        await Task.WhenAll(workers);

        var receipts = new[] { sandbox.ReadReceipt(first), sandbox.ReadReceipt(second) };
        Assert.Equal(new long[] { 1_800_000_000_000, 1_800_000_000_001 }, receipts.Select(item => item.GetProperty("catalog_revision").GetInt64()).Order().ToArray());
        Assert.All(receipts, receipt => Assert.True(receipt.GetProperty("success").GetBoolean()));
        var repository = new AssetCatalogRepository(sandbox.CatalogRoot, sandbox.TimeProvider);
        Assert.True(repository.TryRefresh());
        Assert.Equal(1_800_000_000_001, repository.GetCurrent()!.Manifest.CatalogRevision);
    }

    [Fact]
    public void StaleResponseAndRequest_AreNotAcknowledgedWithoutExactBindings()
    {
        using var sandbox = new CommitSandbox();
        var request = sandbox.Stage("session-stale", "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");
        sandbox.WriteResponse(request, requestContentHash: new string('0', 64), candidateFingerprint: new string('1', 64));

        ProcessUntilRequestConsumed(sandbox, request);

        var receipt = sandbox.ReadReceipt(request);
        Assert.Equal(request.RequestId, receipt.GetProperty("request_id").GetString());
        Assert.Equal(request.RequestContentHash, receipt.GetProperty("request_content_hash").GetString());
        Assert.Equal(request.CandidateFingerprint, receipt.GetProperty("candidate_fingerprint").GetString());
        Assert.Single(Directory.GetFileSystemEntries(sandbox.QuarantineRoot));
    }

    [Fact]
    public void CandidateChangedAfterRequest_FailsClosedWithoutPublishingPointer()
    {
        using var sandbox = new CommitSandbox();
        var request = sandbox.Stage("session-tampered", "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd");
        File.AppendAllText(Path.Combine(request.CandidateRoot, "chunk-0000.json"), " ");

        ProcessUntilRequestConsumed(sandbox, request);

        Assert.False(File.Exists(Path.Combine(sandbox.CatalogRoot, "current.json")));
        Assert.False(File.Exists(sandbox.ResponsePath(request)));
        Assert.Single(Directory.GetFileSystemEntries(sandbox.QuarantineRoot));
    }

    [Fact]
    public void SelectedSlotFailure_RetainsPreviouslyCommittedCatalogAndSkipsCanonical()
    {
        using var sandbox = new CommitSandbox();
        var first = sandbox.Stage("session-first", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        ProcessUntilRequestConsumed(sandbox, first);
        var canonicalBefore = File.ReadAllBytes(Path.Combine(sandbox.CatalogRoot, "current.json"));
        var second = sandbox.Stage("session-second", "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
        Directory.CreateDirectory(Path.Combine(sandbox.CatalogRoot, "current-slot-1.json"));
        File.WriteAllText(Path.Combine(sandbox.CatalogRoot, "current-slot-1.json", "keep.txt"), "keep");

        _ = new AssetCatalogCommitService(sandbox.Root, sandbox.TimeProvider).ProcessPending();

        Assert.Equal(canonicalBefore, File.ReadAllBytes(Path.Combine(sandbox.CatalogRoot, "current.json")));
        var repository = new AssetCatalogRepository(sandbox.CatalogRoot, sandbox.TimeProvider);
        Assert.True(repository.TryRefresh());
        Assert.Equal("session-first", repository.GetCurrent()!.Manifest.SessionId);
        Assert.False(File.Exists(sandbox.ResponsePath(second)));
    }

    [Theory]
    [InlineData("after-slot")]
    [InlineData("after-canonical")]
    public void CrashBoundariesBeforeReceipt_RetryReturnsSameCommitWithoutNewRevision(string crashBoundary)
    {
        using var sandbox = new CommitSandbox();
        var request = sandbox.Stage("session-retry", "1212121212121212121212121212121212121212121212121212121212121212");

        Assert.Throws<SimulatedCrashException>(() =>
            new AssetCatalogCommitService(sandbox.Root, sandbox.TimeProvider, boundary =>
            {
                if (boundary == crashBoundary) throw new SimulatedCrashException();
            }).ProcessPending());
        var repository = new AssetCatalogRepository(sandbox.CatalogRoot, sandbox.TimeProvider);
        Assert.True(repository.TryRefresh());
        var committedRevision = repository.GetCurrent()!.Manifest.CatalogRevision;
        Assert.False(File.Exists(sandbox.ResponsePath(request)));

        _ = new AssetCatalogCommitService(sandbox.Root, sandbox.TimeProvider).ProcessPending();

        Assert.Equal(committedRevision, repository.GetCurrent()!.Manifest.CatalogRevision);
        Assert.Equal(committedRevision, sandbox.ReadReceipt(request).GetProperty("catalog_revision").GetInt64());
    }

    [Fact]
    public void ResponseDestinationDirectory_IsConsumedAndDoesNotBlockCommit()
    {
        using var sandbox = new CommitSandbox();
        var request = sandbox.Stage("session-blocker", "3434343434343434343434343434343434343434343434343434343434343434");
        Directory.CreateDirectory(sandbox.ResponsePath(request));

        ProcessUntilRequestConsumed(sandbox, request);

        Assert.True(sandbox.ReadReceipt(request).GetProperty("success").GetBoolean());
        Assert.Single(Directory.GetFileSystemEntries(sandbox.QuarantineRoot));
    }

    private static void ProcessUntilRequestConsumed(CommitSandbox sandbox, StagedRequest request)
    {
        for (var pass = 0; pass < 10 && File.Exists(sandbox.RequestPath(request)); pass++)
        {
            _ = new AssetCatalogCommitService(sandbox.Root, sandbox.TimeProvider).ProcessPending();
        }
        Assert.False(File.Exists(sandbox.RequestPath(request)));
    }

    private sealed class CommitSandbox : IDisposable
    {
        public CommitSandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "ddai-catalog-commit-tests", Guid.NewGuid().ToString("N"));
            CatalogRoot = Path.Combine(Root, "catalog");
            QuarantineRoot = Path.Combine(Root, "private", "catalog-commit", "quarantine");
            Directory.CreateDirectory(Path.Combine(CatalogRoot, "snapshots"));
            Directory.CreateDirectory(Path.Combine(CatalogRoot, "previews"));
            Directory.CreateDirectory(Path.Combine(Root, "private", "catalog-commit", "requests"));
            Directory.CreateDirectory(Path.Combine(Root, "private", "catalog-commit", "responses"));
            Directory.CreateDirectory(Path.Combine(Root, "private", "catalog-commit", "candidates"));
            TimeProvider = new FixedTimeProvider();
        }

        public string Root { get; }
        public string CatalogRoot { get; }
        public string QuarantineRoot { get; }
        public FixedTimeProvider TimeProvider { get; }

        public StagedRequest Stage(string sessionId, string assetHash)
        {
            var entry = new AssetCatalogEntry(
                "sha256:" + assetHash,
                "Objects",
                "Fixture",
                assetHash,
                "pack",
                "Pack",
                ["Fixture"],
                [],
                null,
                true,
                false);
            var chunkText = AssetCatalogJson.SerializeChunk([entry]);
            var chunkBytes = Encoding.UTF8.GetBytes(chunkText);
            var counts = AssetCategory.All.ToDictionary(category => category, category => category == "Objects" ? 1 : 0, StringComparer.Ordinal);
            var manifest = new AssetCatalogManifest(
                "1.0",
                sessionId,
                0,
                new string('0', 64),
                DateTimeOffset.Parse("2026-08-10T12:00:00Z"),
                true,
                counts,
                [new AssetCatalogChunk("chunk-0000.json", Hash(chunkBytes), 1, chunkBytes.Length)],
                []);
            manifest = manifest with { CatalogFingerprint = AssetCatalogJson.ComputeCatalogFingerprint(manifest) };
            var manifestText = AssetCatalogJson.SerializeManifest(manifest);
            var candidateFingerprint = Hash(Encoding.UTF8.GetBytes(manifestText));
            var candidateRoot = Path.Combine(Root, "private", "catalog-commit", "candidates", candidateFingerprint);
            Directory.CreateDirectory(candidateRoot);
            File.WriteAllText(Path.Combine(candidateRoot, "chunk-0000.json"), chunkText);
            File.WriteAllText(Path.Combine(candidateRoot, "manifest.json"), manifestText);
            var requestContent = RequestContent(sessionId, manifest.SnapshotAt, candidateFingerprint);
            var requestId = Hash(Encoding.UTF8.GetBytes(requestContent));
            var request = new StagedRequest(requestId, requestId, candidateFingerprint, candidateRoot);
            File.WriteAllText(
                Path.Combine(Root, "private", "catalog-commit", "requests", requestId + ".json"),
                JsonSerializer.Serialize(new
                {
                    schema_version = "1.0",
                    request_id = requestId,
                    request_content_hash = requestId,
                    candidate_fingerprint = candidateFingerprint,
                    session_id = sessionId,
                    snapshot_at = manifest.SnapshotAt,
                }, AssetCatalogJson.SerializerOptions));
            return request;
        }

        public void WriteResponse(StagedRequest request, string requestContentHash, string candidateFingerprint) =>
            File.WriteAllText(
                ResponsePath(request),
                JsonSerializer.Serialize(new
                {
                    schema_version = "1.0",
                    request_id = request.RequestId,
                    request_content_hash = requestContentHash,
                    candidate_fingerprint = candidateFingerprint,
                    success = true,
                    catalog_revision = 999,
                    catalog_fingerprint = new string('9', 64),
                    slot_index = 0,
                    state_token = new string('8', 64),
                    error_code = (string?)null,
                }, AssetCatalogJson.SerializerOptions));

        public JsonElement ReadReceipt(StagedRequest request) => JsonDocument.Parse(File.ReadAllText(ResponsePath(request))).RootElement.Clone();
        public string ResponsePath(StagedRequest request) => Path.Combine(Root, "private", "catalog-commit", "responses", request.RequestId + ".json");
        public string RequestPath(StagedRequest request) => Path.Combine(Root, "private", "catalog-commit", "requests", request.RequestId + ".json");

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }

        private static string RequestContent(string sessionId, DateTimeOffset snapshotAt, string candidateFingerprint) =>
            "schema_version=3:1.0\n" +
            "session_id=" + Encoding.UTF8.GetByteCount(sessionId) + ":" + sessionId + "\n" +
            "snapshot_at=33:" + snapshotAt.ToUniversalTime().ToString("O") + "\n" +
            "candidate_fingerprint=64:" + candidateFingerprint + "\n";
    }

    private sealed record StagedRequest(string RequestId, string RequestContentHash, string CandidateFingerprint, string CandidateRoot);

    private sealed class SimulatedCrashException : Exception
    {
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
