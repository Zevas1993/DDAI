namespace DDAI.McpProbe;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = McpProbeOptions.Parse(args);
            using var timeout = new CancellationTokenSource(options.Timeout);
            var text = await new McpStatusProbe().RunAsync(options, timeout.Token);
            await Console.Out.WriteLineAsync(text);
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"DDAI MCP probe failed: {exception.Message}");
            return 1;
        }
    }
}
