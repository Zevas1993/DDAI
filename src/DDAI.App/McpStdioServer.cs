using System.ComponentModel;
using System.Text.Json;
using DDAI.Core.Mailbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DDAI.App;

public static class McpStdioServer
{
    public static async Task RunAsync(CliOptions options, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder([]);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(new AtomicMailbox(options.MailboxRoot));
        builder.Services.AddSingleton(timeProvider);
        builder.Services.AddSingleton<DdaiStatusService>();
        builder.Services.AddSingleton(new DdaiMcpRuntimeOptions(options.Timeout));
        builder.Services
            .AddMcpServer(server => server.ServerInfo = new Implementation
            {
                Name = "ddai",
                Version = "0.1.0",
                Description = "Local Dungeondraft AI connector",
            })
            .WithStdioServerTransport()
            .WithTools<DdaiTools>();

        await builder.Build().RunAsync(cancellationToken);
    }
}

public sealed record DdaiMcpRuntimeOptions(TimeSpan StatusTimeout);

[McpServerToolType]
public sealed class DdaiTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [McpServerTool(
        Name = "ddai_status",
        ReadOnly = true,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false)]
    [Description("Report the live state of the local Dungeondraft DDAI mod through its atomic filesystem mailbox.")]
    public static async Task<string> GetStatusAsync(
        DdaiStatusService statusService,
        DdaiMcpRuntimeOptions runtimeOptions,
        CancellationToken cancellationToken)
    {
        var result = await statusService.GetStatusAsync(runtimeOptions.StatusTimeout, cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
