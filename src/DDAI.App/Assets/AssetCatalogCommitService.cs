using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DDAI.Core.Assets;

namespace DDAI.App.Assets;

public sealed class AssetCatalogCommitService
{
    private const int MaximumRequestsPerPass = 8;
    private const int MaximumDirectoryCandidates = 256;
    private const int MaximumRequestBytes = 4096;
    private readonly string mailboxRoot;
    private readonly string catalogRoot;
    private readonly TimeProvider timeProvider;
    private readonly SafeLocalFileSystem fileSystem;
    private readonly string requestsRoot;
    private readonly string responsesRoot;
    private readonly string candidatesRoot;
    private readonly string quarantineRoot;
    private readonly Action<string>? faultBoundary;

    public AssetCatalogCommitService(string mailboxRoot, TimeProvider timeProvider) : this(mailboxRoot, timeProvider, null)
    {
    }

    internal AssetCatalogCommitService(string mailboxRoot, TimeProvider timeProvider, Action<string>? faultBoundary)
    {
        this.mailboxRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mailboxRoot));
        this.timeProvider = timeProvider;
        catalogRoot = Path.Combine(this.mailboxRoot, "catalog");
        fileSystem = new SafeLocalFileSystem(this.mailboxRoot);
        fileSystem.EnsureDirectory("catalog");
        fileSystem.EnsureDirectory("catalog", "snapshots");
        fileSystem.EnsureDirectory("catalog", "previews");
        requestsRoot = fileSystem.EnsureDirectory("private", "catalog-commit", "requests");
        responsesRoot = fileSystem.EnsureDirectory("private", "catalog-commit", "responses");
        candidatesRoot = fileSystem.EnsureDirectory("private", "catalog-commit", "candidates");
        quarantineRoot = fileSystem.EnsureDirectory("private", "catalog-commit", "quarantine");
        this.faultBoundary = faultBoundary;
    }

    public int ProcessPending()
    {
        using var lease = fileSystem.AcquireDirectoryLease();
        var processed = 0;
        foreach (var requestPath in fileSystem.EnumerateFiles(requestsRoot, "*.json", MaximumDirectoryCandidates).Take(MaximumRequestsPerPass))
        {
            if (ProcessOne(requestPath)) processed++;
        }
        return processed;
    }

    private bool ProcessOne(string requestPath)
    {
        CommitRequest request;
        Candidate candidate;
        try
        {
            request = JsonSerializer.Deserialize<CommitRequest>(
                    fileSystem.ReadBounded(requestPath, MaximumRequestBytes),
                    AssetCatalogJson.SerializerOptions)
                ?? throw new JsonException("Catalog commit request cannot be null.");
            ValidateRequest(request, requestPath);
            candidate = ReadCandidate(request);
        }
        catch (Exception exception) when (AssetPackNormalizationService.IsPrivateDataFailure(exception))
        {
            return Quarantine(requestPath);
        }

        var responsePath = Path.Combine(responsesRoot, request.RequestId + ".json");
        try
        {
            if (fileSystem.EntryExists(responsePath))
            {
                var existing = ReadResponse(responsePath);
                if (ResponseMatchesRequest(existing, request) && IsCommitted(existing))
                {
                    fileSystem.DeleteOrdinaryOrLink(requestPath);
                    return true;
                }
                fileSystem.QuarantineOrdinaryOrDeleteLink(responsePath, quarantineRoot);
            }
        }
        catch (Exception exception) when (AssetPackNormalizationService.IsPrivateDataFailure(exception))
        {
            try { fileSystem.QuarantineOrdinaryOrDeleteLink(responsePath, quarantineRoot); }
            catch (Exception nested) when (AssetPackNormalizationService.IsPrivateDataFailure(nested)) { return false; }
        }

        using var mutex = new Mutex(false, MutexName(catalogRoot));
        var entered = false;
        try
        {
            try { entered = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { entered = true; }
            if (!entered) return false;

            // Re-read every bound input after serialization.
            request = JsonSerializer.Deserialize<CommitRequest>(
                    fileSystem.ReadBounded(requestPath, MaximumRequestBytes),
                    AssetCatalogJson.SerializerOptions)
                ?? throw new JsonException("Catalog commit request cannot be null.");
            ValidateRequest(request, requestPath);
            candidate = ReadCandidate(request);

            var inspections = new AssetCatalogRepository(catalogRoot, timeProvider).InspectPointers();
            AssetCatalogRepository.EnsureNoRevisionConflict(
                inspections.Where(pointer => pointer.Catalog is not null).Select(pointer => pointer.Catalog!));

            var alreadyCommitted = inspections
                .Where(pointer => pointer.Catalog is not null && CandidateMatches(candidate, pointer.Catalog!.Manifest))
                .ToArray();
            var committedSlot = alreadyCommitted.FirstOrDefault(pointer => pointer.SlotIndex >= 0);
            var committedCanonical = alreadyCommitted.FirstOrDefault(pointer => pointer.SlotIndex == -1);
            AssetCatalogManifest finalManifest;
            int slotIndex;
            if (committedSlot is not null)
            {
                finalManifest = committedSlot.Catalog!.Manifest;
                slotIndex = committedSlot.SlotIndex;
                if (committedCanonical is null)
                {
                    PublishCanonical(finalManifest, candidate.SnapshotNamespace(finalManifest.CatalogRevision));
                }
            }
            else
            {
                var advice = new AssetCatalogPublicationAdvisor(catalogRoot, timeProvider)
                    .InspectForPublication(timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                if (!advice.Success)
                {
                    return PublishFailure(request, responsePath, advice.ErrorCode ?? "catalog_pointer_conflict", requestPath);
                }
                slotIndex = advice.SlotIndex!.Value;
                var revision = advice.CatalogRevision!.Value;
                finalManifest = candidate.Manifest with { CatalogRevision = revision };
                finalManifest = finalManifest with { CatalogFingerprint = AssetCatalogJson.ComputeCatalogFingerprint(finalManifest) };
                PublishSnapshot(candidate, finalManifest);
                PublishSlot(finalManifest, candidate.SnapshotNamespace(revision), slotIndex);
                faultBoundary?.Invoke("after-slot");
                PublishCanonical(finalManifest, candidate.SnapshotNamespace(revision));
                faultBoundary?.Invoke("after-canonical");
            }

            var response = new CommitResponse(
                "1.0",
                request.RequestId,
                request.RequestContentHash,
                request.CandidateFingerprint,
                true,
                finalManifest.CatalogRevision,
                finalManifest.CatalogFingerprint,
                slotIndex,
                StateToken(finalManifest, slotIndex),
                null);
            if (fileSystem.EntryExists(responsePath)) fileSystem.QuarantineOrdinaryOrDeleteLink(responsePath, quarantineRoot);
            fileSystem.WriteImmutable(responsePath, response, AssetCatalogJson.SerializerOptions);
            fileSystem.DeleteOrdinaryOrLink(requestPath);
            return true;
        }
        catch (Exception exception) when (AssetPackNormalizationService.IsPrivateDataFailure(exception) || exception is AbandonedMutexException)
        {
            return false;
        }
        finally
        {
            if (entered) mutex.ReleaseMutex();
        }
    }

    private Candidate ReadCandidate(CommitRequest request)
    {
        var candidateRoot = fileSystem.EnsureDirectory("private", "catalog-commit", "candidates", request.CandidateFingerprint);
        var manifestBytes = fileSystem.ReadBounded(Path.Combine(candidateRoot, "manifest.json"), AssetCatalogJson.MaximumJsonBytes);
        if (Hash(manifestBytes) != request.CandidateFingerprint) throw new InvalidDataException("Candidate fingerprint mismatch.");
        var manifest = AssetCatalogJson.DeserializeManifest(Encoding.UTF8.GetString(manifestBytes));
        if (manifest.SessionId != request.SessionId || manifest.SnapshotAt != request.SnapshotAt || manifest.CatalogRevision != 0)
            throw new InvalidDataException("Candidate identity mismatch.");
        var references = new HashSet<string>(StringComparer.Ordinal);
        var counts = AssetCategory.All.ToDictionary(category => category, _ => 0, StringComparer.Ordinal);
        var chunks = new List<CandidateChunk>();
        foreach (var receipt in manifest.Chunks)
        {
            if (Path.GetFileName(receipt.FileName) != receipt.FileName) throw new InvalidDataException("Candidate chunk name is invalid.");
            var bytes = fileSystem.ReadBounded(Path.Combine(candidateRoot, receipt.FileName), AssetCatalogJson.MaximumJsonBytes);
            if (bytes.LongLength != receipt.ByteCount || Hash(bytes) != receipt.Sha256) throw new InvalidDataException("Candidate chunk receipt mismatch.");
            var entries = AssetCatalogJson.DeserializeChunk(Encoding.UTF8.GetString(bytes));
            if (entries.Count != receipt.EntryCount) throw new InvalidDataException("Candidate chunk count mismatch.");
            foreach (var entry in entries)
            {
                if (!references.Add(entry.AssetRef)) throw new InvalidDataException("Candidate contains a duplicate asset reference.");
                counts[entry.Category]++;
            }
            chunks.Add(new CandidateChunk(receipt.FileName, bytes));
        }
        if (AssetCategory.All.Any(category => counts[category] != manifest.CategoryCounts[category]))
            throw new InvalidDataException("Candidate category totals mismatch.");
        return new Candidate(manifest, chunks, request.CandidateFingerprint);
    }

    private void ValidateRequest(CommitRequest request, string path)
    {
        if (request.SchemaVersion != "1.0" || !IsHash(request.RequestId) || !IsHash(request.RequestContentHash) ||
            !IsHash(request.CandidateFingerprint) || request.RequestId != request.RequestContentHash ||
            Path.GetFileNameWithoutExtension(path) != request.RequestId ||
            request.RequestContentHash != Hash(Encoding.UTF8.GetBytes(RequestContent(request))))
            throw new InvalidDataException("Catalog commit request binding is invalid.");
    }

    private void PublishSnapshot(Candidate candidate, AssetCatalogManifest manifest)
    {
        var root = fileSystem.EnsureDirectory("catalog", "snapshots", candidate.SnapshotNamespace(manifest.CatalogRevision));
        foreach (var chunk in candidate.Chunks) fileSystem.WriteImmutableBytes(Path.Combine(root, chunk.FileName), chunk.Bytes);
        fileSystem.WriteImmutableBytes(Path.Combine(root, "manifest.json"), Encoding.UTF8.GetBytes(AssetCatalogJson.SerializeManifest(manifest)));
    }

    private void PublishSlot(AssetCatalogManifest manifest, string snapshotNamespace, int slotIndex)
    {
        var pointer = JsonSerializer.SerializeToUtf8Bytes(new
        {
            session_id = manifest.SessionId,
            manifest = snapshotNamespace + "/manifest.json",
            catalog_revision = manifest.CatalogRevision,
        });
        fileSystem.ReplaceBytes(Path.Combine(catalogRoot, $"current-slot-{slotIndex}.json"), pointer);
    }

    private void PublishCanonical(AssetCatalogManifest manifest, string snapshotNamespace)
    {
        var pointer = JsonSerializer.SerializeToUtf8Bytes(new
        {
            session_id = manifest.SessionId,
            manifest = snapshotNamespace + "/manifest.json",
        });
        fileSystem.ReplaceBytes(Path.Combine(catalogRoot, "current.json"), pointer);
    }

    private bool PublishFailure(CommitRequest request, string responsePath, string errorCode, string requestPath)
    {
        var response = new CommitResponse("1.0", request.RequestId, request.RequestContentHash, request.CandidateFingerprint,
            false, null, null, null, null, errorCode);
        fileSystem.WriteImmutable(responsePath, response, AssetCatalogJson.SerializerOptions);
        fileSystem.DeleteOrdinaryOrLink(requestPath);
        return true;
    }

    private CommitResponse ReadResponse(string path) => JsonSerializer.Deserialize<CommitResponse>(
        fileSystem.ReadBounded(path, MaximumRequestBytes), AssetCatalogJson.SerializerOptions)
        ?? throw new JsonException("Catalog commit response cannot be null.");

    private bool IsCommitted(CommitResponse response)
    {
        if (!response.Success || response.CatalogRevision is null || response.CatalogFingerprint is null || response.SlotIndex is null || response.StateToken is null) return false;
        var repository = new AssetCatalogRepository(catalogRoot, timeProvider);
        return repository.TryRefresh() && repository.GetCurrent() is { } current &&
               current.Manifest.CatalogRevision == response.CatalogRevision && current.Manifest.CatalogFingerprint == response.CatalogFingerprint &&
               response.StateToken == StateToken(current.Manifest, response.SlotIndex.Value);
    }

    private static bool ResponseMatchesRequest(CommitResponse response, CommitRequest request) =>
        response.SchemaVersion == "1.0" && response.RequestId == request.RequestId &&
        response.RequestContentHash == request.RequestContentHash && response.CandidateFingerprint == request.CandidateFingerprint;

    private bool Quarantine(string path)
    {
        try { fileSystem.QuarantineOrdinaryOrDeleteLink(path, quarantineRoot); return true; }
        catch (Exception exception) when (AssetPackNormalizationService.IsPrivateDataFailure(exception)) { return false; }
    }

    private static string RequestContent(CommitRequest request) =>
        Frame("schema_version", request.SchemaVersion) + Frame("session_id", request.SessionId) +
        Frame("snapshot_at", request.SnapshotAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) +
        Frame("candidate_fingerprint", request.CandidateFingerprint);

    private static string Frame(string name, string value) => name + "=" + Encoding.UTF8.GetByteCount(value) + ":" + value + "\n";
    private static bool CandidateMatches(Candidate candidate, AssetCatalogManifest committed)
    {
        var expected = candidate.Manifest with { CatalogRevision = committed.CatalogRevision };
        expected = expected with { CatalogFingerprint = AssetCatalogJson.ComputeCatalogFingerprint(expected) };
        return string.Equals(expected.CatalogFingerprint, committed.CatalogFingerprint, StringComparison.Ordinal);
    }
    private static string StateToken(AssetCatalogManifest manifest, int slotIndex) => Hash(Encoding.UTF8.GetBytes(
        manifest.CatalogRevision + "\n" + manifest.CatalogFingerprint + "\n" + slotIndex));
    private static string MutexName(string catalogRoot) => "Local\\DDAI.AssetCatalog." + Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(catalogRoot).ToLowerInvariant()));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool IsHash(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record CommitRequest(string SchemaVersion, string RequestId, string RequestContentHash, string CandidateFingerprint, string SessionId, DateTimeOffset SnapshotAt);
    private sealed record CommitResponse(string SchemaVersion, string RequestId, string RequestContentHash, string CandidateFingerprint, bool Success,
        long? CatalogRevision, string? CatalogFingerprint, int? SlotIndex, string? StateToken, string? ErrorCode);
    private sealed record Candidate(AssetCatalogManifest Manifest, IReadOnlyList<CandidateChunk> Chunks, string Fingerprint)
    {
        public string SnapshotNamespace(long revision) => Manifest.SessionId + "-" + revision + "-" + Fingerprint;
    }
    private sealed record CandidateChunk(string FileName, byte[] Bytes);
}
