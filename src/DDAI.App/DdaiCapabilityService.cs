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
    bool RuntimeCertified,
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
    private static readonly string[] SchemaOperations = ["status", "apply_plan"];
    private static readonly string[] PngFormat = ["png"];
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
            ExpectedDungeondraftVersion,
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
        var reason = RuntimeReason(receipt, operation);
        return new OperationCapability(operation, SchemaSupported: true, RuntimeCertified: reason == "runtime_certified", reason);
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

    private static string RuntimeReason(RuntimeReceipt? receipt, string operation) => receipt switch
    {
        null => "no_fresh_runtime_receipt",
        { Fresh: false } => "runtime_receipt_stale",
        { MatchesExpectedVersion: false } => "runtime_version_mismatch",
        _ when !receipt.SupportedCommands.Contains(operation, StringComparer.Ordinal) => "bridge_operation_not_advertised",
        _ => "runtime_certified",
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
                !TryGetString(root, "schema_version", out var schemaVersion) || schemaVersion != "1.0" ||
                !TryGetString(root, "event", out var eventName) || eventName != "started" ||
                !TryGetString(root, "mod_version", out var modVersion) ||
                !TryGetString(root, "target_dungeondraft_version", out var dungeondraftVersion) ||
                !TryGetString(root, "session_id", out var sessionId) || string.IsNullOrWhiteSpace(sessionId) ||
                !TryGetString(root, "timestamp", out var timestampText) ||
                !DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) ||
                !TryGetCommands(root, out var supportedCommands))
            {
                return null;
            }

            var now = timeProvider.GetUtcNow();
            var fresh = timestamp >= now - MaximumReceiptAge && timestamp <= now + MaximumReceiptFutureSkew;
            return new RuntimeReceipt(
                timestampText,
                fresh,
                modVersion == ExpectedModVersion && dungeondraftVersion == ExpectedDungeondraftVersion,
                supportedCommands,
                sessionId);
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

    private sealed record RuntimeReceipt(
        string TimestampText,
        bool Fresh,
        bool MatchesExpectedVersion,
        IReadOnlyList<string> SupportedCommands,
        string SessionId);

    private sealed record RuntimeHeartbeat(
        string SessionId,
        string ModVersion,
        DateTimeOffset Timestamp);
}
