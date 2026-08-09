using System.Text.Json;
using System.Text.Json.Serialization;

namespace DDAI.Core.MapPlans;

public static class MapPlanJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters =
        {
            new JsonStringEnumConverter<MapOperationMode>(
                JsonNamingPolicy.SnakeCaseLower,
                allowIntegerValues: false),
        },
    };

    public static string Serialize(MapPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return JsonSerializer.Serialize(plan, SerializerOptions);
    }

    public static MapPlan Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        return JsonSerializer.Deserialize<MapPlan>(json, SerializerOptions)
            ?? throw new JsonException("The map plan payload cannot be JSON null.");
    }
}
