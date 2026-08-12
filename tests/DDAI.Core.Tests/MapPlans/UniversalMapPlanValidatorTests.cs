using DDAI.Core.Assets;
using DDAI.Core.MapPlans;
using DDAI.Core.MapPlans.Operations;

namespace DDAI.Core.Tests.MapPlans;

public sealed class UniversalMapPlanValidatorTests
{
    [Fact]
    public void Validate_AcceptsEveryCategoryAndReturnsDependencyOrderedOperationIds()
    {
        var operations = AllOperations();
        var plan = ValidPlan() with { Operations = operations };

        var result = UniversalMapPlanValidator.Validate(plan, CatalogFor(operations), CapabilitiesFor(operations));

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
        Assert.Equal(
        [
            "terrain", "cave",
            "pattern", "color-pattern", "roof", "simple-tile", "smart-tile", "double-smart-tile",
            "wall",
            "portal",
            "material", "path",
            "object",
            "light",
        ],
            result.ResolvedOperationIds);
    }

    [Fact]
    public void Validate_ReturnsAllIssuesInSchemaEnvelopeOperationGeometryAssetCapabilityOrder()
    {
        var invalidObject = new ObjectPlacementOperation(
            "../unsafe",
            "missing-level",
            "missing-asset",
            new GridPoint(double.NaN, 99),
            361,
            0,
            int.MaxValue,
            (MapSortingMode)99,
            true,
            false,
            "#ABCDEF00");
        var plan = new MapPlan
        {
            SchemaVersion = "3.0",
            RequestId = "../unsafe",
            ExpectedMapId = "wrong-map",
            BaseRevision = -1,
            ExpectedCatalogRevision = -2,
            Mode = MapOperationMode.Replace,
            CoordinateSystem = null,
            Canvas = new MapCanvas(0, -1),
            Operations = [invalidObject, invalidObject],
        };
        var catalog = new UniversalMapPlanCatalog(7, "catalog-fingerprint", []);
        var capabilities = new UniversalMapPlanCapabilities(
            "map-session-001",
            42,
            ["level-0"],
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["object_placement"] = false,
            });

        var result = UniversalMapPlanValidator.Validate(plan, catalog, capabilities);

