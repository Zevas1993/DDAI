using System.Text.Json;
using DDAI.App;
using DDAI.Core.Mailbox;

namespace DDAI.App.Tests;

public sealed class CliAndStatusTests
{
    [Fact]
    public void Parse_RecognizesEveryRequiredCommandAndExplicitPathOverrides()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ddai-cli-root"));
        var serve = CliParser.Parse(["serve", "--stdio", "--mailbox-root", root]);
        var status = CliParser.Parse(["status", "--json", "--mailbox-root", root, "--timeout-ms", "1250"]);

        Assert.Equal(DdaiCommand.Serve, serve.Command);
        Assert.True(serve.Stdio);
        Assert.Equal(root, serve.MailboxRoot);
        Assert.Equal(DdaiCommand.Status, status.Command);
        Assert.True(status.Json);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), status.Timeout);
        Assert.Equal(DdaiCommand.Setup, CliParser.Parse(["setup"]).Command);
        Assert.Equal(DdaiCommand.Diagnose, CliParser.Parse(["diagnose"]).Command);
        Assert.Equal(DdaiCommand.Uninstall, CliParser.Parse(["uninstall"]).Command);
    }

    [Theory]
    [InlineData("serve")]
    [InlineData("status")]
    [InlineData("unknown")]
    public void Parse_RejectsIncompleteOrUnknownInvocation(string command)
    {
        Assert.Throws<CliUsageException>(() => CliParser.Parse([command]));
    }

    [Fact]
    public async Task Status_UsesRealAtomicMailboxAndReturnsFakeModPayload()
    {
        using var sandbox = new TestDirectory();
        var mailbox = new AtomicMailbox(sandbox.Path);
        var service = new DdaiStatusService(mailbox, TimeProvider.System);
        var fakeMod = new FakeModHarness(mailbox);

        var worker = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (fakeMod.HandleOne())
                {
                    return;
                }

                await Task.Delay(10);
            }
        });

        var result = await service.GetStatusAsync(TimeSpan.FromSeconds(2));
        await worker;

        Assert.True(result.Success);
        Assert.Equal("ready", result.Payload.GetProperty("state").GetString());
        Assert.Equal("status", result.Command);
    }

    [Fact]
    public async Task StatusJson_WritesOneJsonDocumentToStdoutAndNoDiagnostics()
    {
        using var sandbox = new TestDirectory();
        var mailbox = new AtomicMailbox(sandbox.Path);
        var fakeMod = new FakeModHarness(mailbox);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var application = new CliApplication(stdout, stderr, TimeProvider.System);
        var worker = Task.Run(async () =>
        {
            while (!fakeMod.HandleOne())
            {
                await Task.Delay(10);
            }
        });

        var exitCode = await application.RunAsync(["status", "--json", "--mailbox-root", sandbox.Path, "--timeout-ms", "2000"]);
        await worker;

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(stdout.ToString());
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("ready", document.RootElement.GetProperty("payload").GetProperty("state").GetString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ddai-app-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
