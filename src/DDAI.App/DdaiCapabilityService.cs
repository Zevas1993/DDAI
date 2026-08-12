using System.ComponentModel;
using System.Text.Json;
using System.Globalization;
using DDAI.App.Assets;
using DDAI.Core.Assets;
using DDAI.Core.Mailbox;

namespace DDAI.App;

public sealed record OperationCapability(
    string Name,
    bool SchemaSupported,
    bool RoutePresent,
    bool MethodsReflectable,
    bool PropertiesReadable,
    bool RuntimeCertified,
    bool Busy,
    string? ToolName,
    IReadOnlyList<string> RequiredMethods,
    IReadOnlyList<string> RequiredProperties,
    string Reason);

public sealed record DdaiPreviewCapabilities(
    IReadOnlyList<string> Formats,
    int MaximumBytes,
    int MaximumEdge);

public sealed record DdaiImportCapabilities(
    IReadOnlyList<string> Formats,
    int MaximumDecodedBytes,
    int MaximumEdge,
    long MaximumPixels);

public sealed record DdaiCapabilities(
    string ConnectorVersion,
    string ModVersion,
    string DungeondraftVersion,
    string RuntimeState,
    long? MapRevision,
    long? CatalogRevision,
    string CatalogState,
    IReadOnlyList<string> AssetCategories,
    DdaiPreviewCapabilities Preview,
    DdaiImportCapabilities Import,
    IReadOnlyList<OperationCapability> Operations);

