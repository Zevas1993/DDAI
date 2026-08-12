using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DDAI.Core.MapPlans;

namespace DDAI.Core.Mailbox;

public enum BridgeTransition
{
    NoWork,
    JournalCreated,
    ResponsePublished,
    ClaimDeleted,
    JournalDeleted,
    InvalidClaimFailed,
    InvalidJournalFailed,
    MutationIntentCreated,
    MutationConfirmed,
    MutationAmbiguityRecorded,
    MutationIntentDeleted,
    UniversalJobAdvanced,
    Blocked,
}

public static class BridgeWireJson
{
    public static JsonSerializerOptions Options => MailboxWireJson.Options;

    public static JsonSerializerOptions OptionsIndented => MailboxWireJson.OptionsIndented;
}

/// <summary>
/// Executable reference model for the Dungeondraft GDScript bridge's durable
/// claim-to-response protocol. Each call performs at most one durable state
/// transition so restart behavior can be verified at every crash boundary.
/// </summary>
public sealed class DungeondraftBridgeStateMachine
{
    private const long MaximumMessageBytes = AtomicMailbox.MaximumMessageBytes;
    private readonly string _root;
    private readonly Func<MailboxRequest, MailboxResponse> _prepareResponse;
    private readonly Func<MailboxRequest, MailboxResponse>? _executeMutation;
    private readonly Func<MailboxRequest, BridgeTransition>? _advanceUniversalPlan;
    private readonly HashSet<string> _preparedMutationKeys = new(StringComparer.Ordinal);

    public DungeondraftBridgeStateMachine(
        string rootDirectory,
        Func<MailboxRequest, MailboxResponse> prepareResponse,
        Func<MailboxRequest, MailboxResponse>? executeMutation = null,
        Func<MailboxRequest, BridgeTransition>? advanceUniversalPlan = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(prepareResponse);
        _root = Path.GetFullPath(rootDirectory);
        _prepareResponse = prepareResponse;
        _executeMutation = executeMutation;
        _advanceUniversalPlan = advanceUniversalPlan;
        foreach (var name in new[] { "requests", "processing", "responses", "failed", "journal", "mutation-intents" })
        {
            Directory.CreateDirectory(DirectoryPath(name));
        }
    }

    public BridgeTransition AdvanceClaim(string canonicalFileName)
    {
        if (!IsCanonicalMailboxFileName(canonicalFileName))
        {
            throw new ArgumentException("A lowercase SHA-256 JSON file name is required.", nameof(canonicalFileName));
        }

        var key = Path.GetFileNameWithoutExtension(canonicalFileName);
        var processingPath = StatePath("processing", canonicalFileName);
        var journalPath = StatePath("journal", canonicalFileName);
        var responsePath = StatePath("responses", canonicalFileName);
        if (!File.Exists(processingPath))
        {
            return AdvanceWithoutClaim(key, journalPath, responsePath);
        }

        if (!TryReadValidatedRequest(processingPath, canonicalFileName, out var request, out var canonicalRequestText))
        {
            MoveToFailed(processingPath, key, "invalid-claim", ReadBoundedTextIfPossible(processingPath));
            return BridgeTransition.InvalidClaimFailed;
        }

        ReconcileRequestDuplicate(canonicalFileName, canonicalRequestText);
        var fingerprint = Sha256(canonicalRequestText);
        if (StringComparer.Ordinal.Equals(request.Command, "apply_plan"))
        {
            if (IsUniversalPlanRequest(request))
            {
                return _advanceUniversalPlan?.Invoke(request) ?? BridgeTransition.Blocked;
            }

            return AdvanceMutationClaim(
                key,
                canonicalFileName,
                request,
                fingerprint,
                journalPath,
                responsePath,
                processingPath);
        }

        return AdvanceJournaledClaim(
            key,
            request,
            fingerprint,
            journalPath,
            responsePath,
            processingPath,
            responseText: null);
    }

