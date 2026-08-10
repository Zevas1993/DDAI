using System.Text.Json;
using DDAI.Core.Mailbox;
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
}
