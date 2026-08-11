using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DDAI.Core.MapPlans;

namespace DDAI.Core.Mailbox;

public enum MapJobSubmissionResult
{
    Accepted,
    AlreadyAccepted,
    Conflict,
    Blocked,
}

public enum MapJobTransition
{
    NoWork,
    Prepared,
    NativeOperationCalled,
    OperationApplied,
    NativeOperationObserved,
    OperationObserved,
    Reversing,
    NativeReversalCalled,
    ReversalProgressed,
    Reversed,
    OutcomeUnknown,
    Committed,
    ResponsePublished,
    CompletionRecorded,
    ClaimDeleted,
    JournalDeleted,
    Blocked,
}

public interface IUniversalMapJobRuntime
{
    IReadOnlyList<long> ApplyOperation(MapJobSubmission submission, int operationIndex);

    bool ObserveOperation(
        MapJobSubmission submission,
        int operationIndex,
        IReadOnlyList<long> nativeNodeIds);

    bool ReverseOperation(
        MapJobSubmission submission,
        int operationIndex,
        IReadOnlyList<long> nativeNodeIds);
}

/// <summary>
/// File-backed executable reference model for universal map jobs. Every
/// Advance call crosses at most one native or durable boundary. Volatile
/// receipts deliberately are not recoverable: losing one after a native call
/// converts the job to outcome_unknown instead of replaying the call.
/// </summary>
public sealed class UniversalMapJobStateMachine : IDisposable
{
    public const string CurrentSchemaVersion = "1.0";
    private const long MaximumMessageBytes = AtomicMailbox.MaximumMessageBytes;
    private const int MaximumNativeNodeIds = 10_000;
    private static readonly ConcurrentDictionary<string, object> SubmissionLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, WeakReference<UniversalMapJobStateMachine>> ActiveMachines = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly string _root;
    private readonly IUniversalMapJobRuntime _runtime;
    private readonly object _advanceGate = new();
    private readonly HashSet<string> _newlyPrepared = new(StringComparer.Ordinal);
    private readonly HashSet<string> _newlyReversing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingApplication> _pendingApplications = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _pendingObservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _pendingReversals = new(StringComparer.Ordinal);
    private bool _disposed;

    public UniversalMapJobStateMachine(string rootDirectory, IUniversalMapJobRuntime runtime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(runtime);
        _root = Path.GetFullPath(rootDirectory);
        _runtime = runtime;
        foreach (var directory in new[] { "processing", "journal", "responses", "completed" })
        {
            Directory.CreateDirectory(Path.Combine(_root, directory));
        }
    }

