using System.Text.Json;
using DDAI.Core.Mailbox;
using DDAI.Core.MapPlans;

namespace DDAI.App;

public sealed record DdaiPlanValidationResult(
    bool Valid,
    MapPlan? CanonicalPlan,
    IReadOnlyList<MapPlanValidationIssue> Issues,
    IReadOnlyList<string> RuntimeChecksRequired);

public sealed record DdaiPlanApplyResult(
    bool Success,
    string Command,
    JsonElement Payload,
    MailboxErrorDetails? Error);

public sealed class DdaiPlanService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static readonly string[] RuntimeChecks =
        ["canvas", "grid_scale", "active_level", "wall_tool", "wall_count"];

    private readonly AtomicMailbox _mailbox;
    private readonly TimeProvider _timeProvider;

    public DdaiPlanService(AtomicMailbox mailbox, TimeProvider timeProvider)
    {
        _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public DdaiPlanValidationResult Validate(MapPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var validation = RectangularRoomPlanValidator.Validate(plan);
        if (!validation.IsValid)
        {
            return new DdaiPlanValidationResult(false, null, validation.Issues, RuntimeChecks);
        }

        var canonical = MapPlanJson.Deserialize(MapPlanJson.Serialize(plan));
        return new DdaiPlanValidationResult(true, canonical, [], RuntimeChecks);
    }

    public async Task<DdaiPlanApplyResult> ApplyAsync(
        MapPlan plan,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(plan);
        if (!validation.Valid)
        {
            return new DdaiPlanApplyResult(
                false,
                "apply_plan",
                JsonSerializer.SerializeToElement(new { issues = validation.Issues }, JsonOptions),
                new MailboxErrorDetails("invalid_plan", "The map plan is invalid."));
        }

        var canonicalPlan = validation.CanonicalPlan!;
        var expectedFingerprint = MapPlanJson.Fingerprint(canonicalPlan);
        var request = MailboxRequest.CreateApplyPlan(canonicalPlan, _timeProvider.GetUtcNow());
        _ = _mailbox.PublishRequest(request);

        var response = await Task.Run(
            () => _mailbox.WaitForResponse(request.RequestId, timeout),
            cancellationToken);
        if (response is null)
        {
            return Failure(
                "apply_timeout",
                "Dungeondraft did not answer before the deadline. The outcome is unknown; inspect the map and retry only with the same request_id.",
                expectedFingerprint,
                outcomeUnknown: true);
        }

        if (!StringComparer.Ordinal.Equals(response.Command, "apply_plan") ||
            response.Payload.ValueKind != JsonValueKind.Object ||
            !response.Payload.TryGetProperty("plan_fingerprint", out var actual) ||
            actual.ValueKind != JsonValueKind.String ||
            !StringComparer.Ordinal.Equals(actual.GetString(), expectedFingerprint))
        {
            return Failure(
                "request_conflict",
                "The request identifier belongs to a different map plan.",
                expectedFingerprint);
        }

        return new DdaiPlanApplyResult(
            response.Success,
            response.Command,
            response.Payload.Clone(),
            response.Error);
    }

    private static DdaiPlanApplyResult Failure(
        string code,
        string message,
        string planFingerprint,
        bool outcomeUnknown = false) =>
        new(
            false,
            "apply_plan",
            JsonSerializer.SerializeToElement(new
            {
                plan_fingerprint = planFingerprint,
                outcome_unknown = outcomeUnknown,
                retry_with_same_request_id = outcomeUnknown,
            }),
            new MailboxErrorDetails(code, message));
}
