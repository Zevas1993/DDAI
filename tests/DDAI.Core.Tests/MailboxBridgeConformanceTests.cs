using System.Security.Cryptography;
using System.Text;

using DDAI.Core.Mailbox;

namespace DDAI.Core.Tests;

public sealed class MailboxBridgeConformanceTests
{
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
