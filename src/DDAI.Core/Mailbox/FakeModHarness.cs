using System.Text.Json;

namespace DDAI.Core.Mailbox;

public sealed class FakeModHarness(AtomicMailbox mailbox)
{
    public bool HandleOne()
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        var claim = mailbox.ClaimNextRequest();
        if (claim is null)
        {
            return false;
        }

        var response = claim.Request.Command == "status"
            ? SuccessResponse(claim.Request)
            : ErrorResponse(claim.Request);
        mailbox.PublishResponse(claim, response);
        return true;
    }

    private static MailboxResponse SuccessResponse(MailboxRequest request) => new()
    {
        SchemaVersion = MailboxRequest.CurrentSchemaVersion,
        RequestId = request.RequestId,
        Command = request.Command,
        Timestamp = DateTimeOffset.UtcNow,
        Success = true,
        Payload = JsonSerializer.SerializeToElement(new { state = "ready" }),
    };

    private static MailboxResponse ErrorResponse(MailboxRequest request) => new()
    {
        SchemaVersion = MailboxRequest.CurrentSchemaVersion,
        RequestId = request.RequestId,
        Command = request.Command,
        Timestamp = DateTimeOffset.UtcNow,
        Success = false,
        Payload = JsonSerializer.SerializeToElement(new { }),
        Error = new MailboxErrorDetails("unsupported_command", "The fake mod only supports status."),
    };
}