    internal static bool IsUniversalPlanRequest(MailboxRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var plan = MapPlanJson.Deserialize(request.Payload.GetRawText());
            return StringComparer.Ordinal.Equals(plan.SchemaVersion, MapPlan.CurrentSchemaVersion) &&
                StringComparer.Ordinal.Equals(plan.RequestId, request.RequestId);
        }
        catch (Exception exception) when (exception is JsonException or MapPlanValidationException or ArgumentException)
        {
            return false;
        }
    }

    private BridgeTransition AdvanceJournaledClaim(
        string key,
        MailboxRequest request,
        string requestFingerprint,
        string journalPath,
        string responsePath,
        string processingPath,
        string? responseText)
    {
        if (!File.Exists(journalPath))
        {
            if (responseText is null)
            {
                var response = _prepareResponse(request);
                ValidateResponse(response, request);
                responseText = JsonSerializer.Serialize(response, BridgeWireJson.Options);
            }

            if (Encoding.UTF8.GetByteCount(responseText) > MaximumMessageBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(responseText), "Serialized responses must not exceed 1 MiB.");
            }

            var journal = new ResponseJournal(
                MailboxRequest.CurrentSchemaVersion,
                request.RequestId,
                requestFingerprint,
                responseText);
            WriteAtomically(journalPath, JsonSerializer.Serialize(journal, BridgeWireJson.Options));
            return BridgeTransition.JournalCreated;
        }

        if (!TryReadValidatedJournal(journalPath, requestFingerprint, request.RequestId, out var prepared) ||
            !TryReadValidatedResponse(prepared.ResponseText, request, out _))
        {
            MoveToFailed(journalPath, key, "invalid-journal", ReadBoundedTextIfPossible(journalPath));
            return BridgeTransition.InvalidJournalFailed;
        }

        if (!File.Exists(responsePath))
        {
            WriteAtomically(responsePath, prepared.ResponseText);
            return BridgeTransition.ResponsePublished;
        }

        var existingResponseText = ReadBoundedTextIfPossible(responsePath);
        if (existingResponseText is null || !ResponsesEquivalent(prepared.ResponseText, existingResponseText))
        {
            WriteConflictDiagnostic(key, prepared.ResponseText, existingResponseText ?? "<unreadable>");
            return BridgeTransition.Blocked;
        }

        File.Delete(processingPath);
        return BridgeTransition.ClaimDeleted;
    }

    private BridgeTransition AdvanceMutationClaim(
        string key,
        string canonicalFileName,
        MailboxRequest request,
        string requestFingerprint,
        string journalPath,
        string responsePath,
        string processingPath)
    {
        if (!TryGetMutationPlanFingerprint(request, out var planFingerprint))
        {
            MoveToFailed(processingPath, key, "invalid-claim", ReadBoundedTextIfPossible(processingPath));
            return BridgeTransition.InvalidClaimFailed;
        }

        var preparedPath = MutationIntentPath(key, "prepared");
        var confirmedPath = MutationIntentPath(key, "confirmed");
        var ambiguousPath = MutationIntentPath(key, "ambiguous");
        if (!PathExists(preparedPath))
        {
            if (PathExists(confirmedPath) || PathExists(ambiguousPath))
            {
                return BridgeTransition.Blocked;
            }

            var prepared = new MutationIntent(
                MailboxRequest.CurrentSchemaVersion,
                request.RequestId,
                requestFingerprint,
                planFingerprint,
                request.Command,
                "prepared",
                null);
            if (!TryWriteBoundedAtomically(preparedPath, JsonSerializer.Serialize(prepared, BridgeWireJson.Options)))
            {
                return BridgeTransition.Blocked;
            }

            _preparedMutationKeys.Add(key);
            return BridgeTransition.MutationIntentCreated;
        }

        if (!TryReadValidatedMutationIntent(
                preparedPath,
                key,
                request.RequestId,
                requestFingerprint,
                planFingerprint,
                request.Command,
                "prepared",
                requireResponse: false,
                out _))
        {
            WriteMutationIntentConflictDiagnostic(key, "prepared");
            return BridgeTransition.Blocked;
        }

        var hasConfirmed = PathExists(confirmedPath);
        var hasAmbiguous = PathExists(ambiguousPath);
        if (hasConfirmed && hasAmbiguous)
        {
            WriteMutationIntentConflictDiagnostic(key, "multiple-outcomes");
            return BridgeTransition.Blocked;
        }

        if (hasConfirmed || hasAmbiguous)
        {
            var outcomePath = hasConfirmed ? confirmedPath : ambiguousPath;
            var outcomeState = hasConfirmed ? "confirmed" : "ambiguous";
            if (!TryReadValidatedMutationIntent(
                    outcomePath,
                    key,
                    request.RequestId,
                    requestFingerprint,
                    planFingerprint,
                    request.Command,
                    outcomeState,
                    requireResponse: true,
                    out var outcome))
            {
                WriteMutationIntentConflictDiagnostic(key, outcomeState);
                return BridgeTransition.Blocked;
            }

            return AdvanceJournaledClaim(
                key,
                request,
                requestFingerprint,
                journalPath,
                responsePath,
                processingPath,
                outcome.ResponseText);
        }

        if (!_preparedMutationKeys.Remove(key))
        {
            var response = MutationUnknownResponse(request, planFingerprint);
            var responseText = JsonSerializer.Serialize(response, BridgeWireJson.Options);
            var ambiguous = new MutationIntent(
                MailboxRequest.CurrentSchemaVersion,
                request.RequestId,
                requestFingerprint,
                planFingerprint,
                request.Command,
                "ambiguous",
                responseText);
            if (!TryWriteBoundedAtomically(ambiguousPath, JsonSerializer.Serialize(ambiguous, BridgeWireJson.Options)))
            {
                return BridgeTransition.Blocked;
            }

            return BridgeTransition.MutationAmbiguityRecorded;
        }

        if (_executeMutation is null)
        {
            return BridgeTransition.Blocked;
        }

        var executedResponse = _executeMutation(request);
        ValidateResponse(executedResponse, request);
        if (!TryReadPayloadPlanFingerprint(executedResponse.Payload, out var responseFingerprint) ||
            !StringComparer.Ordinal.Equals(responseFingerprint, planFingerprint))
        {
            return BridgeTransition.Blocked;
        }

        var executedResponseText = JsonSerializer.Serialize(executedResponse, BridgeWireJson.Options);
        var confirmed = new MutationIntent(
            MailboxRequest.CurrentSchemaVersion,
            request.RequestId,
            requestFingerprint,
            planFingerprint,
            request.Command,
            "confirmed",
            executedResponseText);
        if (!TryWriteBoundedAtomically(confirmedPath, JsonSerializer.Serialize(confirmed, BridgeWireJson.Options)))
        {
            return BridgeTransition.Blocked;
        }

        return BridgeTransition.MutationConfirmed;
    }

    private BridgeTransition AdvanceWithoutClaim(
        string key,
        string journalPath,
        string responsePath)
    {
        if (Directory.Exists(journalPath))
        {
            return BridgeTransition.Blocked;
        }

        if (File.Exists(journalPath))
        {
            if (TryReadValidatedJournal(journalPath, expectedFingerprint: null, expectedRequestId: null, out var staleJournal) &&
                StringComparer.Ordinal.Equals(Sha256(staleJournal.RequestId), key) &&
                TryReadValidatedStandaloneResponse(staleJournal.ResponseText, staleJournal.RequestId, out _) &&
                File.Exists(responsePath) &&
                ResponsesEquivalent(staleJournal.ResponseText, ReadBoundedText(responsePath)))
            {
                File.Delete(journalPath);
                return BridgeTransition.JournalDeleted;
            }

            return BridgeTransition.Blocked;
        }

        var preparedPath = MutationIntentPath(key, "prepared");
        var confirmedPath = MutationIntentPath(key, "confirmed");
        var ambiguousPath = MutationIntentPath(key, "ambiguous");
        if (PathExists(ambiguousPath))
        {
            if (!TryReadValidatedMutationIntent(
                    ambiguousPath,
                    key,
                    expectedRequestId: null,
                    expectedRequestFingerprint: null,
                    expectedPlanFingerprint: null,
                    expectedCommand: "apply_plan",
                    expectedState: "ambiguous",
                    requireResponse: true,
                    out var ambiguous))
            {
                return BridgeTransition.Blocked;
            }

            var diagnosticPath = Path.Combine(DirectoryPath("failed"), $"{key}.mutation-outcome-unknown.json");
            if (Directory.Exists(diagnosticPath))
            {
                return BridgeTransition.Blocked;
            }

            if (!File.Exists(diagnosticPath))
            {
                if (!TryWriteBoundedAtomically(diagnosticPath, JsonSerializer.Serialize(ambiguous, BridgeWireJson.Options)))
                {
                    return BridgeTransition.Blocked;
                }

                return BridgeTransition.MutationAmbiguityRecorded;
            }

            return BridgeTransition.NoWork;
        }

        if (PathExists(confirmedPath))
        {
            if (!TryReadValidatedMutationIntent(
                    confirmedPath,
                    key,
                    expectedRequestId: null,
                    expectedRequestFingerprint: null,
                    expectedPlanFingerprint: null,
                    expectedCommand: "apply_plan",
                    expectedState: "confirmed",
                    requireResponse: true,
                    out var confirmed) ||
                !File.Exists(responsePath) ||
                !ResponsesEquivalent(confirmed.ResponseText!, ReadBoundedText(responsePath)))
            {
                return BridgeTransition.Blocked;
            }

            return TryDeleteFile(confirmedPath)
                ? BridgeTransition.MutationIntentDeleted
                : BridgeTransition.Blocked;
        }

        if (PathExists(preparedPath))
        {
            if (!TryReadValidatedMutationIntent(
                    preparedPath,
                    key,
                    expectedRequestId: null,
                    expectedRequestFingerprint: null,
                    expectedPlanFingerprint: null,
                    expectedCommand: "apply_plan",
                    expectedState: "prepared",
                    requireResponse: false,
                    out var prepared) ||
                !File.Exists(responsePath) ||
                !TryReadValidatedStandaloneResponse(ReadBoundedText(responsePath), prepared.RequestId, out var response) ||
                !StringComparer.Ordinal.Equals(response.Command, prepared.Command) ||
                !TryReadPayloadPlanFingerprint(response.Payload, out var responseFingerprint) ||
                !StringComparer.Ordinal.Equals(responseFingerprint, prepared.PlanFingerprint))
            {
                return BridgeTransition.Blocked;
            }

            return TryDeleteFile(preparedPath)
                ? BridgeTransition.MutationIntentDeleted
                : BridgeTransition.Blocked;
        }

        return BridgeTransition.NoWork;
    }

    public void RecoverAll()
    {
        foreach (var processingPath in Directory.EnumerateFiles(DirectoryPath("processing"), "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(processingPath);
            if (!IsCanonicalMailboxFileName(fileName))
            {
                MoveToFailed(processingPath, Path.GetFileNameWithoutExtension(fileName), "invalid-claim", ReadBoundedTextIfPossible(processingPath));
                continue;
            }

            for (var transitions = 0; transitions < 8; transitions++)
            {
                var transition = AdvanceClaim(fileName);
                if (transition is BridgeTransition.NoWork or BridgeTransition.Blocked or BridgeTransition.JournalDeleted)
                {
                    break;
                }
            }
        }

        foreach (var journalPath in Directory.EnumerateFiles(DirectoryPath("journal"), "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(journalPath);
            if (IsCanonicalMailboxFileName(fileName))
            {
                AdvanceClaim(fileName);
            }
        }

        var mutationFiles = Directory.EnumerateFiles(DirectoryPath("mutation-intents"), "*.json")
            .Select(Path.GetFileName)
            .Where(fileName => fileName is not null)
            .Select(TryGetCanonicalFileNameFromIntent)
            .Where(fileName => fileName is not null)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        foreach (var fileName in mutationFiles)
        {
            for (var transitions = 0; transitions < 8; transitions++)
            {
                var transition = AdvanceClaim(fileName!);
                if (transition is BridgeTransition.NoWork or BridgeTransition.Blocked)
                {
                    break;
                }
            }
        }
    }

    private void ReconcileRequestDuplicate(string canonicalFileName, string canonicalProcessingText)
    {
        var requestPath = StatePath("requests", canonicalFileName);
        if (!File.Exists(requestPath))
        {
            return;
        }

        if (TryReadValidatedRequest(requestPath, canonicalFileName, out _, out var canonicalDuplicateText) &&
            StringComparer.Ordinal.Equals(canonicalProcessingText, canonicalDuplicateText))
        {
            File.Delete(requestPath);
            return;
        }

        MoveToFailed(
            requestPath,
            Path.GetFileNameWithoutExtension(canonicalFileName),
            "duplicate-conflict",
            ReadBoundedTextIfPossible(requestPath));
    }

    private static bool TryReadValidatedRequest(
        string path,
        string canonicalFileName,
        out MailboxRequest request,
        out string canonicalText)
    {
        request = null!;
        canonicalText = string.Empty;
        try
        {
            request = JsonSerializer.Deserialize<MailboxRequest>(ReadBoundedText(path), BridgeWireJson.Options)
                ?? throw new JsonException("Request JSON cannot be null.");
            ValidateRequest(request, canonicalFileName);
            canonicalText = JsonSerializer.Serialize(request, BridgeWireJson.Options);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
        {
            return false;
        }
    }

    private static void ValidateRequest(MailboxRequest request, string canonicalFileName)
    {
        if (!StringComparer.Ordinal.Equals(request.SchemaVersion, MailboxRequest.CurrentSchemaVersion))
        {
            throw new ArgumentException("Unsupported request schema version.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestId);
        if (request.RequestId.Contains(Path.DirectorySeparatorChar) ||
            request.RequestId.Contains(Path.AltDirectorySeparatorChar) ||
            request.RequestId is "." or "..")
        {
            throw new ArgumentException("Request ID is unsafe.");
        }

        if (!StringComparer.Ordinal.Equals(Path.GetFileNameWithoutExtension(canonicalFileName), Sha256(request.RequestId)))
        {
            throw new ArgumentException("Request ID does not match the canonical file name.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Command);
        if (!WireTimestampJsonConverter.IsValid(request.Timestamp))
        {
            throw new ArgumentException("Request timestamp is required.");
        }

        if (request.Payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new ArgumentException("Request payload is required.");
        }
    }

    private static void ValidateResponse(MailboxResponse response, MailboxRequest request)
    {
        if (!TryReadValidatedResponse(JsonSerializer.Serialize(response, BridgeWireJson.Options), request, out _))
        {
            throw new ArgumentException("Prepared response is invalid or does not correlate to its request.", nameof(response));
        }
    }

    private static bool TryReadValidatedResponse(string text, MailboxRequest request, out MailboxResponse response)
    {
        if (!TryReadValidatedStandaloneResponse(text, request.RequestId, out response))
        {
            return false;
        }

        return StringComparer.Ordinal.Equals(response.Command, request.Command);
    }

    private static bool TryReadValidatedStandaloneResponse(string text, string expectedRequestId, out MailboxResponse response)
    {
        response = null!;
        try
        {
            response = JsonSerializer.Deserialize<MailboxResponse>(text, BridgeWireJson.Options)
                ?? throw new JsonException("Response JSON cannot be null.");
            if (!StringComparer.Ordinal.Equals(response.SchemaVersion, MailboxRequest.CurrentSchemaVersion) ||
                !StringComparer.Ordinal.Equals(response.RequestId, expectedRequestId) ||
                string.IsNullOrWhiteSpace(response.Command) ||
                !WireTimestampJsonConverter.IsValid(response.Timestamp) ||
                response.Payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                (response.Success && response.Error is not null) ||
                (!response.Success && (response.Error is null || string.IsNullOrWhiteSpace(response.Error.Code) || string.IsNullOrWhiteSpace(response.Error.Message))))
            {
                throw new ArgumentException("Response envelope is invalid.");
            }

            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryReadValidatedJournal(
        string path,
        string? expectedFingerprint,
        string? expectedRequestId,
        out ResponseJournal journal)
    {
        journal = null!;
        try
        {
            journal = JsonSerializer.Deserialize<ResponseJournal>(ReadBoundedText(path), BridgeWireJson.Options)
                ?? throw new JsonException("Journal JSON cannot be null.");
            if (!StringComparer.Ordinal.Equals(journal.SchemaVersion, MailboxRequest.CurrentSchemaVersion) ||
                string.IsNullOrWhiteSpace(journal.RequestId) ||
                (expectedRequestId is not null && !StringComparer.Ordinal.Equals(journal.RequestId, expectedRequestId)) ||
                (expectedFingerprint is not null && !StringComparer.Ordinal.Equals(journal.RequestFingerprint, expectedFingerprint)) ||
                string.IsNullOrWhiteSpace(journal.ResponseText) ||
                Encoding.UTF8.GetByteCount(journal.ResponseText) > MaximumMessageBytes)
            {
                throw new JsonException("Journal is invalid.");
            }

            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryGetMutationPlanFingerprint(MailboxRequest request, out string fingerprint)
    {
        fingerprint = string.Empty;
        try
        {
            var plan = MapPlanJson.Deserialize(request.Payload.GetRawText());
            if (!StringComparer.Ordinal.Equals(plan.RequestId, request.RequestId) ||
                !RectangularRoomPlanValidator.Validate(plan).IsValid)
            {
                return false;
            }

            fingerprint = MapPlanJson.Fingerprint(plan);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or MapPlanValidationException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryReadValidatedMutationIntent(
        string path,
        string expectedKey,
        string? expectedRequestId,
        string? expectedRequestFingerprint,
        string? expectedPlanFingerprint,
        string expectedCommand,
        string expectedState,
        bool requireResponse,
        out MutationIntent intent)
    {
        intent = null!;
        try
        {
            intent = JsonSerializer.Deserialize<MutationIntent>(ReadBoundedText(path), BridgeWireJson.Options)
                ?? throw new JsonException("Mutation intent JSON cannot be null.");
            if (!StringComparer.Ordinal.Equals(intent.SchemaVersion, MailboxRequest.CurrentSchemaVersion) ||
                string.IsNullOrWhiteSpace(intent.RequestId) ||
                !StringComparer.Ordinal.Equals(Sha256(intent.RequestId), expectedKey) ||
                (expectedRequestId is not null && !StringComparer.Ordinal.Equals(intent.RequestId, expectedRequestId)) ||
                intent.RequestFingerprint is not { Length: 64 } ||
                intent.RequestFingerprint.AsSpan().IndexOfAnyExcept("0123456789abcdef") >= 0 ||
                (expectedRequestFingerprint is not null && !StringComparer.Ordinal.Equals(intent.RequestFingerprint, expectedRequestFingerprint)) ||
                intent.PlanFingerprint is not { Length: 64 } ||
                intent.PlanFingerprint.AsSpan().IndexOfAnyExcept("0123456789abcdef") >= 0 ||
                (expectedPlanFingerprint is not null && !StringComparer.Ordinal.Equals(intent.PlanFingerprint, expectedPlanFingerprint)) ||
                !StringComparer.Ordinal.Equals(intent.Command, expectedCommand) ||
                !StringComparer.Ordinal.Equals(intent.State, expectedState) ||
                (requireResponse && string.IsNullOrWhiteSpace(intent.ResponseText)) ||
                (!requireResponse && intent.ResponseText is not null) ||
                (intent.ResponseText is not null && Encoding.UTF8.GetByteCount(intent.ResponseText) > MaximumMessageBytes))
            {
                throw new JsonException("Mutation intent is invalid.");
            }

            if (intent.ResponseText is not null &&
                (!TryReadValidatedStandaloneResponse(intent.ResponseText, intent.RequestId, out var response) ||
                 !StringComparer.Ordinal.Equals(response.Command, intent.Command) ||
                 !TryReadPayloadPlanFingerprint(response.Payload, out var responseFingerprint) ||
                 !StringComparer.Ordinal.Equals(responseFingerprint, intent.PlanFingerprint)))
            {
                throw new JsonException("Mutation intent response is invalid.");
            }

            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryReadPayloadPlanFingerprint(JsonElement payload, out string fingerprint)
    {
        fingerprint = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("plan_fingerprint", out var property) ||
            property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { Length: 64 } value ||
            value.AsSpan().IndexOfAnyExcept("0123456789abcdef") >= 0)
        {
            return false;
        }

        fingerprint = value;
        return true;
    }

    private static MailboxResponse MutationUnknownResponse(MailboxRequest request, string planFingerprint) => new()
    {
        SchemaVersion = MailboxRequest.CurrentSchemaVersion,
        RequestId = request.RequestId,
        Command = request.Command,
        Timestamp = DateTimeOffset.UtcNow,
        Success = false,
        Payload = JsonSerializer.SerializeToElement(new
        {
            plan_fingerprint = planFingerprint,
            outcome_unknown = true,
        }),
        Error = new MailboxErrorDetails(
            "mutation_outcome_unknown",
            "Dungeondraft may have applied this room before interruption; inspect the map and use Undo once if it appeared.",
            "request_id"),
    };

    private void WriteMutationIntentConflictDiagnostic(string key, string state)
    {
        var path = Path.Combine(DirectoryPath("failed"), $"{key}.mutation-intent-{state}.json");
        if (PathExists(path))
        {
            return;
        }

        var record = JsonSerializer.Serialize(new
        {
            schema_version = MailboxRequest.CurrentSchemaVersion,
            error = new
            {
                code = "mutation_intent_conflict",
                message = "Mutation intent state is invalid or conflicting.",
            },
            state,
        }, BridgeWireJson.Options);
        _ = TryWriteBoundedAtomically(path, record);
    }

    private static bool ResponsesEquivalent(string left, string right)
    {
        try
        {
            using var leftJson = JsonDocument.Parse(left);
            using var rightJson = JsonDocument.Parse(right);
            return JsonElement.DeepEquals(leftJson.RootElement, rightJson.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void WriteConflictDiagnostic(string key, string expected, string existing)
    {
        var fingerprint = Sha256(expected + "\n" + existing)[..16];
        var path = Path.Combine(DirectoryPath("failed"), $"{key}.response-conflict.{fingerprint}.json");
        if (!File.Exists(path))
        {
            var record = JsonSerializer.Serialize(new
            {
                schema_version = MailboxRequest.CurrentSchemaVersion,
                error = new { code = "response_conflict", message = "Existing response differs from the exact journaled response." },
                expected_response_fingerprint = Sha256(expected),
                existing_response_fingerprint = Sha256(existing),
            }, BridgeWireJson.Options);
            WriteAtomically(path, record);
        }
    }

    private void MoveToFailed(string sourcePath, string key, string reason, string? sourceText)
    {
        var fingerprint = Sha256(sourceText ?? Path.GetFileName(sourcePath))[..16];
        var destination = Path.Combine(DirectoryPath("failed"), $"{key}.{reason}.{fingerprint}.json");
        if (File.Exists(destination))
        {
            destination = Path.Combine(DirectoryPath("failed"), $"{key}.{reason}.{fingerprint}.{Guid.NewGuid():N}.json");
        }

        File.Move(sourcePath, destination, overwrite: false);
    }

    private string DirectoryPath(string name) => Path.Combine(_root, name);

    private string StatePath(string state, string canonicalFileName) => Path.Combine(DirectoryPath(state), canonicalFileName);

    private string MutationIntentPath(string key, string state) =>
        Path.Combine(DirectoryPath("mutation-intents"), $"{key}.{state}.json");

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string? TryGetCanonicalFileNameFromIntent(string? fileName)
    {
        if (fileName is null ||
            (!fileName.EndsWith(".prepared.json", StringComparison.Ordinal) &&
             !fileName.EndsWith(".confirmed.json", StringComparison.Ordinal) &&
             !fileName.EndsWith(".ambiguous.json", StringComparison.Ordinal)))
        {
            return null;
        }

        var separator = fileName.IndexOf('.', StringComparison.Ordinal);
        if (separator != 64)
        {
            return null;
        }

        var canonical = fileName[..64] + ".json";
        return IsCanonicalMailboxFileName(canonical) ? canonical : null;
    }

    private static bool IsCanonicalMailboxFileName(string fileName) =>
        fileName.Length == 69 &&
        fileName.EndsWith(".json", StringComparison.Ordinal) &&
        fileName.AsSpan(0, 64).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static string ReadBoundedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("Mailbox files must not exceed 1 MiB.");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        if (Encoding.UTF8.GetByteCount(text) > MaximumMessageBytes)
        {
            throw new InvalidDataException("Mailbox files must not exceed 1 MiB.");
        }

        return text;
    }

    private static string? ReadBoundedTextIfPossible(string path)
    {
        try
        {
            return ReadBoundedText(path);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteAtomically(string destinationPath, string text)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool TryWriteAtomically(string destinationPath, string text)
    {
        try
        {
            WriteAtomically(destinationPath, text);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryWriteBoundedAtomically(string destinationPath, string text) =>
        Encoding.UTF8.GetByteCount(text) <= MaximumMessageBytes &&
        TryWriteAtomically(destinationPath, text);

    private static bool TryDeleteFile(string path)
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

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record ResponseJournal(
        string SchemaVersion,
        string RequestId,
        string RequestFingerprint,
        string ResponseText);

    private sealed record MutationIntent(
        string SchemaVersion,
        string RequestId,
        string RequestFingerprint,
        string PlanFingerprint,
        string Command,
        string State,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResponseText);
}
