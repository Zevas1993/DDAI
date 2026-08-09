using DDAI.Core.MapPlans;

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
}
