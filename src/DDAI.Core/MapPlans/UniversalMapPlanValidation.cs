using DDAI.Core.Assets;
using DDAI.Core.MapPlans.Operations;

namespace DDAI.Core.MapPlans;

public sealed record UniversalMapPlanCatalog(
    long Revision,
    string Fingerprint,
    IReadOnlyList<AssetCatalogEntry> Entries,
    IReadOnlySet<string>? ActiveGeneratedAssetReferences = null);

public sealed record UniversalMapPlanCapabilities(
    string MapId,
    long MapRevision,
    IReadOnlyList<string> LevelIds,
    IReadOnlyDictionary<string, bool> RuntimeCertifiedOperations);

public sealed record UniversalMapPlanValidationResult(
    IReadOnlyList<MapPlanValidationIssue> Issues,
    IReadOnlyList<string> ResolvedOperationIds)
{
    public bool IsValid => Issues.Count == 0;
}

public static class UniversalMapPlanValidator
{
    public const double MinimumNonzeroMagnitude = 1e-300;
    public const int MaximumOperations = 500;
    public const int MaximumGeometryPoints = 10_000;
    private static readonly HashSet<int> SupportedLayers = [-400, -100, 100, 200, 300, 400, 700, 900];

    public static UniversalMapPlanValidationResult Validate(
        MapPlan plan,
        UniversalMapPlanCatalog catalog,
        UniversalMapPlanCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(capabilities);

        var issues = new List<MapPlanValidationIssue>();
        ValidateEnvelope(plan, catalog, capabilities, issues);

        var operations = plan.Operations;
        if (operations is null || operations.Count == 0)
        {
            issues.Add(Issue("invalid_operation_count", "operations", "At least one operation is required."));
            return Invalid(issues);
        }

        if (operations.Count > MaximumOperations)
        {
            issues.Add(Issue(
                "too_many_operations",
                "operations",
                $"A plan cannot contain more than {MaximumOperations} operations."));
            return Invalid(issues);
        }

        long totalPoints = 0;
        foreach (var operation in operations)
        {
            totalPoints += GeometryPointCount(operation);
            if (totalPoints > MaximumGeometryPoints)
            {
                issues.Add(Issue(
                    "too_many_geometry_points",
                    "operations",
                    $"A plan cannot contain more than {MaximumGeometryPoints} geometry points."));
                return Invalid(issues);
            }
        }

        var catalogByReference = catalog.Entries
            .GroupBy(entry => entry.AssetRef, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var knownLevels = capabilities.LevelIds.ToHashSet(StringComparer.Ordinal);
        var observedOperationIds = new HashSet<string>(StringComparer.Ordinal);
        var boundedOperations = new List<(MapOperation Operation, int Index)>();

        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            var path = $"operations[{index}]";
            if (operation is null)
            {
                issues.Add(Issue("invalid_operation", path, "Operation cannot be null."));
                continue;
            }

            ValidateOperationEnvelope(operation, path, knownLevels, issues);
            if (!observedOperationIds.Add(operation.OperationId))
            {
                issues.Add(Issue(
                    "duplicate_operation_id",
                    $"{path}.operation_id",
                    "Operation IDs must be unique."));
            }

            boundedOperations.Add((operation, index));
        }

        foreach (var item in boundedOperations)
        {
            ValidateGeometryAndOptions(
                item.Operation,
                $"operations[{item.Index}]",
                plan.Canvas,
                issues);
        }

        foreach (var item in boundedOperations)
        {
            ValidateAsset(
                item.Operation,
                $"operations[{item.Index}]",
                catalogByReference,
                catalog.ActiveGeneratedAssetReferences,
                issues);
        }

        foreach (var item in boundedOperations)
        {
            ValidateCapability(
                item.Operation,
                $"operations[{item.Index}]",
                capabilities.RuntimeCertifiedOperations,
                issues);
        }

        return issues.Count == 0
            ? new UniversalMapPlanValidationResult(
                [],
                operations
                    .Select((operation, index) => (operation, index))
                    .OrderBy(item => DependencyPhase(item.operation))
                    .ThenBy(item => item.index)
                    .Select(item => item.operation.OperationId)
                    .ToArray())
            : Invalid(issues);
    }

