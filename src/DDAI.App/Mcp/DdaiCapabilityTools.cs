using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace DDAI.App.Mcp;

[McpServerToolType]
public sealed class DdaiCapabilityTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    [McpServerTool(
        Name = "ddai_get_capabilities",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Report the bounded DDAI connector, catalog, and live bridge capability contract.")]
    public static string GetCapabilities(DdaiCapabilityService capabilityService) =>
        JsonSerializer.Serialize(capabilityService.GetCapabilities(), JsonOptions);
}
