using System.Text.Json;
using DDAI.App.Assets;
using DDAI.Core.Assets;
using DDAI.Core.Mailbox;

namespace DDAI.App;

public sealed record DdaiStatusResult(
    bool Success,
    string Command,
    JsonElement Payload,
    MailboxErrorDetails? Error,
    string? MapRevision,
    long? CatalogRevision,
    long? CatalogCacheAgeMilliseconds,
    bool? CatalogLive,
    bool? CatalogComplete,
    int? CatalogEntryCount,
    IReadOnlyList<AssetCatalogError> CatalogErrors,
    IReadOnlyList<string> BridgeCapabilities);

public sealed class DdaiStatusService
{
    private readonly AtomicMailbox mailbox;
    private readonly TimeProvider timeProvider;
    private readonly AssetCatalogRepository catalogRepository;

    public DdaiStatusService(
        AtomicMailbox mailbox,
        TimeProvider timeProvider,
        AssetCatalogRepository? catalogRepository = null)
    {
        this.mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.catalogRepository = catalogRepository ?? new AssetCatalogRepository(
            Path.Combine(mailbox.RootDirectory, "catalog"),
            mailbox.RootDirectory,
            timeProvider);
    }

    public async Task<DdaiStatusResult> GetStatusAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var request = MailboxRequest.CreateStatus(
            $"ddai-status-{Guid.NewGuid():N}",
            timeProvider.GetUtcNow());
        if (!mailbox.PublishRequest(request))
        {
            return Failure("request_conflict", "A request with the generated identifier already exists.");
        }

        var response = await Task.Run(
            () => mailbox.WaitForResponse(request.RequestId, timeout),
            cancellationToken);
        if (response is null)
        {
            return Failure("status_timeout", "Dungeondraft did not answer before the status deadline.");
        }

        return CreateResult(response.Success, response.Command, response.Payload.Clone(), response.Error);
    }

    private DdaiStatusResult Failure(string code, string message) => CreateResult(
        false,
        "status",
        JsonSerializer.SerializeToElement(new { }),
        new MailboxErrorDetails(code, message));

    private DdaiStatusResult CreateResult(
        bool success,
        string command,
        JsonElement payload,
        MailboxErrorDetails? error)
    {
        _ = catalogRepository.TryRefresh();
        var catalog = catalogRepository.GetCurrent();
        var catalogAge = catalog is null
            ? (TimeSpan?)null
            : timeProvider.GetUtcNow() - catalog.Manifest.SnapshotAt;
        if (catalogAge < TimeSpan.Zero)
        {
            catalogAge = TimeSpan.Zero;
        }

        return new DdaiStatusResult(
            success,
            command,
            payload,
            error,
            ReadMapRevision(payload),
            catalog?.Manifest.CatalogRevision,
            catalogAge is null ? null : (long)Math.Ceiling(catalogAge.Value.TotalMilliseconds),
            catalogAge is null ? null : catalogAge <= AssetCatalogRepository.MaximumLiveAge,
            catalog?.Manifest.Complete,
            catalog?.Entries.Count,
            catalog?.Manifest.Errors.ToArray() ?? [],
            ReadBridgeCapabilities(payload));
    }

    private static string? ReadMapRevision(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in new[] { "map_revision", "revision" })
        {
            if (payload.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ReadBridgeCapabilities(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("supported_commands", out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var capabilities = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } capability)
            {
                return [];
            }

            capabilities.Add(capability);
        }

        return capabilities;
    }
}