    public static string RequestKey(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId))).ToLowerInvariant();
    }

    public MapJobSubmissionResult Submit(MapJobSubmission submission)
    {
        ValidateSubmission(submission);
        var text = JsonSerializer.Serialize(submission, JsonOptions);
        if (!SubmissionFitsDurableFiles(submission, text))
        {
            throw new ArgumentOutOfRangeException(nameof(submission), "The durable map job claim must not exceed 1 MiB.");
        }

        var key = RequestKey(submission.RequestId);
        var gate = SubmissionLocks.GetOrAdd(_root, static _ => new object());
        lock (gate)
        {
            var completed = TryRead<CompletionReceipt>(PathFor("completed", key), out var receipt);
            if (completed == ReadResult.Valid)
            {
                var responsePath = PathFor("responses", key);
                if (!TryReadRaw(responsePath, out var response) ||
                    !StringComparer.Ordinal.Equals(receipt!.ResponseFingerprint, Sha256(response!)))
                {
                    return MapJobSubmissionResult.Blocked;
                }

                return IdentityMatches(receipt, submission)
                    ? MapJobSubmissionResult.AlreadyAccepted
                    : MapJobSubmissionResult.Conflict;
            }

            if (completed != ReadResult.Missing)
            {
                return MapJobSubmissionResult.Blocked;
            }

            var claimPath = PathFor("processing", key);
            var claimRead = TryRead<MapJobSubmission>(claimPath, out var existing);
            if (claimRead == ReadResult.Valid)
            {
                return IdentityMatches(existing!, submission)
                    ? MapJobSubmissionResult.AlreadyAccepted
                    : MapJobSubmissionResult.Conflict;
            }

            if (claimRead != ReadResult.Missing)
            {
                return MapJobSubmissionResult.Blocked;
            }

            return TryWriteAtomically(claimPath, text)
                ? MapJobSubmissionResult.Accepted
                : ReconcileConcurrentSubmission(claimPath, submission);
        }
    }

    public MapJobTransition Advance(string requestId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var rootGate = SubmissionLocks.GetOrAdd(_root, static _ => new object());
        lock (rootGate)
        {
            lock (_advanceGate)
            {
                if (ActiveMachines.TryGetValue(_root, out var owner) &&
                    owner.TryGetTarget(out var active) &&
                    !ReferenceEquals(active, this))
                {
                    return MapJobTransition.Blocked;
                }

                ActiveMachines[_root] = new WeakReference<UniversalMapJobStateMachine>(this);
                return AdvanceCore(requestId);
            }
        }
    }

    public void Dispose()
    {
        var rootGate = SubmissionLocks.GetOrAdd(_root, static _ => new object());
        lock (rootGate)
        {
            _disposed = true;
            if (ActiveMachines.TryGetValue(_root, out var owner) &&
                owner.TryGetTarget(out var active) &&
                ReferenceEquals(active, this))
            {
                ActiveMachines.TryRemove(_root, out _);
            }
        }
    }

    private MapJobTransition AdvanceCore(string requestId)
    {
        var key = RequestKey(requestId);
        var claimResult = TryRead<MapJobSubmission>(PathFor("processing", key), out var submission);
        var journalResult = TryRead<MapJobJournal>(PathFor("journal", key), out var journal);

        if (claimResult is ReadResult.Invalid or ReadResult.Blocked ||
            journalResult is ReadResult.Invalid or ReadResult.Blocked)
        {
            return MapJobTransition.Blocked;
        }

        if (submission is not null && !SubmissionIsValid(submission))
        {
            return MapJobTransition.Blocked;
        }

        if (claimResult == ReadResult.Missing && journalResult == ReadResult.Missing)
        {
            return MapJobTransition.NoWork;
        }

        if (journalResult == ReadResult.Missing)
        {
            if (submission is null || !StringComparer.Ordinal.Equals(submission.RequestId, requestId))
            {
                return MapJobTransition.Blocked;
            }

            var prepared = CreatePreparedJournal(submission);
            if (!TryWriteAtomically(PathFor("journal", key), SerializeBounded(prepared)))
            {
                return MapJobTransition.Blocked;
            }

            _newlyPrepared.Add(key);
            return MapJobTransition.Prepared;
        }

        if (journal is null || !JournalIsValid(journal, requestId) ||
            (submission is not null && !IdentityMatches(journal, submission)))
        {
            return MapJobTransition.Blocked;
        }

        if (claimResult == ReadResult.Missing && journal.State != MapJobState.Committed)
        {
            return MapJobTransition.Blocked;
        }

        submission ??= SubmissionFrom(journal);
        return journal.State switch
        {
            MapJobState.Prepared => AdvancePrepared(key, submission, journal),
            MapJobState.OperationApplied => AdvanceApplied(key, submission, journal),
            MapJobState.OperationObserved => AdvanceObserved(key, journal),
            MapJobState.Reversing => AdvanceReversing(key, submission, journal),
            MapJobState.Reversed => WriteJournalTransition(key, journal with
            {
                State = MapJobState.Committed,
                CanonicalResponse = journal.CanonicalReversedResponse,
            }, MapJobTransition.Committed),
            MapJobState.OutcomeUnknown => WriteJournalTransition(key, journal with
            {
                State = MapJobState.Committed,
                CanonicalResponse = UnknownOutcomeResponse(journal),
            }, MapJobTransition.Committed),
            MapJobState.Committed => AdvanceCommitted(key, journal, claimResult),
            _ => MapJobTransition.Blocked,
        };
    }

    public MapJobJournal? ReadJournal(string requestId)
    {
        var result = TryRead<MapJobJournal>(PathFor("journal", RequestKey(requestId)), out var journal);
        return result == ReadResult.Valid && JournalIsValid(journal!, requestId) ? journal : null;
    }

    private MapJobTransition AdvancePrepared(string key, MapJobSubmission submission, MapJobJournal journal)
    {
        if (_pendingApplications.Remove(key, out var pending))
        {
            if (pending.OperationIndex != journal.NextOperationIndex || pending.NativeNodeIds is null)
            {
                return WriteJournalTransition(key, journal with
                {
                    State = MapJobState.OutcomeUnknown,
                    CanonicalResponse = null,
                }, MapJobTransition.OutcomeUnknown);
            }

            return WriteJournalTransition(key, journal with
            {
                State = MapJobState.OperationApplied,
                CurrentOperationNodeIds = pending.NativeNodeIds,
            }, MapJobTransition.OperationApplied);
        }

        if (!_newlyPrepared.Remove(key))
        {
            return WriteJournalTransition(key, journal with
            {
                State = MapJobState.OutcomeUnknown,
                CanonicalResponse = null,
            }, MapJobTransition.OutcomeUnknown);
        }

        try
        {
            var nodes = _runtime.ApplyOperation(submission, journal.NextOperationIndex)?.ToArray();
            if (nodes is null ||
                nodes.Length > MaximumNativeNodeIds ||
                journal.ObservedNativeNodeIds.Count + nodes.Length > MaximumNativeNodeIds ||
                nodes.Any(nodeId => nodeId <= 0) ||
                nodes.Length != nodes.Distinct().Count() ||
                journal.ObservedNativeNodeIds.Concat(nodes).Distinct().Count() != journal.ObservedNativeNodeIds.Count + nodes.Length)
            {
                _pendingApplications[key] = new PendingApplication(journal.NextOperationIndex, null);
                return MapJobTransition.NativeOperationCalled;
            }

            _pendingApplications[key] = new PendingApplication(journal.NextOperationIndex, nodes);
            return MapJobTransition.NativeOperationCalled;
        }
        catch
        {
            _pendingApplications[key] = new PendingApplication(journal.NextOperationIndex, null);
            return MapJobTransition.NativeOperationCalled;
        }
    }

    private MapJobTransition AdvanceApplied(string key, MapJobSubmission submission, MapJobJournal journal)
    {
        if (_pendingObservations.Remove(key, out var observed))
        {
            if (!observed)
            {
                var result = WriteJournalTransition(key, journal with
                {
                    State = MapJobState.Reversing,
                    ReversalOperationIndex = journal.NextOperationIndex,
                }, MapJobTransition.Reversing);
                if (result == MapJobTransition.Reversing)
                {
                    _newlyReversing.Add(key);
                }

                return result;
            }

            var observation = new MapJobOperationObservation(
                journal.NextOperationIndex,
                journal.OperationIds[journal.NextOperationIndex],
                journal.CurrentOperationNodeIds);
            var observations = journal.OperationObservations.Append(observation).ToArray();
            var allNodes = journal.ObservedNativeNodeIds.Concat(journal.CurrentOperationNodeIds).ToArray();
            return WriteJournalTransition(key, journal with
            {
                State = MapJobState.OperationObserved,
                NextOperationIndex = journal.NextOperationIndex + 1,
                ObservedNativeNodeIds = allNodes,
                OperationObservations = observations,
                CurrentOperationNodeIds = [],
            }, MapJobTransition.OperationObserved);
        }

        try
        {
            _pendingObservations[key] = _runtime.ObserveOperation(
                submission,
                journal.NextOperationIndex,
                journal.CurrentOperationNodeIds);
            return MapJobTransition.NativeOperationObserved;
        }
        catch
        {
            _pendingObservations[key] = false;
            return MapJobTransition.NativeOperationObserved;
        }
    }

    private MapJobTransition AdvanceObserved(string key, MapJobJournal journal)
    {
        if (journal.NextOperationIndex < journal.OperationIds.Count)
        {
            var result = WriteJournalTransition(key, journal with { State = MapJobState.Prepared }, MapJobTransition.Prepared);
            if (result == MapJobTransition.Prepared)
            {
                _newlyPrepared.Add(key);
            }

            return result;
        }

        return WriteJournalTransition(key, journal with
        {
            State = MapJobState.Committed,
            CanonicalResponse = journal.CanonicalSuccessResponse,
        }, MapJobTransition.Committed);
    }

    private MapJobTransition AdvanceReversing(string key, MapJobSubmission submission, MapJobJournal journal)
    {
        if (_pendingReversals.Remove(key, out var reversed))
        {
            if (!reversed)
            {
                return WriteJournalTransition(key, journal with { State = MapJobState.OutcomeUnknown }, MapJobTransition.OutcomeUnknown);
            }

            var reversedIndex = journal.ReversalOperationIndex!.Value;
            if (reversedIndex == 0)
            {
                return WriteJournalTransition(key, journal with
                {
                    State = MapJobState.Reversed,
                    CurrentOperationNodeIds = [],
                    ReversalOperationIndex = null,
                }, MapJobTransition.Reversed);
            }

            var previousIndex = reversedIndex - 1;
            var previous = journal.OperationObservations.SingleOrDefault(item => item.OperationIndex == previousIndex);
            if (previous is null)
            {
                return MapJobTransition.Blocked;
            }

            var result = WriteJournalTransition(key, journal with
            {
                CurrentOperationNodeIds = previous.NativeNodeIds,
                ReversalOperationIndex = previousIndex,
            }, MapJobTransition.ReversalProgressed);
            if (result == MapJobTransition.ReversalProgressed)
            {
                _newlyReversing.Add(key);
            }

            return result;
        }

        if (!_newlyReversing.Remove(key))
        {
            return WriteJournalTransition(key, journal with { State = MapJobState.OutcomeUnknown }, MapJobTransition.OutcomeUnknown);
        }

        try
        {
            _pendingReversals[key] = _runtime.ReverseOperation(
                submission,
                journal.ReversalOperationIndex!.Value,
                journal.CurrentOperationNodeIds);
            return MapJobTransition.NativeReversalCalled;
        }
        catch
        {
            _pendingReversals[key] = false;
            return MapJobTransition.NativeReversalCalled;
        }
    }

    private MapJobTransition AdvanceCommitted(string key, MapJobJournal journal, ReadResult claimResult)
    {
        if (journal.CanonicalResponse is null || Utf8Length(journal.CanonicalResponse) > MaximumMessageBytes)
        {
            return MapJobTransition.Blocked;
        }

        var responsePath = PathFor("responses", key);
        if (!File.Exists(responsePath))
        {
            return TryWriteRawAtomically(responsePath, journal.CanonicalResponse)
                ? MapJobTransition.ResponsePublished
                : MapJobTransition.Blocked;
        }

        if (!TryReadRaw(responsePath, out var response) || !StringComparer.Ordinal.Equals(response, journal.CanonicalResponse))
        {
            return MapJobTransition.Blocked;
        }

        var completionPath = PathFor("completed", key);
        if (!File.Exists(completionPath))
        {
            var receipt = new CompletionReceipt(
                CurrentSchemaVersion,
                journal.RequestId,
                journal.PlanFingerprint,
                journal.MapId,
                journal.StartingMapRevision,
                journal.CatalogRevision,
                journal.CatalogFingerprint,
                Sha256(journal.CanonicalResponse));
            return TryWriteAtomically(completionPath, SerializeBounded(receipt))
                ? MapJobTransition.CompletionRecorded
                : MapJobTransition.Blocked;
        }

        if (TryRead<CompletionReceipt>(completionPath, out var existing) != ReadResult.Valid ||
            !IdentityMatches(existing!, SubmissionFrom(journal)) ||
            !StringComparer.Ordinal.Equals(existing!.ResponseFingerprint, Sha256(journal.CanonicalResponse)))
        {
            return MapJobTransition.Blocked;
        }

        if (claimResult == ReadResult.Valid)
        {
            return TryDelete(PathFor("processing", key))
                ? MapJobTransition.ClaimDeleted
                : MapJobTransition.Blocked;
        }

        return TryDelete(PathFor("journal", key))
            ? MapJobTransition.JournalDeleted
            : MapJobTransition.Blocked;
    }

    private MapJobTransition WriteJournalTransition(string key, MapJobJournal journal, MapJobTransition transition)
    {
        try
        {
            return TryReplaceAtomically(PathFor("journal", key), SerializeBounded(journal))
                ? transition
                : MapJobTransition.Blocked;
        }
        catch (InvalidDataException)
        {
            return MapJobTransition.Blocked;
        }
    }

    private static MapJobJournal CreatePreparedJournal(MapJobSubmission submission) => new(
        CurrentSchemaVersion,
        submission.RequestId,
        submission.PlanFingerprint,
        submission.MapId,
        submission.StartingMapRevision,
        submission.CatalogRevision,
        submission.CatalogFingerprint,
        MapJobState.Prepared,
        0,
        [],
        [],
        [],
        null,
        null,
        submission.CanonicalSuccessResponse,
        submission.CanonicalReversedResponse,
        submission.OperationIds);

    private static MapJobSubmission SubmissionFrom(MapJobJournal journal) => new(
        journal.RequestId,
        journal.PlanFingerprint,
        journal.MapId,
        journal.StartingMapRevision,
        journal.CatalogRevision,
        journal.CatalogFingerprint,
        journal.OperationIds,
        journal.CanonicalSuccessResponse,
        journal.CanonicalReversedResponse);

    private MapJobSubmissionResult ReconcileConcurrentSubmission(string path, MapJobSubmission submission)
    {
        var read = TryRead<MapJobSubmission>(path, out var existing);
        return read == ReadResult.Valid && IdentityMatches(existing!, submission)
            ? MapJobSubmissionResult.AlreadyAccepted
            : read == ReadResult.Valid
                ? MapJobSubmissionResult.Conflict
                : MapJobSubmissionResult.Blocked;
    }

    private static bool JournalIsValid(MapJobJournal journal, string requestId) =>
        StringComparer.Ordinal.Equals(journal.SchemaVersion, CurrentSchemaVersion) &&
        StringComparer.Ordinal.Equals(journal.RequestId, requestId) &&
        IsSafeIdentifier(journal.RequestId) &&
        IsSafeIdentifier(journal.MapId) &&
        IsFingerprint(journal.PlanFingerprint) &&
        IsFingerprint(journal.CatalogFingerprint) &&
        journal.StartingMapRevision >= 0 &&
        journal.CatalogRevision >= 0 &&
        journal.OperationIds is { Count: > 0 and <= 500 } &&
        journal.NextOperationIndex >= 0 &&
        journal.NextOperationIndex <= journal.OperationIds.Count &&
        journal.ObservedNativeNodeIds is not null &&
        journal.OperationObservations is not null &&
        journal.CurrentOperationNodeIds is not null &&
        TryValidateCanonicalResponse(journal.CanonicalSuccessResponse) &&
        TryValidateCanonicalResponse(journal.CanonicalReversedResponse) &&
        (journal.CanonicalResponse is null || TryValidateCanonicalResponse(journal.CanonicalResponse)) &&
        JournalStateIsConsistent(journal);

    private static bool JournalStateIsConsistent(MapJobJournal journal)
    {
        var currentIsDisjointFromObserved =
            journal.ObservedNativeNodeIds.Concat(journal.CurrentOperationNodeIds).Distinct().Count() ==
            journal.ObservedNativeNodeIds.Count + journal.CurrentOperationNodeIds.Count;
        if (journal.OperationIds.Any(value => !IsSafeIdentifier(value)) ||
            journal.ObservedNativeNodeIds.Count + journal.CurrentOperationNodeIds.Count > MaximumNativeNodeIds ||
            journal.ObservedNativeNodeIds.Any(nodeId => nodeId <= 0) ||
            journal.CurrentOperationNodeIds.Any(nodeId => nodeId <= 0) ||
            journal.ObservedNativeNodeIds.Distinct().Count() != journal.ObservedNativeNodeIds.Count ||
            journal.OperationIds.Distinct(StringComparer.Ordinal).Count() != journal.OperationIds.Count ||
            journal.CurrentOperationNodeIds.Distinct().Count() != journal.CurrentOperationNodeIds.Count ||
            journal.OperationObservations.Count > journal.OperationIds.Count)
        {
            return false;
        }

        for (var index = 0; index < journal.OperationObservations.Count; index++)
        {
            var observation = journal.OperationObservations[index];
            if (observation is null ||
                observation.OperationIndex != index ||
                !StringComparer.Ordinal.Equals(observation.OperationId, journal.OperationIds[index]) ||
                observation.NativeNodeIds is null ||
                observation.NativeNodeIds.Any(nodeId => nodeId <= 0) ||
                observation.NativeNodeIds.Distinct().Count() != observation.NativeNodeIds.Count)
            {
                return false;
            }
        }

        if (!journal.ObservedNativeNodeIds.SequenceEqual(journal.OperationObservations.SelectMany(item => item.NativeNodeIds)))
        {
            return false;
        }

        return journal.State switch
        {
            MapJobState.Prepared =>
                journal.NextOperationIndex < journal.OperationIds.Count &&
                journal.OperationObservations.Count == journal.NextOperationIndex &&
                journal.CurrentOperationNodeIds.Count == 0 &&
                journal.ReversalOperationIndex is null &&
                journal.CanonicalResponse is null,
            MapJobState.OperationApplied =>
                journal.NextOperationIndex < journal.OperationIds.Count &&
                journal.OperationObservations.Count == journal.NextOperationIndex &&
                currentIsDisjointFromObserved &&
                journal.ReversalOperationIndex is null &&
                journal.CanonicalResponse is null,
            MapJobState.OperationObserved =>
                journal.NextOperationIndex > 0 &&
                journal.OperationObservations.Count == journal.NextOperationIndex &&
                journal.CurrentOperationNodeIds.Count == 0 &&
                journal.ReversalOperationIndex is null &&
                journal.CanonicalResponse is null,
            MapJobState.Reversing =>
                journal.NextOperationIndex < journal.OperationIds.Count &&
                journal.OperationObservations.Count == journal.NextOperationIndex &&
                journal.ReversalOperationIndex is >= 0 &&
                journal.ReversalOperationIndex <= journal.NextOperationIndex &&
                (journal.ReversalOperationIndex == journal.NextOperationIndex
                    ? currentIsDisjointFromObserved
                    : journal.CurrentOperationNodeIds.SequenceEqual(
                        journal.OperationObservations[journal.ReversalOperationIndex.Value].NativeNodeIds)) &&
                journal.CanonicalResponse is null,
            MapJobState.Reversed =>
                journal.CurrentOperationNodeIds.Count == 0 &&
                journal.ReversalOperationIndex is null &&
                journal.CanonicalResponse is null,
            MapJobState.OutcomeUnknown => journal.CanonicalResponse is null,
            MapJobState.Committed => CommittedResponseIsConsistent(journal),
            _ => false,
        };
    }

    private static bool CommittedResponseIsConsistent(MapJobJournal journal)
    {
        if (journal.CanonicalResponse is null)
        {
            return false;
        }

        var isSuccess = StringComparer.Ordinal.Equals(journal.CanonicalResponse, journal.CanonicalSuccessResponse) &&
            journal.NextOperationIndex == journal.OperationIds.Count &&
            journal.OperationObservations.Count == journal.OperationIds.Count &&
            journal.CurrentOperationNodeIds.Count == 0 &&
            journal.ReversalOperationIndex is null;
        var isReversed = StringComparer.Ordinal.Equals(journal.CanonicalResponse, journal.CanonicalReversedResponse) &&
            journal.CurrentOperationNodeIds.Count == 0 &&
            journal.ReversalOperationIndex is null;
        var isOutcomeUnknown = StringComparer.Ordinal.Equals(journal.CanonicalResponse, UnknownOutcomeResponse(journal));
        return isSuccess || isReversed || isOutcomeUnknown;
    }

    private static void ValidateSubmission(MapJobSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (!IsSafeIdentifier(submission.RequestId) || !IsSafeIdentifier(submission.MapId))
        {
            throw new ArgumentException("Request and map IDs must be safe bounded identifiers.", nameof(submission));
        }
        if (!IsFingerprint(submission.PlanFingerprint) || !IsFingerprint(submission.CatalogFingerprint))
        {
            throw new ArgumentException("Plan and catalog fingerprints must be lowercase SHA-256 values.", nameof(submission));
        }

        if (submission.StartingMapRevision < 0 || submission.CatalogRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(submission), "Revisions cannot be negative.");
        }

        if (submission.OperationIds is not { Count: > 0 and <= 500 } ||
            submission.OperationIds.Any(value => !IsSafeIdentifier(value)) ||
            submission.OperationIds.Distinct(StringComparer.Ordinal).Count() != submission.OperationIds.Count)
        {
            throw new ArgumentException("One to 500 unique operation IDs are required.", nameof(submission));
        }

        ValidateCanonicalResponse(submission.CanonicalSuccessResponse, nameof(submission.CanonicalSuccessResponse));
        ValidateCanonicalResponse(submission.CanonicalReversedResponse, nameof(submission.CanonicalReversedResponse));
    }

    private static bool SubmissionIsValid(MapJobSubmission submission)
    {
        try
        {
            ValidateSubmission(submission);
            var text = JsonSerializer.Serialize(submission, JsonOptions);
            return SubmissionFitsDurableFiles(submission, text);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static void ValidateCanonicalResponse(string response, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(response, parameterName);
        if (Utf8Length(response) > MaximumMessageBytes)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Canonical responses must not exceed 1 MiB.");
        }

        using var document = JsonDocument.Parse(response);
        if (document.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateProperties(document.RootElement))
        {
            throw new ArgumentException("Canonical responses must be JSON objects.", parameterName);
        }
    }

    private static bool TryValidateCanonicalResponse(string response)
    {
        try
        {
            ValidateCanonicalResponse(response, nameof(response));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool SubmissionFitsDurableFiles(MapJobSubmission submission, string submissionText)
    {
        if (Utf8Length(submissionText) > MaximumMessageBytes)
        {
            return false;
        }

        var prepared = CreatePreparedJournal(submission);
        var worstNodeIds = Enumerable.Repeat(long.MaxValue, MaximumNativeNodeIds).ToArray();
        var observations = new List<MapJobOperationObservation>(submission.OperationIds.Count);
        var remaining = MaximumNativeNodeIds;
        for (var index = 0; index < submission.OperationIds.Count; index++)
        {
            var slots = submission.OperationIds.Count - index;
            var count = remaining / slots;
            observations.Add(new MapJobOperationObservation(
                index,
                submission.OperationIds[index],
                worstNodeIds.AsSpan(MaximumNativeNodeIds - remaining, count).ToArray()));
            remaining -= count;
        }

        var worstObserved = prepared with
        {
            State = MapJobState.OperationObserved,
            NextOperationIndex = submission.OperationIds.Count,
            ObservedNativeNodeIds = worstNodeIds,
            OperationObservations = observations,
        };
        var successCommitted = worstObserved with
        {
            State = MapJobState.Committed,
            CanonicalResponse = submission.CanonicalSuccessResponse,
        };
        var reversedCommitted = worstObserved with
        {
            State = MapJobState.Committed,
            CanonicalResponse = submission.CanonicalReversedResponse,
        };
        var unknownCommitted = worstObserved with
        {
            State = MapJobState.Committed,
            CanonicalResponse = UnknownOutcomeResponse(prepared),
        };

        return Utf8Length(JsonSerializer.Serialize(prepared, JsonOptions)) <= MaximumMessageBytes &&
            Utf8Length(JsonSerializer.Serialize(worstObserved, JsonOptions)) <= MaximumMessageBytes &&
            Utf8Length(JsonSerializer.Serialize(successCommitted, JsonOptions)) <= MaximumMessageBytes &&
            Utf8Length(JsonSerializer.Serialize(reversedCommitted, JsonOptions)) <= MaximumMessageBytes &&
            Utf8Length(JsonSerializer.Serialize(unknownCommitted, JsonOptions)) <= MaximumMessageBytes;
    }

    private static string UnknownOutcomeResponse(MapJobJournal journal) => JsonSerializer.Serialize(new
    {
        success = false,
        request_id = journal.RequestId,
        plan_fingerprint = journal.PlanFingerprint,
        error = new
        {
            code = "outcome_unknown",
            message = "A native map mutation or reversal may have completed before interruption; it will not be replayed.",
        },
    }, JsonOptions);

    private static bool IdentityMatches(MapJobSubmission left, MapJobSubmission right) =>
        StringComparer.Ordinal.Equals(SerializeIdentity(left), SerializeIdentity(right));

    private static bool IdentityMatches(MapJobJournal journal, MapJobSubmission submission) =>
        IdentityMatches(SubmissionFrom(journal), submission);

    private static bool IdentityMatches(CompletionReceipt receipt, MapJobSubmission submission) =>
        StringComparer.Ordinal.Equals(receipt.SchemaVersion, CurrentSchemaVersion) &&
        StringComparer.Ordinal.Equals(receipt.RequestId, submission.RequestId) &&
        StringComparer.Ordinal.Equals(receipt.PlanFingerprint, submission.PlanFingerprint) &&
        StringComparer.Ordinal.Equals(receipt.MapId, submission.MapId) &&
        receipt.StartingMapRevision == submission.StartingMapRevision &&
        receipt.CatalogRevision == submission.CatalogRevision &&
        StringComparer.Ordinal.Equals(receipt.CatalogFingerprint, submission.CatalogFingerprint);

    private static string SerializeIdentity(MapJobSubmission submission) => JsonSerializer.Serialize(submission, JsonOptions);

    private static bool IsFingerprint(string value) =>
        value is { Length: 64 } && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool IsSafeIdentifier(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        !string.IsNullOrWhiteSpace(value) &&
        value is not "." and not ".." &&
        value.IndexOfAny(['/', '\\']) < 0 &&
        !value.Any(char.IsControl) &&
        !value.Contains('\u2028') &&
        !value.Contains('\u2029');

    private string PathFor(string directory, string key) => Path.Combine(_root, directory, key + ".json");

    private static ReadResult TryRead<T>(string path, out T? value)
    {
        value = default;
        if (!File.Exists(path))
        {
            return Directory.Exists(path) ? ReadResult.Blocked : ReadResult.Missing;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumMessageBytes)
            {
                return ReadResult.Invalid;
            }

            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateProperties(document.RootElement))
            {
                return ReadResult.Invalid;
            }

            value = JsonSerializer.Deserialize<T>(document.RootElement.GetRawText(), JsonOptions);
            return value is null ? ReadResult.Invalid : ReadResult.Valid;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ReadResult.Blocked;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return ReadResult.Invalid;
        }
    }

    private static bool TryReadRaw(string path, out string? value)
    {
        value = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumMessageBytes)
            {
                return false;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            value = reader.ReadToEnd();
            return Utf8Length(value) <= MaximumMessageBytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string SerializeBounded<T>(T value)
    {
        var text = JsonSerializer.Serialize(value, JsonOptions);
        if (Utf8Length(text) > MaximumMessageBytes)
        {
            throw new InvalidDataException("Map job files must not exceed 1 MiB.");
        }

        return text;
    }

    private static bool TryWriteRawAtomically(string destination, string text) => TryWriteAtomically(destination, text);

    private static bool TryWriteAtomically(string destination, string text)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static bool TryReplaceAtomically(string destination, string text)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int Utf8Length(string value) => Encoding.UTF8.GetByteCount(value);

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateProperties(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record PendingApplication(int OperationIndex, IReadOnlyList<long>? NativeNodeIds);

    private sealed record CompletionReceipt(
        string SchemaVersion,
        string RequestId,
        string PlanFingerprint,
        string MapId,
        long StartingMapRevision,
        long CatalogRevision,
        string CatalogFingerprint,
        string ResponseFingerprint);

    private enum ReadResult
    {
        Missing,
        Valid,
        Invalid,
        Blocked,
    }
}
