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
        EnsureMaximumMessageSize(request, nameof(request));

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
                var json = ReadBoundedText(processingPath);
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
        ValidateRequest(claim.Request);
        var key = MessageKey(claim.Request.RequestId);
        var expectedProcessingPath = MessagePath(_processingDirectory, key);
        if (!Path.GetFullPath(claim.ProcessingPath).Equals(expectedProcessingPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Claim must refer to its canonical mailbox processing path.", nameof(claim));
        }

        if (!File.Exists(expectedProcessingPath))
        {
            throw new InvalidOperationException("Claim is no longer present in the mailbox processing directory.");
        }

        ValidateResponse(response);
        if (!StringComparer.Ordinal.Equals(claim.Request.RequestId, response.RequestId) ||
            !StringComparer.Ordinal.Equals(claim.Request.Command, response.Command))
        {
            throw new ArgumentException("Response must correlate to the claimed request.", nameof(response));
        }

        EnsureMaximumMessageSize(response, nameof(response));
        var responsePath = MessagePath(_responsesDirectory, key);
        if (File.Exists(responsePath))
        {
            var existing = ReadValidatedResponse(responsePath, claim.Request.RequestId);
            if (!StringComparer.Ordinal.Equals(existing.Command, response.Command) ||
                !StringComparer.Ordinal.Equals(JsonSerializer.Serialize(existing, JsonOptions), JsonSerializer.Serialize(response, JsonOptions)))
            {
                throw new IOException("A different response has already been published for this request.");
            }
        }
        else
        {
            WriteAtomically(_responsesDirectory, key, response);
        }

        WriteJournalIfAbsent(key + ".response", new JournalEntry(response.RequestId, "responded", response.Timestamp));
        File.Delete(expectedProcessingPath);
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
                return ReadValidatedResponse(path, requestId);
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
            if (HasCompletedCorrelatedResponse(processingPath))
            {
                File.Delete(processingPath);
                continue;
            }

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

        ValidateRequestId(request.RequestId, nameof(request));

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Command);
        if (request.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ArgumentException("Request payload is required.", nameof(request));
        }
    }

    private static void ValidateResponse(MailboxResponse response)
    {
        if (!StringComparer.Ordinal.Equals(response.SchemaVersion, MailboxRequest.CurrentSchemaVersion))
        {
            throw new ArgumentException("Unsupported response schema version.", nameof(response));
        }

        ValidateRequestId(response.RequestId, nameof(response));
        ArgumentException.ThrowIfNullOrWhiteSpace(response.Command);
        if (response.Timestamp == default)
        {
            throw new ArgumentException("Response timestamp is required.", nameof(response));
        }

        if (response.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ArgumentException("Response payload is required.", nameof(response));
        }

        if (response.Success && response.Error is not null)
        {
            throw new ArgumentException("Successful responses cannot carry an error.", nameof(response));
        }

        if (!response.Success &&
            (response.Error is null || string.IsNullOrWhiteSpace(response.Error.Code) || string.IsNullOrWhiteSpace(response.Error.Message)))
        {
            throw new ArgumentException("Failed responses require structured error details.", nameof(response));
        }
    }

    private static void ValidateRequestId(string requestId, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (requestId.Contains(Path.DirectorySeparatorChar) ||
            requestId.Contains(Path.AltDirectorySeparatorChar) ||
            requestId is "." or "..")
        {
            throw new ArgumentException("Request ID must not contain a path traversal segment.", parameterName);
        }
    }

    private static void EnsureMaximumMessageSize<T>(T message, string parameterName)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions).LongLength > MaximumMessageBytes)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Serialized messages must not exceed 1 MiB.");
        }
    }

    private MailboxResponse ReadValidatedResponse(string path, string awaitedRequestId)
    {
        try
        {
            var response = JsonSerializer.Deserialize<MailboxResponse>(ReadBoundedText(path), JsonOptions)
                ?? throw new JsonException("Response JSON cannot be null.");
            ValidateResponse(response);
            if (!StringComparer.Ordinal.Equals(response.RequestId, awaitedRequestId))
            {
                throw new JsonException("Response request ID does not match the awaited request.");
            }

            return response;
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("Response envelope is invalid.", exception);
        }
    }

    private bool HasCompletedCorrelatedResponse(string processingPath)
    {
        try
        {
            var request = JsonSerializer.Deserialize<MailboxRequest>(ReadBoundedText(processingPath), JsonOptions)
                ?? throw new JsonException("Request JSON cannot be null.");
            ValidateRequest(request);
            var key = MessageKey(request.RequestId);
            if (!Path.GetFileNameWithoutExtension(processingPath).Equals(key, StringComparison.Ordinal))
            {
                return false;
            }

            var responsePath = MessagePath(_responsesDirectory, key);
            if (!File.Exists(responsePath))
            {
                return false;
            }

            var response = ReadValidatedResponse(responsePath, request.RequestId);
            return StringComparer.Ordinal.Equals(response.Command, request.Command);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ReadBoundedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("Mailbox messages must not exceed 1 MiB.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = stream.Read(bytes, offset, bytes.Length - offset);
            if (count == 0)
            {
                throw new InvalidDataException("Mailbox message ended before its advertised length.");
            }

            offset += count;
        }

        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("Mailbox message grew beyond its bounded length while being read.");
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private void WriteJournalIfAbsent(string key, JournalEntry entry)
    {
        var path = MessagePath(_journalDirectory, key);
        if (File.Exists(path))
        {
            return;
        }

        try
        {
            WriteAtomically(_journalDirectory, key, entry);
        }
        catch (IOException) when (File.Exists(path))
        {
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
