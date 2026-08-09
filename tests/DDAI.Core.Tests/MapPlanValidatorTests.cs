using DDAI.Core.MapPlans;

namespace DDAI.Core.Tests;

public sealed class MapPlanValidatorTests
{
    [Fact]
    public void Validate_RejectsUnsupportedSchemaVersion()
    {
        var plan = new MapPlan
        {
            SchemaVersion = "2.0",
            RequestId = "request-001",
            BaseRevision = 0,
            Mode = MapOperationMode.Add,
            Canvas = new MapCanvas(20, 20),
        };

        var result = MapPlanValidator.Validate(plan);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("unsupported_schema_version", issue.Code);
        Assert.Equal("schema_version", issue.Path);
    }

    [Fact]
    public void Validate_RejectsBlankRequestId()
    {
        var plan = new MapPlan
        {
            SchemaVersion = MapPlan.CurrentSchemaVersion,
            RequestId = "   ",
            BaseRevision = 0,
            Mode = MapOperationMode.Add,
            Canvas = new MapCanvas(20, 20),
        };

        var result = MapPlanValidator.Validate(plan);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("invalid_request_id", issue.Code);
        Assert.Equal("request_id", issue.Path);
    }

    [Fact]
    public void Validate_RejectsNegativeBaseRevision()
    {
        var plan = new MapPlan
        {
            SchemaVersion = MapPlan.CurrentSchemaVersion,
            RequestId = "request-002",
            BaseRevision = -1,
            Mode = MapOperationMode.Add,
            Canvas = new MapCanvas(20, 20),
        };

        var result = MapPlanValidator.Validate(plan);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("invalid_base_revision", issue.Code);
        Assert.Equal("base_revision", issue.Path);
    }

    [Fact]
    public void Validate_RejectsNonPositiveCanvasWidth()
    {
        var plan = new MapPlan
        {
            SchemaVersion = MapPlan.CurrentSchemaVersion,
            RequestId = "request-005",
            BaseRevision = 0,
            Mode = MapOperationMode.Add,
            Canvas = new MapCanvas(0, 20),
        };

        var result = MapPlanValidator.Validate(plan);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("invalid_canvas_width", issue.Code);
        Assert.Equal("canvas.width", issue.Path);
    }

    [Fact]
    public void Validate_RejectsNonPositiveCanvasHeight()
    {
        var plan = new MapPlan
        {
            SchemaVersion = MapPlan.CurrentSchemaVersion,
            RequestId = "request-006",
            BaseRevision = 0,
            Mode = MapOperationMode.Add,
            Canvas = new MapCanvas(20, 0),
        };

        var result = MapPlanValidator.Validate(plan);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("invalid_canvas_height", issue.Code);
        Assert.Equal("canvas.height", issue.Path);
    }

    [Fact]
    public void Validate_ReturnsEveryEnvelopeIssueInStableOrder()
    {
        var plan = new MapPlan
        {
            SchemaVersion = "2.0",
            RequestId = string.Empty,
            BaseRevision = -1,
            Mode = MapOperationMode.Add,
            Canvas = new MapCanvas(0, 0),
        };

        var result = MapPlanValidator.Validate(plan);

        Assert.Equal(
        [
            "unsupported_schema_version",
            "invalid_request_id",
            "invalid_base_revision",
            "invalid_canvas_width",
            "invalid_canvas_height",
        ],
            result.Issues.Select(issue => issue.Code));
    }

    [Fact]
    public void Validate_ReportsNullCanvasAsStableIssue()
    {
        var plan = new MapPlan
        {
            SchemaVersion = MapPlan.CurrentSchemaVersion,
            RequestId = "request-007",
            BaseRevision = 0,
            Mode = MapOperationMode.Add,
            Canvas = null!,
        };

        var result = MapPlanValidator.Validate(plan);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(("invalid_canvas", "canvas"), (issue.Code, issue.Path));
    }
}
