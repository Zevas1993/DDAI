using System.Text.Json;
using DDAI.Core.Mailbox;

namespace DDAI.App;

public sealed record DdaiStatusResult(
    bool Success,
    string Command,
    JsonElement Payload,
    MailboxErrorDetails? Error);

public sealed class DdaiStatusService(AtomicMailbox mailbox, TimeProvider timeProvider)
{
    public async Task<DdaiStatusResult> GetStatusAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(timeProvider);

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

        return new DdaiStatusResult(response.Success, response.Command, response.Payload.Clone(), response.Error);
    }

    private static DdaiStatusResult Failure(string code, string message) => new(
        false,
        "status",
        JsonSerializer.SerializeToElement(new { }),
        new MailboxErrorDetails(code, message));
}
