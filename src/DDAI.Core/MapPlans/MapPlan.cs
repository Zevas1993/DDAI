using System.Text.Json;
using System.Text.Json.Serialization;
using DDAI.Core.MapPlans.Operations;

namespace DDAI.Core.MapPlans;

[JsonConverter(typeof(MapOperationModeJsonConverter))]
public enum MapOperationMode
{
    Add,
    Replace,
    Patch,
}

public sealed class MapOperationModeJsonConverter : JsonConverter<MapOperationMode>
{
    public override MapOperationMode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() is { } value
            ? value switch
            {
                "add" => MapOperationMode.Add,
                "replace" => MapOperationMode.Replace,
                "patch" => MapOperationMode.Patch,
                _ => throw new JsonException("Unsupported map operation mode."),
            }
            : throw new JsonException("Map operation mode must be a string.");

    public override void Write(
        Utf8JsonWriter writer,
        MapOperationMode value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            MapOperationMode.Add => "add",
            MapOperationMode.Replace => "replace",
            MapOperationMode.Patch => "patch",
            _ => throw new JsonException("Unsupported map operation mode."),
        });
}

public sealed record MapCanvas(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

[JsonConverter(typeof(MapCoordinateSystemJsonConverter))]
public enum MapCoordinateSystem
{
    Grid,
    World,
}

public sealed class MapCoordinateSystemJsonConverter : JsonConverter<MapCoordinateSystem>
{
    public override MapCoordinateSystem Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() is { } value
            ? value switch
            {
                "grid" => MapCoordinateSystem.Grid,
                "world" => MapCoordinateSystem.World,
                _ => throw new JsonException("Unsupported map coordinate system."),
            }
            : throw new JsonException("Map coordinate system must be a string.");

    public override void Write(
        Utf8JsonWriter writer,
        MapCoordinateSystem value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            MapCoordinateSystem.Grid => "grid",
            MapCoordinateSystem.World => "world",
            _ => throw new JsonException("Unsupported map coordinate system."),
        });
}

public sealed record MapRoom(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

public sealed record MapPlan
{
    public const string LegacySchemaVersion = "1.0";
    public const string CurrentSchemaVersion = "2.0";

    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("expected_map_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExpectedMapId { get; init; }

    [JsonPropertyName("base_revision")]
    public required long BaseRevision { get; init; }

    [JsonPropertyName("expected_catalog_revision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ExpectedCatalogRevision { get; init; }

    [JsonPropertyName("mode")]
    public required MapOperationMode Mode { get; init; }

    [JsonPropertyName("coordinate_system")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MapCoordinateSystem? CoordinateSystem { get; init; }

    [JsonPropertyName("canvas")]
    public required MapCanvas Canvas { get; init; }

    [JsonPropertyName("rooms")]
    public IReadOnlyList<MapRoom> Rooms { get; init; } = [];

    [JsonPropertyName("operations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MapOperation>? Operations { get; init; }
}
