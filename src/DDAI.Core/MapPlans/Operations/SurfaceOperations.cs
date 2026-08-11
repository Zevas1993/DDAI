namespace DDAI.Core.MapPlans.Operations;

public sealed record TerrainStrokeOperation(
    string OperationId,
    string LevelId,
    string AssetRef,
    GridPolyline Path,
    double Width,
    double Strength) : MapOperation(OperationId, LevelId);

public sealed record PatternRegionOperation(
    string OperationId,
    string LevelId,
    string AssetRef,
    GridPolygon Region,
    double RotationDegrees,
    int Layer) : MapOperation(OperationId, LevelId);

public sealed record ColorablePatternRegionOperation(
    string OperationId,
    string LevelId,
    string AssetRef,
    GridPolygon Region,
    string ColorRgba,
    double RotationDegrees,
    int Layer) : MapOperation(OperationId, LevelId);

public sealed record CaveRegionOperation(
    string OperationId,
    string LevelId,
    string AssetRef,
    GridPolygon Region,
    string FloorColorRgba,
    string WallColorRgba) : MapOperation(OperationId, LevelId);

public sealed record RoofRegionOperation(
    string OperationId,
    string LevelId,
    string AssetRef,
    GridPolygon Region,
    double Width,
    double Shade) : MapOperation(OperationId, LevelId);

public sealed record SimpleTileRegionOperation(
    string OperationId,
    string LevelId,
    string AssetRef,
    GridPolygon Region,
    int Layer) : MapOperation(OperationId, LevelId);

public sealed record SmartTileRegionOperation(
    string OperationId,
    string LevelId,
    string AssetRef,
    GridPolygon Region,
    int Layer) : MapOperation(OperationId, LevelId);

public sealed record SmartTileDoubleRegionOperation(
    string OperationId,
    string LevelId,
    string AssetRef,
    GridPolygon Region,
    int Layer) : MapOperation(OperationId, LevelId);
