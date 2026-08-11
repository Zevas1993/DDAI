using DDAI.Core.MapPlans.Operations;

namespace DDAI.Core.MapPlans;

public static class UniversalPlanMigration
{
    public static MapPlan MigrateV1(
        MapPlan legacyPlan,
        string expectedMapId,
        long expectedCatalogRevision,
        string levelId,
        string wallAssetRef,
        string wallColorRgba)
    {
        ArgumentNullException.ThrowIfNull(legacyPlan);
        if (!string.Equals(legacyPlan.SchemaVersion, MapPlan.LegacySchemaVersion, StringComparison.Ordinal) ||
            legacyPlan.Mode != MapOperationMode.Add ||
            legacyPlan.Rooms is not { Count: 1 } ||
            legacyPlan.Rooms[0] is not { } room ||
            legacyPlan.Canvas is null ||
            !SafeIdentifier.IsSafe(legacyPlan.RequestId) ||
            !SafeIdentifier.IsSafe(room.Id) ||
            !SafeIdentifier.IsSafe(expectedMapId) ||
            !SafeIdentifier.IsSafe(levelId) ||
            string.IsNullOrWhiteSpace(wallAssetRef) ||
            !IsNormalizedColor(wallColorRgba) ||
            legacyPlan.BaseRevision < 0 ||
            expectedCatalogRevision < 0 ||
            room.X < 0 || room.Y < 0 || room.Width <= 0 || room.Height <= 0 ||
            room.X > legacyPlan.Canvas.Width || room.Width > legacyPlan.Canvas.Width - room.X ||
            room.Y > legacyPlan.Canvas.Height || room.Height > legacyPlan.Canvas.Height - room.Y)
        {
            throw new MapPlanValidationException(
            [
                new MapPlanValidationIssue(
                    "invalid_legacy_plan",
                    "schema_version",
                    "The version 1 plan cannot be migrated safely."),
            ]);
        }

        var right = checked(room.X + room.Width);
        var bottom = checked(room.Y + room.Height);
        return new MapPlan
        {
            SchemaVersion = MapPlan.CurrentSchemaVersion,
            RequestId = legacyPlan.RequestId,
            ExpectedMapId = expectedMapId,
            BaseRevision = legacyPlan.BaseRevision,
            ExpectedCatalogRevision = expectedCatalogRevision,
            Mode = MapOperationMode.Add,
            CoordinateSystem = MapCoordinateSystem.Grid,
            Canvas = legacyPlan.Canvas,
            Operations =
            [
                new WallPolylineOperation(
                    room.Id,
                    levelId,
                    wallAssetRef,
                    new GridPolyline(
                    [
                        new GridPoint(room.X, room.Y),
                        new GridPoint(right, room.Y),
                        new GridPoint(right, bottom),
                        new GridPoint(room.X, bottom),
                    ]),
                    Closed: true,
                    ColorRgba: wallColorRgba),
            ],
        };
    }

    private static bool IsNormalizedColor(string? value) =>
        value is { Length: 9 } &&
        value[0] == '#' &&
        !value.AsSpan(1).ContainsAnyExcept("0123456789abcdef");
}
