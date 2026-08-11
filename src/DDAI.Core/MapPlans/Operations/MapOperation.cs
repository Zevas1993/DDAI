using System.Text.Json;
using System.Text.Json.Serialization;

namespace DDAI.Core.MapPlans.Operations;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation_type")]
[JsonDerivedType(typeof(TerrainStrokeOperation), "terrain_stroke")]
[JsonDerivedType(typeof(PatternRegionOperation), "pattern_region")]
[JsonDerivedType(typeof(ColorablePatternRegionOperation), "colorable_pattern_region")]
[JsonDerivedType(typeof(CaveRegionOperation), "cave_region")]
[JsonDerivedType(typeof(RoofRegionOperation), "roof_region")]
[JsonDerivedType(typeof(ObjectPlacementOperation), "object_placement")]
[JsonDerivedType(typeof(WallPolylineOperation), "wall_polyline")]
[JsonDerivedType(typeof(MaterialStrokeOperation), "material_stroke")]
[JsonDerivedType(typeof(PortalPlacementOperation), "portal_placement")]
[JsonDerivedType(typeof(PathPolylineOperation), "path_polyline")]
[JsonDerivedType(typeof(LightPlacementOperation), "light_placement")]
[JsonDerivedType(typeof(SimpleTileRegionOperation), "simple_tile_region")]
[JsonDerivedType(typeof(SmartTileRegionOperation), "smart_tile_region")]
[JsonDerivedType(typeof(SmartTileDoubleRegionOperation), "smart_tile_double_region")]
public abstract record MapOperation(
    [property: JsonPropertyName("operation_id")] string OperationId,
    [property: JsonPropertyName("level_id")] string LevelId);

public sealed record GridPoint(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y);

public sealed record GridPolyline(
    [property: JsonPropertyName("points")] IReadOnlyList<GridPoint> Points);

public sealed record GridPolygon(
    [property: JsonPropertyName("points")] IReadOnlyList<GridPoint> Points);

[JsonConverter(typeof(MapSortingModeJsonConverter))]
public enum MapSortingMode
{
    Over,
    Under,
}

public sealed class MapSortingModeJsonConverter : JsonConverter<MapSortingMode>
{
    public override MapSortingMode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() is { } value
            ? value switch
            {
                "over" => MapSortingMode.Over,
                "under" => MapSortingMode.Under,
                _ => throw new JsonException("Unsupported map sorting mode."),
            }
            : throw new JsonException("Map sorting mode must be a string.");

    public override void Write(
        Utf8JsonWriter writer,
        MapSortingMode value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            MapSortingMode.Over => "over",
            MapSortingMode.Under => "under",
            _ => throw new JsonException("Unsupported map sorting mode."),
        });
}
