using System.ComponentModel;
using System.Text.Json;
using DDAI.App.Assets;
using DDAI.Core.Mailbox;
using DDAI.Core.MapPlans;
using DDAI.App.Mcp;
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
        builder.Services.AddSingleton(new AssetPackNormalizationService(options.MailboxRoot));
        builder.Services.AddHostedService<AssetPackNormalizationWorker>();
        builder.Services.AddSingleton(timeProvider);
        builder.Services.AddSingleton(serviceProvider => new AssetCatalogRepository(
            Path.Combine(options.MailboxRoot, "catalog"),
            serviceProvider.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<DdaiStatusService>();
        builder.Services.AddSingleton<DdaiPlanService>();
        builder.Services.AddSingleton<DdaiCapabilityService>();
        builder.Services.AddSingleton(new DdaiMcpRuntimeOptions(options.Timeout));
        builder.Services
            .AddMcpServer(server => server.ServerInfo = new Implementation
            {
                Name = "ddai",
                Version = "0.1.0",
                Description = "Local Dungeondraft AI connector",
            })
            .WithStdioServerTransport()
            .WithTools<DdaiTools>()
            .WithTools<DdaiCapabilityTools>();

        await builder.Build().RunAsync(cancellationToken);
    }
}

public sealed record DdaiMcpRuntimeOptions(TimeSpan RequestTimeout);

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
        var result = await statusService.GetStatusAsync(runtimeOptions.RequestTimeout, cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(
        Name = "ddai_validate_plan",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Validate one grid-relative rectangular-room plan without changing Dungeondraft.")]
    public static string ValidatePlan(MapPlan plan, DdaiPlanService planService) =>
        JsonSerializer.Serialize(planService.Validate(plan), JsonOptions);

    [McpServerTool(
        Name = "ddai_apply_plan",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Create one native rectangular wall in the open blank Dungeondraft map. The same request_id is idempotent.")]
    public static async Task<string> ApplyPlanAsync(
        MapPlan plan,
        DdaiPlanService planService,
        DdaiMcpRuntimeOptions runtimeOptions,
        CancellationToken cancellationToken) =>
        JsonSerializer.Serialize(
            await planService.ApplyAsync(plan, runtimeOptions.RequestTimeout, cancellationToken),
            JsonOptions);
}
