using DDAI.Core.Mailbox;
using System.Text.Json;

namespace DDAI.Core.Tests;

public sealed class AtomicMailboxTests
{
    [Fact]
    public void FakeMod_ClaimsStatusRequestAndWritesCorrelatedSuccessResponse()
    {
        using var sandbox = new MailboxSandbox();
        var mailbox = new AtomicMailbox(sandbox.Root);
        var request = MailboxRequest.CreateStatus("status-001", DateTimeOffset.Parse("2026-08-09T12:00:00Z"));

        mailbox.PublishRequest(request);
        var handled = new FakeModHarness(mailbox).HandleOne();
        var response = mailbox.WaitForResponse("status-001", TimeSpan.FromMilliseconds(100));

        Assert.True(handled);
        Assert.NotNull(response);
        Assert.Equal("status-001", response.RequestId);
        Assert.True(response.Success);
        Assert.Equal("status", response.Command);
        Assert.Equal("ready", response.Payload.GetProperty("state").GetString());
    }

    [Fact]
    public void PublishRequest_RejectsTraversalRequestIdWithoutWritingOutsideMailbox()
    {
        using var sandbox = new MailboxSandbox();
        var mailbox = new AtomicMailbox(sandbox.Root);
        var request = MailboxRequest.CreateStatus("../escape", DateTimeOffset.Parse("2026-08-09T12:00:00Z"));

        Assert.Throws<ArgumentException>(() => mailbox.PublishRequest(request));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(sandbox.Root)!, "escape.json")));
    }

    [Fact]
    public void ClaimNextRequest_IgnoresSameDirectoryTemporaryPartialWrite()
    {
        using var sandbox = new MailboxSandbox();
        var mailbox = new AtomicMailbox(sandbox.Root);
        var temporary = Path.Combine(sandbox.Root, "requests", ".status-partial.tmp");

        File.WriteAllText(temporary, "{\"schema_version\":\"1.0\"");

        Assert.Null(mailbox.ClaimNextRequest());
        Assert.True(File.Exists(temporary));
    }

    [Fact]
    public void PublishRequest_IsIdempotentForDuplicateRequestId()
    {
        using var sandbox = new MailboxSandbox();
        var mailbox = new AtomicMailbox(sandbox.Root);
        var first = MailboxRequest.CreateStatus("duplicate-001", DateTimeOffset.Parse("2026-08-09T12:00:00Z"));
        var duplicate = MailboxRequest.CreateStatus("duplicate-001", DateTimeOffset.Parse("2026-08-09T12:01:00Z"));

        Assert.True(mailbox.PublishRequest(first));
        Assert.False(mailbox.PublishRequest(duplicate));
        Assert.True(new FakeModHarness(mailbox).HandleOne());
        Assert.False(new FakeModHarness(mailbox).HandleOne());
    }

    [Fact]
    public void PublishRequest_ConcurrentDuplicateRequestIdHasOneWinner()
    {
        using var sandbox = new MailboxSandbox();
        var mailbox = new AtomicMailbox(sandbox.Root);
        var outcomes = new bool[2];
        var exceptions = new Exception?[2];

        Parallel.For(0, 2, index =>
        {
            try
            {
                outcomes[index] = mailbox.PublishRequest(
                    MailboxRequest.CreateStatus("duplicate-race-001", DateTimeOffset.Parse("2026-08-09T12:00:00Z")));
            }
            catch (Exception exception)
            {
                exceptions[index] = exception;
            }
        });

        Assert.DoesNotContain(exceptions, exception => exception is not null);
        Assert.Equal(1, outcomes.Count(outcome => outcome));
    }

    [Fact]
    public void ClaimNextRequest_MovesMalformedJsonToFailed()
    {
        using var sandbox = new MailboxSandbox();
        var mailbox = new AtomicMailbox(sandbox.Root);
        File.WriteAllText(Path.Combine(sandbox.Root, "requests", "not-json.json"), "{ definitely not json");

        Assert.Null(mailbox.ClaimNextRequest());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(sandbox.Root, "processing")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(sandbox.Root, "failed"), "*.json"));
    }

    [Fact]
    public void ClaimNextRequest_MovesOversizeMessageToFailed()
    {
        using var sandbox = new MailboxSandbox();
        var mailbox = new AtomicMailbox(sandbox.Root);
        var path = Path.Combine(sandbox.Root, "requests", "oversize.json");
        File.WriteAllText(path, new string('x', checked((int)AtomicMailbox.MaximumMessageBytes + 1)));

        Assert.Null(mailbox.ClaimNextRequest());
        var failed = Assert.Single(Directory.EnumerateFiles(Path.Combine(sandbox.Root, "failed"), "*.json"));
        Assert.True(new FileInfo(failed).Length > AtomicMailbox.MaximumMessageBytes);
    }

    [Fact]
    public void PublishRequest_RejectsPayloadOverOneMiB()
    {
        using var sandbox = new MailboxSandbox();
        var mailbox = new AtomicMailbox(sandbox.Root);
        var request = new MailboxRequest
        {
            SchemaVersion = MailboxRequest.CurrentSchemaVersion,
            RequestId = "too-large-001",
            Command = "status",
            Timestamp = DateTimeOffset.Parse("2026-08-09T12:00:00Z"),
            Payload = JsonSerializer.SerializeToElement(new { text = new string('x', checked((int)AtomicMailbox.MaximumMessageBytes)) }),
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => mailbox.PublishRequest(request));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(sandbox.Root, "requests")));
    }

    [Fact]
    public void WaitForResponse_ReturnsNullWhenDeadlineExpires()
    {
        using var sandbox = new MailboxSandbox();
        var mailbox = new AtomicMailbox(sandbox.Root);

        var response = mailbox.WaitForResponse("never-published", TimeSpan.FromMilliseconds(25));

        Assert.Null(response);
    }

    [Fact]
    public void RecoverProcessingRequests_RequeuesClaimAfterCrash()
    {
        using var sandbox = new MailboxSandbox();
        var mailbox = new AtomicMailbox(sandbox.Root);
        mailbox.PublishRequest(MailboxRequest.CreateStatus("recovery-001", DateTimeOffset.Parse("2026-08-09T12:00:00Z")));
        Assert.NotNull(mailbox.ClaimNextRequest());

        var restartedMailbox = new AtomicMailbox(sandbox.Root);

        Assert.Equal(1, restartedMailbox.RecoverProcessingRequests());
        Assert.True(new FakeModHarness(restartedMailbox).HandleOne());
        Assert.True(restartedMailbox.WaitForResponse("recovery-001", TimeSpan.FromMilliseconds(100))!.Success);
    }

    private sealed class MailboxSandbox : IDisposable
    {
        public MailboxSandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "DDAI.Core.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
