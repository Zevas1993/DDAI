namespace DDAI.Core.MapPlans;

public sealed record MapPlanValidationIssue(string Code, string Path, string Message);

public sealed class MapPlanValidationException : Exception
{
    public MapPlanValidationException(IReadOnlyList<MapPlanValidationIssue> issues)
        : base("The map plan payload is invalid.")
    {
        Issues = issues ?? throw new ArgumentNullException(nameof(issues));
    }

    public IReadOnlyList<MapPlanValidationIssue> Issues { get; }
}

public sealed record MapPlanValidationResult(IReadOnlyList<MapPlanValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public static class MapPlanValidator
{
    public static MapPlanValidationResult Validate(MapPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var issues = new List<MapPlanValidationIssue>();

        if (!string.Equals(
                plan.SchemaVersion,
                MapPlan.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            issues.Add(new MapPlanValidationIssue(
                "unsupported_schema_version",
                "schema_version",
                $"Schema version '{plan.SchemaVersion}' is not supported."));
        }

        if (!SafeIdentifier.IsSafe(plan.RequestId))
        {
            issues.Add(new MapPlanValidationIssue(
                "invalid_request_id",
                "request_id",
                "Request ID must contain at least one non-whitespace character."));
        }

        if (plan.BaseRevision < 0)
        {
            issues.Add(new MapPlanValidationIssue(
                "invalid_base_revision",
                "base_revision",
                "Base revision cannot be negative."));
        }

        if (plan.Canvas is null)
        {
            issues.Add(new MapPlanValidationIssue(
                "invalid_canvas",
                "canvas",
                "Canvas is required."));
        }
        else
        {
            if (plan.Canvas.Width <= 0)
            {
                issues.Add(new MapPlanValidationIssue(
                    "invalid_canvas_width",
                    "canvas.width",
                    "Canvas width must be greater than zero."));
            }

            if (plan.Canvas.Height <= 0)
            {
                issues.Add(new MapPlanValidationIssue(
                    "invalid_canvas_height",
                    "canvas.height",
                    "Canvas height must be greater than zero."));
            }
        }

        if (plan.Rooms is null)
        {
            issues.Add(new MapPlanValidationIssue(
                "invalid_rooms",
                "rooms",
                "Rooms cannot be null."));
        }

        return new MapPlanValidationResult(issues);
    }
}

internal static class SafeIdentifier
{
    public static bool IsSafe(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value is not "." and not ".." &&
        !value.Contains('/') &&
        !value.Contains('\\');
}
