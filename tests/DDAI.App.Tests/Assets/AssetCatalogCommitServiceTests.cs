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

    [Fact]
    public void RequestContentHash_BindsTheExactImmutableRequestBytes()
    {
        using var sandbox = new CommitSandbox();
        var compact = sandbox.Stage("session-exact", "4545454545454545454545454545454545454545454545454545454545454545");
        var formatted = sandbox.StageRequestForExistingCandidate(compact, indented: true);

        Assert.NotEqual(compact.RequestId, formatted.RequestId);

        ProcessUntilRequestConsumed(sandbox, compact);
        ProcessUntilRequestConsumed(sandbox, formatted);

        Assert.Equal(Hash(compact.RequestBytes), sandbox.ReadReceipt(compact).GetProperty("request_content_hash").GetString());
        Assert.Equal(Hash(formatted.RequestBytes), sandbox.ReadReceipt(formatted).GetProperty("request_content_hash").GetString());
        Assert.Equal(compact.CandidateFingerprint, sandbox.ReadReceipt(formatted).GetProperty("candidate_fingerprint").GetString());
    }

    [Fact]
    public void CrashAfterDurableResponse_LaterCommitsAndSlotReuse_DoNotInvalidateOldAcknowledgement()
    {
        using var sandbox = new CommitSandbox();
        var first = sandbox.Stage("session-response-crash", "5656565656565656565656565656565656565656565656565656565656565656");

        Assert.Throws<SimulatedCrashException>(() =>
            new AssetCatalogCommitService(sandbox.Root, sandbox.TimeProvider, boundary =>
            {
                if (boundary == "after-response") throw new SimulatedCrashException();
            }).ProcessPending());
        Assert.True(File.Exists(sandbox.ResponsePath(first)));
        Assert.True(File.Exists(sandbox.RequestPath(first)));
        var firstRequestBytes = File.ReadAllBytes(sandbox.RequestPath(first));
        var firstReceiptBytes = File.ReadAllBytes(sandbox.ResponsePath(first));
        File.Delete(sandbox.RequestPath(first));

        var second = sandbox.Stage("session-later-a", "6767676767676767676767676767676767676767676767676767676767676767");
        ProcessUntilRequestConsumed(sandbox, second);
        var third = sandbox.Stage("session-later-b", "7878787878787878787878787878787878787878787878787878787878787878");
        ProcessUntilRequestConsumed(sandbox, third);
        var canonicalBeforeRetry = File.ReadAllBytes(Path.Combine(sandbox.CatalogRoot, "current.json"));
        var repositoryBeforeRetry = new AssetCatalogRepository(sandbox.CatalogRoot, sandbox.TimeProvider);
        Assert.True(repositoryBeforeRetry.TryRefresh());
        var revisionBeforeRetry = repositoryBeforeRetry.GetCurrent()!.Manifest.CatalogRevision;

        File.WriteAllBytes(sandbox.RequestPath(first), firstRequestBytes);
        _ = new AssetCatalogCommitService(sandbox.Root, sandbox.TimeProvider).ProcessPending();

        Assert.False(File.Exists(sandbox.RequestPath(first)));
        Assert.Equal(firstReceiptBytes, File.ReadAllBytes(sandbox.ResponsePath(first)));
        Assert.Equal(canonicalBeforeRetry, File.ReadAllBytes(Path.Combine(sandbox.CatalogRoot, "current.json")));
        var repository = new AssetCatalogRepository(sandbox.CatalogRoot, sandbox.TimeProvider);
        Assert.True(repository.TryRefresh());
        Assert.Equal(revisionBeforeRetry, repository.GetCurrent()!.Manifest.CatalogRevision);
        var oldReceipt = sandbox.ReadReceipt(first);
        Assert.True(File.Exists(Path.Combine(sandbox.CatalogRoot, "snapshots", oldReceipt.GetProperty("manifest_path").GetString()!)));
    }

    [Fact]
    public void EightMalformedEarlyRequests_AreQuarantinedWithoutCrashingAndTheLaterValidRequestProgresses()
    {
        using var sandbox = new CommitSandbox();
        var valid = sandbox.StageWithRequestIdPrefix(
            "f",
            "8989898989898989898989898989898989898989898989898989898989898989");
        string[] malformedBodies =
        [
            "{}",
            "{\"schema_version\":null}",
            "{\"schema_version\":1}",
            "{\"schema_version\":\"1.0\",\"candidate_fingerprint\":null}",
            "{\"schema_version\":\"1.0\",\"candidate_fingerprint\":3}",
            "{\"schema_version\":\"1.0\",\"candidate_fingerprint\":\"" + new string('a', 64) + "\",\"session_id\":null}",
            "{\"schema_version\":\"1.0\",\"candidate_fingerprint\":\"" + new string('a', 64) + "\",\"session_id\":7}",
            "{\"schema_version\":\"1.0\",\"candidate_fingerprint\":\"" + new string('a', 64) + "\",\"session_id\":\"bad\",\"snapshot_at\":null}",
        ];
        foreach (var body in malformedBodies)
        {
            sandbox.WriteMalformedRequestWithPrefix(body, "0");
        }

        var service = new AssetCatalogCommitService(sandbox.Root, sandbox.TimeProvider);
        Assert.Equal(8, service.ProcessPending());
        Assert.True(File.Exists(sandbox.RequestPath(valid)));
        Assert.Equal(8, Directory.GetFileSystemEntries(sandbox.QuarantineRoot).Length);

        Assert.Equal(1, service.ProcessPending());
        Assert.False(File.Exists(sandbox.RequestPath(valid)));
        Assert.True(sandbox.ReadReceipt(valid).GetProperty("success").GetBoolean());
    }

    [Fact]
    public void CandidateAndRequiredParents_CannotBeSwappedWhileTheTransactionWaitsForTheCatalogMutex()
    {
        using var sandbox = new CommitSandbox();
        var request = sandbox.Stage("session-candidate-lease", "9090909090909090909090909090909090909090909090909090909090909090");
        var mutexMethod = typeof(AssetCatalogCommitService).GetMethod(
            "MutexName",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var mutexName = Assert.IsType<string>(mutexMethod!.Invoke(null, [sandbox.CatalogRoot]));
        using var mutex = new Mutex(false, mutexName);
        Assert.True(mutex.WaitOne(TimeSpan.FromSeconds(5)));
        Exception? workerFailure = null;
        var worker = new Thread(() =>
        {
            try { _ = new AssetCatalogCommitService(sandbox.Root, sandbox.TimeProvider).ProcessPending(); }
            catch (Exception exception) { workerFailure = exception; }
        });
        worker.Start();
        Assert.True(SpinWait.SpinUntil(
            () => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0,
            TimeSpan.FromSeconds(5)), "The commit worker did not reach the held catalog mutex.");

        var candidateSwap = request.CandidateRoot + "-swapped";
        var parent = Path.GetDirectoryName(request.CandidateRoot)!;
        var parentSwap = parent + "-swapped";
        var candidateMoved = TryMoveDirectory(request.CandidateRoot, candidateSwap);
        var parentMoved = TryMoveDirectory(parent, parentSwap);
        if (candidateMoved) Directory.Move(candidateSwap, request.CandidateRoot);
        if (parentMoved) Directory.Move(parentSwap, parent);
        mutex.ReleaseMutex();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)));

        Assert.Null(workerFailure);
        Assert.False(candidateMoved);
        Assert.False(parentMoved);
    }

    [Fact]
    public void CatalogMutexName_UsesTheGlobalPerUserNamespaceAcrossWindowsSessions()
    {
        using var sandbox = new CommitSandbox();
        var mutexMethod = typeof(AssetCatalogCommitService).GetMethod(
            "MutexName",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        var mutexName = Assert.IsType<string>(mutexMethod!.Invoke(null, [sandbox.CatalogRoot]));

        Assert.StartsWith("Global\\DDAI.AssetCatalog.", mutexName, StringComparison.Ordinal);
        Assert.DoesNotContain("Local\\", mutexName, StringComparison.Ordinal);
        using var first = new Mutex(false, mutexName, out _);
        using var reopened = Mutex.OpenExisting(mutexName);
        Assert.True(first.WaitOne(TimeSpan.FromSeconds(1)));
        first.ReleaseMutex();
    }

    private static bool TryMoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
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
                ["fixture"],
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
            return WriteRequest(sessionId, manifest.SnapshotAt, candidateFingerprint, candidateRoot, indented: false);
        }

        public StagedRequest StageRequestForExistingCandidate(StagedRequest existing, bool indented) =>
            WriteRequest(existing.SessionId, existing.SnapshotAt, existing.CandidateFingerprint, existing.CandidateRoot, indented);

        public StagedRequest StageWithRequestIdPrefix(string prefix, string assetHash)
        {
            for (var index = 0; index < 10_000; index++)
            {
                var request = Stage("session-valid-" + index, assetHash);
                if (request.RequestId.StartsWith(prefix, StringComparison.Ordinal)) return request;
                File.Delete(RequestPath(request));
            }
            throw new InvalidOperationException("Could not produce the requested deterministic request hash prefix.");
        }

        public void WriteMalformedRequestWithPrefix(string body, string prefix)
        {
            for (var padding = 0; padding < 100_000; padding++)
            {
                var bytes = Encoding.UTF8.GetBytes(body + new string(' ', padding));
                var requestId = Hash(bytes);
                var path = Path.Combine(Root, "private", "catalog-commit", "requests", requestId + ".json");
                if (!requestId.StartsWith(prefix, StringComparison.Ordinal) || File.Exists(path)) continue;
                File.WriteAllBytes(path, bytes);
                return;
            }
            throw new InvalidOperationException("Could not produce the requested malformed request hash prefix.");
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
                    session_id = request.SessionId,
                    success = true,
                    manifest_path = request.SessionId + "-999-" + request.CandidateFingerprint + "/manifest.json",
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

        private StagedRequest WriteRequest(
            string sessionId,
            DateTimeOffset snapshotAt,
            string candidateFingerprint,
            string candidateRoot,
            bool indented)
        {
            var options = AssetCatalogJson.SerializerOptions;
            options.WriteIndented = indented;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema_version = "1.0",
                candidate_fingerprint = candidateFingerprint,
                session_id = sessionId,
                snapshot_at = snapshotAt,
            }, options);
            var requestId = Hash(bytes);
            var request = new StagedRequest(requestId, requestId, candidateFingerprint, candidateRoot, sessionId, snapshotAt, bytes);
            File.WriteAllBytes(RequestPath(request), bytes);
            return request;
        }
    }

    private sealed record StagedRequest(
        string RequestId,
        string RequestContentHash,
        string CandidateFingerprint,
        string CandidateRoot,
        string SessionId,
        DateTimeOffset SnapshotAt,
        byte[] RequestBytes);

    private sealed class SimulatedCrashException : Exception
    {
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
