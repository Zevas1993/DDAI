using System.Diagnostics;
using System.Text.Json;
using DDAI.App.Assets;
using DDAI.Core.Mailbox;
using DDAI.Core.MapPlans;

namespace DDAI.App;

public sealed record UniversalPlanRuntimeContext(
    UniversalMapPlanCatalog Catalog,
    UniversalMapPlanCapabilities Capabilities);

public sealed record UniversalPlanContextResult(
    bool Ready,
    UniversalPlanRuntimeContext? Context,
    MailboxErrorDetails? Error)
{
    public static UniversalPlanContextResult Success(UniversalPlanRuntimeContext context) =>
        new(true, context, null);

    public static UniversalPlanContextResult Failure(string code, string message) =>
        new(false, null, new MailboxErrorDetails(code, message));
}

public interface IUniversalPlanContextProvider
{
    Task<UniversalPlanContextResult> GetAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class LiveUniversalPlanContextProvider(
    DdaiStatusService statusService,
    AssetCatalogRepository catalogRepository) : IUniversalPlanContextProvider
{
    private const int MaximumLevels = 100;
    private const int MaximumCertifiedOperations = 64;
    private static readonly HashSet<string> KnownOperationTypes = new(
    [
        "terrain_stroke", "pattern_region", "colorable_pattern_region", "cave_region", "roof_region",
        "object_placement", "wall_polyline", "material_stroke", "portal_placement", "path_polyline",
        "light_placement", "simple_tile_region", "smart_tile_region", "smart_tile_double_region",
    ], StringComparer.Ordinal);

    public async Task<UniversalPlanContextResult> GetAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _ = catalogRepository.TryRefresh();
        var accepted = catalogRepository.GetCurrent();
        if (accepted is null || !accepted.Live || !accepted.Manifest.Complete)
        {
            return UniversalPlanContextResult.Failure(
                "catalog_context_unavailable",
                "A complete live asset catalog is required for universal plan validation.");
        }

        var status = await statusService.GetStatusAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (!status.Success || !StringComparer.Ordinal.Equals(status.Command, "status") ||
            !TryReadCapabilities(status.Payload, out var capabilities))
        {
            return UniversalPlanContextResult.Failure(
                "runtime_context_unavailable",
                "The live bridge has not advertised revision-locked universal plan capabilities.");
        }

        var current = catalogRepository.GetCurrent();
        if (!CatalogSnapshotRemainedStable(accepted, current))
        {
            return UniversalPlanContextResult.Failure(
                "catalog_context_changed",
                "The live asset catalog expired or changed while runtime capabilities were being acquired.");
        }

        var activeGenerated = accepted.Entries
            .Where(entry => entry.Generated)
            .Select(entry => entry.AssetRef)
            .ToHashSet(StringComparer.Ordinal);
        return UniversalPlanContextResult.Success(new UniversalPlanRuntimeContext(
            new UniversalMapPlanCatalog(
                accepted.Manifest.CatalogRevision,
                accepted.Manifest.CatalogFingerprint,
                accepted.Entries,
                activeGenerated),
            capabilities!));
    }

    internal static bool CatalogSnapshotRemainedStable(
        AcceptedAssetCatalog accepted,
        AcceptedAssetCatalog? current) =>
        accepted.Live &&
        current is not null &&
        current.Live &&
        current.Manifest.CatalogRevision == accepted.Manifest.CatalogRevision &&
        StringComparer.Ordinal.Equals(
            current.Manifest.CatalogFingerprint,
            accepted.Manifest.CatalogFingerprint);

