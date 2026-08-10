using System.Globalization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DDAI.McpProbe;

public sealed class McpStatusProbe
{
    public async Task<string> RunAsync(McpProbeOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var timeoutMilliseconds = checked((int)options.Timeout.TotalMilliseconds);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ddai-live-probe",
            Command = options.ExecutablePath,
            Arguments =
            [
                "serve", "--stdio",
                "--mailbox-root", options.MailboxRoot,
                "--timeout-ms", timeoutMilliseconds.ToString(CultureInfo.InvariantCulture),
            ],
            WorkingDirectory = Path.GetDirectoryName(options.ExecutablePath)!,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            StandardErrorLines = line => Console.Error.WriteLine(line),
        });

        await using var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        if (!tools.Any(tool => tool.Name == "ddai_status"))
        {
            throw new InvalidOperationException("Installed DDAI MCP server did not list ddai_status.");
        }

        var result = await client.CallToolAsync(
            "ddai_status",
            new Dictionary<string, object?>(),
            cancellationToken: cancellationToken);
        if (result.Content.Count != 1 || result.Content[0] is not TextContentBlock textBlock)
        {
            throw new InvalidOperationException(
                "Installed DDAI MCP server did not return exactly one text content block.");
        }

        return textBlock.Text;
    }
}
