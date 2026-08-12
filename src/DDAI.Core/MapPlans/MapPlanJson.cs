using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DDAI.Core.MapPlans.Operations;

namespace DDAI.Core.MapPlans;

public static class MapPlanJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowOutOfOrderMetadataProperties = true,
        RespectRequiredConstructorParameters = true,
    };

    public static string Serialize(MapPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WritePlan(writer, plan);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static JsonElement SerializeToElement(MapPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        using var document = JsonDocument.Parse(Serialize(plan));
        return document.RootElement.Clone();
    }

    public static string Fingerprint(MapPlan plan)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(FingerprintInput(plan)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static string FingerprintInput(MapPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return string.Equals(plan.SchemaVersion, MapPlan.LegacySchemaVersion, StringComparison.Ordinal)
            ? LegacyFingerprintInput(plan)
            : UniversalFingerprintInput(plan);
    }

    public static MapPlan Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        MapPlan plan;
        try
        {
            ValidateWireShape(json);
            plan = JsonSerializer.Deserialize<MapPlan>(json, SerializerOptions)
                ?? throw new JsonException("The map plan payload cannot be JSON null.");
        }
        catch (NotSupportedException exception)
        {
            throw new JsonException("The map plan payload contains an unsupported operation.", exception);
        }

        var validationResult = MapPlanValidator.Validate(plan);
        if (!validationResult.IsValid)
        {
            throw new MapPlanValidationException(validationResult.Issues);
        }

        return plan;
    }

    private static void ValidateWireShape(string json)
    {
        using var document = JsonDocument.Parse(json);
        RejectDuplicateProperties(document.RootElement, "$", new HashSet<string>(StringComparer.Ordinal));

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The map plan payload must be an object.");
        }

        var schemaVersion = document.RootElement.TryGetProperty("schema_version", out var schemaElement) &&
            schemaElement.ValueKind == JsonValueKind.String
                ? schemaElement.GetString()
                : null;
        var allowedTopLevel = string.Equals(schemaVersion, MapPlan.LegacySchemaVersion, StringComparison.Ordinal)
            ? new HashSet<string>(
                ["schema_version", "request_id", "base_revision", "mode", "canvas", "rooms"],
                StringComparer.Ordinal)
            : new HashSet<string>(
                [
                    "schema_version",
                    "request_id",
                    "expected_map_id",
                    "base_revision",
                    "expected_catalog_revision",
                    "mode",
                    "coordinate_system",
                    "canvas",
                    "operations",
                ],
                StringComparer.Ordinal);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!allowedTopLevel.Contains(property.Name))
            {
                throw new JsonException($"Unknown map plan property '{property.Name}'.");
            }
        }

        if (!document.RootElement.TryGetProperty("operations", out var operationsElement))
        {
            return;
        }

        if (operationsElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Map plan operations must be an array.");
        }

        var index = 0;
        foreach (var operation in operationsElement.EnumerateArray())
        {
            ValidateOperationShape(operation, index++);
        }
    }

    private static void RejectDuplicateProperties(
        JsonElement element,
        string path,
        HashSet<string> scratch)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            scratch.Clear();
            foreach (var property in element.EnumerateObject())
            {
                if (!scratch.Add(property.Name))
                {
                    throw new JsonException($"Duplicate JSON property '{property.Name}' at '{path}'.");
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}", new HashSet<string>(StringComparer.Ordinal));
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index++}]", new HashSet<string>(StringComparer.Ordinal));
            }
        }
    }

    private static void ValidateOperationShape(JsonElement operation, int index)
    {
        if (operation.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Map plan operation at index {index} must be an object.");
        }

        if (!operation.TryGetProperty("operation_type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String ||
            typeElement.GetString() is not { } operationType)
        {
            throw new JsonException($"Map plan operation at index {index} requires operation_type.");
        }

        var allowed = OperationProperties(operationType);
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in operation.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new JsonException(
                    $"Unknown property '{property.Name}' for map operation '{operationType}'.");
            }

            present.Add(property.Name);
        }

        if (!present.SetEquals(allowed))
        {
            throw new JsonException($"Map operation '{operationType}' is missing required properties.");
        }
    }

    private static HashSet<string> OperationProperties(string operationType)
    {
        var specific = operationType switch
        {
            "terrain_stroke" => new[] { "asset_ref", "path", "width", "strength" },
            "pattern_region" => new[] { "asset_ref", "region", "rotation_degrees", "layer" },
            "colorable_pattern_region" => new[]
            {
                "asset_ref", "region", "color_rgba", "rotation_degrees", "layer",
            },
            "cave_region" => new[] { "asset_ref", "region", "floor_color_rgba", "wall_color_rgba" },
            "roof_region" => new[] { "asset_ref", "region", "width", "shade" },
            "object_placement" => new[]
            {
                "asset_ref", "position", "rotation_degrees", "scale", "layer", "sorting", "shadow",
                "block_light", "custom_color_rgba",
            },
            "wall_polyline" => new[] { "asset_ref", "path", "closed", "color_rgba" },
            "material_stroke" => new[] { "asset_ref", "path", "width", "smoothness", "layer" },
            "portal_placement" => new[]
            {
                "asset_ref", "position", "wall_operation_id", "rotation_degrees", "closed",
            },
            "path_polyline" => new[]
            {
                "asset_ref", "path", "width", "smoothness", "layer", "sorting", "fade_in", "fade_out",
                "loop",
            },
            "light_placement" => new[]
            {
                "asset_ref", "position", "range", "intensity", "color_rgba", "shadows",
            },
            "simple_tile_region" or "smart_tile_region" or "smart_tile_double_region" =>
                new[] { "asset_ref", "region", "layer" },
            _ => throw new JsonException($"Unsupported map operation type '{operationType}'."),
        };

        return new HashSet<string>(
            new[] { "operation_type", "operation_id", "level_id" }.Concat(specific),
            StringComparer.Ordinal);
    }

    private static void WritePlan(Utf8JsonWriter writer, MapPlan plan)
    {
        writer.WriteStartObject();
        writer.WriteString("schema_version", plan.SchemaVersion);
        writer.WriteString("request_id", plan.RequestId);

        if (string.Equals(plan.SchemaVersion, MapPlan.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            writer.WriteString("expected_map_id", plan.ExpectedMapId);
        }

        writer.WriteNumber("base_revision", plan.BaseRevision);

        if (string.Equals(plan.SchemaVersion, MapPlan.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            if (plan.ExpectedCatalogRevision is { } expectedCatalogRevision)
            {
                writer.WriteNumber("expected_catalog_revision", expectedCatalogRevision);
            }
            else
            {
                writer.WriteNull("expected_catalog_revision");
            }
        }

        writer.WriteString("mode", ModeText(plan.Mode));

        if (string.Equals(plan.SchemaVersion, MapPlan.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            writer.WriteString(
                "coordinate_system",
                plan.CoordinateSystem is { } coordinateSystem
                    ? CoordinateSystemText(coordinateSystem)
                    : null);
        }

        writer.WritePropertyName("canvas");
        if (plan.Canvas is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteNumber("width", plan.Canvas.Width);
            writer.WriteNumber("height", plan.Canvas.Height);
            writer.WriteEndObject();
        }

        if (string.Equals(plan.SchemaVersion, MapPlan.LegacySchemaVersion, StringComparison.Ordinal))
        {
            writer.WritePropertyName("rooms");
            writer.WriteStartArray();
            foreach (var room in plan.Rooms ?? [])
            {
                writer.WriteStartObject();
                writer.WriteString("id", room.Id);
                writer.WriteNumber("x", room.X);
                writer.WriteNumber("y", room.Y);
                writer.WriteNumber("width", room.Width);
                writer.WriteNumber("height", room.Height);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
        else if (string.Equals(plan.SchemaVersion, MapPlan.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            writer.WritePropertyName("operations");
            writer.WriteStartArray();
            foreach (var operation in plan.Operations ?? [])
            {
                WriteOperation(writer, operation);
            }

            writer.WriteEndArray();
        }
        else
        {
            throw new JsonException($"Unsupported map plan schema version '{plan.SchemaVersion}'.");
        }

        writer.WriteEndObject();
    }

    private static void WriteOperation(Utf8JsonWriter writer, MapOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        writer.WriteStartObject();
        writer.WriteString("operation_type", OperationType(operation));
        writer.WriteString("operation_id", operation.OperationId);
        writer.WriteString("level_id", operation.LevelId);

        switch (operation)
        {
            case TerrainStrokeOperation value:
                WriteAssetRef(writer, value.AssetRef);
                WritePolyline(writer, "path", value.Path);
                WriteFiniteNumber(writer, "width", value.Width);
                WriteFiniteNumber(writer, "strength", value.Strength);
                break;
            case PatternRegionOperation value:
                WriteAssetRef(writer, value.AssetRef);
                WritePolygon(writer, "region", value.Region);
                WriteFiniteNumber(writer, "rotation_degrees", value.RotationDegrees);
                writer.WriteNumber("layer", value.Layer);
                break;
            case ColorablePatternRegionOperation value:
                WriteAssetRef(writer, value.AssetRef);
                WritePolygon(writer, "region", value.Region);
                writer.WriteString("color_rgba", value.ColorRgba);
                WriteFiniteNumber(writer, "rotation_degrees", value.RotationDegrees);
                writer.WriteNumber("layer", value.Layer);
                break;
            case CaveRegionOperation value:
                WriteAssetRef(writer, value.AssetRef);
                WritePolygon(writer, "region", value.Region);
                writer.WriteString("floor_color_rgba", value.FloorColorRgba);
                writer.WriteString("wall_color_rgba", value.WallColorRgba);
                break;
            case RoofRegionOperation value:
                WriteAssetRef(writer, value.AssetRef);
                WritePolygon(writer, "region", value.Region);
                WriteFiniteNumber(writer, "width", value.Width);
                WriteFiniteNumber(writer, "shade", value.Shade);
                break;
            case ObjectPlacementOperation value:
                WriteAssetRef(writer, value.AssetRef);
                WritePoint(writer, "position", value.Position);
                WriteFiniteNumber(writer, "rotation_degrees", value.RotationDegrees);
                WriteFiniteNumber(writer, "scale", value.Scale);
                writer.WriteNumber("layer", value.Layer);
                writer.WriteString("sorting", SortingText(value.Sorting));
                writer.WriteBoolean("shadow", value.Shadow);
                writer.WriteBoolean("block_light", value.BlockLight);
                if (value.CustomColorRgba is null)
                {
                    writer.WriteNull("custom_color_rgba");
                }
                else
                {
                    writer.WriteString("custom_color_rgba", value.CustomColorRgba);
                }

                break;
            case WallPolylineOperation value:
                WriteAssetRef(writer, value.AssetRef);
                WritePolyline(writer, "path", value.Path);
                writer.WriteBoolean("closed", value.Closed);
                writer.WriteString("color_rgba", value.ColorRgba);
                break;
            case MaterialStrokeOperation value:
                WriteAssetRef(writer, value.AssetRef);
                WritePolyline(writer, "path", value.Path);
                WriteFiniteNumber(writer, "width", value.Width);
                WriteFiniteNumber(writer, "smoothness", value.Smoothness);
                writer.WriteNumber("layer", value.Layer);
                break;
            case PortalPlacementOperation value:
                WriteAssetRef(writer, value.AssetRef);
                WritePoint(writer, "position", value.Position);
                if (value.WallOperationId is null)
                {
                    writer.WriteNull("wall_operation_id");
                }
                else
                {
                    writer.WriteString("wall_operation_id", value.WallOperationId);
                }

                WriteFiniteNumber(writer, "rotation_degrees", value.RotationDegrees);
                writer.WriteBoolean("closed", value.Closed);
                break;
            case PathPolylineOperation value:
                WriteAssetRef(writer, value.AssetRef);
                WritePolyline(writer, "path", value.Path);
                WriteFiniteNumber(writer, "width", value.Width);
                WriteFiniteNumber(writer, "smoothness", value.Smoothness);
                writer.WriteNumber("layer", value.Layer);
                writer.WriteString("sorting", SortingText(value.Sorting));
                writer.WriteBoolean("fade_in", value.FadeIn);
                writer.WriteBoolean("fade_out", value.FadeOut);
                writer.WriteBoolean("loop", value.Loop);
                break;
            case LightPlacementOperation value:
                WriteAssetRef(writer, value.AssetRef);
                WritePoint(writer, "position", value.Position);
                WriteFiniteNumber(writer, "range", value.Range);
                WriteFiniteNumber(writer, "intensity", value.Intensity);
                writer.WriteString("color_rgba", value.ColorRgba);
                writer.WriteBoolean("shadows", value.Shadows);
                break;
            case SimpleTileRegionOperation value:
                WriteAssetRef(writer, value.AssetRef);
                WritePolygon(writer, "region", value.Region);
                writer.WriteNumber("layer", value.Layer);
                break;
            case SmartTileRegionOperation value:
                WriteAssetRef(writer, value.AssetRef);
                WritePolygon(writer, "region", value.Region);
                writer.WriteNumber("layer", value.Layer);
                break;
            case SmartTileDoubleRegionOperation value:
                WriteAssetRef(writer, value.AssetRef);
                WritePolygon(writer, "region", value.Region);
                writer.WriteNumber("layer", value.Layer);
                break;
            default:
                throw new JsonException($"Unsupported map operation type '{operation.GetType().Name}'.");
        }

        writer.WriteEndObject();
    }

    private static string LegacyFingerprintInput(MapPlan plan)
    {
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

    private static string UniversalFingerprintInput(MapPlan plan)
    {
        var builder = new StringBuilder();
        AppendString(builder, "schema_version", plan.SchemaVersion);
        AppendString(builder, "request_id", plan.RequestId);
        AppendOptionalString(builder, "expected_map_id", plan.ExpectedMapId);
        AppendInteger(builder, "base_revision", plan.BaseRevision);
        AppendOptionalInteger(builder, "expected_catalog_revision", plan.ExpectedCatalogRevision);
        AppendString(builder, "mode", ModeText(plan.Mode));
        AppendOptionalString(
            builder,
            "coordinate_system",
            plan.CoordinateSystem is { } coordinateSystem
                ? CoordinateSystemText(coordinateSystem)
                : null);
        AppendInteger(builder, "canvas_width", plan.Canvas.Width);
        AppendInteger(builder, "canvas_height", plan.Canvas.Height);
        var operations = plan.Operations ?? [];
        AppendInteger(builder, "operations_count", operations.Count);

        for (var index = 0; index < operations.Count; index++)
        {
            AppendOperation(builder, index, operations[index]);
        }

        return builder.ToString();
    }

    private static void AppendOperation(StringBuilder builder, int index, MapOperation operation)
    {
        var prefix = $"operation[{index}]";
        AppendString(builder, $"{prefix}.operation_type", OperationType(operation));
        AppendString(builder, $"{prefix}.operation_id", operation.OperationId);
        AppendString(builder, $"{prefix}.level_id", operation.LevelId);

        switch (operation)
        {
            case TerrainStrokeOperation value:
                AppendAssetAndPolyline(builder, prefix, value.AssetRef, "path", value.Path);
                AppendDouble(builder, $"{prefix}.width", value.Width);
                AppendDouble(builder, $"{prefix}.strength", value.Strength);
                break;
            case PatternRegionOperation value:
                AppendAssetAndPolygon(builder, prefix, value.AssetRef, value.Region);
                AppendDouble(builder, $"{prefix}.rotation_degrees", value.RotationDegrees);
                AppendInteger(builder, $"{prefix}.layer", value.Layer);
                break;
            case ColorablePatternRegionOperation value:
                AppendAssetAndPolygon(builder, prefix, value.AssetRef, value.Region);
                AppendString(builder, $"{prefix}.color_rgba", value.ColorRgba);
                AppendDouble(builder, $"{prefix}.rotation_degrees", value.RotationDegrees);
                AppendInteger(builder, $"{prefix}.layer", value.Layer);
                break;
            case CaveRegionOperation value:
                AppendAssetAndPolygon(builder, prefix, value.AssetRef, value.Region);
                AppendString(builder, $"{prefix}.floor_color_rgba", value.FloorColorRgba);
                AppendString(builder, $"{prefix}.wall_color_rgba", value.WallColorRgba);
                break;
            case RoofRegionOperation value:
                AppendAssetAndPolygon(builder, prefix, value.AssetRef, value.Region);
                AppendDouble(builder, $"{prefix}.width", value.Width);
                AppendDouble(builder, $"{prefix}.shade", value.Shade);
                break;
            case ObjectPlacementOperation value:
                AppendString(builder, $"{prefix}.asset_ref", value.AssetRef);
                AppendPoint(builder, $"{prefix}.position", value.Position);
                AppendDouble(builder, $"{prefix}.rotation_degrees", value.RotationDegrees);
                AppendDouble(builder, $"{prefix}.scale", value.Scale);
                AppendInteger(builder, $"{prefix}.layer", value.Layer);
                AppendString(builder, $"{prefix}.sorting", SortingText(value.Sorting));
                AppendBoolean(builder, $"{prefix}.shadow", value.Shadow);
                AppendBoolean(builder, $"{prefix}.block_light", value.BlockLight);
                AppendOptionalString(builder, $"{prefix}.custom_color_rgba", value.CustomColorRgba);
                break;
            case WallPolylineOperation value:
                AppendAssetAndPolyline(builder, prefix, value.AssetRef, "path", value.Path);
                AppendBoolean(builder, $"{prefix}.closed", value.Closed);
                AppendString(builder, $"{prefix}.color_rgba", value.ColorRgba);
                break;
            case MaterialStrokeOperation value:
                AppendAssetAndPolyline(builder, prefix, value.AssetRef, "path", value.Path);
                AppendDouble(builder, $"{prefix}.width", value.Width);
                AppendDouble(builder, $"{prefix}.smoothness", value.Smoothness);
                AppendInteger(builder, $"{prefix}.layer", value.Layer);
                break;
            case PortalPlacementOperation value:
                AppendString(builder, $"{prefix}.asset_ref", value.AssetRef);
                AppendPoint(builder, $"{prefix}.position", value.Position);
                AppendOptionalString(builder, $"{prefix}.wall_operation_id", value.WallOperationId);
                AppendDouble(builder, $"{prefix}.rotation_degrees", value.RotationDegrees);
                AppendBoolean(builder, $"{prefix}.closed", value.Closed);
                break;
            case PathPolylineOperation value:
                AppendAssetAndPolyline(builder, prefix, value.AssetRef, "path", value.Path);
                AppendDouble(builder, $"{prefix}.width", value.Width);
                AppendDouble(builder, $"{prefix}.smoothness", value.Smoothness);
                AppendInteger(builder, $"{prefix}.layer", value.Layer);
                AppendString(builder, $"{prefix}.sorting", SortingText(value.Sorting));
                AppendBoolean(builder, $"{prefix}.fade_in", value.FadeIn);
                AppendBoolean(builder, $"{prefix}.fade_out", value.FadeOut);
                AppendBoolean(builder, $"{prefix}.loop", value.Loop);
                break;
            case LightPlacementOperation value:
                AppendString(builder, $"{prefix}.asset_ref", value.AssetRef);
                AppendPoint(builder, $"{prefix}.position", value.Position);
                AppendDouble(builder, $"{prefix}.range", value.Range);
                AppendDouble(builder, $"{prefix}.intensity", value.Intensity);
                AppendString(builder, $"{prefix}.color_rgba", value.ColorRgba);
                AppendBoolean(builder, $"{prefix}.shadows", value.Shadows);
                break;
            case SimpleTileRegionOperation value:
                AppendAssetAndPolygon(builder, prefix, value.AssetRef, value.Region);
                AppendInteger(builder, $"{prefix}.layer", value.Layer);
                break;
            case SmartTileRegionOperation value:
                AppendAssetAndPolygon(builder, prefix, value.AssetRef, value.Region);
                AppendInteger(builder, $"{prefix}.layer", value.Layer);
                break;
            case SmartTileDoubleRegionOperation value:
                AppendAssetAndPolygon(builder, prefix, value.AssetRef, value.Region);
                AppendInteger(builder, $"{prefix}.layer", value.Layer);
                break;
            default:
                throw new JsonException($"Unsupported map operation type '{operation.GetType().Name}'.");
        }
    }

    private static void WriteAssetRef(Utf8JsonWriter writer, string assetRef) =>
        writer.WriteString("asset_ref", assetRef);

    private static void WritePoint(Utf8JsonWriter writer, string propertyName, GridPoint point)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        WriteFiniteNumber(writer, "x", point.X);
        WriteFiniteNumber(writer, "y", point.Y);
        writer.WriteEndObject();
    }

    private static void WritePolyline(Utf8JsonWriter writer, string propertyName, GridPolyline polyline) =>
        WritePoints(writer, propertyName, polyline.Points);

    private static void WritePolygon(Utf8JsonWriter writer, string propertyName, GridPolygon polygon) =>
        WritePoints(writer, propertyName, polygon.Points);

    private static void WritePoints(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<GridPoint> points)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WritePropertyName("points");
        writer.WriteStartArray();
        foreach (var point in points)
        {
            writer.WriteStartObject();
            WriteFiniteNumber(writer, "x", point.X);
            WriteFiniteNumber(writer, "y", point.Y);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteFiniteNumber(Utf8JsonWriter writer, string name, double value)
    {
        EnsureFinite(value, name);
        writer.WriteNumber(name, value);
    }

    private static void AppendAssetAndPolyline(
        StringBuilder builder,
        string prefix,
        string assetRef,
        string propertyName,
        GridPolyline polyline)
    {
        AppendString(builder, $"{prefix}.asset_ref", assetRef);
        AppendPoints(builder, $"{prefix}.{propertyName}", polyline.Points);
    }

    private static void AppendAssetAndPolygon(
        StringBuilder builder,
        string prefix,
        string assetRef,
        GridPolygon polygon)
    {
        AppendString(builder, $"{prefix}.asset_ref", assetRef);
        AppendPoints(builder, $"{prefix}.region", polygon.Points);
    }

    private static void AppendPoint(StringBuilder builder, string name, GridPoint point)
    {
        AppendDouble(builder, $"{name}.x", point.X);
        AppendDouble(builder, $"{name}.y", point.Y);
    }

    private static void AppendPoints(
        StringBuilder builder,
        string name,
        IReadOnlyList<GridPoint> points)
    {
        AppendInteger(builder, $"{name}.points_count", points.Count);
        for (var index = 0; index < points.Count; index++)
        {
            AppendPoint(builder, $"{name}.point[{index}]", points[index]);
        }
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

    private static void AppendOptionalString(StringBuilder builder, string name, string? value)
    {
        AppendBoolean(builder, $"{name}.present", value is not null);
        if (value is not null)
        {
            AppendString(builder, name, value);
        }
    }

    private static void AppendInteger(StringBuilder builder, string name, long value)
    {
        builder.Append(name);
        builder.Append('=');
        builder.Append(value.ToString(CultureInfo.InvariantCulture));
        builder.Append('\n');
    }

    private static void AppendOptionalInteger(StringBuilder builder, string name, long? value)
    {
        AppendBoolean(builder, $"{name}.present", value.HasValue);
        if (value.HasValue)
        {
            AppendInteger(builder, name, value.Value);
        }
    }

    private static void AppendDouble(StringBuilder builder, string name, double value)
    {
        EnsureFinite(value, name);
        builder.Append(name);
        builder.Append('=');
        builder.Append(unchecked((ulong)BitConverter.DoubleToInt64Bits(value)).ToString("x16", CultureInfo.InvariantCulture));
        builder.Append('\n');
    }

    private static void AppendBoolean(StringBuilder builder, string name, bool value)
    {
        builder.Append(name);
        builder.Append('=');
        builder.Append(value ? "true" : "false");
        builder.Append('\n');
    }

    private static void EnsureFinite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new JsonException($"Map plan number '{name}' must be finite.");
        }
    }

    private static string OperationType(MapOperation operation) => operation switch
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
        _ => throw new JsonException($"Unsupported map operation type '{operation.GetType().Name}'."),
    };

    private static string ModeText(MapOperationMode mode) => mode switch
    {
        MapOperationMode.Add => "add",
        MapOperationMode.Replace => "replace",
        MapOperationMode.Patch => "patch",
        _ => throw new JsonException("Unsupported map operation mode."),
    };

    private static string CoordinateSystemText(MapCoordinateSystem coordinateSystem) => coordinateSystem switch
    {
        MapCoordinateSystem.Grid => "grid",
        MapCoordinateSystem.World => "world",
        _ => throw new JsonException("Unsupported map coordinate system."),
    };

    private static string SortingText(MapSortingMode sorting) => sorting switch
    {
        MapSortingMode.Over => "over",
        MapSortingMode.Under => "under",
        _ => throw new JsonException("Unsupported map sorting mode."),
    };
}
