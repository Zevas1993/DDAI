using System.Text.Json;
using DDAI.App;
using DDAI.Core.Assets;
using DDAI.Core.Mailbox;
using DDAI.Core.MapPlans;
using DDAI.Core.MapPlans.Operations;

namespace DDAI.App.Tests;

public sealed class DdaiUniversalPlanServiceTests
{
    [Fact]
    public async Task Validate_ReturnsCanonicalPlanOrderRevisionsAndFingerprint()
    {
        using var sandbox = new TestDirectory();
        var context = ValidContext();
        var service = CreateService(sandbox, context);
        var plan = ValidPlan("universal-validate-001");

        var result = await service.ValidateAsync(plan, TimeSpan.FromSeconds(1));

        Assert.True(result.Valid);
        Assert.Equal(MapPlanJson.Serialize(plan), MapPlanJson.Serialize(result.CanonicalPlan!));
        Assert.Equal(["wall-a"], result.ResolvedOperationIds);
        Assert.Equal(MapPlanJson.Fingerprint(plan), result.PlanFingerprint);
        Assert.Equal("map-session-001", result.MapId);
        Assert.Equal(42, result.MapRevision);
        Assert.Equal(7, result.CatalogRevision);
        Assert.Equal(new string('c', 64), result.CatalogFingerprint);
        Assert.Equal(["map_identity", "map_revision", "catalog_revision", "catalog_fingerprint", "runtime_certification"], result.RuntimeChecksRequired);
    }

