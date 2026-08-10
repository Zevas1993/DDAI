using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DDAI.Core.MapPlans;

public static class MapPlanJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public static string Serialize(MapPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return JsonSerializer.Serialize(plan, SerializerOptions);
    }

    public static JsonElement SerializeToElement(MapPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return JsonSerializer.SerializeToElement(plan, SerializerOptions);
    }

    public static string Fingerprint(MapPlan plan)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(FingerprintInput(plan)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static string FingerprintInput(MapPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var builder = new StringBuilder();
        AppendString(builder, "schema_version", plan.SchemaVersion);
        AppendString(builder, "request_id", plan.RequestId);
        AppendInteger(builder, "base_revision", plan.BaseRevision);
        AppendString(builder, "mode", ModeText(plan.Mode));
        AppendInteger(builder, "canvas_width", plan.Canvas.Width);
        AppendInteger(builder, "canvas_height", plan.Canvas.Height);
        AppendInteger(builder, "rooms_count", plan.Rooms.Count);

        for (var index = 0; index < plan.Rooms.Count; index++)
        {
            var room = plan.Rooms[index];
            AppendString(builder, $"room[{index}].id", room.Id);
            AppendInteger(builder, $"room[{index}].x", room.X);
            AppendInteger(builder, $"room[{index}].y", room.Y);
            AppendInteger(builder, $"room[{index}].width", room.Width);
            AppendInteger(builder, $"room[{index}].height", room.Height);
        }

        return builder.ToString();
    }

    public static MapPlan Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var plan = JsonSerializer.Deserialize<MapPlan>(json, SerializerOptions)
            ?? throw new JsonException("The map plan payload cannot be JSON null.");

        var validationResult = MapPlanValidator.Validate(plan);
        if (!validationResult.IsValid)
        {
            throw new MapPlanValidationException(validationResult.Issues);
        }

        return plan;
    }

    private static void AppendString(StringBuilder builder, string name, string value)
    {
        builder.Append(name);
        builder.Append('=');
        builder.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append('\n');
    }

    private static void AppendInteger(StringBuilder builder, string name, long value)
    {
        builder.Append(name);
        builder.Append('=');
        builder.Append(value.ToString(CultureInfo.InvariantCulture));
        builder.Append('\n');
    }

    private static string ModeText(MapOperationMode mode) => mode switch
    {
        MapOperationMode.Add => "add",
        MapOperationMode.Replace => "replace",
        MapOperationMode.Patch => "patch",
        _ => throw new JsonException("Unsupported map operation mode."),
    };
}
