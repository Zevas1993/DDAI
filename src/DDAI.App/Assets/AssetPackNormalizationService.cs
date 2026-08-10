using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DDAI.Core.Assets;
using Microsoft.Extensions.Hosting;

namespace DDAI.App.Assets;

public sealed class AssetPackNormalizationService
{
    public const int MaximumRequestsPerPass = 8;
    private const int MaximumDirectoryCandidates = 256;
    private const int MaximumRequestBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    private readonly SafeLocalFileSystem fileSystem;
    private readonly string requestsRoot;
    private readonly string responsesRoot;
    private readonly string quarantineRoot;

    public AssetPackNormalizationService(string mailboxRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mailboxRoot);
        fileSystem = new SafeLocalFileSystem(Path.GetFullPath(mailboxRoot));
        fileSystem.EnsureDirectory("private", "pack-normalization");
        requestsRoot = fileSystem.EnsureDirectory("private", "pack-normalization", "requests");
        responsesRoot = fileSystem.EnsureDirectory("private", "pack-normalization", "responses");
        quarantineRoot = fileSystem.EnsureDirectory("private", "pack-normalization", "quarantine");
    }

    public int ProcessPending()
    {
        using var lease = fileSystem.AcquireDirectoryLease();
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
        NormalizationRequest request;
        try
        {
            request = JsonSerializer.Deserialize<NormalizationRequest>(
                    fileSystem.ReadBounded(requestPath, MaximumRequestBytes),
                    JsonOptions)
                ?? throw new JsonException("Pack normalization request cannot be null.");
            var expectedId = Hash(request.Value);
            if (!string.Equals(request.SchemaVersion, "1.0", StringComparison.Ordinal) ||
                !string.Equals(request.RequestId, expectedId, StringComparison.Ordinal) ||
                !string.Equals(request.RequestContentHash, ComputeRequestContentHash(request.RequestId, request.Value), StringComparison.Ordinal) ||
                !string.Equals(Path.GetFileNameWithoutExtension(requestPath), expectedId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Pack normalization request identity is invalid.");
            }
        }
        catch (Exception exception) when (IsPrivateDataFailure(exception))
        {
            return QuarantineRequest(requestPath);
        }

        var normalized = AssetReference.NormalizePackId(request.Value);
        var responsePath = Path.Combine(responsesRoot, request.RequestId + ".json");
        var response = new NormalizationResponse(
            "1.0",
            request.RequestId,
            request.RequestContentHash,
            normalized.Length == 0 ? null : normalized);
        if (fileSystem.EntryExists(responsePath))
        {
            try
            {
                var existing = ReadResponse(responsePath);
                if (existing == response)
                {
                    fileSystem.DeleteOrdinaryOrLink(requestPath);
                    return true;
                }
            }
            catch (Exception exception) when (IsPrivateDataFailure(exception))
            {
            }

            try
            {
                fileSystem.QuarantineOrdinaryOrDeleteLink(responsePath, quarantineRoot);
            }
            catch (Exception exception) when (IsPrivateDataFailure(exception))
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
        catch (Exception exception) when (IsPrivateDataFailure(exception))
        {
            return false;
        }
    }

    private bool QuarantineRequest(string requestPath)
    {
        try
        {
            fileSystem.QuarantineOrdinaryOrDeleteLink(requestPath, quarantineRoot);
            return true;
        }
        catch (Exception exception) when (IsPrivateDataFailure(exception))
        {
            return false;
        }
    }

    internal static bool IsPrivateDataFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException
        or JsonException
        or InvalidDataException
        or ArgumentException
        or NotSupportedException
        or PathTooLongException
        or CryptographicException
        or System.ComponentModel.Win32Exception;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static string ComputeRequestContentHash(string requestId, string value) => Hash(
        "schema_version=3:1.0\nrequest_id=64:" + requestId + "\nvalue=" +
        Encoding.UTF8.GetByteCount(value) + ":" + value + "\n");

    private NormalizationResponse ReadResponse(string responsePath)
    {
        using var document = JsonDocument.Parse(fileSystem.ReadBounded(responsePath, MaximumRequestBytes));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schema_version", out var schema) || schema.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("request_id", out var requestId) || requestId.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("request_content_hash", out var requestHash) || requestHash.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("normalized_pack_id", out var normalized) ||
            normalized.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            throw new JsonException("Pack normalization response is malformed.");
        }

        return new NormalizationResponse(
            schema.GetString()!,
            requestId.GetString()!,
            requestHash.GetString()!,
            normalized.ValueKind == JsonValueKind.Null ? null : normalized.GetString());
    }

    private sealed record NormalizationRequest(
        string SchemaVersion,
        string RequestId,
        string RequestContentHash,
        string Value);

    private sealed record NormalizationResponse(
        string SchemaVersion,
        string RequestId,
        string RequestContentHash,
        string? NormalizedPackId);
}

public sealed class AssetPackNormalizationWorker(AssetPackNormalizationService service) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            service.ProcessPending();
            await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken);
        }
    }
}
