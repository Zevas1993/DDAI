using DDAI.Core.MapPlans;
using System.Text.Json;

namespace DDAI.Core.Tests;

public sealed class MapPlanJsonTests
{
    [Fact]
    public void Serialize_UsesStableSnakeCaseWireFormat()
    {
        var plan = new MapPlan
        {
            SchemaVersion = MapPlan.CurrentSchemaVersion,
            RequestId = "request-003",
            BaseRevision = 7,
            Mode = MapOperationMode.Add,
            Canvas = new MapCanvas(30, 20),
        };

        var json = MapPlanJson.Serialize(plan);

        Assert.Equal(
            "{\"schema_version\":\"1.0\",\"request_id\":\"request-003\",\"base_revision\":7,\"mode\":\"add\",\"canvas\":{\"width\":30,\"height\":20}}",
            json);
    }

    [Fact]
    public void Deserialize_ReadsStableSnakeCaseWireFormat()
    {
        const string Json =
            "{\"schema_version\":\"1.0\",\"request_id\":\"request-004\",\"base_revision\":9,\"mode\":\"patch\",\"canvas\":{\"width\":40,\"height\":25}}";

        var plan = MapPlanJson.Deserialize(Json);

        Assert.Equal("1.0", plan.SchemaVersion);
        Assert.Equal("request-004", plan.RequestId);
        Assert.Equal(9, plan.BaseRevision);
        Assert.Equal(MapOperationMode.Patch, plan.Mode);
        Assert.Equal(new MapCanvas(40, 25), plan.Canvas);
    }

    [Fact]
    public void Deserialize_RejectsInvalidEnvelopeValuesWithStableIssues()
    {
        const string Json =
            "{\"schema_version\":\"2.0\",\"request_id\":\"   \",\"base_revision\":-1,\"mode\":\"add\",\"canvas\":{\"width\":0,\"height\":0}}";

        var exception = Assert.Throws<MapPlanValidationException>(
            () => MapPlanJson.Deserialize(Json));

        Assert.Equal(
        [
            ("unsupported_schema_version", "schema_version"),
            ("invalid_request_id", "request_id"),
            ("invalid_base_revision", "base_revision"),
            ("invalid_canvas_width", "canvas.width"),
            ("invalid_canvas_height", "canvas.height"),
        ],
            exception.Issues.Select(issue => (issue.Code, issue.Path)));
    }

    [Fact]
    public void Deserialize_RejectsMissingMode()
    {
        const string Json =
            "{\"schema_version\":\"1.0\",\"request_id\":\"request-005\",\"base_revision\":0,\"canvas\":{\"width\":40,\"height\":25}}";

        Assert.Throws<JsonException>(() => MapPlanJson.Deserialize(Json));
    }

    [Fact]
    public void Deserialize_RejectsMissingBaseRevision()
    {
        const string Json =
            "{\"schema_version\":\"1.0\",\"request_id\":\"request-006\",\"mode\":\"add\",\"canvas\":{\"width\":40,\"height\":25}}";

        Assert.Throws<JsonException>(() => MapPlanJson.Deserialize(Json));
    }

    [Fact]
    public void Deserialize_RejectsNullCanvasWithStableIssue()
    {
        const string Json =
            "{\"schema_version\":\"1.0\",\"request_id\":\"request-006\",\"base_revision\":0,\"mode\":\"add\",\"canvas\":null}";

        var exception = Assert.Throws<MapPlanValidationException>(
            () => MapPlanJson.Deserialize(Json));

        var issue = Assert.Single(exception.Issues);
        Assert.Equal(("invalid_canvas", "canvas"), (issue.Code, issue.Path));
    }
}
