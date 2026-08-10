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

    [Fact]
    public void ProcessPending_PublishesExplicitNullForValidNormalizedEmptyValue()
    {
        using var sandbox = new NormalizationSandbox();
        const string value = "\u3000";
        var requestId = Hash(value);
        sandbox.WriteRequest(requestId, value);
        var service = new AssetPackNormalizationService(sandbox.MailboxRoot);

        Assert.Equal(1, service.ProcessPending());

        using var response = JsonDocument.Parse(File.ReadAllText(sandbox.ResponsePath(requestId)));
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("normalized_pack_id").ValueKind);
        Assert.Empty(sandbox.RequestPaths());
    }

    [Fact]
    public void ProcessPending_QuarantinesEightPoisonRequestsSoNinthValidRequestProgresses()
    {
        using var sandbox = new NormalizationSandbox();
        for (var index = 0; index < AssetPackNormalizationService.MaximumRequestsPerPass; index++)
        {
            sandbox.WriteRawRequest($"000000000000000000000000000000000000000000000000000000000000000{index}", "{");
        }
        const string value = " Pack Café ";
        var requestId = Hash(value);
        sandbox.WriteRequest(requestId, value);
        var service = new AssetPackNormalizationService(sandbox.MailboxRoot);

        Assert.Equal(AssetPackNormalizationService.MaximumRequestsPerPass, service.ProcessPending());
        Assert.Equal(AssetPackNormalizationService.MaximumRequestsPerPass, sandbox.QuarantinePaths().Length);
        Assert.Single(sandbox.RequestPaths());
        Assert.Equal(1, service.ProcessPending());
        Assert.True(File.Exists(sandbox.ResponsePath(requestId)));
    }

    [Fact]
    public void ProcessPending_QuarantinesConflictingResponseAndPublishesCorrectReplacement()
    {
        using var sandbox = new NormalizationSandbox();
        const string value = " Pack Café ";
        var requestId = Hash(value);
        sandbox.WriteRequest(requestId, value);
        sandbox.WriteResponse(requestId, "attacker-value");
        var service = new AssetPackNormalizationService(sandbox.MailboxRoot);

        Assert.Equal(1, service.ProcessPending());

        using var response = JsonDocument.Parse(File.ReadAllText(sandbox.ResponsePath(requestId)));
        Assert.Equal("pack café", response.RootElement.GetProperty("normalized_pack_id").GetString());
        Assert.Single(sandbox.QuarantinePaths());
    }

    [Fact]
    public void ProcessPending_RestartDeletesRequestAfterRevalidatingDurableResponse()
    {
        using var sandbox = new NormalizationSandbox();
        const string value = " Pack Café ";
        var requestId = Hash(value);
        sandbox.WriteRequest(requestId, value);
        sandbox.WriteResponse(requestId, "pack café");

        var restarted = new AssetPackNormalizationService(sandbox.MailboxRoot);

        Assert.Equal(1, restarted.ProcessPending());
        Assert.Empty(sandbox.RequestPaths());
        Assert.Empty(sandbox.QuarantinePaths());
    }

    [Theory]
    [InlineData("requests")]
    [InlineData("responses")]
    public void Constructor_RejectsNormalizationRootJunction(string child)
    {
        using var sandbox = new NormalizationSandbox(createServiceRoots: false);
        var privateRoot = sandbox.NormalizationRoot;
        Directory.CreateDirectory(privateRoot);
        var external = Path.Combine(sandbox.MailboxRoot, "external-" + child);
        Directory.CreateDirectory(external);
        var link = Path.Combine(privateRoot, child);
        CreateDirectoryJunction(link, external);
        try
        {
            Assert.Throws<InvalidDataException>(() => new AssetPackNormalizationService(sandbox.MailboxRoot));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void ProcessPending_DoesNotFollowRequestFileSymbolicLink()
    {
        using var sandbox = new NormalizationSandbox();
        const string value = " Pack Café ";
        var requestId = Hash(value);
        var external = Path.Combine(sandbox.MailboxRoot, "external-request.json");
        File.WriteAllText(external, JsonSerializer.Serialize(new { schema_version = "1.0", request_id = requestId, value }));
        try
        {
            File.CreateSymbolicLink(sandbox.RequestPath(requestId), external);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }
        var original = File.ReadAllText(external);
        try
        {
            var service = new AssetPackNormalizationService(sandbox.MailboxRoot);
            Assert.Equal(1, service.ProcessPending());
            Assert.Equal(original, File.ReadAllText(external));
            Assert.False(File.Exists(sandbox.ResponsePath(requestId)));
            Assert.Single(sandbox.QuarantinePaths());
        }
        finally
        {
            File.Delete(sandbox.RequestPath(requestId));
        }
    }

    [Fact]
    public void ProcessPending_DoesNotFollowConflictingResponseFileSymbolicLink()
    {
        using var sandbox = new NormalizationSandbox();
        const string value = " Pack Café ";
        var requestId = Hash(value);
        sandbox.WriteRequest(requestId, value);
        var external = Path.Combine(sandbox.MailboxRoot, "external-response.json");
        File.WriteAllText(external, "attacker");
        try
        {
            File.CreateSymbolicLink(sandbox.ResponsePath(requestId), external);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }
        try
        {
            var service = new AssetPackNormalizationService(sandbox.MailboxRoot);
            Assert.Equal(1, service.ProcessPending());
            Assert.Equal("attacker", File.ReadAllText(external));
            using var response = JsonDocument.Parse(File.ReadAllText(sandbox.ResponsePath(requestId)));
            Assert.Equal("pack café", response.RootElement.GetProperty("normalized_pack_id").GetString());
            Assert.Single(sandbox.QuarantinePaths());
        }
        finally
        {
            File.Delete(sandbox.ResponsePath(requestId));
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class NormalizationSandbox : IDisposable
    {
        public NormalizationSandbox(bool createServiceRoots = true)
        {
            MailboxRoot = Path.Combine(Path.GetTempPath(), "ddai-pack-normalization-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(MailboxRoot);
            if (createServiceRoots)
            {
                Directory.CreateDirectory(Path.Combine(NormalizationRoot, "requests"));
                Directory.CreateDirectory(Path.Combine(NormalizationRoot, "responses"));
            }
        }

        public string MailboxRoot { get; }

        public string NormalizationRoot => Path.Combine(MailboxRoot, "private", "pack-normalization");

        public void WriteRequest(string requestId, string value) => File.WriteAllText(
            RequestPath(requestId),
            JsonSerializer.Serialize(new { schema_version = "1.0", request_id = requestId, value }));

        public void WriteRawRequest(string requestId, string json) => File.WriteAllText(RequestPath(requestId), json);

        public string RequestPath(string requestId) => Path.Combine(NormalizationRoot, "requests", requestId + ".json");

        public string ResponsePath(string requestId) =>
            Path.Combine(NormalizationRoot, "responses", requestId + ".json");

        public void WriteResponse(string requestId, string normalizedPackId)
        {
            Directory.CreateDirectory(Path.Combine(NormalizationRoot, "responses"));
            File.WriteAllText(
                ResponsePath(requestId),
                JsonSerializer.Serialize(new { schema_version = "1.0", request_id = requestId, normalized_pack_id = normalizedPackId }));
        }

        public string[] RequestPaths() => Directory.GetFiles(
            Path.Combine(NormalizationRoot, "requests"),
            "*.json",
            SearchOption.TopDirectoryOnly);

        public string[] QuarantinePaths() => Directory.Exists(Path.Combine(NormalizationRoot, "quarantine"))
            ? Directory.GetFiles(Path.Combine(NormalizationRoot, "quarantine"), "*", SearchOption.TopDirectoryOnly)
            : [];

        public void Dispose()
        {
            if (Directory.Exists(MailboxRoot))
            {
                Directory.Delete(MailboxRoot, recursive: true);
            }
        }
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("cmd.exe could not create the junction fixture.");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && Directory.Exists(linkPath));
    }
}
