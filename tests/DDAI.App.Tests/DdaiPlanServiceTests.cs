using System.Text.Json;
using DDAI.App;
using DDAI.Core.Mailbox;
using DDAI.Core.MapPlans;

namespace DDAI.App.Tests;

public sealed class DdaiPlanServiceTests
{
    [Fact]
    public void Validate_ReturnsCanonicalPlanAndOrderedRuntimeChecks()
    {
        using var sandbox = new TestDirectory();
        var service = new DdaiPlanService(new AtomicMailbox(sandbox.Path), TimeProvider.System);
        var plan = ValidPlan("validate-001");

        var result = service.Validate(plan);

        Assert.True(result.Valid);
        Assert.Equal(MapPlanJson.Serialize(plan), MapPlanJson.Serialize(result.CanonicalPlan!));
        Assert.Equal(["canvas", "grid_scale", "active_level", "wall_tool", "wall_count"], result.RuntimeChecksRequired);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Apply_InvalidPlanReturnsEveryIssueWithoutPublishing()
    {
        using var sandbox = new TestDirectory();
        var mailbox = new AtomicMailbox(sandbox.Path);
        var service = new DdaiPlanService(mailbox, TimeProvider.System);
        var plan = ValidPlan("invalid-001") with
        {
            Mode = MapOperationMode.Patch,
            BaseRevision = 2,
            Rooms = [],
        };

        var result = await service.ApplyAsync(plan, TimeSpan.FromMilliseconds(50));

        Assert.False(result.Success);
        Assert.Equal("apply_plan", result.Command);
        Assert.Equal("invalid_plan", result.Error!.Code);
        Assert.Equal(
            ["unsupported_mode", "unsupported_base_revision", "invalid_room_count"],
            result.Payload.GetProperty("issues").EnumerateArray()
                .Select(issue => issue.GetProperty("code").GetString()));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(sandbox.Path, "requests")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(sandbox.Path, "processing")));
    }

    [Fact]
    public async Task Apply_UsesRealMailboxAndReturnsFakeBridgeResult()
    {
        using var sandbox = new TestDirectory();
        var mailbox = new AtomicMailbox(sandbox.Path);
        var service = new DdaiPlanService(mailbox, TimeProvider.System);
        var plan = ValidPlan("apply-service-001");
        var worker = HandleOneAsync(mailbox);

        var result = await service.ApplyAsync(plan, TimeSpan.FromSeconds(2));
        await worker;

        Assert.True(result.Success);
        Assert.Equal("apply_plan", result.Command);
        Assert.True(result.Payload.GetProperty("applied").GetBoolean());
        Assert.Equal(1, result.Payload.GetProperty("created_walls").GetInt32());
        Assert.Equal("room-entrance", result.Payload.GetProperty("room_id").GetString());
        Assert.True(result.Payload.GetProperty("undo_available").GetBoolean());
        Assert.Equal("Use Dungeondraft Undo once", result.Payload.GetProperty("undo_instruction").GetString());
        Assert.Equal(MapPlanJson.Fingerprint(plan), result.Payload.GetProperty("plan_fingerprint").GetString());
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Apply_IdenticalRetryReturnsExistingResponseWithoutRepublishing()
    {
        using var sandbox = new TestDirectory();
        var mailbox = new AtomicMailbox(sandbox.Path);
        var service = new DdaiPlanService(mailbox, TimeProvider.System);
        var plan = ValidPlan("apply-retry-001");
        var worker = HandleOneAsync(mailbox);

        var first = await service.ApplyAsync(plan, TimeSpan.FromSeconds(2));
        await worker;
        var second = await service.ApplyAsync(plan, TimeSpan.FromMilliseconds(100));

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(first.Payload.GetRawText(), second.Payload.GetRawText());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(sandbox.Path, "requests")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(sandbox.Path, "processing")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(sandbox.Path, "responses")));
    }

    [Fact]
    public async Task Apply_DifferentPlanWithSameRequestIdFailsClosed()
    {
        using var sandbox = new TestDirectory();
        var mailbox = new AtomicMailbox(sandbox.Path);
        var service = new DdaiPlanService(mailbox, TimeProvider.System);
        var original = ValidPlan("apply-conflict-001");
        var conflicting = original with
        {
            Rooms = [new MapRoom("room-entrance", 9, 7, 10, 8)],
        };
        var worker = HandleOneAsync(mailbox);

        var first = await service.ApplyAsync(original, TimeSpan.FromSeconds(2));
        await worker;
        var second = await service.ApplyAsync(conflicting, TimeSpan.FromMilliseconds(100));

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Equal("request_conflict", second.Error!.Code);
        Assert.Equal(MapPlanJson.Fingerprint(conflicting), second.Payload.GetProperty("plan_fingerprint").GetString());
        Assert.Single(Directory.EnumerateFiles(Path.Combine(sandbox.Path, "responses")));
    }

    [Fact]
    public async Task Apply_TimeoutReportsUnknownOutcomeAndRetainsRequestEvidence()
    {
        using var sandbox = new TestDirectory();
        var mailbox = new AtomicMailbox(sandbox.Path);
        var service = new DdaiPlanService(mailbox, TimeProvider.System);
        var plan = ValidPlan("apply-timeout-001");

        var result = await service.ApplyAsync(plan, TimeSpan.FromMilliseconds(25));

        Assert.False(result.Success);
        Assert.Equal("apply_timeout", result.Error!.Code);
        Assert.Contains("outcome is unknown", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("same request_id", result.Error.Message, StringComparison.Ordinal);
        Assert.True(result.Payload.GetProperty("outcome_unknown").GetBoolean());
        Assert.True(result.Payload.GetProperty("retry_with_same_request_id").GetBoolean());
        Assert.Equal(MapPlanJson.Fingerprint(plan), result.Payload.GetProperty("plan_fingerprint").GetString());
        Assert.Single(Directory.EnumerateFiles(Path.Combine(sandbox.Path, "requests")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(sandbox.Path, "processing")));
    }

    private static async Task HandleOneAsync(AtomicMailbox mailbox)
    {
        var fakeMod = new FakeModHarness(mailbox);
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (fakeMod.HandleOne())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The fake mod did not receive an apply-plan request.");
    }

    private static MapPlan ValidPlan(string requestId) => new()
    {
        SchemaVersion = MapPlan.CurrentSchemaVersion,
        RequestId = requestId,
        BaseRevision = 0,
        Mode = MapOperationMode.Add,
        Canvas = new MapCanvas(40, 30),
        Rooms = [new MapRoom("room-entrance", 8, 7, 10, 8)],
    };

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ddai-plan-service-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
