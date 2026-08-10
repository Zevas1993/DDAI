using System.Text.Json;

namespace DDAI.App.Assets;

public sealed class AssetCatalogPublicationAdviceService
{
    private const int MaximumRequestsPerPass = 8;
    private const int MaximumDirectoryCandidates = 256;
    private const int MaximumBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    private readonly SafeLocalFileSystem fileSystem;
    private readonly AssetCatalogPublicationAdvisor advisor;
    private readonly string requestsRoot;
    private readonly string responsesRoot;
    private readonly string quarantineRoot;

    public AssetCatalogPublicationAdviceService(string mailboxRoot, TimeProvider timeProvider)
    {
        fileSystem = new SafeLocalFileSystem(Path.GetFullPath(mailboxRoot));
        requestsRoot = fileSystem.EnsureDirectory("private", "catalog-publication", "requests");
        responsesRoot = fileSystem.EnsureDirectory("private", "catalog-publication", "responses");
        quarantineRoot = fileSystem.EnsureDirectory("private", "catalog-publication", "quarantine");
        advisor = new AssetCatalogPublicationAdvisor(Path.Combine(Path.GetFullPath(mailboxRoot), "catalog"), timeProvider);
    }

    public int ProcessPending()
    {
        var processed = 0;
        foreach (var requestPath in fileSystem
                     .EnumerateFiles(requestsRoot, "*.json", MaximumDirectoryCandidates)
                     .Take(MaximumRequestsPerPass))
        {
            if (ProcessOne(requestPath))
            {
                processed++;
            }
        }

        return processed;
    }

    private bool ProcessOne(string requestPath)
    {
        PublicationRequest request;
        try
        {
            request = JsonSerializer.Deserialize<PublicationRequest>(fileSystem.ReadBounded(requestPath, MaximumBytes), JsonOptions)
                ?? throw new JsonException("Catalog publication request cannot be null.");
            if (request.SchemaVersion != "1.0" || !IsCanonicalHash(request.RequestId) ||
                Path.GetFileNameWithoutExtension(requestPath) != request.RequestId || request.WallClockRevision < 0)
            {
                throw new InvalidDataException("Catalog publication request is invalid.");
            }
        }
        catch (Exception exception) when (AssetPackNormalizationService.IsPrivateDataFailure(exception))
        {
            return Quarantine(requestPath);
        }

        var advice = advisor.InspectForPublication(request.WallClockRevision);
        var response = new PublicationResponse(
            "1.0",
            request.RequestId,
            advice.Success,
            advice.CatalogRevision,
            advice.SlotIndex,
            advice.ErrorCode);
        var responsePath = Path.Combine(responsesRoot, request.RequestId + ".json");
        if (File.Exists(responsePath))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<PublicationResponse>(
                    fileSystem.ReadBounded(responsePath, MaximumBytes), JsonOptions);
                if (existing == response)
                {
                    fileSystem.DeleteOrdinaryOrLink(requestPath);
                    return true;
                }
            }
            catch (Exception exception) when (AssetPackNormalizationService.IsPrivateDataFailure(exception))
            {
            }

            try
            {
                fileSystem.QuarantineOrdinaryOrDeleteLink(responsePath, quarantineRoot);
            }
            catch (Exception exception) when (AssetPackNormalizationService.IsPrivateDataFailure(exception))
            {
                return false;
            }
        }

        try
        {
            fileSystem.WriteImmutable(responsePath, response, JsonOptions);
            fileSystem.DeleteOrdinaryOrLink(requestPath);
            return true;
        }
        catch (Exception exception) when (AssetPackNormalizationService.IsPrivateDataFailure(exception))
        {
            return false;
        }
    }

    private bool Quarantine(string path)
    {
        try
        {
            fileSystem.QuarantineOrdinaryOrDeleteLink(path, quarantineRoot);
            return true;
        }
        catch (Exception exception) when (AssetPackNormalizationService.IsPrivateDataFailure(exception))
        {
            return false;
        }
    }

    private static bool IsCanonicalHash(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record PublicationRequest(string SchemaVersion, string RequestId, long WallClockRevision);

    private sealed record PublicationResponse(
        string SchemaVersion,
        string RequestId,
        bool Success,
        long? CatalogRevision,
        int? SlotIndex,
        string? ErrorCode);
}
