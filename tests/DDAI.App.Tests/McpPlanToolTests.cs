using System.Reflection;
using System.Text.Json;
using DDAI.App;
using DDAI.Core.Assets;
using DDAI.Core.Mailbox;
using DDAI.Core.MapPlans;
using DDAI.Core.MapPlans.Operations;
using ModelContextProtocol.Server;

namespace DDAI.App.Tests;

public sealed class McpPlanToolTests
{
    [Theory]
    [InlineData("ValidatePlan", "ddai_validate_plan", true, false)]
    [InlineData("ApplyPlanAsync", "ddai_apply_plan", false, true)]
    public void PlanTools_DeclareAccurateSafetyMetadata(
        string methodName,
        string toolName,
        bool readOnly,
        bool destructive)
    {
        var method = typeof(DdaiTools).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        var attribute = Assert.Single(method!.GetCustomAttributes<McpServerToolAttribute>());

        Assert.Equal(toolName, attribute.Name);
        Assert.Equal(readOnly, attribute.ReadOnly);
        Assert.Equal(destructive, attribute.Destructive);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
    }

    [Fact]
    public async Task ValidatePlan_RoutesVersionTwoThroughUniversalLiveValidation()
    {
        using var sandbox = new TestDirectory();
        var mailbox = new AtomicMailbox(sandbox.Path);
        var legacy = new DdaiPlanService(mailbox, TimeProvider.System);
        var asset = new AssetCatalogEntry(
            "asset-Walls", "Walls", "Wall", new string('a', 64), "official", "Official", [], [], null, true, false);
        var context = new UniversalPlanRuntimeContext(
            new UniversalMapPlanCatalog(7, new string('c', 64), [asset], new HashSet<string>(StringComparer.Ordinal)),
            new UniversalMapPlanCapabilities(
                "map-session-001",
                42,
                ["level-0"],
                new Dictionary<string, bool>(StringComparer.Ordinal) { ["wall_polyline"] = true }));
        var universal = new DdaiUniversalPlanService(
            mailbox,
            TimeProvider.System,
            new FixedContextProvider(context));
        var plan = new MapPlan
        {
            SchemaVersion = MapPlan.CurrentSchemaVersion,
            RequestId = "mcp-universal-001",
            ExpectedMapId = "map-session-001",
            BaseRevision = 42,
            ExpectedCatalogRevision = 7,
            Mode = MapOperationMode.Add,
            CoordinateSystem = MapCoordinateSystem.Grid,
            Canvas = new MapCanvas(40, 30),
            Operations =
            [
                new WallPolylineOperation(
                    "wall-a",
                    "level-0",
                    "asset-Walls",
                    new GridPolyline([new GridPoint(1, 1), new GridPoint(8, 1)]),
                    false,
                    "#ffffffff"),
            ],
        };

        var json = await DdaiTools.ValidatePlan(
            plan,
            legacy,
            universal,
            new DdaiMcpRuntimeOptions(TimeSpan.FromSeconds(1)),
            CancellationToken.None);

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal("wall-a", document.RootElement.GetProperty("resolved_operation_ids")[0].GetString());
        Assert.Equal(MapPlanJson.Fingerprint(plan), document.RootElement.GetProperty("plan_fingerprint").GetString());
    }

