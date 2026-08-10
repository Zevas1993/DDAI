using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DDAI.Core.Assets;
using Microsoft.Extensions.Hosting;

namespace DDAI.App.Assets;

public sealed class AssetPackNormalizationService
{
    public const int MaximumRequestsPerPass = 8;
    private const int MaximumRequestBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    private readonly string requestsRoot;
    private readonly string responsesRoot;

    public AssetPackNormalizationService(string mailboxRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mailboxRoot);
        var privateRoot = Path.Combine(Path.GetFullPath(mailboxRoot), "private", "pack-normalization");
        requestsRoot = Path.Combine(privateRoot, "requests");
        responsesRoot = Path.Combine(privateRoot, "responses");
        Directory.CreateDirectory(requestsRoot);
        Directory.CreateDirectory(responsesRoot);
    }

    public int ProcessPending()
    {
        var processed = 0;
        foreach (var requestPath in Directory.EnumerateFiles(requestsRoot, "*.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal)
                     .Take(MaximumRequestsPerPass))
        {
            try
            {
                var request = JsonSerializer.Deserialize<NormalizationRequest>(ReadBounded(requestPath), JsonOptions)
                    ?? throw new JsonException("Pack normalization request cannot be null.");
                var expectedId = Hash(request.Value);
                if (!string.Equals(request.SchemaVersion, "1.0", StringComparison.Ordinal) ||
                    !string.Equals(request.RequestId, expectedId, StringComparison.Ordinal) ||
                    !string.Equals(Path.GetFileNameWithoutExtension(requestPath), expectedId, StringComparison.Ordinal))
                {
                    continue;
                }

                var normalized = AssetReference.NormalizePackId(request.Value);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                var responsePath = Path.Combine(responsesRoot, expectedId + ".json");
                var response = new NormalizationResponse("1.0", expectedId, normalized);
                if (!File.Exists(responsePath))
                {
                    WriteImmutable(responsePath, response);
                }
                else
                {
                    var existing = JsonSerializer.Deserialize<NormalizationResponse>(ReadBounded(responsePath), JsonOptions);
                    if (existing != response)
                    {
                        continue;
                    }
                }

                File.Delete(requestPath);
                processed++;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or InvalidDataException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                // A malformed private request cannot terminate the MCP host or block other bounded work.
            }
        }

        return processed;
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumRequestBytes)
        {
            throw new InvalidDataException("Pack normalization data exceeds its byte limit.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void WriteImmutable<T>(string destinationPath, T value)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            "." + Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record NormalizationRequest(string SchemaVersion, string RequestId, string Value);

    private sealed record NormalizationResponse(string SchemaVersion, string RequestId, string NormalizedPackId);
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
