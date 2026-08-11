using System.Reflection;
using System.Text.Json;
using DDAI.App.Mcp;
using DDAI.App.Assets;
using DDAI.Core.Assets;
using DDAI.Core.Mailbox;
using ModelContextProtocol.Server;

namespace DDAI.App.Tests.Mcp;

public sealed class DdaiCapabilityToolTests
{
    [Fact]
    public void CapabilityTool_IsReadOnlyAndListsEveryCategory()
    {
        var method = typeof(DdaiCapabilityTools).GetMethod(nameof(DdaiCapabilityTools.GetCapabilities));
        var attribute = method!.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.Equal("ddai_get_capabilities", attribute.Name);
        Assert.True(attribute.ReadOnly);
        Assert.False(attribute.Destructive);
    }

    [Fact]
    public void GetCapabilities_ReportsClosedContractAndEveryCategory()
    {
        using var sandbox = new CapabilitySandbox();
        var capabilities = sandbox.CreateService().GetCapabilities();

        Assert.Equal("0.1.0", capabilities.ConnectorVersion);
        Assert.Equal("0.2.1", capabilities.ModVersion);
        Assert.Equal("1.2.0.1", capabilities.DungeondraftVersion);
        Assert.Equal("closed", capabilities.RuntimeState);
        Assert.Null(capabilities.MapRevision);
        Assert.Null(capabilities.CatalogRevision);
        Assert.Equal(AssetCategory.All, capabilities.AssetCategories);
        Assert.Equal(["png"], capabilities.Preview.Formats);
        Assert.Equal(AssetCatalogRepository.MaximumPreviewBytes, capabilities.Preview.MaximumBytes);
        Assert.Equal(256, capabilities.Preview.MaximumEdge);
        Assert.Equal(["png"], capabilities.Import.Formats);
        Assert.Equal(8_388_608, capabilities.Import.MaximumDecodedBytes);
        Assert.Equal(4096, capabilities.Import.MaximumEdge);
        Assert.Equal(16_777_216, capabilities.Import.MaximumPixels);
        Assert.Collection(
            capabilities.Operations,
            operation => Assert.Equal(new OperationCapability("status", true, false, "no_fresh_runtime_receipt"), operation),
            operation => Assert.Equal(new OperationCapability("apply_plan", true, false, "no_fresh_runtime_receipt"), operation));
    }

    [Fact]
    public void GetCapabilities_RejectsStaleAndMismatchedRuntimeReceipts()
    {
        using var sandbox = new CapabilitySandbox();
        sandbox.WriteReceipt(sandbox.Now.AddSeconds(-31), "0.2.1", "1.2.0.1", ["status", "apply_plan"]);

        var stale = sandbox.CreateService().GetCapabilities();

        Assert.Equal("stale", stale.RuntimeState);
        Assert.All(stale.Operations, operation => Assert.Equal("runtime_receipt_stale", operation.Reason));

        sandbox.WriteReceipt(sandbox.Now, "0.2.2", "1.2.0.1", ["status", "apply_plan"]);
        var mismatched = sandbox.CreateService().GetCapabilities();

        Assert.Equal("incompatible", mismatched.RuntimeState);
        Assert.All(mismatched.Operations, operation => Assert.False(operation.RuntimeCertified));
        Assert.All(mismatched.Operations, operation => Assert.Equal("runtime_version_mismatch", operation.Reason));
    }

    [Fact]
    public void GetCapabilities_CertifiesOnlyAnAdvertisedOperationFromAnExactFreshReceipt()
    {
        using var sandbox = new CapabilitySandbox();
        sandbox.WriteReceipt(sandbox.Now, "0.2.1", "1.2.0.1", ["status"]);

        var capabilities = sandbox.CreateService().GetCapabilities();

        Assert.Equal("live", capabilities.RuntimeState);
        var status = Assert.Single(capabilities.Operations, operation => operation.Name == "status");
        Assert.True(status.RuntimeCertified);
        Assert.Equal("runtime_certified", status.Reason);
        var applyPlan = Assert.Single(capabilities.Operations, operation => operation.Name == "apply_plan");
        Assert.False(applyPlan.RuntimeCertified);
        Assert.Equal("bridge_operation_not_advertised", applyPlan.Reason);
    }

    [Fact]
    public void GetCapabilities_PrefersBridgeNewerSessionWhenReceiptsShareATimestamp()
    {
        using var sandbox = new CapabilitySandbox();
        sandbox.WriteReceipt(sandbox.Now, "0.2.1", "1.2.0.1", ["status"], "1723377600-1000", slot: 0);
        sandbox.WriteReceipt(sandbox.Now, "0.2.1", "1.2.0.1", ["apply_plan"], "1723377600-1001", slot: 1);

        var capabilities = sandbox.CreateService().GetCapabilities();

        Assert.False(Assert.Single(capabilities.Operations, operation => operation.Name == "status").RuntimeCertified);
        Assert.True(Assert.Single(capabilities.Operations, operation => operation.Name == "apply_plan").RuntimeCertified);
    }

    [Fact]
    public void GetCapabilities_FailsClosedForAnOversizedRuntimeReceipt()
    {
        using var sandbox = new CapabilitySandbox();
        File.WriteAllBytes(
            Path.Combine(sandbox.Root, "runtime-receipt-slot-0.json"),
            new byte[AtomicMailbox.MaximumMessageBytes + 1]);

        var capabilities = sandbox.CreateService().GetCapabilities();

        Assert.Equal("closed", capabilities.RuntimeState);
        Assert.All(capabilities.Operations, operation => Assert.False(operation.RuntimeCertified));
    }

    [Fact]
    public void CapabilityTool_SerializesSnakeCaseStructuredOutput()
    {
        using var sandbox = new CapabilitySandbox();

        var json = DdaiCapabilityTools.GetCapabilities(sandbox.CreateService());
        using var document = JsonDocument.Parse(json);

        Assert.Equal("closed", document.RootElement.GetProperty("runtime_state").GetString());
        Assert.True(document.RootElement.TryGetProperty("asset_categories", out _));
        Assert.True(document.RootElement.GetProperty("operations")[0].TryGetProperty("runtime_certified", out _));
    }

    private sealed class CapabilitySandbox : IDisposable
    {
        public CapabilitySandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "ddai-capability-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        }

        public string Root { get; }
        public DateTimeOffset Now { get; }

        public DdaiCapabilityService CreateService() => new(
            new AtomicMailbox(Root),
            new AssetCatalogRepository(Path.Combine(Root, "catalog"), new FixedTimeProvider(Now)),
            new FixedTimeProvider(Now));

        public void WriteReceipt(
            DateTimeOffset timestamp,
            string modVersion,
            string dungeondraftVersion,
            IReadOnlyList<string> commands,
            string sessionId = "test-session",
            int slot = 0)
        {
            var path = Path.Combine(Root, $"runtime-receipt-slot-{slot}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                schema_version = "1.0",
                @event = "started",
                mod_version = modVersion,
                target_dungeondraft_version = dungeondraftVersion,
                timestamp = timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                session_id = sessionId,
                supported_commands = commands,
            }));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