public sealed class DdaiCapabilityService(
    AtomicMailbox mailbox,
    AssetCatalogRepository catalogRepository,
    TimeProvider timeProvider)
{
    public const string ConnectorVersion = "0.1.0";
    public const string ExpectedModVersion = "0.2.1";
    public const string ExpectedDungeondraftVersion = "1.2.0.1";
    private static readonly string[] SchemaOperations =
    [
        "terrain_stroke", "pattern_region", "colorable_pattern_region", "cave_region",
        "roof_region", "object_placement", "wall_polyline", "material_stroke",
        "portal_placement", "path_polyline", "light_placement", "simple_tile_region",
        "smart_tile_region", "smart_tile_double_region",
    ];
    private static readonly string[] PngFormat = ["png"];
    private static readonly HashSet<string> RuntimeReceiptProperties = new(StringComparer.Ordinal)
    {
        "schema_version", "event", "mod_version", "target_dungeondraft_version",
        "timestamp", "session_id", "supported_commands", "operation_certifications",
    };
    private static readonly IReadOnlyDictionary<string, RuntimeOperationSpec> OperationSpecs =
        new Dictionary<string, RuntimeOperationSpec>(StringComparer.Ordinal)
        {
            ["terrain_stroke"] = new("TerrainBrush", ["SetBiome", "SetSize", "UpdateBrush"], ["IsPainting", "brush"]),
            ["pattern_region"] = new("PatternShapeTool", [], ["Texture"]),
            ["colorable_pattern_region"] = new("PatternShapeTool", [], ["Texture"]),
            ["cave_region"] = new("CaveBrush", [], []),
            ["roof_region"] = new("RoofTool", ["DrawRect", "FinishShape"], ["isDrawing", "Texture"]),
            ["object_placement"] = new("ObjectTool", ["Confirm", "SetLayer", "SetSorting", "SetShadow", "SetBlockLight"], ["Texture", "Preview"]),
            ["wall_polyline"] = new("WallTool", ["EndWall"], ["isDrawing", "Texture"]),
            ["material_stroke"] = new("MaterialBrush", ["SetMaterial", "SetLayer", "SetSmooth"], ["Mesh"]),
            ["portal_placement"] = new("PortalTool", ["SetFreestanding", "FindBestLocation", "ChangeTexture"], ["Texture"]),
            ["path_polyline"] = new("PathTool", ["StartPath", "EndPath", "SetLayer"], ["isDrawing", "Texture", "ActivePath"]),
            ["light_placement"] = new("LightTool", ["CreatePreview", "ChangeColor", "SetShadows"], ["texture", "preview"]),
            ["simple_tile_region"] = new("FloorShapeTool", [], []),
            ["smart_tile_region"] = new("FloorShapeTool", [], []),
            ["smart_tile_double_region"] = new("FloorShapeTool", [], []),
        };
    private static readonly TimeSpan MaximumReceiptAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumReceiptFutureSkew = TimeSpan.FromSeconds(5);
    private const int MaximumHeartbeatBytes = 4_096;

    public DdaiCapabilities GetCapabilities()
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(catalogRepository);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var runtime = ReadLatestReceipt();
        var catalog = ReadCatalog();
        return new DdaiCapabilities(
            ConnectorVersion,
            ExpectedModVersion,
            runtime?.DungeondraftVersion ?? ExpectedDungeondraftVersion,
            RuntimeState(runtime),
            MapRevision: null,
            catalog?.Manifest.CatalogRevision,
            CatalogState(catalog),
            AssetCategory.All.ToArray(),
            new DdaiPreviewCapabilities(PngFormat.ToArray(), AssetCatalogRepository.MaximumPreviewBytes, GeneratedAssetStore.MaximumPreviewEdge),
            new DdaiImportCapabilities(PngFormat.ToArray(), GeneratedAssetStore.MaximumDecodedBytes, GeneratedAssetStore.MaximumEdge, GeneratedAssetStore.MaximumPixels),
            SchemaOperations.Select(operation => OperationCapabilityFor(operation, runtime)).ToArray());
    }

    private AcceptedAssetCatalog? ReadCatalog()
    {
        _ = catalogRepository.TryRefresh();
        return catalogRepository.GetCurrent();
    }

    private OperationCapability OperationCapabilityFor(string operation, RuntimeReceipt? receipt)
    {
        var certification = receipt?.OperationCertifications.SingleOrDefault(value => value.OperationType == operation);
        var reason = RuntimeReason(receipt, certification);
        return new OperationCapability(
            operation,
            SchemaSupported: true,
            RoutePresent: certification?.RoutePresent == true,
            MethodsReflectable: certification?.MethodsReflectable == true,
            PropertiesReadable: certification?.PropertiesReadable == true,
            RuntimeCertified: receipt is { Fresh: true, MatchesExpectedVersion: true } && certification?.RuntimeCertified == true,
            Busy: certification?.Busy == true,
            certification?.ToolName,
            certification?.RequiredMethods ?? [],
            certification?.RequiredProperties ?? [],
            reason);
    }

    private string RuntimeState(RuntimeReceipt? receipt) => receipt switch
    {
        null => "closed",
        { Fresh: false } => "stale",
        { MatchesExpectedVersion: false } => "incompatible",
        _ => "live",
    };

    private static string CatalogState(AcceptedAssetCatalog? catalog) => catalog switch
    {
        null => "closed",
        { Live: false } => "stale",
        _ => "live",
    };

    private static string RuntimeReason(RuntimeReceipt? receipt, RuntimeOperationCertification? certification) => receipt switch
    {
        null => "no_fresh_runtime_receipt",
        { Fresh: false } => "runtime_receipt_stale",
        { MatchesExpectedVersion: false } => "runtime_version_mismatch",
        _ when certification is null => "runtime_probe_missing",
        _ when certification.ExactDungeondraftVersion != ExpectedDungeondraftVersion => "runtime_version_mismatch",
        _ => certification.Reason,
    };

    private RuntimeReceipt? ReadLatestReceipt()
    {
        var root = mailbox.RootDirectory;
        var paths = new[]
        {
            Path.Combine(root, "runtime-receipt-slot-0.json"),
            Path.Combine(root, "runtime-receipt-slot-1.json"),
            Path.Combine(root, "runtime-receipt.json"),
        };
        RuntimeReceipt? latest = null;
        foreach (var path in paths)
        {
            var receipt = TryReadReceipt(path);
            if (receipt is not null && (latest is null || RuntimeReceiptIsNewer(receipt, latest)))
            {
                latest = receipt;
            }
        }

        return latest is not null && !latest.Fresh && HasFreshMatchingHeartbeat(latest.SessionId)
            ? latest with { Fresh = true }
            : latest;
    }

    private bool HasFreshMatchingHeartbeat(string expectedSessionId)
    {
        var directory = Path.Combine(mailbox.RootDirectory, "runtime-heartbeats");
        var now = timeProvider.GetUtcNow();
        for (var slot = 0; slot < 8; slot++)
        {
            var heartbeat = TryReadHeartbeat(Path.Combine(directory, $"heartbeat-slot-{slot}.json"));
            if (heartbeat is not null &&
                heartbeat.SessionId == expectedSessionId &&
                heartbeat.ModVersion == ExpectedModVersion &&
                heartbeat.Timestamp >= now - MaximumReceiptAge &&
                heartbeat.Timestamp <= now + MaximumReceiptFutureSkew)
            {
                return true;
            }
        }

        return false;
    }

    private RuntimeHeartbeat? TryReadHeartbeat(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(ReadBoundedReceipt(path, MaximumHeartbeatBytes));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4 ||
                !TryGetString(root, "schema_version", out var schemaVersion) || schemaVersion != "1.0" ||
                !TryGetString(root, "session_id", out var sessionId) || string.IsNullOrWhiteSpace(sessionId) ||
                !TryGetString(root, "mod_version", out var modVersion) ||
                !root.TryGetProperty("timestamp", out var timestampProperty) ||
                timestampProperty.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var timestamp = JsonSerializer.Deserialize<DateTimeOffset>(
                timestampProperty.GetRawText(),
                MailboxWireJson.Options);
            return new RuntimeHeartbeat(sessionId, modVersion, timestamp);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or NotSupportedException or OverflowException or Win32Exception)
        {
            return null;
        }
    }

    private RuntimeReceipt? TryReadReceipt(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(ReadBoundedReceipt(path, checked((int)AtomicMailbox.MaximumMessageBytes)));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !HasExactUniqueProperties(root, RuntimeReceiptProperties) ||
                !TryGetString(root, "schema_version", out var schemaVersion) || schemaVersion != "1.0" ||
                !TryGetString(root, "event", out var eventName) || eventName != "started" ||
                !TryGetString(root, "mod_version", out var modVersion) ||
                !TryGetString(root, "target_dungeondraft_version", out var dungeondraftVersion) ||
                !TryGetString(root, "session_id", out var sessionId) || string.IsNullOrWhiteSpace(sessionId) ||
                !TryGetString(root, "timestamp", out var timestampText) ||
                !DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) ||
                !TryGetCommands(root, out var supportedCommands) ||
                !TryGetOperationCertifications(root, out var operationCertifications))
            {
                return null;
            }

            var observedVersions = operationCertifications
                .Select(value => value.ExactDungeondraftVersion)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (observedVersions.Length != 1)
            {
                return null;
            }

            var now = timeProvider.GetUtcNow();
            var fresh = timestamp >= now - MaximumReceiptAge && timestamp <= now + MaximumReceiptFutureSkew;
            return new RuntimeReceipt(
                timestampText,
                fresh,
                modVersion == ExpectedModVersion &&
                    dungeondraftVersion == ExpectedDungeondraftVersion &&
                    observedVersions[0] == ExpectedDungeondraftVersion,
                observedVersions[0],
                supportedCommands,
                sessionId,
                operationCertifications);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or NotSupportedException or OverflowException or Win32Exception)
        {
            return null;
        }
    }

    private byte[] ReadBoundedReceipt(string path, int maximumBytes) =>
        new SafeLocalFileSystem(mailbox.RootDirectory).ReadBounded(path, maximumBytes);

    private static bool RuntimeReceiptIsNewer(RuntimeReceipt candidate, RuntimeReceipt current)
    {
        var timestampComparison = StringComparer.Ordinal.Compare(candidate.TimestampText, current.TimestampText);
        if (timestampComparison != 0)
        {
            return timestampComparison > 0;
        }

        var candidateSequence = RuntimeSessionSequence(candidate.SessionId);
        var currentSequence = RuntimeSessionSequence(current.SessionId);
        if (candidateSequence.First != currentSequence.First)
        {
            return candidateSequence.First > currentSequence.First;
        }

        if (candidateSequence.Second != currentSequence.Second)
        {
            return candidateSequence.Second > currentSequence.Second;
        }

        return StringComparer.Ordinal.Compare(candidate.SessionId, current.SessionId) > 0;
    }

    private static (long First, long Second) RuntimeSessionSequence(string value)
    {
        var parts = value.Split('-', StringSplitOptions.None);
        return parts.Length == 2 &&
            long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var first) &&
            long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var second)
            ? (first, second)
            : (-1, -1);
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { Length: > 0 } text)
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryGetCommands(JsonElement root, out IReadOnlyList<string> commands)
    {
        commands = [];
        if (!root.TryGetProperty("supported_commands", out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 } command)
            {
                return false;
            }

            values.Add(command);
        }

        commands = values;
        return true;
    }

    private static bool TryGetOperationCertifications(
        JsonElement root,
        out IReadOnlyList<RuntimeOperationCertification> certifications)
    {
        certifications = [];
        if (!root.TryGetProperty("operation_certifications", out var property) ||
            property.ValueKind != JsonValueKind.Array || property.GetArrayLength() != SchemaOperations.Length)
        {
            return false;
        }

        var values = new List<RuntimeOperationCertification>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 11 ||
                !TryGetString(item, "operation_type", out var operationType) ||
                !SchemaOperations.Contains(operationType, StringComparer.Ordinal) || !seen.Add(operationType) ||
                !TryGetString(item, "tool_name", out var toolName) || toolName.Length > 64 ||
                !TryGetStringArray(item, "required_methods", out var requiredMethods) ||
                !TryGetStringArray(item, "required_properties", out var requiredProperties) ||
                !OperationSpecMatches(operationType, toolName, requiredMethods, requiredProperties) ||
                !TryGetBoolean(item, "route_present", out var routePresent) ||
                !TryGetBoolean(item, "methods_reflectable", out var methodsReflectable) ||
                !TryGetBoolean(item, "properties_readable", out var propertiesReadable) ||
                !TryGetBoolean(item, "busy", out var busy) ||
                !TryGetString(item, "exact_dungeondraft_version", out var exactVersion) ||
                !TryGetBoolean(item, "runtime_certified", out var runtimeCertified) ||
                !TryGetString(item, "reason", out var reason) || reason.Length > 128 ||
                !CertificationTupleIsValid(
                    routePresent,
                    methodsReflectable,
                    propertiesReadable,
                    busy,
                    exactVersion,
                    runtimeCertified,
                    reason))
            {
                return false;
            }

            values.Add(new RuntimeOperationCertification(
                operationType,
                toolName,
                requiredMethods,
                requiredProperties,
                routePresent,
                methodsReflectable,
                propertiesReadable,
                busy,
                exactVersion,
                runtimeCertified,
                reason));
        }

        certifications = values;
        return true;
    }

    private static bool OperationSpecMatches(
        string operationType,
        string toolName,
        IReadOnlyList<string> requiredMethods,
        IReadOnlyList<string> requiredProperties) =>
        OperationSpecs.TryGetValue(operationType, out var spec) &&
        spec.ToolName == toolName &&
        spec.RequiredMethods.SequenceEqual(requiredMethods, StringComparer.Ordinal) &&
        spec.RequiredProperties.SequenceEqual(requiredProperties, StringComparer.Ordinal);

    private static bool CertificationTupleIsValid(
        bool routePresent,
        bool methodsReflectable,
        bool propertiesReadable,
        bool busy,
        string exactVersion,
        bool runtimeCertified,
        string reason)
    {
        if (!routePresent && (methodsReflectable || propertiesReadable || busy || runtimeCertified))
        {
            return false;
        }

        if (exactVersion != ExpectedDungeondraftVersion)
        {
            return !runtimeCertified && reason == "runtime_version_mismatch";
        }

        if (!routePresent)
        {
            return !methodsReflectable && !propertiesReadable && !busy && !runtimeCertified &&
                reason is "tool_unavailable" or "level_contract_unavailable" or "world_ui_contract_unavailable";
        }

        if (!runtimeCertified)
        {
            return reason == "executor_not_live_certified";
        }

        return reason == (busy ? "tool_busy" : "runtime_certified");
    }

    private static bool HasExactUniqueProperties(JsonElement root, IReadOnlySet<string> expected)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return seen.SetEquals(expected);
    }

    private static bool TryGetStringArray(JsonElement root, string propertyName, out IReadOnlyList<string> values)
    {
        values = [];
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array || property.GetArrayLength() > 32)
        {
            return false;
        }

        var result = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 128 } value)
            {
                return false;
            }

            result.Add(value);
        }

        values = result;
        return true;
    }

    private static bool TryGetBoolean(JsonElement root, string propertyName, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private sealed record RuntimeReceipt(
        string TimestampText,
        bool Fresh,
        bool MatchesExpectedVersion,
        string DungeondraftVersion,
        IReadOnlyList<string> SupportedCommands,
        string SessionId,
        IReadOnlyList<RuntimeOperationCertification> OperationCertifications);

    private sealed record RuntimeOperationCertification(
        string OperationType,
        string ToolName,
        IReadOnlyList<string> RequiredMethods,
        IReadOnlyList<string> RequiredProperties,
        bool RoutePresent,
        bool MethodsReflectable,
        bool PropertiesReadable,
        bool Busy,
        string ExactDungeondraftVersion,
        bool RuntimeCertified,
        string Reason);

    private sealed record RuntimeOperationSpec(
        string ToolName,
        IReadOnlyList<string> RequiredMethods,
        IReadOnlyList<string> RequiredProperties);

    private sealed record RuntimeHeartbeat(
        string SessionId,
        string ModVersion,
        DateTimeOffset Timestamp);
}
