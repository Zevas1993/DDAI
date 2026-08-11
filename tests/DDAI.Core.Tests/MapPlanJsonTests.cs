using DDAI.Core.MapPlans;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DDAI.Core.Tests;

public sealed class MapPlanJsonTests
{
    [Fact]
    public void Serialize_UsesStableSnakeCaseWireFormat()
    {
        var plan = new MapPlan
        {
            SchemaVersion = MapPlan.LegacySchemaVersion,
            RequestId = "request-003",
            BaseRevision = 7,
            Mode = MapOperationMode.Add,
            Canvas = new MapCanvas(30, 20),
            Rooms = [new MapRoom("room-entrance", 8, 7, 10, 8)],
        };

        var json = MapPlanJson.Serialize(plan);

        Assert.Equal(
            "{\"schema_version\":\"1.0\",\"request_id\":\"request-003\",\"base_revision\":7,\"mode\":\"add\",\"canvas\":{\"width\":30,\"height\":20},\"rooms\":[{\"id\":\"room-entrance\",\"x\":8,\"y\":7,\"width\":10,\"height\":8}]}",
            json);
    }

    [Fact]
    public void Serialize_DefaultOptionsUseTheSameLowercaseMode()
    {
        var plan = ValidPlan();

        var json = JsonSerializer.Serialize(plan);

        Assert.Contains("\"mode\":\"add\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"mode\":\"Add\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_LegacyEnvelopeWithoutRoomsDefaultsToEmpty()
    {
        const string Json =
            "{\"schema_version\":\"1.0\",\"request_id\":\"request-legacy\",\"base_revision\":0,\"mode\":\"add\",\"canvas\":{\"width\":40,\"height\":25}}";

        var plan = MapPlanJson.Deserialize(Json);

        Assert.Empty(plan.Rooms);
    }

    [Fact]
    public void Fingerprint_UsesLanguageNeutralUtf8Framing()
    {
        var plan = ValidPlan() with
        {
            RequestId = "room-é\"",
            Rooms = [new MapRoom("café\"", 8, 7, 10, 8)],
        };
        const string ExpectedInput =
            "schema_version=3:1.0\n" +
            "request_id=8:room-é\"\n" +
            "base_revision=0\n" +
            "mode=3:add\n" +
            "canvas_width=40\n" +
            "canvas_height=30\n" +
            "rooms_count=1\n" +
            "room[0].id=6:café\"\n" +
            "room[0].x=8\n" +
            "room[0].y=7\n" +
            "room[0].width=10\n" +
            "room[0].height=8\n";
        var expected = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(ExpectedInput))).ToLowerInvariant();

        Assert.Equal(ExpectedInput, MapPlanJson.FingerprintInput(plan));
        Assert.Equal(expected, MapPlanJson.Fingerprint(plan));
        Assert.Matches("^[0-9a-f]{64}$", MapPlanJson.Fingerprint(plan));
    }

    [Fact]
    public void SerializeToElement_UsesTheCanonicalPlanWireShape()
    {
        var element = MapPlanJson.SerializeToElement(ValidPlan());

        Assert.Equal(MapPlanJson.Serialize(ValidPlan()), element.GetRawText());
    }

    [Fact]
    public void Deserialize_RejectsNonCanonicalModeCasing()
    {
        const string Json =
            "{\"schema_version\":\"1.0\",\"request_id\":\"request-mode\",\"base_revision\":0,\"mode\":\"Add\",\"canvas\":{\"width\":40,\"height\":25}}";

        Assert.Throws<JsonException>(() => MapPlanJson.Deserialize(Json));
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
            "{\"schema_version\":\"3.0\",\"request_id\":\"   \",\"base_revision\":-1,\"mode\":\"add\",\"canvas\":{\"width\":0,\"height\":0}}";

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

    private static MapPlan ValidPlan() => new()
    {
        SchemaVersion = MapPlan.LegacySchemaVersion,
        RequestId = "room-job-001",
        BaseRevision = 0,
        Mode = MapOperationMode.Add,
        Canvas = new MapCanvas(40, 30),
        Rooms = [new MapRoom("room-entrance", 8, 7, 10, 8)],
    };
}
