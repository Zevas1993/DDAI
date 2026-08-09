using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DDAI.Core.Mailbox;

public enum BridgeTransition
{
    NoWork,
    JournalCreated,
    ResponsePublished,
    ClaimDeleted,
    JournalDeleted,
    InvalidClaimFailed,
    InvalidJournalFailed,
    Blocked,
}

public static class BridgeWireJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public static JsonSerializerOptions OptionsIndented { get; } = new(Options)
    {
        WriteIndented = true,
    };
}

/// <summary>
/// Executable reference model for the Dungeondraft GDScript bridge's durable
/// claim-to-response protocol. Each call performs at most one durable state
/// transition so restart behavior can be verified at every crash boundary.
/// </summary>
public sealed class DungeondraftBridgeStateMachine
{
    private const long MaximumMessageBytes = AtomicMailbox.MaximumMessageBytes;
    private readonly string _root;
    private readonly Func<MailboxRequest, MailboxResponse> _prepareResponse;

    public DungeondraftBridgeStateMachine(
        string rootDirectory,
        Func<MailboxRequest, MailboxResponse> prepareResponse)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(prepareResponse);
        _root = Path.GetFullPath(rootDirectory);
        _prepareResponse = prepareResponse;
        foreach (var name in new[] { "requests", "processing", "responses", "failed", "journal" })
        {
            Directory.CreateDirectory(DirectoryPath(name));
        }
    }

    public BridgeTransition AdvanceClaim(string canonicalFileName)
    {
        if (!IsCanonicalMailboxFileName(canonicalFileName))
        {
            throw new ArgumentException("A lowercase SHA-256 JSON file name is required.", nameof(canonicalFileName));
        }

        var key = Path.GetFileNameWithoutExtension(canonicalFileName);
        var processingPath = StatePath("processing", canonicalFileName);
        var journalPath = StatePath("journal", canonicalFileName);
        var responsePath = StatePath("responses", canonicalFileName);
        if (!File.Exists(processingPath))
        {
            if (File.Exists(journalPath) &&
                TryReadValidatedJournal(journalPath, expectedFingerprint: null, expectedRequestId: null, out var staleJournal) &&
                StringComparer.Ordinal.Equals(Sha256(staleJournal.RequestId), key) &&
                TryReadValidatedStandaloneResponse(staleJournal.ResponseText, staleJournal.RequestId, out _) &&
                File.Exists(responsePath) &&
                ResponsesEquivalent(staleJournal.ResponseText, ReadBoundedText(responsePath)))
            {
                File.Delete(journalPath);
                return BridgeTransition.JournalDeleted;
            }

            return BridgeTransition.NoWork;
        }

        if (!TryReadValidatedRequest(processingPath, canonicalFileName, out var request, out var canonicalRequestText))
        {
            MoveToFailed(processingPath, key, "invalid-claim", ReadBoundedTextIfPossible(processingPath));
            return BridgeTransition.InvalidClaimFailed;
        }

        ReconcileRequestDuplicate(canonicalFileName, canonicalRequestText);
        var fingerprint = Sha256(canonicalRequestText);
        if (!File.Exists(journalPath))
        {
            var response = _prepareResponse(request);
            ValidateResponse(response, request);
            var responseText = JsonSerializer.Serialize(response, BridgeWireJson.Options);
            if (Encoding.UTF8.GetByteCount(responseText) > MaximumMessageBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(response), "Serialized responses must not exceed 1 MiB.");
            }

            var journal = new ResponseJournal(
                MailboxRequest.CurrentSchemaVersion,
                request.RequestId,
                fingerprint,
                responseText);
            WriteAtomically(journalPath, JsonSerializer.Serialize(journal, BridgeWireJson.Options));
            return BridgeTransition.JournalCreated;
        }

        if (!TryReadValidatedJournal(journalPath, fingerprint, request.RequestId, out var prepared) ||
            !TryReadValidatedResponse(prepared.ResponseText, request, out _))
        {
            MoveToFailed(journalPath, key, "invalid-journal", ReadBoundedTextIfPossible(journalPath));
            return BridgeTransition.InvalidJournalFailed;
        }

        if (!File.Exists(responsePath))
        {
            WriteAtomically(responsePath, prepared.ResponseText);
            return BridgeTransition.ResponsePublished;
        }

        var existingResponseText = ReadBoundedTextIfPossible(responsePath);
        if (existingResponseText is null || !ResponsesEquivalent(prepared.ResponseText, existingResponseText))
        {
            WriteConflictDiagnostic(key, prepared.ResponseText, existingResponseText ?? "<unreadable>");
            return BridgeTransition.Blocked;
        }

        File.Delete(processingPath);
        return BridgeTransition.ClaimDeleted;
    }

    public void RecoverAll()
    {
        foreach (var processingPath in Directory.EnumerateFiles(DirectoryPath("processing"), "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(processingPath);
            if (!IsCanonicalMailboxFileName(fileName))
            {
                MoveToFailed(processingPath, Path.GetFileNameWithoutExtension(fileName), "invalid-claim", ReadBoundedTextIfPossible(processingPath));
                continue;
            }

            for (var transitions = 0; transitions < 5; transitions++)
            {
                var transition = AdvanceClaim(fileName);
                if (transition is BridgeTransition.NoWork or BridgeTransition.Blocked or BridgeTransition.JournalDeleted)
                {
                    break;
                }
            }
        }

        foreach (var journalPath in Directory.EnumerateFiles(DirectoryPath("journal"), "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(journalPath);
            if (IsCanonicalMailboxFileName(fileName))
            {
                AdvanceClaim(fileName);
            }
        }
    }

    private void ReconcileRequestDuplicate(string canonicalFileName, string canonicalProcessingText)
    {
        var requestPath = StatePath("requests", canonicalFileName);
        if (!File.Exists(requestPath))
        {
            return;
        }

        if (TryReadValidatedRequest(requestPath, canonicalFileName, out _, out var canonicalDuplicateText) &&
            StringComparer.Ordinal.Equals(canonicalProcessingText, canonicalDuplicateText))
        {
            File.Delete(requestPath);
            return;
        }

        MoveToFailed(
            requestPath,
            Path.GetFileNameWithoutExtension(canonicalFileName),
            "duplicate-conflict",
            ReadBoundedTextIfPossible(requestPath));
    }

    private static bool TryReadValidatedRequest(
        string path,
        string canonicalFileName,
        out MailboxRequest request,
        out string canonicalText)
    {
        request = null!;
        canonicalText = string.Empty;
        try
        {
            request = JsonSerializer.Deserialize<MailboxRequest>(ReadBoundedText(path), BridgeWireJson.Options)
                ?? throw new JsonException("Request JSON cannot be null.");
            ValidateRequest(request, canonicalFileName);
            canonicalText = JsonSerializer.Serialize(request, BridgeWireJson.Options);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
        {
            return false;
        }
    }

    private static void ValidateRequest(MailboxRequest request, string canonicalFileName)
    {
        if (!StringComparer.Ordinal.Equals(request.SchemaVersion, MailboxRequest.CurrentSchemaVersion))
        {
            throw new ArgumentException("Unsupported request schema version.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestId);
        if (request.RequestId.Contains(Path.DirectorySeparatorChar) ||
            request.RequestId.Contains(Path.AltDirectorySeparatorChar) ||
            request.RequestId is "." or "..")
        {
            throw new ArgumentException("Request ID is unsafe.");
        }

        if (!StringComparer.Ordinal.Equals(Path.GetFileNameWithoutExtension(canonicalFileName), Sha256(request.RequestId)))
        {
            throw new ArgumentException("Request ID does not match the canonical file name.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Command);
        if (request.Timestamp == default)
        {
            throw new ArgumentException("Request timestamp is required.");
        }

        if (request.Payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new ArgumentException("Request payload is required.");
        }
    }

    private static void ValidateResponse(MailboxResponse response, MailboxRequest request)
    {
        if (!TryReadValidatedResponse(JsonSerializer.Serialize(response, BridgeWireJson.Options), request, out _))
        {
            throw new ArgumentException("Prepared response is invalid or does not correlate to its request.", nameof(response));
        }
    }

    private static bool TryReadValidatedResponse(string text, MailboxRequest request, out MailboxResponse response)
    {
        if (!TryReadValidatedStandaloneResponse(text, request.RequestId, out response))
        {
            return false;
        }

        return StringComparer.Ordinal.Equals(response.Command, request.Command);
    }

    private static bool TryReadValidatedStandaloneResponse(string text, string expectedRequestId, out MailboxResponse response)
    {
        response = null!;
        try
        {
            response = JsonSerializer.Deserialize<MailboxResponse>(text, BridgeWireJson.Options)
                ?? throw new JsonException("Response JSON cannot be null.");
            if (!StringComparer.Ordinal.Equals(response.SchemaVersion, MailboxRequest.CurrentSchemaVersion) ||
                !StringComparer.Ordinal.Equals(response.RequestId, expectedRequestId) ||
                string.IsNullOrWhiteSpace(response.Command) ||
                response.Timestamp == default ||
                response.Payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                (response.Success && response.Error is not null) ||
                (!response.Success && (response.Error is null || string.IsNullOrWhiteSpace(response.Error.Code) || string.IsNullOrWhiteSpace(response.Error.Message))))
            {
                throw new ArgumentException("Response envelope is invalid.");
            }

            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryReadValidatedJournal(
        string path,
        string? expectedFingerprint,
        string? expectedRequestId,
        out ResponseJournal journal)
    {
        journal = null!;
        try
        {
            journal = JsonSerializer.Deserialize<ResponseJournal>(ReadBoundedText(path), BridgeWireJson.Options)
                ?? throw new JsonException("Journal JSON cannot be null.");
            if (!StringComparer.Ordinal.Equals(journal.SchemaVersion, MailboxRequest.CurrentSchemaVersion) ||
                string.IsNullOrWhiteSpace(journal.RequestId) ||
                (expectedRequestId is not null && !StringComparer.Ordinal.Equals(journal.RequestId, expectedRequestId)) ||
                (expectedFingerprint is not null && !StringComparer.Ordinal.Equals(journal.RequestFingerprint, expectedFingerprint)) ||
                string.IsNullOrWhiteSpace(journal.ResponseText) ||
                Encoding.UTF8.GetByteCount(journal.ResponseText) > MaximumMessageBytes)
            {
                throw new JsonException("Journal is invalid.");
            }

            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool ResponsesEquivalent(string left, string right)
    {
        try
        {
            using var leftJson = JsonDocument.Parse(left);
            using var rightJson = JsonDocument.Parse(right);
            return JsonElement.DeepEquals(leftJson.RootElement, rightJson.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void WriteConflictDiagnostic(string key, string expected, string existing)
    {
        var fingerprint = Sha256(expected + "\n" + existing)[..16];
        var path = Path.Combine(DirectoryPath("failed"), $"{key}.response-conflict.{fingerprint}.json");
        if (!File.Exists(path))
        {
            var record = JsonSerializer.Serialize(new
            {
                schema_version = MailboxRequest.CurrentSchemaVersion,
                error = new { code = "response_conflict", message = "Existing response differs from the exact journaled response." },
                expected_response_fingerprint = Sha256(expected),
                existing_response_fingerprint = Sha256(existing),
            }, BridgeWireJson.Options);
            WriteAtomically(path, record);
        }
    }

    private void MoveToFailed(string sourcePath, string key, string reason, string? sourceText)
    {
        var fingerprint = Sha256(sourceText ?? Path.GetFileName(sourcePath))[..16];
        var destination = Path.Combine(DirectoryPath("failed"), $"{key}.{reason}.{fingerprint}.json");
        if (File.Exists(destination))
        {
            destination = Path.Combine(DirectoryPath("failed"), $"{key}.{reason}.{fingerprint}.{Guid.NewGuid():N}.json");
        }

        File.Move(sourcePath, destination, overwrite: false);
    }

    private string DirectoryPath(string name) => Path.Combine(_root, name);

    private string StatePath(string state, string canonicalFileName) => Path.Combine(DirectoryPath(state), canonicalFileName);

    private static bool IsCanonicalMailboxFileName(string fileName) =>
        fileName.Length == 69 &&
        fileName.EndsWith(".json", StringComparison.Ordinal) &&
        fileName.AsSpan(0, 64).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static string ReadBoundedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("Mailbox files must not exceed 1 MiB.");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        if (Encoding.UTF8.GetByteCount(text) > MaximumMessageBytes)
        {
            throw new InvalidDataException("Mailbox files must not exceed 1 MiB.");
        }

        return text;
    }

    private static string? ReadBoundedTextIfPossible(string path)
    {
        try
        {
            return ReadBoundedText(path);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteAtomically(string destinationPath, string text)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(text);
                writer.Flush();
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

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record ResponseJournal(
        string SchemaVersion,
        string RequestId,
        string RequestFingerprint,
        string ResponseText);
}
