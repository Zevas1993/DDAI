using DDAI.Core.MapPlans;
using DDAI.Core.MapPlans.Operations;
using System.Globalization;
using System.Text.Json;

namespace DDAI.Core.Tests.MapPlans;

public sealed class UniversalMapPlanJsonTests
{
    [Fact]
    public void RoundTrip_PreservesEverySupportedOperationType()
    {
        var polygon = new GridPolygon(
        [
            new GridPoint(1.25, 2.5),
            new GridPoint(9.5, 2.5),
            new GridPoint(9.5, 8.75),
        ]);
        var line = new GridPolyline(
        [
            new GridPoint(1.25, 2.5),
            new GridPoint(5.5, 4.75),
            new GridPoint(9.5, 8.75),
        ]);
        var plan = new MapPlan
        {
            SchemaVersion = MapPlan.CurrentSchemaVersion,
            RequestId = "universal-job-001",
            ExpectedMapId = "map-session-001",
            BaseRevision = 42,
            ExpectedCatalogRevision = 73,
            Mode = MapOperationMode.Add,
            CoordinateSystem = MapCoordinateSystem.Grid,
            Canvas = new MapCanvas(40, 30),
            Operations =
            [
                new TerrainStrokeOperation("terrain", "level-0", "asset-terrain", line, 2.5, 0.75),
                new PatternRegionOperation("pattern", "level-0", "asset-pattern", polygon, 15, 2),
                new ColorablePatternRegionOperation("color-pattern", "level-0", "asset-color-pattern", polygon, "#aabbccdd", 30, 3),
                new CaveRegionOperation("cave", "level-0", "asset-cave", polygon, "#112233ff", "#445566ff"),
                new RoofRegionOperation("roof", "level-0", "asset-roof", polygon, 1.5, 0.4),
                new ObjectPlacementOperation("object", "level-0", "asset-object", new GridPoint(6.5, 7.25), 90, 1.25, 4, MapSortingMode.Over, true, false, "#fedcbaff"),
                new WallPolylineOperation("wall", "level-0", "asset-wall", line, true, "#778899ff"),
                new MaterialStrokeOperation("material", "level-0", "asset-material", line, 3.5, 0.6, 1),
                new PortalPlacementOperation("portal", "level-0", "asset-portal", new GridPoint(5.5, 4.75), "wall", 180, true),
                new PathPolylineOperation("path", "level-0", "asset-path", line, 1.75, 0.25, 2, MapSortingMode.Under, true, false, true),
                new LightPlacementOperation("light", "level-0", "asset-light", new GridPoint(8.5, 9.25), 6, 0.8, "#ffeeddff", true),
                new SimpleTileRegionOperation("simple-tile", "level-0", "asset-simple-tile", polygon, 0),
                new SmartTileRegionOperation("smart-tile", "level-0", "asset-smart-tile", polygon, 1),
                new SmartTileDoubleRegionOperation("double-smart-tile", "level-0", "asset-double-smart-tile", polygon, 2),
            ],
        };

        var json = MapPlanJson.Serialize(plan);
        var roundTrip = MapPlanJson.Deserialize(json);

        Assert.Equal("2.0", roundTrip.SchemaVersion);
        Assert.Equal("map-session-001", roundTrip.ExpectedMapId);
        Assert.Equal(73, roundTrip.ExpectedCatalogRevision);
        Assert.Equal(MapCoordinateSystem.Grid, roundTrip.CoordinateSystem);
        var operations = Assert.IsAssignableFrom<IReadOnlyList<MapOperation>>(roundTrip.Operations);
        Assert.Collection(
            operations,
            operation => Assert.IsType<TerrainStrokeOperation>(operation),
            operation => Assert.IsType<PatternRegionOperation>(operation),
            operation => Assert.IsType<ColorablePatternRegionOperation>(operation),
            operation => Assert.IsType<CaveRegionOperation>(operation),
            operation => Assert.IsType<RoofRegionOperation>(operation),
            operation => Assert.IsType<ObjectPlacementOperation>(operation),
            operation => Assert.IsType<WallPolylineOperation>(operation),
            operation => Assert.IsType<MaterialStrokeOperation>(operation),
            operation => Assert.IsType<PortalPlacementOperation>(operation),
            operation => Assert.IsType<PathPolylineOperation>(operation),
            operation => Assert.IsType<LightPlacementOperation>(operation),
            operation => Assert.IsType<SimpleTileRegionOperation>(operation),
            operation => Assert.IsType<SmartTileRegionOperation>(operation),
            operation => Assert.IsType<SmartTileDoubleRegionOperation>(operation));

        var objectPlacement = Assert.IsType<ObjectPlacementOperation>(operations[5]);
        Assert.Equal(90, objectPlacement.RotationDegrees);
        Assert.Equal(1.25, objectPlacement.Scale);
        Assert.Equal(4, objectPlacement.Layer);
        Assert.Equal(MapSortingMode.Over, objectPlacement.Sorting);
        Assert.True(objectPlacement.Shadow);
        Assert.False(objectPlacement.BlockLight);
        Assert.Equal("#fedcbaff", objectPlacement.CustomColorRgba);
    }

