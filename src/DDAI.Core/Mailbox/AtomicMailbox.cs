using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DDAI.Core.Mailbox;

public sealed class AtomicMailbox
{
    public const long MaximumMessageBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    private readonly string _requestsDirectory;
    private readonly string _processingDirectory;
    private readonly string _responsesDirectory;
    private readonly string _failedDirectory;
    private readonly string _journalDirectory;

    public AtomicMailbox(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        RootDirectory = Path.GetFullPath(rootDirectory);
        _requestsDirectory = CreateDirectory("requests");
        _processingDirectory = CreateDirectory("processing");
        _responsesDirectory = CreateDirectory("responses");
        _failedDirectory = CreateDirectory("failed");
        _journalDirectory = CreateDirectory("journal");
    }

    public string RootDirectory { get; }

    public bool PublishRequest(MailboxRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        if (JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions).LongLength > MaximumMessageBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Serialized requests must not exceed 1 MiB.");
        }

        var key = MessageKey(request.RequestId);
        if (ExistsInAnyState(key))
        {
            return false;
        }

        try
        {
            WriteAtomically(_requestsDirectory, key, request);
        }
        catch (IOException) when (File.Exists(MessagePath(_requestsDirectory, key)))
        {
            return false;
        }

        WriteAtomically(_journalDirectory, key, new JournalEntry(request.RequestId, "published", request.Timestamp));
        return true;
    }

    public ClaimedMailboxRequest? ClaimNextRequest()
    {
        foreach (var requestPath in Directory.EnumerateFiles(_requestsDirectory, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var processingPath = Path.Combine(_processingDirectory, Path.GetFileName(requestPath));
            try
            {
                File.Move(requestPath, processingPath, overwrite: false);
            }
            catch (IOException)
            {
                continue;
            }

            if (new FileInfo(processingPath).Length > MaximumMessageBytes)
            {
                MoveToFailed(processingPath, "oversize");
                continue;
            }

            try
            {
                var json = File.ReadAllText(processingPath, Encoding.UTF8);
                var request = JsonSerializer.Deserialize<MailboxRequest>(json, JsonOptions)
                    ?? throw new JsonException("Request JSON cannot be null.");
                ValidateRequest(request);
                if (!Path.GetFileNameWithoutExtension(processingPath).Equals(MessageKey(request.RequestId), StringComparison.Ordinal))
                {
                    throw new JsonException("Request ID does not match the mailbox file key.");
                }

                return new ClaimedMailboxRequest(request, processingPath);
            }
            catch (JsonException)
            {
                MoveToFailed(processingPath, "malformed");
            }
            catch (ArgumentException)
            {
                MoveToFailed(processingPath, "invalid");
            }
        }

        return null;
    }

    public void PublishResponse(ClaimedMailboxRequest claim, MailboxResponse response)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(response);
        if (!StringComparer.Ordinal.Equals(claim.Request.RequestId, response.RequestId) ||
            !StringComparer.Ordinal.Equals(claim.Request.Command, response.Command))
        {
            throw new ArgumentException("Response must correlate to the claimed request.", nameof(response));
        }

        var key = MessageKey(response.RequestId);
        WriteAtomically(_responsesDirectory, key, response);
        WriteAtomically(_journalDirectory, key + ".response", new JournalEntry(response.RequestId, "responded", response.Timestamp));
        File.Delete(claim.ProcessingPath);
    }

    public MailboxResponse? WaitForResponse(string requestId, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var path = MessagePath(_responsesDirectory, MessageKey(requestId));
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed <= timeout)
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                return JsonSerializer.Deserialize<MailboxResponse>(json, JsonOptions)
                    ?? throw new JsonException("Response JSON cannot be null.");
            }

            Thread.Sleep(10);
        }

        return null;
    }

    public int RecoverProcessingRequests()
    {
        var recovered = 0;
        foreach (var processingPath in Directory.EnumerateFiles(_processingDirectory, "*.json"))
        {
            var requestPath = Path.Combine(_requestsDirectory, Path.GetFileName(processingPath));
            try
            {
                File.Move(processingPath, requestPath, overwrite: false);
                recovered++;
            }
            catch (IOException)
            {
                MoveToFailed(processingPath, "recovery-conflict");
            }
        }

        return recovered;
    }

    private string CreateDirectory(string name)
    {
        var path = Path.Combine(RootDirectory, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private bool ExistsInAnyState(string key) =>
        File.Exists(MessagePath(_requestsDirectory, key)) ||
        File.Exists(MessagePath(_processingDirectory, key)) ||
        File.Exists(MessagePath(_responsesDirectory, key)) ||
        File.Exists(MessagePath(_failedDirectory, key)) ||
        File.Exists(MessagePath(_journalDirectory, key));

    private static string MessageKey(string requestId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(requestId));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string MessagePath(string directory, string key) => Path.Combine(directory, key + ".json");

    private static void ValidateRequest(MailboxRequest request)
    {
        if (!StringComparer.Ordinal.Equals(request.SchemaVersion, MailboxRequest.CurrentSchemaVersion))
        {
            throw new ArgumentException("Unsupported request schema version.", nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestId);
        if (request.RequestId.Contains(Path.DirectorySeparatorChar) ||
            request.RequestId.Contains(Path.AltDirectorySeparatorChar) ||
            request.RequestId is "." or "..")
        {
            throw new ArgumentException("Request ID must not contain a path traversal segment.", nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Command);
        if (request.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ArgumentException("Request payload is required.", nameof(request));
        }
    }

    private static void WriteAtomically<T>(string directory, string key, T message)
    {
        var destination = MessagePath(directory, key);
        var temporary = Path.Combine(directory, $".{key}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, message, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void MoveToFailed(string sourcePath, string reason)
    {
        var failedName = $"{Path.GetFileNameWithoutExtension(sourcePath)}.{reason}.{Guid.NewGuid():N}.json";
        File.Move(sourcePath, Path.Combine(_failedDirectory, failedName), overwrite: false);
    }

    private sealed record JournalEntry(string RequestId, string State, DateTimeOffset Timestamp);
}