    private static void ValidateEnvelope(
        MapPlan plan,
        UniversalMapPlanCatalog catalog,
        UniversalMapPlanCapabilities capabilities,
        List<MapPlanValidationIssue> issues)
    {
        if (!string.Equals(plan.SchemaVersion, MapPlan.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            issues.Add(Issue("unsupported_schema_version", "schema_version", "Universal plans require schema 2.0."));
        }

        if (!SafeIdentifier.IsSafe(plan.RequestId))
        {
            issues.Add(Issue("invalid_request_id", "request_id", "Request ID is unsafe."));
        }

        if (!SafeIdentifier.IsSafe(plan.ExpectedMapId))
        {
            issues.Add(Issue("invalid_map_id", "expected_map_id", "Expected map ID is unsafe."));
        }
        else if (!string.Equals(plan.ExpectedMapId, capabilities.MapId, StringComparison.Ordinal))
        {
            issues.Add(Issue("map_id_mismatch", "expected_map_id", "The open map does not match the plan."));
        }

        if (plan.BaseRevision < 0)
        {
            issues.Add(Issue("invalid_base_revision", "base_revision", "Base revision cannot be negative."));
        }
        else if (plan.BaseRevision != capabilities.MapRevision)
        {
            issues.Add(Issue("map_revision_mismatch", "base_revision", "The map revision does not match the plan."));
        }

        if (plan.ExpectedCatalogRevision is null or < 0)
        {
            issues.Add(Issue(
                "invalid_catalog_revision",
                "expected_catalog_revision",
                "Expected catalog revision must be nonnegative."));
        }
        else if (plan.ExpectedCatalogRevision != catalog.Revision)
        {
            issues.Add(Issue(
                "catalog_revision_mismatch",
                "expected_catalog_revision",
                "The catalog revision does not match the plan."));
        }

        if (plan.Mode is not MapOperationMode.Add and not MapOperationMode.Patch)
        {
            issues.Add(Issue("unsupported_mode", "mode", "Universal plans support add or patch mode."));
        }

        if (plan.CoordinateSystem is null || !Enum.IsDefined(plan.CoordinateSystem.Value))
        {
            issues.Add(Issue(
                "invalid_coordinate_system",
                "coordinate_system",
                "A supported coordinate system is required."));
        }

        if (plan.Canvas is null)
        {
            issues.Add(Issue("invalid_canvas", "canvas", "Canvas is required."));
        }
        else
        {
            if (plan.Canvas.Width <= 0)
            {
                issues.Add(Issue("invalid_canvas_width", "canvas.width", "Canvas width must be positive."));
            }

            if (plan.Canvas.Height <= 0)
            {
                issues.Add(Issue("invalid_canvas_height", "canvas.height", "Canvas height must be positive."));
            }
        }
    }

    private static void ValidateOperationEnvelope(
        MapOperation operation,
        string path,
        IReadOnlySet<string> knownLevels,
        List<MapPlanValidationIssue> issues)
    {
        if (!SafeIdentifier.IsSafe(operation.OperationId))
        {
            issues.Add(Issue("invalid_operation_id", $"{path}.operation_id", "Operation ID is unsafe."));
        }

        if (!SafeIdentifier.IsSafe(operation.LevelId))
        {
            issues.Add(Issue("invalid_level_id", $"{path}.level_id", "Level ID is unsafe."));
        }
        else if (!knownLevels.Contains(operation.LevelId))
        {
            issues.Add(Issue("unknown_level", $"{path}.level_id", "The level is not available."));
        }
    }

    private static void ValidateGeometryAndOptions(
        MapOperation operation,
        string path,
        MapCanvas? canvas,
        List<MapPlanValidationIssue> issues)
    {
        switch (operation)
        {
            case TerrainStrokeOperation value:
                ValidatePolyline(value.Path, $"{path}.path", canvas, issues);
                ValidatePositive(value.Width, $"{path}.width", "invalid_width", issues);
                ValidateUnit(value.Strength, $"{path}.strength", "invalid_strength", issues);
                break;
            case PatternRegionOperation value:
                ValidatePolygon(value.Region, $"{path}.region", canvas, issues);
                ValidateRotation(value.RotationDegrees, path, issues);
                ValidateLayer(value.Layer, path, issues);
                break;
            case ColorablePatternRegionOperation value:
                ValidatePolygon(value.Region, $"{path}.region", canvas, issues);
                ValidateColor(value.ColorRgba, $"{path}.color_rgba", issues);
                ValidateRotation(value.RotationDegrees, path, issues);
                ValidateLayer(value.Layer, path, issues);
                break;
            case CaveRegionOperation value:
                ValidatePolygon(value.Region, $"{path}.region", canvas, issues);
                ValidateColor(value.FloorColorRgba, $"{path}.floor_color_rgba", issues);
                ValidateColor(value.WallColorRgba, $"{path}.wall_color_rgba", issues);
                break;
            case RoofRegionOperation value:
                ValidatePolygon(value.Region, $"{path}.region", canvas, issues);
                ValidatePositive(value.Width, $"{path}.width", "invalid_width", issues);
                ValidateUnit(value.Shade, $"{path}.shade", "invalid_shade", issues);
                break;
            case ObjectPlacementOperation value:
                ValidatePoint(value.Position, $"{path}.position", canvas, issues);
                ValidateRotation(value.RotationDegrees, path, issues);
                ValidatePositive(value.Scale, $"{path}.scale", "invalid_scale", issues);
                ValidateLayer(value.Layer, path, issues);
                if (!Enum.IsDefined(value.Sorting))
                {
                    issues.Add(Issue("invalid_sorting", $"{path}.sorting", "Sorting mode is unsupported."));
                }
                if (value.CustomColorRgba is not null)
                {
                    ValidateColor(value.CustomColorRgba, $"{path}.custom_color_rgba", issues);
                }
                break;
            case WallPolylineOperation value:
                ValidatePolyline(value.Path, $"{path}.path", canvas, issues);
                ValidateColor(value.ColorRgba, $"{path}.color_rgba", issues);
                break;
            case MaterialStrokeOperation value:
                ValidatePolyline(value.Path, $"{path}.path", canvas, issues);
                ValidatePositive(value.Width, $"{path}.width", "invalid_width", issues);
                ValidateUnit(value.Smoothness, $"{path}.smoothness", "invalid_smoothness", issues);
                ValidateLayer(value.Layer, path, issues);
                break;
            case PortalPlacementOperation value:
                ValidatePoint(value.Position, $"{path}.position", canvas, issues);
                ValidateRotation(value.RotationDegrees, path, issues);
                if (value.WallOperationId is not null && !SafeIdentifier.IsSafe(value.WallOperationId))
                {
                    issues.Add(Issue(
                        "invalid_wall_operation_id",
                        $"{path}.wall_operation_id",
                        "Wall operation ID is unsafe."));
                }
                break;
            case PathPolylineOperation value:
                ValidatePolyline(value.Path, $"{path}.path", canvas, issues);
                ValidatePositive(value.Width, $"{path}.width", "invalid_width", issues);
                ValidateUnit(value.Smoothness, $"{path}.smoothness", "invalid_smoothness", issues);
                ValidateLayer(value.Layer, path, issues);
                if (!Enum.IsDefined(value.Sorting))
                {
                    issues.Add(Issue("invalid_sorting", $"{path}.sorting", "Sorting mode is unsupported."));
                }
                break;
            case LightPlacementOperation value:
                ValidatePoint(value.Position, $"{path}.position", canvas, issues);
                ValidatePositive(value.Range, $"{path}.range", "invalid_range", issues);
                ValidatePositive(value.Intensity, $"{path}.intensity", "invalid_intensity", issues);
                ValidateColor(value.ColorRgba, $"{path}.color_rgba", issues);
                break;
            case SimpleTileRegionOperation value:
                ValidatePolygon(value.Region, $"{path}.region", canvas, issues);
                ValidateLayer(value.Layer, path, issues);
                break;
            case SmartTileRegionOperation value:
                ValidatePolygon(value.Region, $"{path}.region", canvas, issues);
                ValidateLayer(value.Layer, path, issues);
                break;
            case SmartTileDoubleRegionOperation value:
                ValidatePolygon(value.Region, $"{path}.region", canvas, issues);
                ValidateLayer(value.Layer, path, issues);
                break;
        }
    }

    private static void ValidateAsset(
        MapOperation operation,
        string path,
        IReadOnlyDictionary<string, AssetCatalogEntry> catalogByReference,
        IReadOnlySet<string>? activeGeneratedAssetReferences,
        List<MapPlanValidationIssue> issues)
    {
        var assetRef = AssetRef(operation);
        if (string.IsNullOrWhiteSpace(assetRef) || !catalogByReference.TryGetValue(assetRef, out var entry))
        {
            issues.Add(Issue("asset_not_found", $"{path}.asset_ref", "The asset is not active in the catalog."));
            return;
        }

        var expectedCategory = Category(operation);
        if (!string.Equals(entry.Category, expectedCategory, StringComparison.Ordinal))
        {
            issues.Add(Issue(
                "asset_category_mismatch",
                $"{path}.asset_ref",
                $"The asset must belong to category '{expectedCategory}'."));
        }

        if (entry.Generated &&
            (activeGeneratedAssetReferences is null ||
             !activeGeneratedAssetReferences.Contains(entry.AssetRef)))
        {
            issues.Add(Issue(
                "generated_asset_inactive",
                $"{path}.asset_ref",
                "The generated asset is staged but not active in the live catalog."));
        }
    }

    private static void ValidateCapability(
        MapOperation operation,
        string path,
        IReadOnlyDictionary<string, bool> capabilities,
        List<MapPlanValidationIssue> issues)
    {
        var type = OperationType(operation);
        if (!capabilities.TryGetValue(type, out var certified) || !certified)
        {
            issues.Add(Issue(
                "runtime_not_certified",
                $"{path}.operation_type",
                $"The runtime has not certified '{type}'."));
        }
    }

    private static void ValidatePolyline(
        GridPolyline polyline,
        string path,
        MapCanvas? canvas,
        List<MapPlanValidationIssue> issues)
    {
        if (polyline?.Points is null || polyline.Points.Count < 2)
        {
            issues.Add(Issue("invalid_polyline", path, "A polyline requires at least two points."));
            return;
        }

        ValidatePoints(polyline.Points, path, canvas, issues);
    }

    private static void ValidatePolygon(
        GridPolygon polygon,
        string path,
        MapCanvas? canvas,
        List<MapPlanValidationIssue> issues)
    {
        if (polygon?.Points is null || polygon.Points.Count < 3 ||
            polygon.Points.Distinct().Take(3).Count() < 3)
        {
            issues.Add(Issue("invalid_polygon", path, "A polygon requires at least three unique points."));
            return;
        }

        ValidatePoints(polygon.Points, path, canvas, issues);
    }

    private static void ValidatePoints(
        IReadOnlyList<GridPoint> points,
        string path,
        MapCanvas? canvas,
        List<MapPlanValidationIssue> issues)
    {
        var invalidCoordinate = false;
        var outOfBounds = false;
        foreach (var point in points)
        {
            if (point is null)
            {
                invalidCoordinate = true;
                continue;
            }

            var finiteX = double.IsFinite(point.X) && IsRepresentableMagnitude(point.X);
            var finiteY = double.IsFinite(point.Y) && IsRepresentableMagnitude(point.Y);
            if (!finiteX || !finiteY)
            {
                invalidCoordinate = true;
            }

            if (canvas is not null &&
                (finiteX && (point.X < 0 || point.X > canvas.Width) ||
                 finiteY && (point.Y < 0 || point.Y > canvas.Height)))
            {
                outOfBounds = true;
            }
        }

        if (invalidCoordinate)
        {
            issues.Add(Issue("invalid_coordinate", path, "Geometry coordinates must be finite and preserve their value in the Dungeondraft runtime."));
        }

        if (outOfBounds)
        {
            issues.Add(Issue("point_out_of_bounds", path, "Geometry exceeds the declared canvas."));
        }
    }

    private static void ValidatePoint(
        GridPoint point,
        string path,
        MapCanvas? canvas,
        List<MapPlanValidationIssue> issues) =>
        ValidatePoints([point], path, canvas, issues);

    private static void ValidateRotation(double value, string path, List<MapPlanValidationIssue> issues)
    {
        if (!double.IsFinite(value) || !IsRepresentableMagnitude(value) || value is < -360 or > 360)
        {
            issues.Add(Issue("invalid_rotation", $"{path}.rotation_degrees", "Rotation must be between -360 and 360."));
        }
    }

    private static void ValidatePositive(
        double value,
        string path,
        string code,
        List<MapPlanValidationIssue> issues)
    {
        if (!double.IsFinite(value) || value < MinimumNonzeroMagnitude)
        {
            issues.Add(Issue(code, path, "The value must be finite and positive."));
        }
    }

    private static void ValidateUnit(
        double value,
        string path,
        string code,
        List<MapPlanValidationIssue> issues)
    {
        if (!double.IsFinite(value) || !IsRepresentableMagnitude(value) || value is < 0 or > 1)
        {
            issues.Add(Issue(code, path, "The value must be between zero and one."));
        }
    }

    private static bool IsRepresentableMagnitude(double value) =>
        value == 0 || Math.Abs(value) >= MinimumNonzeroMagnitude;

    private static void ValidateLayer(int value, string path, List<MapPlanValidationIssue> issues)
    {
        if (!SupportedLayers.Contains(value))
        {
            issues.Add(Issue("invalid_layer", $"{path}.layer", "Layer is outside the supported range."));
        }
    }

    private static void ValidateColor(string? value, string path, List<MapPlanValidationIssue> issues)
    {
        if (value is null || value.Length != 9 || value[0] != '#' ||
            value.AsSpan(1).ContainsAnyExcept("0123456789abcdef"))
        {
            issues.Add(Issue("invalid_color", path, "Color must be normalized lowercase #rrggbbaa."));
        }
    }

    private static int GeometryPointCount(MapOperation operation) => operation switch
    {
        TerrainStrokeOperation value => value.Path?.Points?.Count ?? 0,
        PatternRegionOperation value => value.Region?.Points?.Count ?? 0,
        ColorablePatternRegionOperation value => value.Region?.Points?.Count ?? 0,
        CaveRegionOperation value => value.Region?.Points?.Count ?? 0,
        RoofRegionOperation value => value.Region?.Points?.Count ?? 0,
        ObjectPlacementOperation => 1,
        WallPolylineOperation value => value.Path?.Points?.Count ?? 0,
        MaterialStrokeOperation value => value.Path?.Points?.Count ?? 0,
        PortalPlacementOperation => 1,
        PathPolylineOperation value => value.Path?.Points?.Count ?? 0,
        LightPlacementOperation => 1,
        SimpleTileRegionOperation value => value.Region?.Points?.Count ?? 0,
        SmartTileRegionOperation value => value.Region?.Points?.Count ?? 0,
        SmartTileDoubleRegionOperation value => value.Region?.Points?.Count ?? 0,
        _ => 0,
    };

    private static int DependencyPhase(MapOperation operation) => operation switch
    {
        TerrainStrokeOperation or CaveRegionOperation => 1,
        PatternRegionOperation or ColorablePatternRegionOperation or RoofRegionOperation or
            SimpleTileRegionOperation or SmartTileRegionOperation or SmartTileDoubleRegionOperation => 2,
        WallPolylineOperation => 3,
        PortalPlacementOperation => 4,
        PathPolylineOperation or MaterialStrokeOperation => 5,
        ObjectPlacementOperation => 6,
        LightPlacementOperation => 7,
        _ => int.MaxValue,
    };

    internal static string OperationType(MapOperation operation) => operation switch
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
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    internal static string Category(MapOperation operation) => operation switch
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
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

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
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static UniversalMapPlanValidationResult Invalid(IReadOnlyList<MapPlanValidationIssue> issues) =>
        new(issues, []);

    private static MapPlanValidationIssue Issue(string code, string path, string message) =>
        new(code, path, message);
}