    [Theory]
    [InlineData("{\"schema_version\":\"2.0\",\"request_id\":\"strict-001\",\"expected_map_id\":\"map-session-001\",\"base_revision\":42,\"expected_catalog_revision\":7,\"mode\":\"add\",\"coordinate_system\":\"grid\",\"canvas\":{\"width\":40,\"height\":30},\"operations\":[],\"unknown\":true}")]
    [InlineData("{\"schema_version\":\"2.0\",\"request_id\":\"strict-001\",\"request_id\":\"strict-002\",\"expected_map_id\":\"map-session-001\",\"base_revision\":42,\"expected_catalog_revision\":7,\"mode\":\"add\",\"coordinate_system\":\"grid\",\"canvas\":{\"width\":40,\"height\":30},\"operations\":[]}")]
    public void PlanToolFilter_RejectsUnknownAndDuplicateWireProperties(string json)
    {
        using var document = JsonDocument.Parse(json);
        var parameters = new ModelContextProtocol.Protocol.CallToolRequestParams
        {
            Name = "ddai_validate_plan",
            Arguments = new Dictionary<string, JsonElement> { ["plan"] = document.RootElement.Clone() },
        };

        Assert.ThrowsAny<JsonException>(() => McpStdioServer.ValidatePlanToolArguments(parameters));
    }

    [Fact]
    public void PlanToolSerializer_UsesTheCanonicalSnakeCaseWireVocabulary()
    {
        var plan = new MapPlan
        {
            SchemaVersion = MapPlan.CurrentSchemaVersion,
            RequestId = "schema-vocabulary-001",
            ExpectedMapId = "map-session-001",
            BaseRevision = 42,
            ExpectedCatalogRevision = 7,
            Mode = MapOperationMode.Add,
            CoordinateSystem = MapCoordinateSystem.Grid,
            Canvas = new MapCanvas(40, 30),
            Operations =
            [
                new WallPolylineOperation(
                    "wall-a",
                    "level-0",
                    "asset-Walls",
                    new GridPolyline([new GridPoint(1, 1), new GridPoint(8, 1)]),
                    false,
                    "#ffffffff"),
            ],
        };

        var json = JsonSerializer.Serialize(plan, McpStdioServer.CreatePlanToolJsonOptions());

        Assert.Contains("\"mode\":\"add\"", json, StringComparison.Ordinal);
        Assert.Contains("\"coordinate_system\":\"grid\"", json, StringComparison.Ordinal);
        Assert.Contains("\"asset_ref\":\"asset-Walls\"", json, StringComparison.Ordinal);
        Assert.Contains("\"color_rgba\":\"#ffffffff\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("assetRef", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanToolSerializer_AcceptsOutOfOrderOperationDiscriminator()
    {
        const string json = """
            {"schema_version":"2.0","request_id":"out-of-order-001","expected_map_id":"map-session-001","base_revision":42,"expected_catalog_revision":7,"mode":"add","coordinate_system":"grid","canvas":{"width":40,"height":30},"operations":[{"operation_id":"wall-a","operation_type":"wall_polyline","level_id":"level-0","asset_ref":"asset-Walls","path":{"points":[{"x":1,"y":1},{"x":8,"y":1}]},"closed":false,"color_rgba":"#ffffffff"}]}
            """;

        var plan = JsonSerializer.Deserialize<MapPlan>(json, McpStdioServer.CreatePlanToolJsonOptions());

        Assert.IsType<WallPolylineOperation>(Assert.Single(plan!.Operations!));
    }

    [Fact]
    public void PlanToolFilter_AllowsSemanticIssuesToReachStructuredValidation()
    {
        using var document = JsonDocument.Parse("""
            {"schema_version":"2.0","request_id":"semantic-001","expected_map_id":"map-session-001","base_revision":-1,"expected_catalog_revision":7,"mode":"add","coordinate_system":"grid","canvas":{"width":0,"height":30},"operations":[]}
            """);
        var parameters = new ModelContextProtocol.Protocol.CallToolRequestParams
        {
            Name = "ddai_validate_plan",
            Arguments = new Dictionary<string, JsonElement> { ["plan"] = document.RootElement.Clone() },
        };

        McpStdioServer.ValidatePlanToolArguments(parameters);
    }

    private sealed class FixedContextProvider(UniversalPlanRuntimeContext context) : IUniversalPlanContextProvider
    {
        public Task<UniversalPlanContextResult> GetAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(UniversalPlanContextResult.Success(context));
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ddai-mcp-plan-tool", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