    internal static bool TryReadCapabilities(JsonElement payload, out UniversalMapPlanCapabilities? capabilities)
    {
        capabilities = null;
        if (payload.ValueKind != JsonValueKind.Object || HasDuplicateProperties(payload) ||
            !payload.TryGetProperty("map_id", out var mapIdProperty) ||
            mapIdProperty.ValueKind != JsonValueKind.String ||
            mapIdProperty.GetString() is not { Length: > 0 and <= 128 } mapId ||
            !IsSafeValue(mapId) ||
            !payload.TryGetProperty("map_job_revision", out var revisionProperty) ||
            revisionProperty.ValueKind != JsonValueKind.Number ||
            !revisionProperty.TryGetInt64(out var revision) ||
            revision < 0 ||
            !TryReadBoundedStrings(payload, "level_ids", MaximumLevels, out var levels) ||
            levels.Count == 0 ||
            !TryReadBoundedStrings(payload, "certified_operation_types", MaximumCertifiedOperations, out var operations) ||
            operations.Any(operation => !KnownOperationTypes.Contains(operation)))
        {
            return false;
        }

        capabilities = new UniversalMapPlanCapabilities(
            mapId,
            revision,
            levels,
            operations.ToDictionary(value => value, _ => true, StringComparer.Ordinal));
        return true;
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadBoundedStrings(
        JsonElement payload,
        string propertyName,
        int maximumCount,
        out IReadOnlyList<string> values)
    {
        values = [];
        if (!payload.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var items = property.EnumerateArray().ToArray();
        if (items.Length > maximumCount)
        {
            return false;
        }

        var result = new List<string>(items.Length);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.String ||
                item.GetString() is not { Length: > 0 and <= 128 } value ||
                !IsSafeValue(value) ||
                !observed.Add(value))
            {
                return false;
            }

            result.Add(value);
        }

        values = result;
        return true;
    }

    private static bool IsSafeValue(string value) =>
        value is not "." and not ".." &&
        value.IndexOfAny(['/', '\\']) < 0 &&
        !value.Any(char.IsControl) &&
        !value.Contains('\u2028') &&
        !value.Contains('\u2029');
}

public sealed record DdaiUniversalPlanValidationResult(
    bool Valid,
    MapPlan? CanonicalPlan,
    IReadOnlyList<MapPlanValidationIssue> Issues,
    IReadOnlyList<string> ResolvedOperationIds,
    string? PlanFingerprint,
    string? MapId,
    long? MapRevision,
    long? CatalogRevision,
    string? CatalogFingerprint,
    IReadOnlyList<string> RuntimeChecksRequired);

