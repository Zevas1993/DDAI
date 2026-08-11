namespace DDAI.Core.MapPlans.Operations;

public sealed record ObjectPlacementOperation(
    string OperationId,
    string LevelId,
    string AssetRef,
    GridPoint Position,
    double RotationDegrees,
    double Scale,
    int Layer,
    MapSortingMode Sorting,
    bool Shadow,
    bool BlockLight,
    string? CustomColorRgba) : MapOperation(OperationId, LevelId);

public sealed record MaterialStrokeOperation(
    string OperationId,
    string LevelId,
    string AssetRef,
    GridPolyline Path,
    double Width,
    double Smoothness,
    int Layer) : MapOperation(OperationId, LevelId);

public sealed record PathPolylineOperation(
    string OperationId,
    string LevelId,
    string AssetRef,
    GridPolyline Path,
    double Width,
    double Smoothness,
    int Layer,
    MapSortingMode Sorting,
    bool FadeIn,
    bool FadeOut,
    bool Loop) : MapOperation(OperationId, LevelId);

public sealed record LightPlacementOperation(
    string OperationId,
    string LevelId,
    string AssetRef,
    GridPoint Position,
    double Range,
    double Intensity,
    string ColorRgba,
    bool Shadows) : MapOperation(OperationId, LevelId);
