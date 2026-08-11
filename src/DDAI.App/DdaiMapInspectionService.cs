using System.Diagnostics;
using System.Text.Json;
using DDAI.App.Assets;
using DDAI.Core.Mailbox;
using DDAI.Core.Maps;

namespace DDAI.App;

public sealed record DdaiMapInspectionResult(
    bool Success,
    string Command,
    MapSnapshotPage? Page,
    long? CatalogRevision,
    MailboxErrorDetails? Error);

public sealed class DdaiMapInspectionService(
    AtomicMailbox mailbox,
    AssetCatalogRepository catalogRepository,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan PollSlice = TimeSpan.FromMilliseconds(50);

    private readonly AtomicMailbox mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
    private readonly AssetCatalogRepository catalogRepository = catalogRepository ?? throw new ArgumentNullException(nameof(catalogRepository));
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<DdaiMapInspectionResult> InspectAsync(
        MapInspectionQuery query,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        MapSnapshotJson.ValidateQuery(query);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = catalogRepository.TryRefresh();
        var catalog = catalogRepository.GetCurrent();

        var request = MailboxRequest.CreateInspectMap(
            "ddai-inspect-" + Guid.NewGuid().ToString("N"),
            query,
            timeProvider.GetUtcNow());
        if (!mailbox.PublishRequest(request))
        {
            return Failure("request_conflict", "The inspection request could not be published.", catalog);
        }

        MailboxResponse? response;
        try
        {
            response = await WaitForResponseAsync(request.RequestId, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return Failure("invalid_response", "Dungeondraft returned a malformed inspection response.", catalog);
        }

        if (response is null)
        {
            return Failure("inspect_timeout", "Dungeondraft did not answer before the inspection deadline.", catalog);
        }

        if (!StringComparer.Ordinal.Equals(response.Command, "inspect_map"))
        {
            return Failure("invalid_response", "Dungeondraft returned an uncorrelated inspection response.", catalog);
        }

        if (!response.Success)
        {
            return response.Error is null
                ? Failure("invalid_response", "Dungeondraft returned a failed inspection without an error.", catalog)
                : new DdaiMapInspectionResult(false, "inspect_map", null, catalog?.Manifest.CatalogRevision, response.Error);
        }

        try
        {
            var page = MapSnapshotJson.DeserializePage(response.Payload.GetRawText());
            if (page.Items.Any(item => item.AssetRef is not null &&
                    (catalog is null || !catalog.Entries.Any(entry =>
                        string.Equals(entry.AssetRef, item.AssetRef, StringComparison.Ordinal)))))
            {
                return Failure(
                    "catalog_revision_mismatch",
                    "The inspection response references an asset outside the correlated catalog revision.",
                    catalog);
            }

            return new DdaiMapInspectionResult(
                true,
                "inspect_map",
                page,
                catalog?.Manifest.CatalogRevision,
                null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            return Failure("invalid_response", "Dungeondraft returned an invalid inspection page.", catalog);
        }
    }

    private async Task<MailboxResponse?> WaitForResponseAsync(
        string requestId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = timeout - stopwatch.Elapsed;
            if (remaining < TimeSpan.Zero)
            {
                break;
            }

            var slice = remaining < PollSlice ? remaining : PollSlice;
            var response = await Task.Run(
                () => mailbox.WaitForResponse(requestId, slice),
                cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                return response;
            }
        }
        while (stopwatch.Elapsed <= timeout);

        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    private static DdaiMapInspectionResult Failure(
        string code,
        string message,
        AcceptedAssetCatalog? catalog) =>
        new(false, "inspect_map", null, catalog?.Manifest.CatalogRevision, new MailboxErrorDetails(code, message));
}