public sealed class DdaiUniversalPlanService
{
    private static readonly TimeSpan PollSlice = TimeSpan.FromMilliseconds(50);
    private static readonly string[] RuntimeChecks =
        ["map_identity", "map_revision", "catalog_revision", "catalog_fingerprint", "runtime_certification"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly AtomicMailbox mailbox;
    private readonly TimeProvider timeProvider;
    private readonly IUniversalPlanContextProvider contextProvider;
    private sealed record CanonicalPlanSnapshot(MapPlan Plan, string Json, string Fingerprint);

    public DdaiUniversalPlanService(
        AtomicMailbox mailbox,
        TimeProvider timeProvider,
        IUniversalPlanContextProvider contextProvider)
    {
        this.mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.contextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
    }

    public async Task<DdaiUniversalPlanValidationResult> ValidateAsync(
        MapPlan plan,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryCreateSnapshot(plan, out var snapshot, out var invalid))
        {
            return invalid!;
        }

        return await ValidateSnapshotAsync(snapshot!, timeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DdaiUniversalPlanValidationResult> ValidateSnapshotAsync(
        CanonicalPlanSnapshot snapshot,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var canonical = snapshot.Plan;
        if (!StringComparer.Ordinal.Equals(canonical.SchemaVersion, MapPlan.CurrentSchemaVersion))
        {
            return Invalid([new MapPlanValidationIssue(
                "unsupported_schema_version",
                "schema_version",
                "Universal plan validation requires schema 2.0.")]);
        }

        UniversalPlanContextResult contextResult;
        try
        {
            contextResult = await contextProvider
                .GetAsync(timeout, cancellationToken)
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Invalid([new MapPlanValidationIssue(
                "runtime_context_timeout",
                "runtime",
                "The live Dungeondraft validation context was not available before the deadline.")]);
        }
        if (!contextResult.Ready || contextResult.Context is null)
        {
            var code = contextResult.Error?.Code ?? "runtime_context_unavailable";
            return Invalid([new MapPlanValidationIssue(code, "runtime", "The live Dungeondraft validation context is unavailable.")]);
        }

        var context = contextResult.Context;
        var validation = UniversalMapPlanValidator.Validate(canonical, context.Catalog, context.Capabilities);
        return new DdaiUniversalPlanValidationResult(
            validation.IsValid,
            validation.IsValid ? canonical : null,
            validation.Issues,
            validation.ResolvedOperationIds,
            snapshot.Fingerprint,
            context.Capabilities.MapId,
            context.Capabilities.MapRevision,
            context.Catalog.Revision,
            context.Catalog.Fingerprint,
            RuntimeChecks);
    }

    public async Task<DdaiPlanApplyResult> ApplyAsync(
        MapPlan plan,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        if (!TryCreateSnapshot(plan, out var snapshot, out var malformed))
        {
            return InvalidApply(malformed!, "unavailable");
        }

        var expectedFingerprint = snapshot!.Fingerprint;
        var existing = TryReadExistingResponse(snapshot.Plan.RequestId);
        if (existing is not null)
        {
            UniversalPlanContextResult existingContext;
            try
            {
                var remaining = Remaining(timeout, stopwatch);
                existingContext = await contextProvider.GetAsync(remaining, cancellationToken)
                    .WaitAsync(remaining, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return Failure("runtime_context_timeout", "The completed response could not be checked against the live context before the deadline.", expectedFingerprint);
            }

            if (!existingContext.Ready || existingContext.Context is null)
            {
                return Failure(
                    existingContext.Error?.Code ?? "runtime_context_unavailable",
                    "The completed response cannot be trusted without the live map and catalog context.",
                    expectedFingerprint);
            }

            return CorrelateResponse(existing, snapshot.Plan, expectedFingerprint, existingContext.Context);
        }

        var validation = await ValidateSnapshotAsync(
            snapshot,
            Remaining(timeout, stopwatch),
            cancellationToken).ConfigureAwait(false);
        if (!validation.Valid || validation.CanonicalPlan is null)
        {
            return InvalidApply(validation, expectedFingerprint);
        }

        var canonical = validation.CanonicalPlan;
        var context = new UniversalPlanRuntimeContext(
            new UniversalMapPlanCatalog(
                validation.CatalogRevision!.Value,
                validation.CatalogFingerprint!,
                [],
                new HashSet<string>(StringComparer.Ordinal)),
            new UniversalMapPlanCapabilities(
                validation.MapId!,
                validation.MapRevision!.Value,
                [],
                new Dictionary<string, bool>(StringComparer.Ordinal)));
        using var payloadDocument = JsonDocument.Parse(snapshot.Json);
        var request = new MailboxRequest
        {
            SchemaVersion = MailboxRequest.CurrentSchemaVersion,
            RequestId = canonical.RequestId,
            Command = "apply_plan",
            Timestamp = timeProvider.GetUtcNow(),
            Payload = payloadDocument.RootElement.Clone(),
        };

        if (Remaining(timeout, stopwatch) <= TimeSpan.Zero)
        {
            return Failure("apply_timeout", "The apply deadline elapsed before publication.", expectedFingerprint);
        }

        bool published;
        try
        {
            published = mailbox.PublishRequest(request);
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return Failure("request_rejected", "The canonical plan could not be published safely.", expectedFingerprint);
        }

        if (!published)
        {
            existing = TryReadExistingResponse(canonical.RequestId);
            if (existing is not null)
            {
                return CorrelateResponse(existing, canonical, expectedFingerprint, context);
            }

            try
            {
                var active = mailbox.TryReadActiveRequest(canonical.RequestId);
                if (active is null)
                {
                    existing = TryReadExistingResponse(canonical.RequestId);
                    return existing is not null
                        ? CorrelateResponse(existing, canonical, expectedFingerprint, context)
                        : Failure("request_conflict", "The request identifier is already owned by different work.", expectedFingerprint);
                }

                if (!StringComparer.Ordinal.Equals(active.Command, "apply_plan") ||
                    !StringComparer.Ordinal.Equals(active.Payload.GetRawText(), snapshot.Json))
                {
                    return Failure("request_conflict", "The request identifier is already owned by different work.", expectedFingerprint);
                }
            }
            catch (Exception exception) when (exception is JsonException
                or InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or ArgumentException)
            {
                existing = TryReadExistingResponse(canonical.RequestId);
                return existing is not null
                    ? CorrelateResponse(existing, canonical, expectedFingerprint, context)
                    : Failure("request_conflict", "The active request could not be correlated safely.", expectedFingerprint);
            }
        }

        MailboxResponse? response;
        try
        {
            response = await WaitForResponseAsync(
                request.RequestId,
                Remaining(timeout, stopwatch),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return Failure("invalid_response", "Dungeondraft returned a malformed plan response.", expectedFingerprint);
        }

        if (response is null)
        {
            return Failure(
                "apply_timeout",
                "Dungeondraft did not answer before the deadline. The outcome is unknown; retry only with the same request_id.",
                expectedFingerprint,
                outcomeUnknown: true);
        }

        return CorrelateResponse(response, canonical, expectedFingerprint, context);
    }

    public async Task<DdaiPlanApplyResult> UndoLastJobAsync(
        string requestId,
        string targetRequestId,
        string expectedMapId,
        long expectedMapRevision,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedMapId);
        if (expectedMapRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedMapRevision));
        }
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var existing = TryReadExistingResponse(requestId);
        if (existing is not null)
        {
            return CorrelateUndoResponse(
                existing,
                targetRequestId,
                expectedMapId,
                expectedMapRevision);
        }

        UniversalPlanContextResult contextResult;
        try
        {
            var remaining = Remaining(timeout, stopwatch);
            contextResult = await contextProvider.GetAsync(remaining, cancellationToken)
                .WaitAsync(remaining, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return UndoFailure("runtime_context_timeout", "The live map context was not available before the undo deadline.", targetRequestId);
        }

        if (!contextResult.Ready || contextResult.Context is null)
        {
            return UndoFailure(contextResult.Error?.Code ?? "runtime_context_unavailable", "The live map context is unavailable.", targetRequestId);
        }
        if (!StringComparer.Ordinal.Equals(contextResult.Context.Capabilities.MapId, expectedMapId))
        {
            return UndoFailure("map_id_mismatch", "The open map identity does not match the requested undo.", targetRequestId);
        }
        if (contextResult.Context.Capabilities.MapRevision != expectedMapRevision)
        {
            return UndoFailure("map_revision_mismatch", "The open map changed after the target DDAI job completed.", targetRequestId);
        }

        var request = MailboxRequest.CreateUndoLastJob(
            requestId,
            targetRequestId,
            expectedMapId,
            expectedMapRevision,
            timeProvider.GetUtcNow());
        if (Remaining(timeout, stopwatch) <= TimeSpan.Zero)
        {
            return UndoFailure("undo_timeout", "The undo deadline elapsed before publication.", targetRequestId);
        }

        bool published;
        try
        {
            published = mailbox.PublishRequest(request);
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return UndoFailure("request_rejected", "The undo request could not be published safely.", targetRequestId);
        }

        if (!published)
        {
            existing = TryReadExistingResponse(requestId);
            if (existing is not null)
            {
                return CorrelateUndoResponse(existing, targetRequestId, expectedMapId, expectedMapRevision);
            }
            var active = mailbox.TryReadActiveRequest(requestId);
            if (active is null ||
                !StringComparer.Ordinal.Equals(active.Command, request.Command) ||
                !StringComparer.Ordinal.Equals(active.Payload.GetRawText(), request.Payload.GetRawText()))
            {
                return UndoFailure("request_conflict", "The undo request identifier belongs to different work.", targetRequestId);
            }
        }

        MailboxResponse? response;
        try
        {
            response = await WaitForResponseAsync(
                requestId,
                Remaining(timeout, stopwatch),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return UndoFailure("invalid_response", "Dungeondraft returned a malformed undo response.", targetRequestId);
        }
        if (response is null)
        {
            return UndoFailure("undo_timeout", "Dungeondraft did not answer before the undo deadline.", targetRequestId, outcomeUnknown: true);
        }

        return CorrelateUndoResponse(response, targetRequestId, expectedMapId, expectedMapRevision);
    }

    private static DdaiPlanApplyResult CorrelateUndoResponse(
        MailboxResponse response,
        string targetRequestId,
        string expectedMapId,
        long expectedMapRevision)
    {
        var payload = response.Payload;
        var correlated = StringComparer.Ordinal.Equals(response.Command, "undo_last_job") &&
            payload.ValueKind == JsonValueKind.Object &&
            !HasDuplicatePropertiesRecursive(payload) &&
            HasExactProperties(payload,
                "target_request_id",
                "map_id",
                "starting_map_revision",
                "map_revision",
                "outcome_unknown") &&
            HasString(payload, "target_request_id", targetRequestId) &&
            HasString(payload, "map_id", expectedMapId) &&
            HasInteger(payload, "starting_map_revision", expectedMapRevision) &&
            payload.TryGetProperty("map_revision", out var revisionProperty) &&
            revisionProperty.ValueKind == JsonValueKind.Number &&
            revisionProperty.TryGetInt64(out var resultRevision) &&
            resultRevision >= expectedMapRevision &&
            (!response.Success || resultRevision > expectedMapRevision) &&
            payload.TryGetProperty("outcome_unknown", out var unknownProperty) &&
            unknownProperty.ValueKind is JsonValueKind.True or JsonValueKind.False;
        return correlated
            ? new DdaiPlanApplyResult(response.Success, response.Command, payload.Clone(), response.Error)
            : UndoFailure("request_conflict", "The undo response does not belong to the requested job or map revision.", targetRequestId);
    }

    private MailboxResponse? TryReadExistingResponse(string requestId)
    {
        try
        {
            return mailbox.WaitForResponse(requestId, TimeSpan.FromMilliseconds(1));
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return null;
        }
    }

    private async Task<MailboxResponse?> WaitForResponseAsync(
        string requestId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = timeout - stopwatch.Elapsed;
            if (remaining < TimeSpan.Zero)
            {
                break;
            }

            var slice = remaining < PollSlice ? remaining : PollSlice;
            var response = await Task.Run(() => mailbox.WaitForResponse(requestId, slice), cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                return response;
            }
        }
        while (stopwatch.Elapsed <= timeout);

        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    private static DdaiPlanApplyResult CorrelateResponse(
        MailboxResponse response,
        MapPlan plan,
        string expectedFingerprint,
        UniversalPlanRuntimeContext? context)
    {
        var payload = response.Payload;
        long resultRevision = 0;
        var hasResultRevision = payload.ValueKind == JsonValueKind.Object &&
            !HasDuplicatePropertiesRecursive(payload) &&
            payload.TryGetProperty("map_revision", out var resultRevisionProperty) &&
            resultRevisionProperty.ValueKind == JsonValueKind.Number &&
            resultRevisionProperty.TryGetInt64(out resultRevision) &&
            resultRevision >= plan.BaseRevision &&
            (!response.Success || resultRevision > plan.BaseRevision);
        var correlated = StringComparer.Ordinal.Equals(response.Command, "apply_plan") &&
            payload.ValueKind == JsonValueKind.Object &&
            HasString(payload, "plan_fingerprint", expectedFingerprint) &&
            HasString(payload, "map_id", plan.ExpectedMapId!) &&
            HasInteger(payload, "starting_map_revision", plan.BaseRevision) &&
            plan.ExpectedCatalogRevision is { } expectedCatalogRevision &&
            HasInteger(payload, "catalog_revision", expectedCatalogRevision) &&
            payload.TryGetProperty("catalog_fingerprint", out var catalogFingerprint) &&
            catalogFingerprint.ValueKind == JsonValueKind.String &&
            catalogFingerprint.GetString() is { Length: 64 } fingerprint &&
            fingerprint.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0 &&
            hasResultRevision &&
            (context is null ||
                StringComparer.Ordinal.Equals(fingerprint, context.Catalog.Fingerprint) &&
                StringComparer.Ordinal.Equals(plan.ExpectedMapId, context.Capabilities.MapId) &&
                (context.Capabilities.MapRevision == plan.BaseRevision ||
                    context.Capabilities.MapRevision >= resultRevision));
        if (!correlated)
        {
            return Failure(
                "request_conflict",
                "The request identifier belongs to a different map plan or runtime revision.",
                expectedFingerprint);
        }

        return new DdaiPlanApplyResult(response.Success, response.Command, payload.Clone(), response.Error);
    }

    private static bool HasString(JsonElement payload, string name, string expected) =>
        payload.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        StringComparer.Ordinal.Equals(property.GetString(), expected);

    private static bool HasInteger(JsonElement payload, string name, long expected) =>
        payload.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt64(out var actual) &&
        actual == expected;

    private static bool HasExactProperties(JsonElement payload, params string[] expected)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var names = payload.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == expected.Length &&
            names.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static bool TryCreateSnapshot(
        MapPlan plan,
        out CanonicalPlanSnapshot? snapshot,
        out DdaiUniversalPlanValidationResult? invalid)
    {
        snapshot = null;
        invalid = null;
        try
        {
            var json = MapPlanJson.Serialize(plan);
            var canonical = MapPlanJson.Deserialize(json);
            snapshot = new CanonicalPlanSnapshot(canonical, json, MapPlanJson.Fingerprint(canonical));
            return true;
        }
        catch (MapPlanValidationException exception)
        {
            invalid = Invalid(exception.Issues);
            return false;
        }
        catch (Exception exception) when (exception is JsonException
            or ArgumentException
            or NotSupportedException
            or OverflowException)
        {
            invalid = Invalid([new MapPlanValidationIssue("invalid_plan", "plan", "The map plan is not canonical JSON.")]);
            return false;
        }
    }

    private static bool HasDuplicatePropertiesRecursive(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicatePropertiesRecursive(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicatePropertiesRecursive(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static TimeSpan Remaining(TimeSpan timeout, Stopwatch stopwatch)
    {
        var remaining = timeout - stopwatch.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static DdaiUniversalPlanValidationResult Invalid(IReadOnlyList<MapPlanValidationIssue> issues) => new(
        false,
        null,
        issues,
        [],
        null,
        null,
        null,
        null,
        null,
        RuntimeChecks);

    private static DdaiPlanApplyResult InvalidApply(
        DdaiUniversalPlanValidationResult validation,
        string fallbackFingerprint) => new(
        false,
        "apply_plan",
        JsonSerializer.SerializeToElement(new
        {
            plan_fingerprint = validation.PlanFingerprint ?? fallbackFingerprint,
            issues = validation.Issues,
        }, JsonOptions),
        new MailboxErrorDetails("invalid_plan", "The universal map plan is invalid."));

    private static DdaiPlanApplyResult Failure(
        string code,
        string message,
        string planFingerprint,
        bool outcomeUnknown = false) => new(
        false,
        "apply_plan",
        JsonSerializer.SerializeToElement(new
        {
            plan_fingerprint = planFingerprint,
            outcome_unknown = outcomeUnknown,
            retry_with_same_request_id = outcomeUnknown,
        }, JsonOptions),
        new MailboxErrorDetails(code, message));

    private static DdaiPlanApplyResult UndoFailure(
        string code,
        string message,
        string targetRequestId,
        bool outcomeUnknown = false) => new(
        false,
        "undo_last_job",
        JsonSerializer.SerializeToElement(new
        {
            target_request_id = targetRequestId,
            outcome_unknown = outcomeUnknown,
            retry_with_same_request_id = outcomeUnknown,
        }, JsonOptions),
        new MailboxErrorDetails(code, message));
}
