using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DDAI.Core.Assets;
using Microsoft.Win32.SafeHandles;

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
            request = ReadRequest(requestPath);
        }
        catch (Exception exception) when (AssetPackNormalizationService.IsPrivateDataFailure(exception))
        {
            return Quarantine(requestPath);
        }

        var initialRequestHash = request.RequestContentHash;
        var candidateRoot = Path.Combine(candidatesRoot, request.CandidateFingerprint);
        IDisposable candidateLease;
        try
        {
            candidateLease = fileSystem.AcquireDirectoryLease(candidateRoot);
        }
        catch (Exception exception) when (AssetPackNormalizationService.IsPrivateDataFailure(exception))
        {
            return Quarantine(requestPath);
        }
        using var heldCandidateLease = candidateLease;
        try
        {
            request = ReadRequest(requestPath);
            if (!string.Equals(request.RequestContentHash, initialRequestHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Catalog commit request bytes changed before candidate validation.");
            }
            candidate = ReadCandidate(request, candidateRoot);
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
                if (ResponseMatchesRequest(existing, request) && IsCommitted(existing, request, candidate))
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

        CatalogMutex mutex;
        try
        {
            mutex = CreateCatalogMutex(catalogRoot);
        }
        catch (Exception exception) when (AssetPackNormalizationService.IsPrivateDataFailure(exception))
        {
            // A pre-existing global object owned by another principal must fail closed. Never
            // fall back to a session-local mutex, which would permit concurrent publishers.
            return false;
        }
        using var heldMutex = mutex;
        var entered = false;
        try
        {
            try { entered = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { entered = true; }
            if (!entered) return false;

            // Re-read every bound input after serialization.
            var serializedRequestHash = request.RequestContentHash;
            request = ReadRequest(requestPath);
            if (!string.Equals(request.RequestContentHash, serializedRequestHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Catalog commit request bytes changed during processing.");
            }
            candidate = ReadCandidate(request, candidateRoot);

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

            var manifestPath = candidate.SnapshotNamespace(finalManifest.CatalogRevision) + "/manifest.json";
            var response = new CommitResponse(
                "1.0",
                request.RequestId,
                request.RequestContentHash,
                request.CandidateFingerprint,
                request.SessionId,
                true,
                manifestPath,
                finalManifest.CatalogRevision,
                finalManifest.CatalogFingerprint,
                slotIndex,
                StateToken(request, manifestPath, finalManifest, slotIndex),
                null);
            if (fileSystem.EntryExists(responsePath)) fileSystem.QuarantineOrdinaryOrDeleteLink(responsePath, quarantineRoot);
            fileSystem.WriteImmutable(responsePath, response, AssetCatalogJson.SerializerOptions);
            faultBoundary?.Invoke("after-response");
            fileSystem.DeleteOrdinaryOrLink(requestPath);
            return true;
        }
        catch (Exception exception) when (AssetPackNormalizationService.IsPrivateDataFailure(exception) || exception is AbandonedMutexException)
        {
            return false;
        }
        finally
        {
            if (entered) mutex.Release();
        }
    }

    private Candidate ReadCandidate(CommitRequest request, string candidateRoot)
    {
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

    private CommitRequest ReadRequest(string path)
    {
        var bytes = fileSystem.ReadBounded(path, MaximumRequestBytes);
        var requestHash = Hash(bytes);
        if (!string.Equals(Path.GetFileNameWithoutExtension(path), requestHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Catalog commit request bytes do not match the immutable request path.");
        }

        using var document = JsonDocument.Parse(bytes, StrictDocumentOptions);
        var root = document.RootElement;
        RequireExactObjectShape(root, ["schema_version", "candidate_fingerprint", "session_id", "snapshot_at"]);
        var schemaVersion = RequiredString(root, "schema_version");
        var candidateFingerprint = RequiredString(root, "candidate_fingerprint");
        var sessionId = RequiredString(root, "session_id");
        var snapshotElement = root.GetProperty("snapshot_at");
        if (snapshotElement.ValueKind != JsonValueKind.String ||
            !snapshotElement.TryGetDateTimeOffset(out var snapshotAt) ||
            snapshotAt == DateTimeOffset.MinValue || snapshotAt.Offset != TimeSpan.Zero ||
            schemaVersion != "1.0" || !IsHash(candidateFingerprint) || !IsSafeSegment(sessionId))
        {
            throw new InvalidDataException("Catalog commit request binding is invalid.");
        }

        return new CommitRequest(schemaVersion, requestHash, requestHash, candidateFingerprint, sessionId, snapshotAt);
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
            request.SessionId, false, null, null, null, null, null, errorCode);
        fileSystem.WriteImmutable(responsePath, response, AssetCatalogJson.SerializerOptions);
        fileSystem.DeleteOrdinaryOrLink(requestPath);
        return true;
    }

    private CommitResponse ReadResponse(string path)
    {
        using var document = JsonDocument.Parse(fileSystem.ReadBounded(path, MaximumRequestBytes), StrictDocumentOptions);
        var root = document.RootElement;
        RequireExactObjectShape(
            root,
            [
                "schema_version", "request_id", "request_content_hash", "candidate_fingerprint", "session_id",
                "success", "manifest_path", "catalog_revision", "catalog_fingerprint", "slot_index", "state_token", "error_code",
            ]);
        var successElement = root.GetProperty("success");
        if (successElement.ValueKind != JsonValueKind.True && successElement.ValueKind != JsonValueKind.False)
        {
            throw new JsonException("Catalog commit response success is malformed.");
        }

        var response = new CommitResponse(
            RequiredString(root, "schema_version"),
            RequiredString(root, "request_id"),
            RequiredString(root, "request_content_hash"),
            RequiredString(root, "candidate_fingerprint"),
            RequiredString(root, "session_id"),
            successElement.GetBoolean(),
            OptionalString(root, "manifest_path"),
            OptionalInt64(root, "catalog_revision"),
            OptionalString(root, "catalog_fingerprint"),
            OptionalInt32(root, "slot_index"),
            OptionalString(root, "state_token"),
            OptionalString(root, "error_code"));
        if (response.SchemaVersion != "1.0" || !IsHash(response.RequestId) || !IsHash(response.RequestContentHash) ||
            !IsHash(response.CandidateFingerprint) || string.IsNullOrWhiteSpace(response.SessionId) ||
            (response.CatalogFingerprint is not null && !IsHash(response.CatalogFingerprint)) ||
            (response.StateToken is not null && !IsHash(response.StateToken)))
        {
            throw new JsonException("Catalog commit response binding is malformed.");
        }

        return response;
    }

    private bool IsCommitted(CommitResponse response, CommitRequest request, Candidate candidate)
    {
        if (!response.Success || response.ManifestPath is null || response.CatalogRevision is null ||
            response.CatalogFingerprint is null || response.SlotIndex is null or < 0 or > 1 || response.StateToken is null)
        {
            return false;
        }

        var expectedPath = candidate.SnapshotNamespace(response.CatalogRevision.Value) + "/manifest.json";
        if (!string.Equals(response.ManifestPath, expectedPath, StringComparison.Ordinal)) return false;
        var repository = new AssetCatalogRepository(catalogRoot, timeProvider);
        var committed = repository.ReadImmutableSnapshot(response.ManifestPath);
        return committed.Manifest.SessionId == request.SessionId &&
               committed.Manifest.CatalogRevision == response.CatalogRevision &&
               committed.Manifest.CatalogFingerprint == response.CatalogFingerprint &&
               CandidateMatches(candidate, committed.Manifest) &&
               response.StateToken == StateToken(request, response.ManifestPath, committed.Manifest, response.SlotIndex.Value);
    }

    private static bool ResponseMatchesRequest(CommitResponse response, CommitRequest request) =>
        response.SchemaVersion == "1.0" && response.RequestId == request.RequestId &&
        response.RequestContentHash == request.RequestContentHash && response.CandidateFingerprint == request.CandidateFingerprint &&
        response.SessionId == request.SessionId;

    private bool Quarantine(string path)
    {
        try { fileSystem.QuarantineOrdinaryOrDeleteLink(path, quarantineRoot); return true; }
        catch (Exception exception) when (AssetPackNormalizationService.IsPrivateDataFailure(exception)) { return false; }
    }

    private static readonly JsonDocumentOptions StrictDocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

    private static void RequireExactObjectShape(JsonElement root, IReadOnlyList<string> expectedNames)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Catalog commit private data must be an object.");
        }

        var expected = new HashSet<string>(expectedNames, StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !observed.Add(property.Name))
            {
                throw new JsonException("Catalog commit private data has an unsupported or duplicate property.");
            }
        }

        if (observed.Count != expected.Count)
        {
            throw new JsonException("Catalog commit private data is missing a required property.");
        }
    }

    private static string RequiredString(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            ? text
            : throw new JsonException($"Catalog commit property '{name}' must be a string.");
    }

    private static string? OptionalString(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new JsonException($"Catalog commit property '{name}' must be a string or null."),
        };
    }

    private static long? OptionalInt64(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result) || result < 0)
        {
            throw new JsonException($"Catalog commit property '{name}' must be a non-negative integer or null.");
        }
        return result;
    }

    private static int? OptionalInt32(JsonElement root, string name)
    {
        var value = OptionalInt64(root, name);
        if (value is null) return null;
        if (value > int.MaxValue) throw new JsonException($"Catalog commit property '{name}' is too large.");
        return (int)value.Value;
    }

    private static bool CandidateMatches(Candidate candidate, AssetCatalogManifest committed)
    {
        var expected = candidate.Manifest with { CatalogRevision = committed.CatalogRevision };
        expected = expected with { CatalogFingerprint = AssetCatalogJson.ComputeCatalogFingerprint(expected) };
        return string.Equals(expected.CatalogFingerprint, committed.CatalogFingerprint, StringComparison.Ordinal);
    }
    private static string StateToken(CommitRequest request, string manifestPath, AssetCatalogManifest manifest, int slotIndex) =>
        Hash(Encoding.UTF8.GetBytes(
            request.RequestContentHash + "\n" + request.CandidateFingerprint + "\n" + request.SessionId + "\n" +
            manifestPath + "\n" + manifest.CatalogRevision.ToString(CultureInfo.InvariantCulture) + "\n" +
            manifest.CatalogFingerprint + "\n" + slotIndex.ToString(CultureInfo.InvariantCulture)));
    private static string MutexName(string catalogRoot)
    {
        var userHash = Hash(Encoding.UTF8.GetBytes(CurrentUserSid()));
        var rootHash = Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(catalogRoot).ToLowerInvariant()));
        return "Global\\DDAI.AssetCatalog." + userHash + "." + rootHash;
    }

    private static CatalogMutex CreateCatalogMutex(string catalogRoot)
    {
        var sid = CurrentUserSid();
        var securityDescriptor = IntPtr.Zero;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                "D:P(A;;GA;;;SY)(A;;GA;;;" + sid + ")",
                SecurityDescriptorRevision,
                out securityDescriptor,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = securityDescriptor,
                InheritHandle = false,
            };
            var handle = CreateMutexEx(
                ref attributes,
                MutexName(catalogRoot),
                0,
                Synchronize | MutexModifyState);
            if (!handle.IsInvalid) return new CatalogMutex(handle);

            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            var nativeFailure = new Win32Exception(error);
            if (error == ErrorAccessDenied)
            {
                throw new UnauthorizedAccessException("The global catalog mutex is owned by another principal.", nativeFailure);
            }
            throw nativeFailure;
        }
        finally
        {
            _ = LocalFree(securityDescriptor);
        }
    }

    private static string CurrentUserSid() => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new UnauthorizedAccessException("The current Windows user has no security identifier.");

    private const uint SecurityDescriptorRevision = 1;
    private const uint MutexModifyState = 0x0001;
    private const uint Synchronize = 0x00100000;
    private const int ErrorAccessDenied = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    private sealed class CatalogMutex : WaitHandle
    {
        private readonly SafeWaitHandle ownedHandle;

        public CatalogMutex(SafeWaitHandle handle)
        {
            SafeWaitHandle = handle;
            ownedHandle = handle;
        }

        public void Release()
        {
            if (!ReleaseMutexNative(ownedHandle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSecurityDescriptorRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateMutexEx(
        ref SecurityAttributes mutexAttributes,
        string name,
        uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll", EntryPoint = "ReleaseMutex", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseMutexNative(SafeWaitHandle mutex);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool IsHash(string? value) => value is not null && value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsSafeSegment(string value) => value.Length is > 0 and <= 128 && value is not ("." or "..") &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_');

    private sealed record CommitRequest(
        string SchemaVersion,
        string RequestId,
        string RequestContentHash,
        string CandidateFingerprint,
        string SessionId,
        DateTimeOffset SnapshotAt);
    private sealed record CommitResponse(
        string SchemaVersion,
        string RequestId,
        string RequestContentHash,
        string CandidateFingerprint,
        string SessionId,
        bool Success,
        string? ManifestPath,
        long? CatalogRevision,
        string? CatalogFingerprint,
        int? SlotIndex,
        string? StateToken,
        string? ErrorCode);
    private sealed record Candidate(AssetCatalogManifest Manifest, IReadOnlyList<CandidateChunk> Chunks, string Fingerprint)
    {
        public string SnapshotNamespace(long revision) => Manifest.SessionId + "-" + revision + "-" + Fingerprint;
    }
    private sealed record CandidateChunk(string FileName, byte[] Bytes);
}
