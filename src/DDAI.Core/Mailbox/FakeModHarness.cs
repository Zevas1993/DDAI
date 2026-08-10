using System.Text.Json;
using DDAI.Core.MapPlans;

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

        var response = claim.Request.Command switch
        {
            "status" => StatusResponse(claim.Request),
            "apply_plan" => ApplyPlanResponse(claim.Request),
            _ => ErrorResponse(claim.Request),
        };
        mailbox.PublishResponse(claim, response);
        return true;
    }

    private static MailboxResponse StatusResponse(MailboxRequest request) => new()
    {
        SchemaVersion = MailboxRequest.CurrentSchemaVersion,
        RequestId = request.RequestId,
        Command = request.Command,
        Timestamp = DateTimeOffset.UtcNow,
        Success = true,
        Payload = JsonSerializer.SerializeToElement(new { state = "ready" }),
    };

    private static MailboxResponse ApplyPlanResponse(MailboxRequest request)
    {
        var plan = MapPlanJson.Deserialize(request.Payload.GetRawText());
        var validation = RectangularRoomPlanValidator.Validate(plan);
        if (!validation.IsValid)
        {
            return new MailboxResponse
            {
                SchemaVersion = MailboxRequest.CurrentSchemaVersion,
                RequestId = request.RequestId,
                Command = request.Command,
                Timestamp = DateTimeOffset.UtcNow,
                Success = false,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    plan_fingerprint = MapPlanJson.Fingerprint(plan),
                }),
                Error = new MailboxErrorDetails("invalid_plan", "The fake mod rejected the map plan."),
            };
        }

        return new MailboxResponse
        {
            SchemaVersion = MailboxRequest.CurrentSchemaVersion,
            RequestId = request.RequestId,
            Command = request.Command,
            Timestamp = DateTimeOffset.UtcNow,
            Success = true,
            Payload = JsonSerializer.SerializeToElement(new
            {
                applied = true,
                created_walls = 1,
                room_id = plan.Rooms[0].Id,
                undo_available = true,
                undo_instruction = "Use Dungeondraft Undo once",
                plan_fingerprint = MapPlanJson.Fingerprint(plan),
            }),
        };
    }

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
