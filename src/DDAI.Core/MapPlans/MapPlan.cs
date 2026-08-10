using System.Text.Json;
using System.Text.Json.Serialization;

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

public sealed record MapRoom(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

public sealed record MapPlan
{
    public const string CurrentSchemaVersion = "1.0";

    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("base_revision")]
    public required long BaseRevision { get; init; }

    [JsonPropertyName("mode")]
    public required MapOperationMode Mode { get; init; }

    [JsonPropertyName("canvas")]
    public required MapCanvas Canvas { get; init; }

    [JsonPropertyName("rooms")]
    public IReadOnlyList<MapRoom> Rooms { get; init; } = [];
}