    [Fact]
    public void Serialize_UsesExactDeterministicV2WireShapeAcrossCultures()
    {
        var plan = ObjectPlan();
        const string Expected =
            "{\"schema_version\":\"2.0\",\"request_id\":\"universal-object-001\",\"expected_map_id\":\"map-session-001\",\"base_revision\":42,\"expected_catalog_revision\":73,\"mode\":\"add\",\"coordinate_system\":\"grid\",\"canvas\":{\"width\":40,\"height\":30},\"operations\":[{\"operation_type\":\"object_placement\",\"operation_id\":\"object\",\"level_id\":\"level-0\",\"asset_ref\":\"asset-object\",\"position\":{\"x\":6.5,\"y\":7.25},\"rotation_degrees\":90,\"scale\":1.25,\"layer\":4,\"sorting\":\"over\",\"shadow\":true,\"block_light\":false,\"custom_color_rgba\":\"#fedcbaff\"}]}";
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var french = MapPlanJson.Serialize(plan);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var turkish = MapPlanJson.Serialize(plan);

            Assert.Equal(Expected, french);
            Assert.Equal(Expected, turkish);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Theory]
    [InlineData("\"operation_id\":\"object\"", "\"unexpected\":true,\"operation_id\":\"object\"")]
    [InlineData("\"operation_type\":\"object_placement\",", "")]
    [InlineData("\"operation_type\":\"object_placement\",", "\"operation_type\":\"object_placement\",\"operation_type\":\"object_placement\",")]
    [InlineData("\"operation_type\":\"object_placement\"", "\"operation_type\":\"Object_Placement\"")]
    [InlineData("\"operation_type\":\"object_placement\"", "\"operation_type\":\"unknown_operation\"")]
    public void Deserialize_RejectsNonCanonicalOperationObjects(string oldText, string newText)
    {
        var canonicalJson = MapPlanJson.Serialize(ObjectPlan());
        var json = canonicalJson.Replace(
            oldText,
            newText,
            StringComparison.Ordinal);

        Assert.NotEqual(canonicalJson, json);
        Assert.Throws<JsonException>(() => MapPlanJson.Deserialize(json));
    }

    [Fact]
    public void Deserialize_AcceptsDiscriminatorInAnyObjectMemberOrder()
    {
        var canonical = MapPlanJson.Serialize(ObjectPlan());
        var reordered = canonical.Replace(
            "\"operation_type\":\"object_placement\",\"operation_id\":\"object\"",
            "\"operation_id\":\"object\",\"operation_type\":\"object_placement\"",
            StringComparison.Ordinal);
        Assert.NotEqual(canonical, reordered);

        var plan = MapPlanJson.Deserialize(reordered);

        Assert.IsType<ObjectPlacementOperation>(Assert.Single(plan.Operations!));
        Assert.Equal(canonical, MapPlanJson.Serialize(plan));
    }

    [Fact]
    public void Deserialize_RejectsMissingNestedCoordinateInsteadOfDefaultingItToZero()
    {
        var canonical = MapPlanJson.Serialize(ObjectPlan());
        var missingY = canonical.Replace(",\"y\":7.25", string.Empty, StringComparison.Ordinal);
        Assert.NotEqual(canonical, missingY);

        Assert.Throws<JsonException>(() => MapPlanJson.Deserialize(missingY));
    }