    [Fact]
    public async Task Apply_InvalidOrStalePlanDoesNotPublish()
    {
        using var sandbox = new TestDirectory();
        var service = CreateService(sandbox, ValidContext());
        var plan = ValidPlan("universal-invalid-001") with { BaseRevision = 41 };

        var result = await service.ApplyAsync(plan, TimeSpan.FromMilliseconds(50));

        Assert.False(result.Success);
        Assert.Equal("invalid_plan", result.Error!.Code);
        Assert.Contains(result.Payload.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "map_revision_mismatch");
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(sandbox.Path, "requests")));
    }

    [Fact]
    public async Task Apply_NonFinitePlanReturnsInvalidWithoutPublishingOrThrowing()
    {
        using var sandbox = new TestDirectory();
        var service = CreateService(sandbox, ValidContext());
        var plan = ValidPlan("universal-nonfinite-001") with
        {
            Operations =
            [
                new WallPolylineOperation(
                    "wall-a",
                    "level-0",
                    "asset-Walls",
                    new GridPolyline([new GridPoint(double.NaN, 1), new GridPoint(8, 1)]),
                    false,
                    "#ffffffff"),
            ],
        };

        var result = await service.ApplyAsync(plan, TimeSpan.FromMilliseconds(50));

        Assert.False(result.Success);
        Assert.Equal("invalid_plan", result.Error!.Code);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(sandbox.Path, "requests")));
    }

    [Fact]
    public async Task Apply_UsesRealMailboxAndCorrelatesEveryRevision()
    {
        using var sandbox = new TestDirectory();
        var mailbox = new AtomicMailbox(sandbox.Path);
        var context = ValidContext();
        var service = new DdaiUniversalPlanService(mailbox, TimeProvider.System, new FakeContextProvider(context));
        var plan = ValidPlan("universal-apply-001");
        var worker = HandleOneAsync(mailbox, plan, context);

        var result = await service.ApplyAsync(plan, TimeSpan.FromSeconds(10));
        await worker;

        Assert.True(result.Success, $"{result.Error?.Code}: {result.Error?.Message}; {result.Payload.GetRawText()}");
        Assert.Equal("apply_plan", result.Command);
        Assert.Equal("map-session-001", result.Payload.GetProperty("map_id").GetString());
        Assert.Equal(43, result.Payload.GetProperty("map_revision").GetInt64());
        Assert.Equal(7, result.Payload.GetProperty("catalog_revision").GetInt64());
        Assert.Equal(new string('c', 64), result.Payload.GetProperty("catalog_fingerprint").GetString());
        Assert.Equal(MapPlanJson.Fingerprint(plan), result.Payload.GetProperty("plan_fingerprint").GetString());
    }

    [Fact]
    public async Task Apply_CorrelatedDuplicateRevalidatesLiveContext()
    {
        using var sandbox = new TestDirectory();
        var mailbox = new AtomicMailbox(sandbox.Path);
        var provider = new FakeContextProvider(ValidContext());
        var service = new DdaiUniversalPlanService(mailbox, TimeProvider.System, provider);
        var plan = ValidPlan("universal-retry-001");
        var worker = HandleOneAsync(mailbox, plan, ValidContext());
        var first = await service.ApplyAsync(plan, TimeSpan.FromSeconds(10));
        await worker;
        provider.Result = UniversalPlanContextResult.Success(ValidContext() with
        {
            Capabilities = ValidContext().Capabilities with { MapRevision = 43 },
        });
        var second = await service.ApplyAsync(plan, TimeSpan.FromMilliseconds(100));

        Assert.True(first.Success);
        Assert.True(second.Success, $"{second.Error?.Code}: {second.Error?.Message}; {second.Payload.GetRawText()}");
        Assert.Equal(first.Payload.GetRawText(), second.Payload.GetRawText());
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task Apply_UncorrelatedResponseFailsClosed()
    {
        using var sandbox = new TestDirectory();
        var mailbox = new AtomicMailbox(sandbox.Path);
        var context = ValidContext();
        var service = new DdaiUniversalPlanService(mailbox, TimeProvider.System, new FakeContextProvider(context));
        var plan = ValidPlan("universal-conflict-001");
        var worker = HandleOneAsync(mailbox, plan, context, catalogFingerprint: new string('d', 64));

        var result = await service.ApplyAsync(plan, TimeSpan.FromSeconds(10));
        await worker;

        Assert.False(result.Success);
        Assert.Equal("request_conflict", result.Error!.Code);
        Assert.Equal(MapPlanJson.Fingerprint(plan), result.Payload.GetProperty("plan_fingerprint").GetString());
    }

    [Fact]
    public async Task Apply_TimeoutRetainsUnknownOutcomeEvidence()
    {
        using var sandbox = new TestDirectory();
        var service = CreateService(sandbox, ValidContext());
        var plan = ValidPlan("universal-timeout-001");

        var result = await service.ApplyAsync(plan, TimeSpan.FromMilliseconds(500));

        Assert.False(result.Success);
        Assert.Equal("apply_timeout", result.Error!.Code);
        Assert.True(result.Payload.GetProperty("outcome_unknown").GetBoolean());
        Assert.True(result.Payload.GetProperty("retry_with_same_request_id").GetBoolean());
        Assert.Single(Directory.EnumerateFiles(Path.Combine(sandbox.Path, "requests")));
    }

    [Fact]
    public async Task Apply_ActiveDuplicateWaitsButDifferentPlanFailsImmediately()
    {
        using var sandbox = new TestDirectory();
        var mailbox = new AtomicMailbox(sandbox.Path);
        var service = new DdaiUniversalPlanService(mailbox, TimeProvider.System, new FakeContextProvider(ValidContext()));
        var original = ValidPlan("universal-active-001");
        Assert.True(mailbox.PublishRequest(MailboxRequest.CreateApplyPlan(original, DateTimeOffset.UtcNow)));

        var same = await service.ApplyAsync(original, TimeSpan.FromMilliseconds(500));
        var conflicting = await service.ApplyAsync(
            original with { Canvas = new MapCanvas(39, 30) },
            TimeSpan.FromSeconds(2));

        Assert.Equal("apply_timeout", same.Error!.Code);
        Assert.True(same.Payload.GetProperty("outcome_unknown").GetBoolean());
        Assert.Equal("request_conflict", conflicting.Error!.Code);
        Assert.False(conflicting.Payload.GetProperty("outcome_unknown").GetBoolean());
    }

    [Fact]
    public async Task Apply_UsesOneDeadlineAcrossContextAndResponse()
    {
        using var sandbox = new TestDirectory();
        var provider = new DelayedContextProvider(ValidContext(), TimeSpan.FromMilliseconds(200));
        var service = new DdaiUniversalPlanService(new AtomicMailbox(sandbox.Path), TimeProvider.System, provider);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await service.ApplyAsync(ValidPlan("universal-deadline-001"), TimeSpan.FromMilliseconds(500));

        Assert.Equal("apply_timeout", result.Error!.Code);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(sandbox.Path, "requests")));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(650), $"elapsed={stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Apply_ContextTimeoutDoesNotPublish()
    {
        using var sandbox = new TestDirectory();
        var provider = new DelayedContextProvider(ValidContext(), TimeSpan.FromSeconds(1));
        var service = new DdaiUniversalPlanService(new AtomicMailbox(sandbox.Path), TimeProvider.System, provider);

        var result = await service.ApplyAsync(ValidPlan("universal-context-timeout-001"), TimeSpan.FromMilliseconds(50));

        Assert.Equal("invalid_plan", result.Error!.Code);
        Assert.Contains(result.Payload.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "runtime_context_timeout");
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(sandbox.Path, "requests")));
    }

    [Fact]
    public async Task Apply_CancellationAfterPublishRetainsRequest()
    {
        using var sandbox = new TestDirectory();
        using var cancellation = new CancellationTokenSource();
        var service = CreateService(sandbox, ValidContext());
        var task = service.ApplyAsync(ValidPlan("universal-cancel-001"), TimeSpan.FromSeconds(10), cancellation.Token);
        var requests = Path.Combine(sandbox.Path, "requests");
        Assert.True(SpinWait.SpinUntil(() => Directory.EnumerateFiles(requests).Any(), TimeSpan.FromSeconds(2)));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Single(Directory.EnumerateFiles(requests));
    }

    [Fact]
    public async Task Apply_RejectsDuplicateCorrelationProperties()
    {
        using var sandbox = new TestDirectory();
        var mailbox = new AtomicMailbox(sandbox.Path);
        var context = ValidContext();
        var service = new DdaiUniversalPlanService(mailbox, TimeProvider.System, new FakeContextProvider(context));
        var plan = ValidPlan("universal-duplicate-response-001");
        var worker = HandleOneWithPayloadAsync(mailbox, plan, """
            {"plan_fingerprint":"FINGERPRINT","map_id":"map-session-001","map_id":"map-session-002","starting_map_revision":42,"map_revision":43,"catalog_revision":7,"catalog_fingerprint":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"}
            """.Replace("FINGERPRINT", MapPlanJson.Fingerprint(plan), StringComparison.Ordinal));

        var result = await service.ApplyAsync(plan, TimeSpan.FromSeconds(10));
        await worker;

        Assert.Equal("invalid_response", result.Error!.Code);
    }

    private static DdaiUniversalPlanService CreateService(TestDirectory sandbox, UniversalPlanRuntimeContext context) =>
        new(new AtomicMailbox(sandbox.Path), TimeProvider.System, new FakeContextProvider(context));

    private static UniversalPlanRuntimeContext ValidContext()
    {
        var asset = new AssetCatalogEntry(
            "asset-Walls",
            "Walls",
            "Stone wall",
            new string('a', 64),
            "official",
            "Official",
            [],
            [],
            null,
            true,
            false);
        return new UniversalPlanRuntimeContext(
            new UniversalMapPlanCatalog(7, new string('c', 64), [asset], new HashSet<string>(StringComparer.Ordinal)),
            new UniversalMapPlanCapabilities(
                "map-session-001",
                42,
                ["level-0"],
                new Dictionary<string, bool>(StringComparer.Ordinal) { ["wall_polyline"] = true }));
    }

    private static MapPlan ValidPlan(string requestId) => new()
    {
        SchemaVersion = MapPlan.CurrentSchemaVersion,
        RequestId = requestId,
        ExpectedMapId = "map-session-001",
        BaseRevision = 42,
        ExpectedCatalogRevision = 7,
        Mode = MapOperationMode.Add,
        CoordinateSystem = MapCoordinateSystem.Grid,
        Canvas = new MapCanvas(40, 30),
        Operations =
        [
            new WallPolylineOperation(
                "wall-a",
                "level-0",
                "asset-Walls",
                new GridPolyline([new GridPoint(1, 1), new GridPoint(8, 1)]),
                false,
                "#ffffffff"),
        ],
    };

    private static async Task HandleOneAsync(
        AtomicMailbox mailbox,
        MapPlan plan,
        UniversalPlanRuntimeContext context,
        string? catalogFingerprint = null)
    {
        for (var attempt = 0; attempt < 1_000; attempt++)
        {
            var claim = mailbox.ClaimNextRequest();
            if (claim is not null)
            {
                mailbox.PublishResponse(claim, new MailboxResponse
                {
                    SchemaVersion = MailboxRequest.CurrentSchemaVersion,
                    RequestId = claim.Request.RequestId,
                    Command = "apply_plan",
                    Timestamp = DateTimeOffset.UtcNow,
                    Success = true,
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        plan_fingerprint = MapPlanJson.Fingerprint(plan),
                        map_id = context.Capabilities.MapId,
                        starting_map_revision = context.Capabilities.MapRevision,
                        map_revision = context.Capabilities.MapRevision + 1,
                        catalog_revision = context.Catalog.Revision,
                        catalog_fingerprint = catalogFingerprint ?? context.Catalog.Fingerprint,
                    }),
                    Error = null,
                });
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The fake bridge did not receive the universal plan.");
    }

    private static async Task HandleOneWithPayloadAsync(AtomicMailbox mailbox, MapPlan plan, string payloadJson)
    {
        for (var attempt = 0; attempt < 1_000; attempt++)
        {
            var claim = mailbox.ClaimNextRequest();
            if (claim is not null)
            {
                using var document = JsonDocument.Parse(payloadJson);
                mailbox.PublishResponse(claim, new MailboxResponse
                {
                    SchemaVersion = MailboxRequest.CurrentSchemaVersion,
                    RequestId = plan.RequestId,
                    Command = "apply_plan",
                    Timestamp = DateTimeOffset.UtcNow,
                    Success = true,
                    Payload = document.RootElement.Clone(),
                });
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The fake bridge did not receive the universal plan.");
    }

    private sealed class FakeContextProvider(UniversalPlanRuntimeContext context) : IUniversalPlanContextProvider
    {
        public UniversalPlanContextResult Result { get; set; } = UniversalPlanContextResult.Success(context);

        public int Calls { get; private set; }

        public Task<UniversalPlanContextResult> GetAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class DelayedContextProvider(UniversalPlanRuntimeContext context, TimeSpan delay) : IUniversalPlanContextProvider
    {
        public async Task<UniversalPlanContextResult> GetAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return UniversalPlanContextResult.Success(context);
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ddai-universal-service", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
