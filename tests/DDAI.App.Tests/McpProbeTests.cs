using System.Text.Json;
using DDAI.Core.Mailbox;
using DDAI.Core.MapPlans;
using DDAI.McpProbe;

namespace DDAI.App.Tests;

[Collection("Published executable")]
public sealed class McpProbeTests : IClassFixture<PublishedExecutableFixture>
{
    private readonly PublishedExecutableFixture fixture;

    public McpProbeTests(PublishedExecutableFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public void Parse_RequiresAndNormalizesTheLiveProbeOptions()
    {
        var root = Path.Combine(Path.GetTempPath(), "ddai-mcp-probe-options", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "ddai.exe");
            var mailbox = Path.Combine(root, "mailbox");
            File.WriteAllBytes(executable, [0x4D, 0x5A]);

            var options = McpProbeOptions.Parse([
                "--exe", executable,
                "--mailbox-root", mailbox,
                "--timeout-ms", "2500",
            ]);

            Assert.Equal(Path.GetFullPath(executable), options.ExecutablePath);
            Assert.Equal(Path.GetFullPath(mailbox), options.MailboxRoot);
            Assert.Equal(TimeSpan.FromMilliseconds(2500), options.Timeout);
            Assert.Equal("ddai_status", options.ToolName);
            Assert.Null(options.PlanFilePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("ddai_validate_plan")]
    [InlineData("ddai_apply_plan")]
    public void Parse_AcceptsPlanToolsWithAnAbsoluteExistingPlanFile(string toolName)
    {
        var root = Path.Combine(Path.GetTempPath(), "ddai-mcp-probe-plan-options", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "ddai.exe");
            var planFile = Path.Combine(root, "plan.json");
            File.WriteAllBytes(executable, [0x4D, 0x5A]);
            File.WriteAllText(planFile, MapPlanJson.Serialize(ValidPlan("probe-options-001")));

            var options = McpProbeOptions.Parse([
                "--exe", executable,
                "--mailbox-root", Path.Combine(root, "mailbox"),
                "--timeout-ms", "2500",
                "--tool", toolName,
                "--plan-file", planFile,
            ]);

            Assert.Equal(toolName, options.ToolName);
            Assert.Equal(Path.GetFullPath(planFile), options.PlanFilePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("unknown-tool")]
    [InlineData("plan-with-status")]
    [InlineData("plan-tool-without-plan")]
    [InlineData("relative-plan")]
    [InlineData("missing-plan")]
    public void Parse_RejectsInvalidToolAndPlanFileCombinations(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "ddai-mcp-probe-plan-invalid", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "ddai.exe");
            var mailbox = Path.Combine(root, "mailbox");
            var planFile = Path.Combine(root, "plan.json");
            File.WriteAllBytes(executable, [0x4D, 0x5A]);
            File.WriteAllText(planFile, "{}");
            var arguments = scenario switch
            {
                "unknown-tool" => BaseArgs(executable, mailbox).Concat(["--tool", "other"]).ToArray(),
                "plan-with-status" => BaseArgs(executable, mailbox).Concat(["--plan-file", planFile]).ToArray(),
                "plan-tool-without-plan" => BaseArgs(executable, mailbox).Concat(["--tool", "ddai_apply_plan"]).ToArray(),
                "relative-plan" => BaseArgs(executable, mailbox).Concat(["--tool", "ddai_validate_plan", "--plan-file", "plan.json"]).ToArray(),
                "missing-plan" => BaseArgs(executable, mailbox).Concat(["--tool", "ddai_validate_plan", "--plan-file", Path.Combine(root, "missing.json")]).ToArray(),
                _ => throw new InvalidOperationException("Unknown test scenario."),
            };

            Assert.Throws<ArgumentException>(() => McpProbeOptions.Parse(arguments));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("relative-executable")]
    [InlineData("missing-executable")]
    [InlineData("relative-mailbox")]
    [InlineData("zero-timeout")]
    [InlineData("non-numeric-timeout")]
    public void Parse_RejectsMalformedLiveProbeOptions(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "ddai-mcp-probe-invalid", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "ddai.exe");
            var mailbox = Path.Combine(root, "mailbox");
            File.WriteAllBytes(executable, [0x4D, 0x5A]);
            var arguments = scenario switch
            {
                "unknown" => new[] { "--unknown", "value" },
                "duplicate" => new[] { "--exe", executable, "--exe", executable, "--mailbox-root", mailbox, "--timeout-ms", "2500" },
                "missing" => new[] { "--exe", executable, "--mailbox-root", mailbox },
                "relative-executable" => new[] { "--exe", "ddai.exe", "--mailbox-root", mailbox, "--timeout-ms", "2500" },
                "missing-executable" => new[] { "--exe", Path.Combine(root, "missing.exe"), "--mailbox-root", mailbox, "--timeout-ms", "2500" },
                "relative-mailbox" => new[] { "--exe", executable, "--mailbox-root", "mailbox", "--timeout-ms", "2500" },
                "zero-timeout" => new[] { "--exe", executable, "--mailbox-root", mailbox, "--timeout-ms", "0" },
                "non-numeric-timeout" => new[] { "--exe", executable, "--mailbox-root", mailbox, "--timeout-ms", "soon" },
                _ => throw new InvalidOperationException("Unknown test scenario."),
            };

            Assert.Throws<ArgumentException>(() => McpProbeOptions.Parse(arguments));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task RunAsync_UsesOfficialSdkAgainstPublishedExecutableAndRealMailbox()
    {
        var root = Path.Combine(Path.GetTempPath(), "ddai-mcp-live-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var workerCancellation = new CancellationTokenSource();
        try
        {
            var mailboxRoot = Path.Combine(root, "mailbox");
            var mailbox = new AtomicMailbox(mailboxRoot);
            var fakeMod = new FakeModHarness(mailbox);
            var worker = Task.Run(async () =>
            {
                while (!workerCancellation.IsCancellationRequested)
                {
                    if (!fakeMod.HandleOne())
                    {
                        await Task.Delay(10, workerCancellation.Token);
                    }
                }
            }, workerCancellation.Token);
            var options = McpProbeOptions.Parse([
                "--exe", fixture.ExecutablePath,
                "--mailbox-root", mailboxRoot,
                "--timeout-ms", "2500",
            ]);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var text = await new McpStatusProbe().RunAsync(options, deadline.Token);

            using var document = JsonDocument.Parse(text);
            Assert.True(document.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("ready", document.RootElement.GetProperty("payload").GetProperty("state").GetString());

            workerCancellation.Cancel();
            try
            {
                await worker;
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            workerCancellation.Cancel();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task RunAsync_UsesOfficialSdkForValidateAndApplyPlan()
    {
        var root = Path.Combine(Path.GetTempPath(), "ddai-mcp-plan-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var workerCancellation = new CancellationTokenSource();
        try
        {
            var mailboxRoot = Path.Combine(root, "mailbox");
            var planFile = Path.Combine(root, "plan.json");
            var plan = ValidPlan("probe-plan-001");
            File.WriteAllText(planFile, MapPlanJson.Serialize(plan));
            var mailbox = new AtomicMailbox(mailboxRoot);
            var fakeMod = new FakeModHarness(mailbox);
            var worker = Task.Run(async () =>
            {
                while (!workerCancellation.IsCancellationRequested)
                {
                    if (!fakeMod.HandleOne())
                    {
                        await Task.Delay(10, workerCancellation.Token);
                    }
                }
            }, workerCancellation.Token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var validation = await new McpToolProbe().RunAsync(
                ParsePlanOptions(fixture.ExecutablePath, mailboxRoot, planFile, "ddai_validate_plan"),
                deadline.Token);
            var application = await new McpToolProbe().RunAsync(
                ParsePlanOptions(fixture.ExecutablePath, mailboxRoot, planFile, "ddai_apply_plan"),
                deadline.Token);

            using var validationDocument = JsonDocument.Parse(validation);
            using var applicationDocument = JsonDocument.Parse(application);
            Assert.True(validationDocument.RootElement.GetProperty("valid").GetBoolean());
            Assert.True(applicationDocument.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("room-entrance", applicationDocument.RootElement.GetProperty("payload").GetProperty("room_id").GetString());
            Assert.Equal(MapPlanJson.Fingerprint(plan), applicationDocument.RootElement.GetProperty("payload").GetProperty("plan_fingerprint").GetString());

            workerCancellation.Cancel();
            try
            {
                await worker;
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            workerCancellation.Cancel();
            Directory.Delete(root, recursive: true);
        }
    }

    private static McpProbeOptions ParsePlanOptions(
        string executable,
        string mailboxRoot,
        string planFile,
        string toolName) =>
        McpProbeOptions.Parse([
            "--exe", executable,
            "--mailbox-root", mailboxRoot,
            "--timeout-ms", "2500",
            "--tool", toolName,
            "--plan-file", planFile,
        ]);

    private static string[] BaseArgs(string executable, string mailbox) =>
        ["--exe", executable, "--mailbox-root", mailbox, "--timeout-ms", "2500"];

    private static MapPlan ValidPlan(string requestId) => new()
    {
        SchemaVersion = MapPlan.CurrentSchemaVersion,
        RequestId = requestId,
        BaseRevision = 0,
        Mode = MapOperationMode.Add,
        Canvas = new MapCanvas(40, 30),
        Rooms = [new MapRoom("room-entrance", 8, 7, 10, 8)],
    };
}
