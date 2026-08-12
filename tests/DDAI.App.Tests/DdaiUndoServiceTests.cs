using System.Text.Json;
using DDAI.App;
using DDAI.Core.Assets;
using DDAI.Core.Mailbox;
using DDAI.Core.MapPlans;

namespace DDAI.App.Tests;

public sealed class DdaiUndoServiceTests
{
    [Fact]
    public void MailboxRequest_BindsUndoToOneCompletedJobAndRevision()
    {
        var timestamp = DateTimeOffset.Parse("2026-08-12T20:00:00Z");

        var request = MailboxRequest.CreateUndoLastJob(
            "undo-001", "apply-001", "map-session-001", 43, timestamp);

        Assert.Equal("undo-001", request.RequestId);
        Assert.Equal("undo_last_job", request.Command);
        Assert.Equal(timestamp, request.Timestamp);
        Assert.Equal("apply-001", request.Payload.GetProperty("target_request_id").GetString());
        Assert.Equal("map-session-001", request.Payload.GetProperty("expected_map_id").GetString());
        Assert.Equal(43, request.Payload.GetProperty("expected_map_revision").GetInt64());
        Assert.Equal(3, request.Payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Undo_PublishesCorrelatedRequestAndReturnsTheExactReversedJob()
    {
        using var sandbox = new TestDirectory();
        var mailbox = new AtomicMailbox(sandbox.Path);
        var service = CreateService(mailbox, revision: 43);
        var worker = HandleUndoAsync(mailbox, "undo-002", "apply-002", "map-session-001", 43);

        var result = await service.UndoLastJobAsync(
            "undo-002", "apply-002", "map-session-001", 43, TimeSpan.FromSeconds(10));
        await worker;

        Assert.True(result.Success, $"{result.Error?.Code}: {result.Error?.Message}");
        Assert.Equal("undo_last_job", result.Command);
        Assert.Equal("apply-002", result.Payload.GetProperty("target_request_id").GetString());
        Assert.Equal(44, result.Payload.GetProperty("map_revision").GetInt64());
        Assert.False(result.Payload.GetProperty("outcome_unknown").GetBoolean());
    }

    [Fact]
    public async Task Undo_RejectsStaleOrDifferentMapBeforePublication()
    {
        using var sandbox = new TestDirectory();
        var mailbox = new AtomicMailbox(sandbox.Path);
        var service = CreateService(mailbox, revision: 44);

        var stale = await service.UndoLastJobAsync(
            "undo-stale", "apply-stale", "map-session-001", 43, TimeSpan.FromSeconds(1));
        var differentMap = await service.UndoLastJobAsync(
            "undo-map", "apply-map", "other-map", 44, TimeSpan.FromSeconds(1));

        Assert.Equal("map_revision_mismatch", stale.Error?.Code);
        Assert.Equal("map_id_mismatch", differentMap.Error?.Code);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(sandbox.Path, "requests")));
    }

    [Fact]
    public async Task Undo_RejectsAnUncorrelatedResponse()
    {
        using var sandbox = new TestDirectory();
        var mailbox = new AtomicMailbox(sandbox.Path);
        var service = CreateService(mailbox, revision: 43);
        var worker = HandleUndoAsync(
            mailbox, "undo-conflict", "different-apply", "map-session-001", 43,
            claimedTarget: "apply-conflict");

        var result = await service.UndoLastJobAsync(
            "undo-conflict", "apply-conflict", "map-session-001", 43, TimeSpan.FromSeconds(10));
        await worker;

        Assert.False(result.Success);
        Assert.Equal("request_conflict", result.Error?.Code);
    }