        Assert.False(result.IsValid);
        Assert.Equal(
        [
            "unsupported_schema_version",
            "invalid_request_id",
            "map_id_mismatch",
            "invalid_base_revision",
            "invalid_catalog_revision",
            "unsupported_mode",
            "invalid_coordinate_system",
            "invalid_canvas_width",
            "invalid_canvas_height",
            "invalid_operation_id",
            "unknown_level",
            "invalid_operation_id",
            "unknown_level",
            "duplicate_operation_id",
            "invalid_coordinate",
            "point_out_of_bounds",
            "invalid_rotation",
            "invalid_scale",
            "invalid_layer",
            "invalid_sorting",
            "invalid_color",
            "invalid_coordinate",
            "point_out_of_bounds",
            "invalid_rotation",
            "invalid_scale",
            "invalid_layer",
            "invalid_sorting",
            "invalid_color",
            "asset_not_found",
            "asset_not_found",
            "runtime_not_certified",
            "runtime_not_certified",
        ],
            result.Issues.Select(issue => issue.Code));
        Assert.Empty(result.ResolvedOperationIds);
    }

    [Fact]
    public void Validate_RejectsOperationAndPointCapsBeforeEnumeratingUnboundedInput()
    {
        var tooManyOperations = Enumerable.Range(0, 501)
            .Select(index => (MapOperation)new ObjectPlacementOperation(
                $"object-{index}",
                "level-0",
                "asset-Objects",
                new GridPoint(1, 1),
                0,
                1,
                0,
                MapSortingMode.Over,
                true,
                false,
                null))
            .ToArray();
        var tooManyPoints = Enumerable.Range(0, 10_001)
            .Select(index => new GridPoint(index % 40, index % 30))
            .ToArray();
        var pointPlan = ValidPlan() with
        {
            Operations =
            [
                new TerrainStrokeOperation(
                    "terrain",
                    "level-0",
                    "asset-Terrain",
                    new GridPolyline(tooManyPoints),
                    1,
                    1),
            ],
        };

        var operationResult = UniversalMapPlanValidator.Validate(
            ValidPlan() with { Operations = tooManyOperations },
            CatalogFor(tooManyOperations.Take(1)),
            CapabilitiesFor(tooManyOperations.Take(1)));
        var pointResult = UniversalMapPlanValidator.Validate(
            pointPlan,
            CatalogFor(pointPlan.Operations!),
            CapabilitiesFor(pointPlan.Operations!));

        Assert.Contains(operationResult.Issues, issue => issue.Code == "too_many_operations");
        Assert.Contains(pointResult.Issues, issue => issue.Code == "too_many_geometry_points");
    }

    [Fact]
    public void Validate_AcceptsExactlyMaximumOperationsAndGeometryPoints()
    {
        var operations = Enumerable.Range(0, UniversalMapPlanValidator.MaximumOperations)
            .Select(index => (MapOperation)new ObjectPlacementOperation(
                $"object-{index}",
                "level-0",
                "asset-Objects",
                new GridPoint(index % 40, index % 30),
                0,
                1,
                100,
                MapSortingMode.Over,
                true,
                false,
                null))
            .ToArray();
        var points = Enumerable.Range(0, UniversalMapPlanValidator.MaximumGeometryPoints)
            .Select(index => new GridPoint(index % 40, index % 30))
            .ToArray();
        var terrain = new TerrainStrokeOperation(
            "terrain",
            "level-0",
            "asset-Terrain",
            new GridPolyline(points),
            1,
            1);

        var operationResult = UniversalMapPlanValidator.Validate(
            ValidPlan() with { Operations = operations },
            CatalogFor(operations),
            CapabilitiesFor(operations));
        var pointResult = UniversalMapPlanValidator.Validate(
            ValidPlan() with { Operations = [terrain] },
            CatalogFor([terrain]),
            CapabilitiesFor([terrain]));

        Assert.True(operationResult.IsValid);
        Assert.True(pointResult.IsValid);
    }

    [Fact]
    public void MigrateV1_ProducesEquivalentClosedWallWithoutChoosingAnAsset()
    {
        var legacy = new MapPlan
        {
            SchemaVersion = MapPlan.LegacySchemaVersion,
            RequestId = "legacy-room-001",
            BaseRevision = 4,
            Mode = MapOperationMode.Add,
            Canvas = new MapCanvas(40, 30),
            Rooms = [new MapRoom("room-a", 2, 3, 5, 7)],
        };

        var migrated = UniversalPlanMigration.MigrateV1(
            legacy,
            expectedMapId: "map-session-001",
            expectedCatalogRevision: 7,
            levelId: "level-0",
            wallAssetRef: "asset-Walls",
            wallColorRgba: "#aabbccdd");

        Assert.Equal(MapPlan.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Equal(legacy.RequestId, migrated.RequestId);
        Assert.Equal(legacy.BaseRevision, migrated.BaseRevision);
        Assert.Equal(legacy.Canvas, migrated.Canvas);
        var wall = Assert.IsType<WallPolylineOperation>(Assert.Single(migrated.Operations!));
        Assert.Equal("asset-Walls", wall.AssetRef);
        Assert.Equal("#aabbccdd", wall.ColorRgba);
        Assert.True(wall.Closed);
        Assert.Equal(
        [
            new GridPoint(2, 3),
            new GridPoint(7, 3),
            new GridPoint(7, 10),
            new GridPoint(2, 10),
        ],
            wall.Path.Points);
    }

    [Fact]
    public void Validate_RejectsCategoryConfusionAndGeneratedAssetThatIsNotActive()
    {
        var first = new ObjectPlacementOperation(
            "wrong-category",
            "level-0",
            "asset-wrong",
            new GridPoint(2, 2),
            0,
            1,
            100,
            MapSortingMode.Over,
            true,
            false,
            null);
        var second = first with { OperationId = "inactive-generated", AssetRef = "asset-generated" };
        var plan = ValidPlan() with { Operations = [first, second] };
        var catalog = new UniversalMapPlanCatalog(
            7,
            "catalog-fingerprint",
            [
                CatalogEntry("asset-wrong", "Walls", generated: false),
                CatalogEntry("asset-generated", "Objects", generated: true),
            ],
            ActiveGeneratedAssetReferences: new HashSet<string>(StringComparer.Ordinal));

        var result = UniversalMapPlanValidator.Validate(plan, catalog, CapabilitiesFor([first, second]));

        Assert.Equal(
            ["asset_category_mismatch", "generated_asset_inactive"],
            result.Issues.Select(issue => issue.Code));
    }

    [Fact]
    public void Validate_GeneratedAssetWithoutAffirmativeActiveSetFailsClosed()
    {
        var operation = new ObjectPlacementOperation(
            "generated",
            "level-0",
            "asset-generated",
            new GridPoint(2, 2),
            0,
            1,
            100,
            MapSortingMode.Over,
            true,
            false,
            null);
        var catalog = new UniversalMapPlanCatalog(
            7,
            "catalog-fingerprint",
            [CatalogEntry("asset-generated", "Objects", generated: true)]);

        var result = UniversalMapPlanValidator.Validate(
            ValidPlan() with { Operations = [operation] },
            catalog,
            CapabilitiesFor([operation]));

        Assert.Contains(result.Issues, issue => issue.Code == "generated_asset_inactive");
    }

    [Fact]
    public void Validate_DuplicateOperationStillReportsItsGeometryAssetAndCapabilityIssues()
    {
        var first = new ObjectPlacementOperation(
            "duplicate",
            "level-0",
            "asset-Objects",
            new GridPoint(2, 2),
            0,
            1,
            100,
            MapSortingMode.Over,
            true,
            false,
            null);
        var second = new LightPlacementOperation(
            "duplicate",
            "level-0",
            "missing-light",
            new GridPoint(double.NaN, 2),
            5,
            0,
            "#ffffffff",
            true);
        var capabilities = CapabilitiesFor([first, second]);
        var runtimeOperations = capabilities.RuntimeCertifiedOperations.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal);
        runtimeOperations["light_placement"] = false;
        capabilities = capabilities with { RuntimeCertifiedOperations = runtimeOperations };

        var result = UniversalMapPlanValidator.Validate(
            ValidPlan() with { Operations = [first, second] },
            CatalogFor([first]),
            capabilities);

        Assert.Equal(
            [
                "duplicate_operation_id",
                "invalid_coordinate",
                "invalid_intensity",
                "asset_not_found",
                "runtime_not_certified",
            ],
            result.Issues.Select(issue => issue.Code));
    }

    [Fact]
    public void Validate_NullCanvasAndNullOperationFailClosedWithoutThrowing()
    {
        var plan = ValidPlan() with
        {
            Canvas = null!,
            Operations = [null!],
        };

        var result = UniversalMapPlanValidator.Validate(
            plan,
            new UniversalMapPlanCatalog(7, "catalog-fingerprint", []),
            new UniversalMapPlanCapabilities(
                "map-session-001",
                42,
                ["level-0"],
                new Dictionary<string, bool>(StringComparer.Ordinal)));

        Assert.Equal(
            ["invalid_canvas", "invalid_operation"],
            result.Issues.Select(issue => issue.Code));
    }

    [Fact]
    public void Validate_RejectsDegeneratePolygonAndAcceptsExactCanvasBoundary()
    {
        var degenerate = new PatternRegionOperation(
            "pattern",
            "level-0",
            "asset-Patterns",
            new GridPolygon([new GridPoint(0, 0), new GridPoint(0, 0), new GridPoint(1, 1)]),
            0,
            100);
        var boundary = degenerate with
        {
            OperationId = "boundary",
            Region = new GridPolygon(
            [
                new GridPoint(0, 0),
                new GridPoint(40, 0),
                new GridPoint(40, 30),
            ]),
        };

        var invalid = UniversalMapPlanValidator.Validate(
            ValidPlan() with { Operations = [degenerate] },
            CatalogFor([degenerate]),
            CapabilitiesFor([degenerate]));
        var valid = UniversalMapPlanValidator.Validate(
            ValidPlan() with { Operations = [boundary] },
            CatalogFor([boundary]),
            CapabilitiesFor([boundary]));

        Assert.Contains(invalid.Issues, issue => issue.Code == "invalid_polygon");
        Assert.DoesNotContain(valid.Issues, issue => issue.Code is "invalid_polygon" or "point_out_of_bounds");
    }

    [Theory]
    [InlineData(-400)]
    [InlineData(-100)]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(300)]
    [InlineData(400)]
    [InlineData(700)]
    [InlineData(900)]
    public void Validate_AcceptsEveryDocumentedDungeondraftLayerId(int layer)
    {
        var operation = new ObjectPlacementOperation(
            "object",
            "level-0",
            "asset-Objects",
            new GridPoint(2, 2),
            0,
            1,
            layer,
            MapSortingMode.Over,
            true,
            false,
            null);

        var result = UniversalMapPlanValidator.Validate(
            ValidPlan() with { Operations = [operation] },
            CatalogFor([operation]),
            CapabilitiesFor([operation]));

        Assert.DoesNotContain(result.Issues, issue => issue.Code == "invalid_layer");
    }

    [Fact]
    public void Validate_AcceptsInclusiveNumericBoundariesAndRuntimeRepresentableSmallValues()
    {
        var line = new GridPolyline([new GridPoint(0, 0), new GridPoint(40, 30)]);
        MapOperation[] operations =
        [
            new ObjectPlacementOperation("negative-rotation", "level-0", "asset-Objects", new GridPoint(0, 0), -360, 1e-300, 100, MapSortingMode.Over, true, false, null),
            new ObjectPlacementOperation("positive-rotation", "level-0", "asset-Objects", new GridPoint(40, 30), 360, 1e-300, 900, MapSortingMode.Under, false, true, "#00000000"),
            new TerrainStrokeOperation("zero-unit", "level-0", "asset-Terrain", line, 1e-300, 0),
            new TerrainStrokeOperation("one-unit", "level-0", "asset-Terrain", line, 1e-300, 1),
            new LightPlacementOperation("light", "level-0", "asset-Lights", new GridPoint(1, 1), 1e-300, 1e-300, "#ffffffff", false),
        ];

        var result = UniversalMapPlanValidator.Validate(
            ValidPlan() with { Operations = operations },
            CatalogFor(operations),
            CapabilitiesFor(operations));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsNumbersThatUnderflowInTheTargetRuntime()
    {
        var operation = new WallPolylineOperation(
            "wall", "level-0", "asset-Walls",
            new GridPolyline([new GridPoint(double.Epsilon, 1), new GridPoint(2, 1)]),
            false, "#ffffffff");

        var result = UniversalMapPlanValidator.Validate(
            ValidPlan() with { Operations = [operation] },
            CatalogFor([operation]),
            CapabilitiesFor([operation]));

        Assert.Contains(result.Issues, issue => issue.Code == "invalid_coordinate");
    }

    [Fact]
    public void Validate_RejectsZeroAndNonFiniteNumericValuesAcrossOperationFamilies()
    {
        var line = new GridPolyline([new GridPoint(0, 0), new GridPoint(1, 1)]);
        MapOperation[] operations =
        [
            new ObjectPlacementOperation("object", "level-0", "asset-Objects", new GridPoint(double.PositiveInfinity, 0), double.NegativeInfinity, 0, 100, MapSortingMode.Over, true, false, null),
            new TerrainStrokeOperation("terrain", "level-0", "asset-Terrain", line, 0, double.PositiveInfinity),
            new MaterialStrokeOperation("material", "level-0", "asset-Materials", line, double.PositiveInfinity, double.NaN, 100),
            new LightPlacementOperation("light", "level-0", "asset-Lights", new GridPoint(1, 1), double.PositiveInfinity, 0, "#ffffffff", false),
        ];

        var result = UniversalMapPlanValidator.Validate(
            ValidPlan() with { Operations = operations },
            CatalogFor(operations),
            CapabilitiesFor(operations));

        Assert.Contains(result.Issues, issue => issue.Code == "invalid_coordinate");
        Assert.Contains(result.Issues, issue => issue.Code == "invalid_rotation");
        Assert.Contains(result.Issues, issue => issue.Code == "invalid_scale");
        Assert.Contains(result.Issues, issue => issue.Code == "invalid_width");
        Assert.Contains(result.Issues, issue => issue.Code == "invalid_strength");
        Assert.Contains(result.Issues, issue => issue.Code == "invalid_smoothness");
        Assert.Contains(result.Issues, issue => issue.Code == "invalid_range");
        Assert.Contains(result.Issues, issue => issue.Code == "invalid_intensity");
    }

    [Fact]
    public void MigrateV1_RejectsExtremeGeometryAndNonCanonicalExplicitStyle()
    {
        var extreme = new MapPlan
        {
            SchemaVersion = MapPlan.LegacySchemaVersion,
            RequestId = "legacy-extreme",
            BaseRevision = 0,
            Mode = MapOperationMode.Add,
            Canvas = new MapCanvas(int.MaxValue, int.MaxValue),
            Rooms = [new MapRoom("room", int.MaxValue, 0, 1, 1)],
        };

        Assert.Throws<MapPlanValidationException>(() => UniversalPlanMigration.MigrateV1(
            extreme,
            "map-session-001",
            7,
            "level-0",
            "asset-Walls",
            "#ffffffff"));
        Assert.Throws<MapPlanValidationException>(() => UniversalPlanMigration.MigrateV1(
            extreme with { Rooms = [new MapRoom("room", 0, 0, 1, 1)] },
            "map-session-001",
            7,
            "level-0",
            "asset-Walls",
            "#FFFFFFFF"));
    }

    private static MapPlan ValidPlan() => new()
    {
        SchemaVersion = MapPlan.CurrentSchemaVersion,
        RequestId = "universal-valid-001",
        ExpectedMapId = "map-session-001",
        BaseRevision = 42,
        ExpectedCatalogRevision = 7,
        Mode = MapOperationMode.Add,
        CoordinateSystem = MapCoordinateSystem.Grid,
        Canvas = new MapCanvas(40, 30),
        Operations = [AllOperations()[0]],
    };

    private static IReadOnlyList<MapOperation> AllOperations()
    {
        var polygon = new GridPolygon(
        [
            new GridPoint(1, 1),
            new GridPoint(8, 1),
            new GridPoint(8, 8),
        ]);
        var line = new GridPolyline([new GridPoint(1, 1), new GridPoint(8, 8)]);
        return
        [
            new TerrainStrokeOperation("terrain", "level-0", "asset-Terrain", line, 2, 0.5),
            new PatternRegionOperation("pattern", "level-0", "asset-Patterns", polygon, 0, 100),
            new ColorablePatternRegionOperation("color-pattern", "level-0", "asset-Patterns-Colorable", polygon, "#aabbccdd", 0, 100),
            new CaveRegionOperation("cave", "level-0", "asset-Caves", polygon, "#112233ff", "#445566ff"),
            new RoofRegionOperation("roof", "level-0", "asset-Roofs", polygon, 1, 0.5),
            new ObjectPlacementOperation("object", "level-0", "asset-Objects", new GridPoint(4, 4), 0, 1, 100, MapSortingMode.Over, true, false, null),
            new WallPolylineOperation("wall", "level-0", "asset-Walls", line, true, "#ffffffff"),
            new MaterialStrokeOperation("material", "level-0", "asset-Materials", line, 2, 0.5, 100),
            new PortalPlacementOperation("portal", "level-0", "asset-Portals", new GridPoint(4, 4), "wall", 0, true),
            new PathPolylineOperation("path", "level-0", "asset-Paths", line, 1, 0.5, 100, MapSortingMode.Under, false, false, false),
            new LightPlacementOperation("light", "level-0", "asset-Lights", new GridPoint(4, 4), 5, 1, "#ffffffff", true),
            new SimpleTileRegionOperation("simple-tile", "level-0", "asset-Simple-Tiles", polygon, 100),
            new SmartTileRegionOperation("smart-tile", "level-0", "asset-Smart-Tiles", polygon, 100),
            new SmartTileDoubleRegionOperation("double-smart-tile", "level-0", "asset-Smart-Tiles-Double", polygon, 100),
        ];
    }

    private static UniversalMapPlanCatalog CatalogFor(IEnumerable<MapOperation> operations)
    {
        var entries = operations
            .Select(operation => new AssetCatalogEntry(
                AssetRef(operation),
                Category(operation),
                operation.OperationId,
                new string('a', 64),
                "official",
                "Official",
                [],
                [],
                null,
                true,
                false))
            .DistinctBy(entry => entry.AssetRef, StringComparer.Ordinal)
            .ToArray();
        return new UniversalMapPlanCatalog(7, "catalog-fingerprint", entries);
    }

    private static AssetCatalogEntry CatalogEntry(string assetRef, string category, bool generated) => new(
        assetRef,
        category,
        assetRef,
        new string('a', 64),
        "official",
        "Official",
        [],
        [],
        null,
        true,
        generated);

    private static UniversalMapPlanCapabilities CapabilitiesFor(IEnumerable<MapOperation> operations) => new(
        "map-session-001",
        42,
        ["level-0"],
        operations
            .Select(OperationType)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(value => value, _ => true, StringComparer.Ordinal));

    private static string AssetRef(MapOperation operation) => operation switch
    {
        TerrainStrokeOperation value => value.AssetRef,
        PatternRegionOperation value => value.AssetRef,
        ColorablePatternRegionOperation value => value.AssetRef,
        CaveRegionOperation value => value.AssetRef,
        RoofRegionOperation value => value.AssetRef,
        ObjectPlacementOperation value => value.AssetRef,
        WallPolylineOperation value => value.AssetRef,
        MaterialStrokeOperation value => value.AssetRef,
        PortalPlacementOperation value => value.AssetRef,
        PathPolylineOperation value => value.AssetRef,
        LightPlacementOperation value => value.AssetRef,
        SimpleTileRegionOperation value => value.AssetRef,
        SmartTileRegionOperation value => value.AssetRef,
        SmartTileDoubleRegionOperation value => value.AssetRef,
        _ => throw new InvalidOperationException(),
    };

    private static string Category(MapOperation operation) => operation switch
    {
        TerrainStrokeOperation => "Terrain",
        PatternRegionOperation => "Patterns",
        ColorablePatternRegionOperation => "Patterns Colorable",
        CaveRegionOperation => "Caves",
        RoofRegionOperation => "Roofs",
        ObjectPlacementOperation => "Objects",
        WallPolylineOperation => "Walls",
        MaterialStrokeOperation => "Materials",
        PortalPlacementOperation => "Portals",
        PathPolylineOperation => "Paths",
        LightPlacementOperation => "Lights",
        SimpleTileRegionOperation => "Simple Tiles",
        SmartTileRegionOperation => "Smart Tiles",
        SmartTileDoubleRegionOperation => "Smart Tiles Double",
        _ => throw new InvalidOperationException(),
    };

    private static string OperationType(MapOperation operation) => operation switch
    {
        TerrainStrokeOperation => "terrain_stroke",
        PatternRegionOperation => "pattern_region",
        ColorablePatternRegionOperation => "colorable_pattern_region",
        CaveRegionOperation => "cave_region",
        RoofRegionOperation => "roof_region",
        ObjectPlacementOperation => "object_placement",
        WallPolylineOperation => "wall_polyline",
        MaterialStrokeOperation => "material_stroke",
        PortalPlacementOperation => "portal_placement",
        PathPolylineOperation => "path_polyline",
        LightPlacementOperation => "light_placement",
        SimpleTileRegionOperation => "simple_tile_region",
        SmartTileRegionOperation => "smart_tile_region",
        SmartTileDoubleRegionOperation => "smart_tile_double_region",
        _ => throw new InvalidOperationException(),
    };
}
