using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DDAI.App.Assets;
using DDAI.Core.Assets;

namespace DDAI.App.Tests.Assets;

public sealed class AssetPackNormalizationServiceTests
{
    [Theory]
    [InlineData(" Pack Café ")]
    [InlineData(" ＰＡＣＫ-１２３ ")]
    public void ProcessPending_PreservesExactAssetReferenceNormalizationParity(string rawPackId)
    {
        using var sandbox = new NormalizationSandbox();
        var requestId = Hash(rawPackId);
        sandbox.WriteRequest(requestId, rawPackId);
        var service = new AssetPackNormalizationService(sandbox.MailboxRoot);

        Assert.Equal(1, service.ProcessPending());

        using var response = JsonDocument.Parse(File.ReadAllText(sandbox.ResponsePath(requestId)));
        var normalized = response.RootElement.GetProperty("normalized_pack_id").GetString();
        Assert.Equal(AssetReference.NormalizePackId(rawPackId), normalized);
        Assert.Equal(
            AssetReference.Create(rawPackId, "Objects", "res://packs/private/object.png"),
            AssetReference.Create(normalized, "Objects", "res://packs/private/object.png"));
        Assert.DoesNotContain("resource", File.ReadAllText(sandbox.ResponsePath(requestId)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Worker_ProcessesRequestCreatedAfterConnectorStartup()
    {
        using var sandbox = new NormalizationSandbox();
        var service = new AssetPackNormalizationService(sandbox.MailboxRoot);
        var worker = new AssetPackNormalizationWorker(service);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            const string rawPackId = " Pack Café ";
            var requestId = Hash(rawPackId);
            sandbox.WriteRequest(requestId, rawPackId);

            var responsePath = sandbox.ResponsePath(requestId);
            for (var attempt = 0; attempt < 50 && !File.Exists(responsePath); attempt++)
            {
                await Task.Delay(20);
            }

            Assert.True(File.Exists(responsePath));
            using var response = JsonDocument.Parse(File.ReadAllText(responsePath));
            Assert.Equal("pack café", response.RootElement.GetProperty("normalized_pack_id").GetString());
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Fact]
    public void ProcessPending_RemovesAnsweredRequestsSoLaterBatchesCannotStarve()
    {
        using var sandbox = new NormalizationSandbox();
        for (var index = 0; index < AssetPackNormalizationService.MaximumRequestsPerPass + 1; index++)
        {
            var value = " Pack Café " + index;
            sandbox.WriteRequest(Hash(value), value);
        }
        var service = new AssetPackNormalizationService(sandbox.MailboxRoot);

        Assert.Equal(AssetPackNormalizationService.MaximumRequestsPerPass, service.ProcessPending());
        Assert.Single(sandbox.RequestPaths());
        Assert.Equal(1, service.ProcessPending());
        Assert.Empty(sandbox.RequestPaths());
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class NormalizationSandbox : IDisposable
    {
        public NormalizationSandbox()
        {
            MailboxRoot = Path.Combine(Path.GetTempPath(), "ddai-pack-normalization-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(MailboxRoot, "private", "pack-normalization", "requests"));
        }

        public string MailboxRoot { get; }

        public void WriteRequest(string requestId, string value) => File.WriteAllText(
            Path.Combine(MailboxRoot, "private", "pack-normalization", "requests", requestId + ".json"),
            JsonSerializer.Serialize(new { schema_version = "1.0", request_id = requestId, value }));

        public string ResponsePath(string requestId) =>
            Path.Combine(MailboxRoot, "private", "pack-normalization", "responses", requestId + ".json");

        public string[] RequestPaths() => Directory.GetFiles(
            Path.Combine(MailboxRoot, "private", "pack-normalization", "requests"),
            "*.json",
            SearchOption.TopDirectoryOnly);

        public void Dispose()
        {
            if (Directory.Exists(MailboxRoot))
            {
                Directory.Delete(MailboxRoot, recursive: true);
            }
        }
    }
}