    [Fact]
    public async Task Undo_ReplaysCompletedResponseAfterTheSuccessfulUndoAdvancedTheLiveRevision()
    {
        using var sandbox = new TestDirectory();
        var mailbox = new AtomicMailbox(sandbox.Path);
        var firstService = CreateService(mailbox, revision: 43);
        var worker = HandleUndoAsync(mailbox, "undo-replay", "apply-replay", "map-session-001", 43);

        var first = await firstService.UndoLastJobAsync(
            "undo-replay", "apply-replay", "map-session-001", 43, TimeSpan.FromSeconds(10));
        await worker;

        var advancedContextService = CreateService(mailbox, revision: 44);
        var replay = await advancedContextService.UndoLastJobAsync(
            "undo-replay", "apply-replay", "map-session-001", 43, TimeSpan.FromSeconds(1));

        Assert.True(first.Success);
        Assert.True(replay.Success, $"{replay.Error?.Code}: {replay.Error?.Message}");
        Assert.Equal(first.Payload.GetRawText(), replay.Payload.GetRawText());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(sandbox.Path, "requests")));
    }

    [Fact]
    public async Task Undo_RejectsAnOpenWorldResponsePayload()
    {
        using var sandbox = new TestDirectory();
        var mailbox = new AtomicMailbox(sandbox.Path);
        var service = CreateService(mailbox, revision: 43);
        var worker = HandleUndoAsync(
            mailbox, "undo-extra", "apply-extra", "map-session-001", 43,
            includeUnexpectedPayloadField: true);

        var result = await service.UndoLastJobAsync(
            "undo-extra", "apply-extra", "map-session-001", 43, TimeSpan.FromSeconds(10));
        await worker;

        Assert.False(result.Success);
        Assert.Equal("request_conflict", result.Error?.Code);
    }

    [Fact]
    public async Task Undo_ReturnsStructuredRequestRejectedWhenPublicationFails()
    {
        using var sandbox = new TestDirectory();
        var mailbox = new AtomicMailbox(sandbox.Path);
        var requests = Path.Combine(sandbox.Path, "requests");
        Directory.Delete(requests);
        File.WriteAllText(requests, "not-a-directory");
        var service = CreateService(mailbox, revision: 43);

        var result = await service.UndoLastJobAsync(
            "undo-publish-fault", "apply-publish-fault", "map-session-001", 43, TimeSpan.FromSeconds(1));

        Assert.False(result.Success);
        Assert.Equal("request_rejected", result.Error?.Code);
    }

    private static DdaiUniversalPlanService CreateService(AtomicMailbox mailbox, long revision) =>
        new(mailbox, TimeProvider.System, new FixedContextProvider(ValidContext(revision)));

    private static UniversalPlanRuntimeContext ValidContext(long revision) => new(
        new UniversalMapPlanCatalog(7, new string('c', 64), [], new HashSet<string>(StringComparer.Ordinal)),
        new UniversalMapPlanCapabilities(
            "map-session-001",
            revision,
            ["level-0"],
            new Dictionary<string, bool>(StringComparer.Ordinal) { ["wall_polyline"] = true }));

    private static async Task HandleUndoAsync(
        AtomicMailbox mailbox,
        string requestId,
        string responseTarget,
        string mapId,
        long startingRevision,
        string? claimedTarget = null,
        bool includeUnexpectedPayloadField = false)
    {
        for (var attempt = 0; attempt < 1_000; attempt++)
        {
            var claim = mailbox.ClaimNextRequest();
            if (claim is not null)
            {
                Assert.Equal("undo_last_job", claim.Request.Command);
                Assert.Equal(claimedTarget ?? responseTarget, claim.Request.Payload.GetProperty("target_request_id").GetString());
                mailbox.PublishResponse(claim, new MailboxResponse
                {
                    SchemaVersion = MailboxRequest.CurrentSchemaVersion,
                    RequestId = requestId,
                    Command = "undo_last_job",
                    Timestamp = DateTimeOffset.UtcNow,
                    Success = true,
                    Payload = includeUnexpectedPayloadField
                        ? JsonSerializer.SerializeToElement(new
                        {
                            target_request_id = responseTarget,
                            map_id = mapId,
                            starting_map_revision = startingRevision,
                            map_revision = startingRevision + 1,
                            outcome_unknown = false,
                            unexpected = true,
                        })
                        : JsonSerializer.SerializeToElement(new
                        {
                            target_request_id = responseTarget,
                            map_id = mapId,
                            starting_map_revision = startingRevision,
                            map_revision = startingRevision + 1,
                            outcome_unknown = false,
                        }),
                });
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The fake bridge did not receive the undo request.");
    }

    private sealed class FixedContextProvider(UniversalPlanRuntimeContext context) : IUniversalPlanContextProvider
    {
        public Task<UniversalPlanContextResult> GetAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(UniversalPlanContextResult.Success(context));
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ddai-undo", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
