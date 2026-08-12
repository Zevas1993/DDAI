using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DDAI.Core.MapPlans;

namespace DDAI.Core.Maps;

public sealed record MapInspectionRegion(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height);

public sealed record MapInspectionQuery(
    [property: JsonPropertyName("region")] MapInspectionRegion? Region = null,
    [property: JsonPropertyName("level")] int? Level = null,
    [property: JsonPropertyName("limit")] int Limit = 100,
    [property: JsonPropertyName("cursor")] string? Cursor = null);

public sealed record MapSnapshotBounds(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height);

public sealed record MapSnapshotLevel(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("current")] bool Current);

public sealed record MapSnapshotItem(
    [property: JsonPropertyName("node_id")] long NodeId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("bounds")] MapSnapshotBounds Bounds,
    [property: JsonPropertyName("level")] int Level,
    [property: JsonPropertyName("asset_ref")] string? AssetRef,
    [property: JsonPropertyName("resource_fingerprint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResourceFingerprint = null);

public sealed record MapSnapshotPage(
    [property: JsonPropertyName("map_id")] string MapId,
    [property: JsonPropertyName("map_revision")] string MapRevision,
    [property: JsonPropertyName("canvas")] MapCanvas Canvas,
    [property: JsonPropertyName("grid_size")] double GridSize,
    [property: JsonPropertyName("levels")] IReadOnlyList<MapSnapshotLevel> Levels,
    [property: JsonPropertyName("items")] IReadOnlyList<MapSnapshotItem> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("unsupported_kinds")] IReadOnlyList<string> UnsupportedKinds);

public static class MapSnapshotJson
{
    public const int MaximumJsonBytes = 1024 * 1024;
    public const int MaximumLimit = 500;
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private const int MaximumCursorOffset = 10_000;

    private static readonly HashSet<string> SupportedKinds =
        new(["wall", "path", "roof", "pattern_shape", "object"], StringComparer.Ordinal);

    private static readonly HashSet<string> UnsupportedKinds =
        new(["object", "portal", "light", "text", "material", "floor_shape", "object_asset_correlation"], StringComparer.Ordinal);

    private static readonly JsonSerializerOptions WireSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    public static JsonElement SerializeQueryToElement(MapInspectionQuery query)
    {
        ValidateQuery(query);
        return JsonSerializer.SerializeToElement(query, WireSerializerOptions);
    }

    public static string SerializePage(MapSnapshotPage page)
    {
        ValidatePage(page);
        return SerializeBounded(page);
    }

    public static MapSnapshotPage DeserializePage(string json)
    {
        using var document = ParseBounded(json);
        ValidatePageWireShape(document.RootElement);

        var page = JsonSerializer.Deserialize<MapSnapshotPage>(json, WireSerializerOptions)
            ?? throw new JsonException("Map snapshot page cannot be JSON null.");
        ValidatePage(page);
        return page;
    }

    public static void ValidateQuery(MapInspectionQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > MaximumLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Inspection limit must be between 1 and 500.");
        }

        if (query.Level is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Inspection level cannot be negative.");
        }

        if (query.Region is { } region)
        {
            if (!IsFinite(region.X) || !IsFinite(region.Y) ||
                !IsFinite(region.Width) || !IsFinite(region.Height) ||
                region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
                region.Width > double.MaxValue - region.X || region.Height > double.MaxValue - region.Y ||
                !HasCanonicalPrecision(region.X) || !HasCanonicalPrecision(region.Y) ||
                !HasCanonicalPrecision(region.Width) || !HasCanonicalPrecision(region.Height))
            {
                throw new ArgumentOutOfRangeException(nameof(query), "Inspection region must be finite, non-negative, have positive size, and use at most six decimal places.");
            }
        }

        if (query.Cursor is not null && !TryParseCursor(query.Cursor, out _))
        {
            throw new ArgumentException("Inspection cursor is not canonical.", nameof(query));
        }
    }

    public static string CreateCursor(
        string mapId,
        string mapRevision,
        int level,
        MapInspectionRegion? region,
        int offset)
    {
        RequireHash(mapId, "map id");
        RequireHash(mapRevision, "map revision");
        if (level < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        if (offset is < 0 or > MaximumCursorOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (region is not null)
        {
            ValidateQuery(new MapInspectionQuery(region, level, 1));
        }

        var regionText = region is null
            ? "null"
            : string.Join(",",
                FormatCoordinate(region.X),
                FormatCoordinate(region.Y),
                FormatCoordinate(region.Width),
                FormatCoordinate(region.Height));
        var input = string.Concat(
            "map_id=", mapId, "\n",
            "map_revision=", mapRevision, "\n",
            "level=", level.ToString(CultureInfo.InvariantCulture), "\n",
            "region=", regionText, "\n",
            "offset=", offset.ToString(CultureInfo.InvariantCulture), "\n");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
        return string.Concat(fingerprint, ":", offset.ToString(CultureInfo.InvariantCulture));
    }

    public static int GetCursorOffset(string cursor) =>
        TryParseCursor(cursor, out var offset)
            ? offset
            : throw new ArgumentException("Inspection cursor is not canonical.", nameof(cursor));

    public static void ValidatePageForQuery(MapSnapshotPage page, MapInspectionQuery query)
    {
        ValidateQuery(query);
        ValidatePage(page);

        if (query.Region is { } requestedRegion && !FitsWithinCanvas(requestedRegion, page.Canvas))
        {
            throw new JsonException("Requested inspection region is outside the returned map canvas.");
        }

        var currentLevel = page.Levels.Single(level => level.Current);
        if (query.Level is { } requestedLevel && requestedLevel != currentLevel.Id)
        {
            throw new JsonException("Map snapshot current level does not match the requested level.");
        }

        if (page.Items.Count > query.Limit || page.Items.Any(item => item.Level != currentLevel.Id))
        {
            throw new JsonException("Map snapshot items do not satisfy the requested limit or current level.");
        }

        if (query.Region is { } region && page.Items.Any(item => !Intersects(item.Bounds, region)))
        {
            throw new JsonException("Map snapshot contains an item outside the requested region.");
        }

        var requestOffset = query.Cursor is null ? 0 : GetCursorOffset(query.Cursor);
        if (query.Cursor is not null)
        {
            var expectedSubmitted = CreateCursor(
                page.MapId,
                page.MapRevision,
                currentLevel.Id,
                query.Region,
                requestOffset);
            if (!string.Equals(query.Cursor, expectedSubmitted, StringComparison.Ordinal) || page.Items.Count == 0)
            {
                throw new JsonException("Submitted cursor does not correlate to the returned map state and filters.");
            }
        }

        if (page.Truncated)
        {
            int nextOffset;
            try
            {
                nextOffset = checked(requestOffset + page.Items.Count);
            }
            catch (OverflowException exception)
            {
                throw new JsonException("Map snapshot cursor progression overflows.", exception);
            }

            var expectedNext = CreateCursor(
                page.MapId,
                page.MapRevision,
                currentLevel.Id,
                query.Region,
                nextOffset);
            if (!string.Equals(page.NextCursor, expectedNext, StringComparison.Ordinal))
            {
                throw new JsonException("Next cursor does not represent exact item-count progression.");
            }
        }
    }

    private static string SerializeBounded<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, WireSerializerOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
        {
            throw new JsonException("Map snapshot JSON exceeds the 1 MiB limit.");
        }

        return json;
    }

    private static JsonDocument ParseBounded(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
        {
            throw new JsonException("Map snapshot JSON exceeds the 1 MiB limit.");
        }

        return JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
    }

    private static void ValidatePageWireShape(JsonElement root)
    {
        EnsureObjectShape(
            root,
            "map snapshot page",
            ["map_id", "map_revision", "canvas", "grid_size", "levels", "items", "next_cursor", "truncated", "unsupported_kinds"]);
        EnsureObjectShape(root.GetProperty("canvas"), "map canvas", ["width", "height"]);

        EnsureArrayObjects(root.GetProperty("levels"), "map snapshot level", ["id", "label", "current"]);
        var items = root.GetProperty("items");
        if (items.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Map snapshot item collection must be an array.");
        }
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Map snapshot items must be objects.");
            }

            var itemFields = new List<string> { "node_id", "kind", "bounds", "level", "asset_ref" };
            if (item.TryGetProperty("resource_fingerprint", out _))
            {
                itemFields.Add("resource_fingerprint");
            }
            EnsureObjectShape(item, "map snapshot item", itemFields);
            EnsureObjectShape(item.GetProperty("bounds"), "map snapshot bounds", ["x", "y", "width", "height"]);
        }

        if (root.GetProperty("unsupported_kinds").ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Unsupported kinds must be a JSON array.");
        }
    }

    private static void EnsureArrayObjects(JsonElement element, string name, IReadOnlyList<string> expectedNames)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"{name} collection must be an array.");
        }

        foreach (var item in element.EnumerateArray())
        {
            EnsureObjectShape(item, name, expectedNames);
        }
    }

    private static void EnsureObjectShape(JsonElement element, string name, IReadOnlyList<string> expectedNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"{name} must be a JSON object.");
        }

        var expected = new HashSet<string>(expectedNames, StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name))
            {
                throw new JsonException($"{name} contains unsupported property '{property.Name}'.");
            }

            if (!observed.Add(property.Name))
            {
                throw new JsonException($"{name} contains duplicate property '{property.Name}'.");
            }
        }

        if (observed.Count != expected.Count)
        {
            var missing = expected.First(property => !observed.Contains(property));
            throw new JsonException($"{name} is missing required property '{missing}'.");
        }
    }

    private static void ValidatePage(MapSnapshotPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        RequireHash(page.MapId, "map id");
        RequireHash(page.MapRevision, "map revision");
        if (page.Canvas is null || page.Canvas.Width <= 0 || page.Canvas.Height <= 0)
        {
            throw new JsonException("Map canvas dimensions must be positive.");
        }

        if (!IsFinite(page.GridSize) || page.GridSize <= 0)
        {
            throw new JsonException("Grid size must be finite and positive.");
        }

        if (page.Levels is null || page.Levels.Count == 0 || page.Levels.Count > 128)
        {
            throw new JsonException("Map snapshot must contain between 1 and 128 levels.");
        }

        var levelIds = new HashSet<int>();
        var currentLevels = 0;
        foreach (var level in page.Levels)
        {
            if (level is null || level.Id < 0 || !levelIds.Add(level.Id) ||
                string.IsNullOrWhiteSpace(level.Label) || level.Label.Length > 256)
            {
                throw new JsonException("Map snapshot levels must be unique, non-negative, and labeled.");
            }

            if (level.Current)
            {
                currentLevels++;
            }
        }

        if (currentLevels != 1)
        {
            throw new JsonException("Map snapshot must identify exactly one current level.");
        }

        if (page.Items is null || page.Items.Count > MaximumLimit)
        {
            throw new JsonException("Map snapshot page contains too many items.");
        }

        var nodeIds = new HashSet<long>();
        foreach (var item in page.Items)
        {
            if (item is null)
            {
                throw new JsonException("Map snapshot items cannot contain null.");
            }

            RequireSafeInteger(item.NodeId, "node id");
            if (!nodeIds.Add(item.NodeId) || !SupportedKinds.Contains(item.Kind) || !levelIds.Contains(item.Level))
            {
                throw new JsonException("Map snapshot item identity, kind, or level is invalid.");
            }

            ValidateBounds(item.Bounds);
            if (item.AssetRef is not null)
            {
                RequireAssetReference(item.AssetRef);
            }
            if (item.ResourceFingerprint is not null)
            {
                if (!string.Equals(item.Kind, "object", StringComparison.Ordinal) || item.AssetRef is not null)
                {
                    throw new JsonException("Only an unresolved object may carry an internal resource fingerprint.");
                }

                RequireHash(item.ResourceFingerprint, "resource fingerprint");
            }
        }

        if (page.UnsupportedKinds is null ||
            page.UnsupportedKinds.Count > UnsupportedKinds.Count ||
            page.UnsupportedKinds.Any(kind => !UnsupportedKinds.Contains(kind)) ||
            page.UnsupportedKinds.Distinct(StringComparer.Ordinal).Count() != page.UnsupportedKinds.Count)
        {
            throw new JsonException("Unsupported kinds must be unique members of the closed inspection-kind set.");
        }

        if (page.Truncated && page.Items.Count == 0 ||
            page.Truncated != (page.NextCursor is not null) ||
            (page.NextCursor is not null && !TryParseCursor(page.NextCursor, out _)))
        {
            throw new JsonException("Pagination state is inconsistent or non-canonical.");
        }
    }

    private static void ValidateBounds(MapSnapshotBounds bounds)
    {
        if (bounds is null || !IsFinite(bounds.X) || !IsFinite(bounds.Y) ||
            !IsFinite(bounds.Width) || !IsFinite(bounds.Height) || bounds.Width < 0 || bounds.Height < 0 ||
            (bounds.Width == 0 && bounds.Height == 0))
        {
            throw new JsonException("Map snapshot bounds must be finite, non-negative, and span at least one dimension.");
        }
    }

    private static bool TryParseCursor(string cursor, out int offset)
    {
        offset = 0;
        if (cursor is null || cursor.Length is < 66 or > 71 || cursor[64] != ':' || !IsCanonicalHash(cursor.AsSpan(0, 64)))
        {
            return false;
        }

        var digits = cursor.AsSpan(65);
        return digits.Length > 0 && digits.IndexOfAnyExceptInRange('0', '9') < 0 &&
            int.TryParse(digits, out offset) && offset >= 0 && offset <= MaximumCursorOffset &&
            (digits.Length == 1 || digits[0] != '0');
    }

    private static void RequireSafeInteger(long value, string name)
    {
        if (value < 0 || value > MaximumSafeInteger)
        {
            throw new JsonException($"{name} must be a non-negative JSON-safe integer.");
        }
    }

    private static void RequireHash(string? value, string name)
    {
        if (value is null || !IsCanonicalHash(value.AsSpan()))
        {
            throw new JsonException($"{name} must contain 64 lowercase hexadecimal characters.");
        }
    }

    private static void RequireAssetReference(string value)
    {
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            !IsCanonicalHash(value.AsSpan(7)))
        {
            throw new JsonException("Asset reference must contain a sha256 prefix and 64 lowercase hexadecimal characters.");
        }
    }

    private static bool IsCanonicalHash(ReadOnlySpan<char> value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if ((character < '0' || character > '9') && (character < 'a' || character > 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Intersects(MapSnapshotBounds bounds, MapInspectionRegion region) =>
        bounds.X < region.X + region.Width && bounds.X + bounds.Width > region.X &&
        bounds.Y < region.Y + region.Height && bounds.Y + bounds.Height > region.Y;

    private static bool FitsWithinCanvas(MapInspectionRegion region, MapCanvas canvas) =>
        IsFinite(region.X) && IsFinite(region.Y) && IsFinite(region.Width) && IsFinite(region.Height) &&
        region.X >= 0 && region.Y >= 0 && region.Width > 0 && region.Height > 0 &&
        region.X <= canvas.Width && region.Y <= canvas.Height &&
        region.Width <= canvas.Width - region.X && region.Height <= canvas.Height - region.Y;

    private static bool HasCanonicalPrecision(double value)
    {
        if (value == 0 && BitConverter.DoubleToInt64Bits(value) < 0)
        {
            return false;
        }

        return double.TryParse(FormatCoordinate(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var canonical) &&
            canonical.Equals(value);
    }

    private static string FormatCoordinate(double value) => value.ToString("F6", CultureInfo.InvariantCulture);

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
