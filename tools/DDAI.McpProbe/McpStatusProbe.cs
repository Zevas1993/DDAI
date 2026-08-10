namespace DDAI.McpProbe;

public sealed class McpStatusProbe
{
    public async Task<string> RunAsync(McpProbeOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ToolName != "ddai_status" || options.PlanFilePath is not null)
        {
            throw new ArgumentException("McpStatusProbe only accepts ddai_status options.", nameof(options));
        }

        return await new McpToolProbe().RunAsync(options, cancellationToken);
    }
}
