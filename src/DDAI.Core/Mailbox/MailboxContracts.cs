using System.Text.Json;

namespace DDAI.Core.Mailbox;

public sealed record MailboxRequest
{
    public const string CurrentSchemaVersion = "1.0";

    public required string SchemaVersion { get; init; }

    public required string RequestId { get; init; }

    public required string Command { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required JsonElement Payload { get; init; }

    public static MailboxRequest CreateStatus(string requestId, DateTimeOffset timestamp) => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        RequestId = requestId,
        Command = "status",
        Timestamp = timestamp,
        Payload = JsonSerializer.SerializeToElement(new { }),
    };
}

public sealed record MailboxErrorDetails(string Code, string Message, string? Path = null);

public sealed record MailboxResponse
{
    public required string SchemaVersion { get; init; }

    public required string RequestId { get; init; }

    public required string Command { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required bool Success { get; init; }

    public required JsonElement Payload { get; init; }

    public MailboxErrorDetails? Error { get; init; }
}

public sealed record ClaimedMailboxRequest(MailboxRequest Request, string ProcessingPath);
