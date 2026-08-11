using System.Text.Json.Serialization;

namespace DDAI.Core.MapPlans;

[JsonConverter(typeof(JsonStringEnumConverter<MapJobState>))]
public enum MapJobState
{
    Prepared,
    OperationApplied,
    OperationObserved,
    Committed,
    Reversing,
    Reversed,
    OutcomeUnknown,
}

public sealed record MapJobSubmission(
    string RequestId,
    string PlanFingerprint,
    string MapId,
    long StartingMapRevision,
    long CatalogRevision,
    string CatalogFingerprint,
    IReadOnlyList<string> OperationIds,
    string CanonicalSuccessResponse,
    string CanonicalReversedResponse);

public sealed record MapJobOperationObservation(
    int OperationIndex,
    string OperationId,
    IReadOnlyList<long> NativeNodeIds);

public sealed record MapJobJournal(
    string SchemaVersion,
    string RequestId,
    string PlanFingerprint,
    string MapId,
    long StartingMapRevision,
    long CatalogRevision,
    string CatalogFingerprint,
    MapJobState State,
    int NextOperationIndex,
    IReadOnlyList<long> ObservedNativeNodeIds,
    IReadOnlyList<MapJobOperationObservation> OperationObservations,
    IReadOnlyList<long> CurrentOperationNodeIds,
    int? ReversalOperationIndex,
    string? CanonicalResponse,
    string CanonicalSuccessResponse,
    string CanonicalReversedResponse,
    IReadOnlyList<string> OperationIds);
