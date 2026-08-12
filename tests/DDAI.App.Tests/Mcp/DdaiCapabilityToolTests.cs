using System.Diagnostics;
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
    private static readonly string[] ExpectedMapOperations =
    [
        "terrain_stroke", "pattern_region", "colorable_pattern_region", "cave_region",
        "roof_region", "object_placement", "wall_polyline", "material_stroke",
        "portal_placement", "path_polyline", "light_placement", "simple_tile_region",
        "smart_tile_region", "smart_tile_double_region",
    ];

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
        Assert.Equal(ExpectedMapOperations, capabilities.Operations.Select(operation => operation.Name));
        Assert.All(capabilities.Operations, operation =>
        {
            Assert.True(operation.SchemaSupported);
            Assert.False(operation.RoutePresent);
            Assert.False(operation.MethodsReflectable);
            Assert.False(operation.PropertiesReadable);
            Assert.False(operation.RuntimeCertified);
            Assert.False(operation.Busy);
            Assert.Equal("no_fresh_runtime_receipt", operation.Reason);
        });
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
    public void GetCapabilities_RenewsAnExactStartupReceiptOnlyFromFreshMatchingHeartbeat()
    {
        using var sandbox = new CapabilitySandbox();
        sandbox.WriteReceipt(
            sandbox.Now.AddMinutes(-10),
            "0.2.1",
            "1.2.0.1",
            ["status", "apply_plan"],
            sessionId: "active-session");
        sandbox.WriteHeartbeat(sandbox.Now, "0.2.1", "active-session");

        var live = sandbox.CreateService().GetCapabilities();

        Assert.Equal("live", live.RuntimeState);
        Assert.All(live.Operations, operation =>
        {
            Assert.False(operation.RuntimeCertified);
            Assert.Equal("executor_not_live_certified", operation.Reason);
        });

        sandbox.WriteHeartbeat(sandbox.Now, "0.2.1", "different-session");
        var mismatched = sandbox.CreateService().GetCapabilities();
        Assert.Equal("stale", mismatched.RuntimeState);

        sandbox.WriteHeartbeat(sandbox.Now.AddSeconds(-31), "0.2.1", "active-session");
        var stale = sandbox.CreateService().GetCapabilities();
        Assert.Equal("stale", stale.RuntimeState);
    }

    [Fact]
    public void GetCapabilities_RejectsHeartbeatDirectoryJunction()
    {
        using var sandbox = new CapabilitySandbox();
        sandbox.WriteReceipt(
            sandbox.Now.AddMinutes(-10),
            "0.2.1",
            "1.2.0.1",
            ["status", "apply_plan"],
            sessionId: "active-session");
        var external = Path.Combine(Path.GetTempPath(), "ddai-capability-external", Guid.NewGuid().ToString("N"));
        var junction = Path.Combine(sandbox.Root, "runtime-heartbeats");
        Directory.CreateDirectory(external);
        try
        {
            CapabilitySandbox.WriteHeartbeatFile(external, sandbox.Now, "0.2.1", "active-session");
            CreateDirectoryJunction(junction, external);

            var capabilities = sandbox.CreateService().GetCapabilities();

            Assert.Equal("stale", capabilities.RuntimeState);
            Assert.All(capabilities.Operations, operation => Assert.False(operation.RuntimeCertified));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
            if (Directory.Exists(external)) Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public void GetCapabilities_CertifiesOnlyAnAdvertisedOperationFromAnExactFreshReceipt()
    {
        using var sandbox = new CapabilitySandbox();
        sandbox.WriteReceipt(
            sandbox.Now,
            "0.2.1",
            "1.2.0.1",
            ["status", "apply_plan", "inspect_map"],
            certifications:
            [
                Certification("wall_polyline", "WallTool", available: true, busy: false, certified: true, "runtime_certified"),
                Certification("object_placement", "ObjectTool", available: true, busy: false, certified: false, "executor_not_live_certified"),
            ]);

        var capabilities = sandbox.CreateService().GetCapabilities();

        Assert.Equal("live", capabilities.RuntimeState);
        var wall = Assert.Single(capabilities.Operations, operation => operation.Name == "wall_polyline");
        Assert.True(wall.RoutePresent);
        Assert.True(wall.RuntimeCertified);
        Assert.False(wall.Busy);
        Assert.Equal("WallTool", wall.ToolName);
        Assert.Equal("runtime_certified", wall.Reason);
        var objectPlacement = Assert.Single(capabilities.Operations, operation => operation.Name == "object_placement");
        Assert.True(objectPlacement.RoutePresent);
        Assert.False(objectPlacement.RuntimeCertified);
        Assert.Equal("executor_not_live_certified", objectPlacement.Reason);
        var terrain = Assert.Single(capabilities.Operations, operation => operation.Name == "terrain_stroke");
        Assert.True(terrain.RoutePresent);
        Assert.False(terrain.RuntimeCertified);
        Assert.Equal("executor_not_live_certified", terrain.Reason);
    }

    [Fact]
    public void GetCapabilities_RejectsAReasonThatForgesCertificationAgainstTheBoolean()
    {
        using var sandbox = new CapabilitySandbox();
        sandbox.WriteReceipt(
            sandbox.Now,
            "0.2.1",
            "1.2.0.1",
            ["status", "apply_plan", "inspect_map"],
            certifications:
            [
                Certification("object_placement", "ObjectTool", available: true, busy: false, certified: false, "runtime_certified"),
            ]);

        var capabilities = sandbox.CreateService().GetCapabilities();

        Assert.Equal("closed", capabilities.RuntimeState);
        Assert.All(capabilities.Operations, operation => Assert.False(operation.RuntimeCertified));
    }

    [Fact]
    public void GetCapabilities_RejectsMissingNullAndPartialCertificationMatrices()
    {
        using var sandbox = new CapabilitySandbox();
        sandbox.WriteReceipt(
            sandbox.Now,
            "0.2.1",
            "1.2.0.1",
            ["status", "apply_plan", "inspect_map"],
            certifications: [],
            completeMatrix: false);
        Assert.Equal("closed", sandbox.CreateService().GetCapabilities().RuntimeState);

        sandbox.WriteReceipt(
            sandbox.Now,
            "0.2.1",
            "1.2.0.1",
            ["status", "apply_plan", "inspect_map"],
            certifications: [Certification("wall_polyline", "WallTool", true, false, true, "runtime_certified")],
            completeMatrix: false);
        Assert.Equal("closed", sandbox.CreateService().GetCapabilities().RuntimeState);
    }

    [Fact]
    public void GetCapabilities_RejectsUnknownDuplicateAndMissingRootReceiptFields()
    {
        using var sandbox = new CapabilitySandbox();
        var valid = sandbox.CreateReceiptJson();

        sandbox.WriteRawReceipt(valid[..^1] + ",\"unexpected\":true}");
        Assert.Equal("closed", sandbox.CreateService().GetCapabilities().RuntimeState);

        sandbox.WriteRawReceipt(valid.Replace(
            "\"schema_version\":\"1.0\"",
            "\"schema_version\":\"1.0\",\"schema_version\":\"1.0\"",
            StringComparison.Ordinal));
        Assert.Equal("closed", sandbox.CreateService().GetCapabilities().RuntimeState);

        sandbox.WriteRawReceipt(valid.Replace("\"event\":\"started\",", string.Empty, StringComparison.Ordinal));
        Assert.Equal("closed", sandbox.CreateService().GetCapabilities().RuntimeState);
    }

    [Fact]
    public void GetCapabilities_RejectsImpossibleAbsentRouteEvidence()
    {
        using var sandbox = new CapabilitySandbox();
        sandbox.WriteReceipt(
            sandbox.Now,
            "0.2.1",
            "1.2.0.1",
            ["status", "apply_plan", "inspect_map"],
            certifications:
            [
                Certification(
                    "wall_polyline",
                    "WallTool",
                    available: false,
                    busy: false,
                    certified: false,
                    "tool_unavailable",
                    methodsReflectable: true),
            ]);

        Assert.Equal("closed", sandbox.CreateService().GetCapabilities().RuntimeState);

        sandbox.WriteReceipt(
            sandbox.Now,
            "0.2.1",
            "1.2.0.1",
            ["status", "apply_plan", "inspect_map"],
            certifications:
            [
                Certification(
                    "wall_polyline",
                    "WallTool",
                    available: false,
                    busy: true,
                    certified: false,
                    "runtime_version_mismatch",
                    methodsReflectable: true,
                    propertiesReadable: true,
                    exactVersion: "unknown"),
            ]);

        Assert.Equal("closed", sandbox.CreateService().GetCapabilities().RuntimeState);
    }

    [Fact]
    public void GetCapabilities_ReportsAnUnallowlistedRuntimeIdentityAsIncompatible()
    {
        using var sandbox = new CapabilitySandbox();
        var receipt = sandbox.CreateReceiptJson()
            .Replace(
                "\"exact_dungeondraft_version\":\"1.2.0.1\"",
                "\"exact_dungeondraft_version\":\"unknown\"",
                StringComparison.Ordinal)
            .Replace(
                "\"reason\":\"executor_not_live_certified\"",
                "\"reason\":\"runtime_version_mismatch\"",
                StringComparison.Ordinal);
        sandbox.WriteRawReceipt(receipt);

        var capabilities = sandbox.CreateService().GetCapabilities();

        Assert.Equal("incompatible", capabilities.RuntimeState);
        Assert.Equal("unknown", capabilities.DungeondraftVersion);
        Assert.All(capabilities.Operations, operation => Assert.False(operation.RuntimeCertified));
    }

    [Fact]
    public void GetCapabilities_PrefersBridgeNewerSessionWhenReceiptsShareATimestamp()
    {
        using var sandbox = new CapabilitySandbox();
        sandbox.WriteReceipt(
            sandbox.Now,
            "0.2.1",
            "1.2.0.1",
            ["status", "apply_plan", "inspect_map"],
            "1723377600-1000",
            slot: 0,
            certifications: [Certification("wall_polyline", "WallTool", true, false, false, "executor_not_live_certified")]);
        sandbox.WriteReceipt(
            sandbox.Now,
            "0.2.1",
            "1.2.0.1",
            ["status", "apply_plan", "inspect_map"],
            "1723377600-1001",
            slot: 1,
            certifications: [Certification("wall_polyline", "WallTool", true, false, true, "runtime_certified")]);

        var capabilities = sandbox.CreateService().GetCapabilities();

        Assert.True(Assert.Single(capabilities.Operations, operation => operation.Name == "wall_polyline").RuntimeCertified);
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
        var operation = document.RootElement.GetProperty("operations")[0];
        Assert.True(operation.TryGetProperty("route_present", out _));
        Assert.True(operation.TryGetProperty("methods_reflectable", out _));
        Assert.True(operation.TryGetProperty("properties_readable", out _));
        Assert.True(operation.TryGetProperty("runtime_certified", out _));
        Assert.True(operation.TryGetProperty("busy", out _));
        Assert.True(operation.TryGetProperty("tool_name", out _));
    }

    private static object Certification(
        string operationType,
        string toolName,
        bool available,
        bool busy,
        bool certified,
        string reason,
        bool? methodsReflectable = null,
        bool? propertiesReadable = null,
        string exactVersion = "1.2.0.1") => new
        {
            operation_type = operationType,
            tool_name = toolName,
            required_methods = RequiredMethods(operationType),
            required_properties = RequiredProperties(operationType),
            route_present = available,
            methods_reflectable = methodsReflectable ?? available,
            properties_readable = propertiesReadable ?? available,
            busy,
            exact_dungeondraft_version = exactVersion,
            runtime_certified = certified,
            reason,
        };

    private static string[] RequiredMethods(string operation) => operation switch
    {
        "terrain_stroke" => ["SetBiome", "SetSize", "UpdateBrush"],
        "roof_region" => ["DrawRect", "FinishShape"],
        "object_placement" => ["Confirm", "SetLayer", "SetSorting", "SetShadow", "SetBlockLight"],
        "wall_polyline" => ["EndWall"],
        "material_stroke" => ["SetMaterial", "SetLayer", "SetSmooth"],
        "portal_placement" => ["SetFreestanding", "FindBestLocation", "ChangeTexture"],
        "path_polyline" => ["StartPath", "EndPath", "SetLayer"],
        "light_placement" => ["CreatePreview", "ChangeColor", "SetShadows"],
        _ => [],
    };

    private static string[] RequiredProperties(string operation) => operation switch
    {
        "terrain_stroke" => ["IsPainting", "brush"],
        "pattern_region" or "colorable_pattern_region" => ["Texture"],
        "roof_region" => ["isDrawing", "Texture"],
        "object_placement" => ["Texture", "Preview"],
        "wall_polyline" => ["isDrawing", "Texture"],
        "material_stroke" => ["Mesh"],
        "portal_placement" => ["Texture"],
        "path_polyline" => ["isDrawing", "Texture", "ActivePath"],
        "light_placement" => ["texture", "preview"],
        _ => [],
    };

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
            int slot = 0,
            IReadOnlyList<object>? certifications = null,
            bool completeMatrix = true)
        {
            var path = Path.Combine(Root, $"runtime-receipt-slot-{slot}.json");
            var matrix = completeMatrix ? CompleteCertificationMatrix(certifications) : certifications;
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                schema_version = "1.0",
                @event = "started",
                mod_version = modVersion,
                target_dungeondraft_version = dungeondraftVersion,
                timestamp = timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                session_id = sessionId,
                supported_commands = commands,
                operation_certifications = matrix,
            }));
        }

        public string CreateReceiptJson() => JsonSerializer.Serialize(new
        {
            schema_version = "1.0",
            @event = "started",
            mod_version = "0.2.1",
            target_dungeondraft_version = "1.2.0.1",
            timestamp = Now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            session_id = "test-session",
            supported_commands = new[] { "status", "apply_plan", "inspect_map" },
            operation_certifications = CompleteCertificationMatrix(null),
        });

        public void WriteRawReceipt(string json) => File.WriteAllText(
            Path.Combine(Root, "runtime-receipt-slot-0.json"),
            json);

        private static IReadOnlyList<object> CompleteCertificationMatrix(IReadOnlyList<object>? overrides)
        {
            var serializedOverrides = (overrides ?? [])
                .Select(value => JsonSerializer.SerializeToElement(value))
                .ToDictionary(
                    value => value.GetProperty("operation_type").GetString()!,
                    value => (object)value,
                    StringComparer.Ordinal);
            return ExpectedMapOperations
                .Select(operation => serializedOverrides.TryGetValue(operation, out var value)
                    ? value
                    : Certification(
                        operation,
                        ToolName(operation),
                        available: true,
                        busy: false,
                        certified: false,
                        "executor_not_live_certified"))
                .ToArray();
        }

        private static string ToolName(string operation) => operation switch
        {
            "terrain_stroke" => "TerrainBrush",
            "pattern_region" or "colorable_pattern_region" => "PatternShapeTool",
            "cave_region" => "CaveBrush",
            "roof_region" => "RoofTool",
            "object_placement" => "ObjectTool",
            "wall_polyline" => "WallTool",
            "material_stroke" => "MaterialBrush",
            "portal_placement" => "PortalTool",
            "path_polyline" => "PathTool",
            "light_placement" => "LightTool",
            _ => "FloorShapeTool",
        };

        public void WriteHeartbeat(DateTimeOffset timestamp, string modVersion, string sessionId, int slot = 0)
        {
            var directory = Path.Combine(Root, "runtime-heartbeats");
            Directory.CreateDirectory(directory);
            WriteHeartbeatFile(directory, timestamp, modVersion, sessionId, slot);
        }

        public static void WriteHeartbeatFile(
            string directory,
            DateTimeOffset timestamp,
            string modVersion,
            string sessionId,
            int slot = 0)
        {
            File.WriteAllText(
                Path.Combine(directory, $"heartbeat-slot-{slot}.json"),
                JsonSerializer.Serialize(new
                {
                    schema_version = "1.0",
                    session_id = sessionId,
                    mod_version = modVersion,
                    timestamp = timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                }));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("cmd.exe could not create the junction fixture.");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && Directory.Exists(linkPath), "The Windows junction fixture could not be created.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
