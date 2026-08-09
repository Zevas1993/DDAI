using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using DDAI.Core.Mailbox;

namespace DDAI.Core.Tests;

public sealed class MailboxBridgeConformanceTests
{
    [Theory]
    [InlineData("2026-08-09T12:00:00Z", "\"2026-08-09T12:00:00+00:00\"")]
    [InlineData("2026-08-09T12:00:00.1234000+05:30", "\"2026-08-09T12:00:00.1234+05:30\"")]
    [InlineData("2026-08-09T12:00:00-00:00", "\"2026-08-09T12:00:00+00:00\"")]
    public void WireSerialization_UsesDeterministicExplicitOffsetForm(string input, string expectedJson)
    {
        var value = DateTimeOffset.Parse(input, System.Globalization.CultureInfo.InvariantCulture);

        var json = JsonSerializer.Serialize(value, BridgeWireJson.Options);

        Assert.Equal(expectedJson, json);
    }

    [Theory]
    [MemberData(nameof(WireTimestampContractCases.All), MemberType = typeof(WireTimestampContractCases))]
    public void RawTimestampContract_IsEnforcedByAtomicMailbox(string timestamp, bool accepted)
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

    [Fact]
    public void AtomicMailbox_RejectsNoncanonicalRawResponseTimestamp()
    {
        var root = Path.Combine(Path.GetTempPath(), "ddai-response-timestamp-tests", Guid.NewGuid().ToString("N"));
        try
        {
            const string requestId = "response-timestamp-contract";
            var mailbox = new AtomicMailbox(root);
            var request = MailboxRequest.CreateStatus(requestId, DateTimeOffset.Parse("2026-08-09T12:00:00Z"));
            Assert.True(mailbox.PublishRequest(request));
            var claim = Assert.IsType<ClaimedMailboxRequest>(mailbox.ClaimNextRequest());
            mailbox.PublishResponse(claim, new MailboxResponse
            {
                SchemaVersion = MailboxRequest.CurrentSchemaVersion,
                RequestId = requestId,
                Command = "status",
                Timestamp = DateTimeOffset.Parse("2026-08-09T12:00:01Z"),
                Success = true,
                Payload = JsonSerializer.SerializeToElement(new { state = "ready" }),
            });
            var responsePath = Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "responses"), "*.json"));
            File.WriteAllText(
                responsePath,
                $"{{\"schema_version\":\"1.0\",\"request_id\":\"{requestId}\",\"command\":\"status\",\"timestamp\":\"2026-08-09\",\"success\":true,\"payload\":{{\"state\":\"ready\"}}}}");

            Assert.Throws<JsonException>(() => mailbox.WaitForResponse(requestId, TimeSpan.FromMilliseconds(100)));
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

public static class WireTimestampContractCases
{
    public static TheoryData<string, bool> All { get; } = new()
    {
        { "2026-08-09T12:00:00Z", true },
        { "2026-08-09T12:00:00+00:00", true },
        { "2026-08-09T12:00:00-00:00", true },
        { "2026-08-09T12:00:00+14:00", true },
        { "2026-08-09T12:00:00-14:00", true },
        { "2026-08-09T12:00:00.1+01:30", true },
        { "2026-08-09T12:00:00.1234567Z", true },
        { "2024-02-29T23:59:59.9999999-04:00", true },
        { "0001-01-01T00:00:00.0000001Z", true },
        { "0001-01-01T00:00:00-01:00", true },
        { "0001-01-01T01:00:00.0000001+01:00", true },
        { "0001-01-01T14:00:00.0000001+14:00", true },
        { "9999-12-31T23:59:59+01:00", true },
        { "9999-12-31T23:59:59.9999999+00:00", true },
        { "9999-12-31T23:59:59.9999999-00:00", true },
        { "9999-12-31T22:59:59.9999999-01:00", true },
        { "9999-12-31T09:59:59.9999999-14:00", true },

        { "2026-08-09", false },
        { "2026-08-09T12:00", false },
        { "2026-08-09T12:00Z", false },
        { "2026-08-09T12:00:00", false },
        { "2026-08-09T12:00:00.1234567", false },
        { "2026-08-09T12:00:00+01", false },
        { "2026-08-09T12:00:00z", false },
        { "2026-08-09T12:00:00.12345678Z", false },
        { "2026-08-09T12:00:00+14:01", false },
        { "2026-08-09T12:00:00+15:00", false },
        { "2026-08-09T12:00:00+01:60", false },
        { "2026-02-29T12:00:00Z", false },
        { "2026-02-30T12:00:00Z", false },
        { "0000-01-01T12:00:00Z", false },
        { "2026-13-09T12:00:00Z", false },
        { "2026-08-09T24:00:00Z", false },
        { "2026-08-09T12:60:00Z", false },
        { "2026-08-09T12:00:60Z", false },
        { "0001-01-01T00:00:00Z", false },
        { "0001-01-01T00:00:00+00:00", false },
        { "0001-01-01T00:00:00-00:00", false },
        { "0001-01-01T00:00:00+01:00", false },
        { "0001-01-01T01:00:00+01:00", false },
        { "0001-01-01T14:00:00+14:00", false },
        { "9999-12-31T23:59:59-01:00", false },
        { "9999-12-31T23:00:00-01:00", false },
        { "9999-12-31T10:00:00-14:00", false },

        { "+026-08-09T12:00:00Z", false },
        { "2026-+8-09T12:00:00Z", false },
        { "2026-08-+9T12:00:00Z", false },
        { "2026-08-09T+2:00:00Z", false },
        { "2026-08-09T12:+0:00Z", false },
        { "2026-08-09T12:00:+0Z", false },
        { "2026-08-09T12:00:00.+123Z", false },
        { "2026-08-09T12:00:00++1:00", false },
        { "2026-08-09T12:00:00+0+1:00", false },
        { "2026-08-09T12:00:00+00:+1", false },
        { "٢٠٢٦-08-09T12:00:00Z", false },
        { "2026-٠٨-09T12:00:00Z", false },
        { "2026-08-٠٩T12:00:00Z", false },
        { "2026-08-09T١٢:00:00Z", false },
        { "2026-08-09T12:٠٠:00Z", false },
        { "2026-08-09T12:00:٠٠Z", false },
        { "2026-08-09T12:00:00.١٢٣Z", false },
        { "2026-08-09T12:00:00+١٢:00", false },
        { "2026-08-09T12:00:00+01:٠٠", false },
        { " 2026-08-09T12:00:00Z", false },
        { "2026-08-09T12:00:00Z ", false },
        { "2026-08-09T12:00: 0Z", false },
        { "2026-08-09T12:00:00+ 1:00", false },
        { "2026-08-09T12:00:00+01: 0", false },
        { "2026/08/09T12:00:00Z", false },
        { "2026-08--09T12:00:00Z", false },
        { "2026-08-09TT12:00:00Z", false },
        { "2026-08-09T12:00:00.", false },
        { "2026-08-09T12:00:00..1Z", false },
        { "2026-08-09T12:00:00Z+00:00", false },
        { "2026-08-09T12:00:00+01:", false },
        { "2026-08-09T12:00:00+01:0", false },
        { "2026-08-09T12:00:00+01:000", false },
    };
}
