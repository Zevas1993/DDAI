using System.Security.Cryptography;
using System.Text;

using DDAI.Core.Mailbox;

namespace DDAI.Core.Tests;

public sealed class MailboxBridgeConformanceTests
{
    [Theory]
    [InlineData("0001-01-01T00:00:00Z", false)]
    [InlineData("0001-01-01T00:00:00+00:00", false)]
    [InlineData("0001-01-01T00:00:00-00:00", false)]
    [InlineData("0001-01-01T00:00:00+01:00", false)]
    [InlineData("0001-01-01T00:00:00-01:00", true)]
    [InlineData("0001-01-01T01:00:00+01:00", false)]
    [InlineData("0001-01-01T01:00:00.0000001+01:00", true)]
    [InlineData("0001-01-01T01:00:00.00000001+01:00", false)]
    [InlineData("0001-01-01T14:00:00+14:00", false)]
    [InlineData("0001-01-01T14:00:00.0000001+14:00", true)]
    [InlineData("0001-01-01T14:00:00.0000000000000001+14:00", false)]
    [InlineData("2026-08-09T12:00:00", true)]
    [InlineData("2026-08-09T12:00:00Z", true)]
    [InlineData("2026-08-09T12:00:00+00:00", true)]
    [InlineData("2026-08-09T12:00:00-00:00", true)]
    [InlineData("2026-08-09T12:00:00-04:00", true)]
    [InlineData("2026-08-09T12:00:00+14:00", true)]
    [InlineData("2026-08-09T12:00:00+14:01", false)]
    [InlineData("9999-12-31T23:59:59-01:00", false)]
    [InlineData("9999-12-31T23:59:59+01:00", true)]
    [InlineData("9999-12-31T23:59:59.9999999+00:00", true)]
    [InlineData("9999-12-31T23:59:59.9999999-00:00", true)]
    [InlineData("9999-12-31T22:59:59.9999999-01:00", true)]
    [InlineData("9999-12-31T23:00:00-01:00", false)]
    [InlineData("9999-12-31T09:59:59.9999999-14:00", true)]
    [InlineData("9999-12-31T10:00:00-14:00", false)]
    [InlineData("2026-02-30T12:00:00Z", false)]
    public void RawTimestampTruthTable_MatchesAtomicMailboxSystemTextJsonBehavior(string timestamp, bool accepted)
    {
        var root = Path.Combine(Path.GetTempPath(), "ddai-timestamp-truth-tests", Guid.NewGuid().ToString("N"));
        try
        {
            const string requestId = "timestamp-truth-table";
            var mailbox = new AtomicMailbox(root);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId))).ToLowerInvariant();
            File.WriteAllText(
                Path.Combine(root, "requests", key + ".json"),
                $"{{\"schema_version\":\"1.0\",\"request_id\":\"{requestId}\",\"command\":\"status\",\"timestamp\":\"{timestamp}\",\"payload\":{{}}}}");

            var claim = mailbox.ClaimNextRequest();

            Assert.Equal(accepted, claim is not null);
            Assert.Equal(accepted ? 0 : 1, Directory.EnumerateFiles(Path.Combine(root, "failed"), "*.json").Count());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("{\"schema_version\":\"1.0\",\"request_id\":\"canonical-001\",\"command\":\"status\",\"timestamp\":\"2026-08-09T12:00:00Z\",\"payload\":{}}", "other-file-id")]
    [InlineData("{\"schema_version\":\"1.0\",\"request_id\":\"null-payload-001\",\"command\":\"status\",\"timestamp\":\"2026-08-09T12:00:00Z\",\"payload\":null}", "null-payload-001")]
    [InlineData("{\"schema_version\":\"1.0\",\"request_id\":\"invalid-timestamp-001\",\"command\":\"status\",\"timestamp\":\"not-a-date\",\"payload\":{}}", "invalid-timestamp-001")]
    public void InvalidEnvelope_IsFailedWithoutPublishingACorrelatedResponse(string requestJson, string fileKeyRequestId)
    {
        var root = Path.Combine(Path.GetTempPath(), "ddai-conformance-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var mailbox = new AtomicMailbox(root);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileKeyRequestId))).ToLowerInvariant();
            File.WriteAllText(Path.Combine(root, "requests", key + ".json"), requestJson);

            Assert.Null(mailbox.ClaimNextRequest());
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "responses"), "*.json"));
            Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "failed"), "*.json"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