    [Fact]
    public void Fingerprint_FramesEveryObjectPlacementControl()
    {
        var baseline = ObjectPlan();
        var original = Assert.IsType<ObjectPlacementOperation>(Assert.Single(baseline.Operations!));
        MapPlan Changed(ObjectPlacementOperation operation) => baseline with { Operations = [operation] };

        var fingerprints = new HashSet<string>(StringComparer.Ordinal)
        {
            MapPlanJson.Fingerprint(baseline),
            MapPlanJson.Fingerprint(Changed(original with { AssetRef = "asset-object-2" })),
            MapPlanJson.Fingerprint(Changed(original with { Position = new GridPoint(6.75, 7.25) })),
            MapPlanJson.Fingerprint(Changed(original with { RotationDegrees = 91 })),
            MapPlanJson.Fingerprint(Changed(original with { Scale = 1.5 })),
            MapPlanJson.Fingerprint(Changed(original with { Layer = 5 })),
            MapPlanJson.Fingerprint(Changed(original with { Sorting = MapSortingMode.Under })),
            MapPlanJson.Fingerprint(Changed(original with { Shadow = false })),
            MapPlanJson.Fingerprint(Changed(original with { BlockLight = true })),
            MapPlanJson.Fingerprint(Changed(original with { CustomColorRgba = null })),
        };

        Assert.Equal(10, fingerprints.Count);
    }

    [Fact]
    public void LegacyV1Plan_SerializesToItsOriginalByteCompatibleShape()
    {
        var plan = new MapPlan
        {
            SchemaVersion = MapPlan.LegacySchemaVersion,
            RequestId = "room-job-001",
            BaseRevision = 0,
            Mode = MapOperationMode.Add,
            Canvas = new MapCanvas(40, 30),
            Rooms = [new MapRoom("room-entrance", 8, 7, 10, 8)],
        };
        const string Expected =
            "{\"schema_version\":\"1.0\",\"request_id\":\"room-job-001\",\"base_revision\":0,\"mode\":\"add\",\"canvas\":{\"width\":40,\"height\":30},\"rooms\":[{\"id\":\"room-entrance\",\"x\":8,\"y\":7,\"width\":10,\"height\":8}]}";

        var serialized = MapPlanJson.Serialize(plan);
        var roundTrip = MapPlanJson.Deserialize(serialized);

        Assert.Equal(Expected, serialized);
        Assert.Equal(Expected, MapPlanJson.Serialize(roundTrip));
    }

    [Fact]
    public void LegacyV1Plan_GenericSerializationDoesNotAddUniversalOperations()
    {
        var plan = new MapPlan
        {
            SchemaVersion = MapPlan.LegacySchemaVersion,
            RequestId = "room-job-generic",
            BaseRevision = 0,
            Mode = MapOperationMode.Add,
            Canvas = new MapCanvas(40, 30),
            Rooms = [],
        };
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

        var json = JsonSerializer.Serialize(plan, options);

        Assert.DoesNotContain("\"operations\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Canonicalization_RejectsNonFiniteNumbers(double value)
    {
        var plan = ObjectPlan();
        var operation = Assert.IsType<ObjectPlacementOperation>(Assert.Single(plan.Operations!));
        var invalid = plan with { Operations = [operation with { Scale = value }] };

        Assert.Throws<JsonException>(() => MapPlanJson.Serialize(invalid));
        Assert.Throws<JsonException>(() => MapPlanJson.Fingerprint(invalid));
    }

    private static MapPlan ObjectPlan() => new()
    {
        SchemaVersion = MapPlan.CurrentSchemaVersion,
        RequestId = "universal-object-001",
        ExpectedMapId = "map-session-001",
        BaseRevision = 42,
        ExpectedCatalogRevision = 73,
        Mode = MapOperationMode.Add,
        CoordinateSystem = MapCoordinateSystem.Grid,
        Canvas = new MapCanvas(40, 30),
        Operations =
        [
            new ObjectPlacementOperation(
                "object",
                "level-0",
                "asset-object",
                new GridPoint(6.5, 7.25),
                90,
                1.25,
                4,
                MapSortingMode.Over,
                true,
                false,
                "#fedcbaff"),
        ],
    };
}
