namespace DDAI.Core.MapPlans.Operations;

public sealed record WallPolylineOperation(
    string OperationId,
    string LevelId,
    string AssetRef,
    GridPolyline Path,
    bool Closed,
    string ColorRgba) : MapOperation(OperationId, LevelId);

public sealed record PortalPlacementOperation(
    string OperationId,
    string LevelId,
    string AssetRef,
    GridPoint Position,
    string? WallOperationId,
    double RotationDegrees,
    bool Closed) : MapOperation(OperationId, LevelId);
