using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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
        builder.Services.AddSingleton(new GeneratedAssetStore(options.MailboxRoot));
        builder.Services.AddSingleton(serviceProvider => new AssetCatalogRepository(
            Path.Combine(options.MailboxRoot, "catalog"),
            options.MailboxRoot,
            serviceProvider.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(serviceProvider =>
        {
            var catalogRepository = serviceProvider.GetRequiredService<AssetCatalogRepository>();
            return new AssetSearchService(
                () =>
                {
                    _ = catalogRepository.TryRefresh();
                    return catalogRepository.GetCurrent();
                },
                catalogRepository.GetStagedGeneratedEntries);
        });
        builder.Services.AddSingleton(serviceProvider => new DdaiStatusService(
            serviceProvider.GetRequiredService<AtomicMailbox>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<AssetCatalogRepository>()));
        builder.Services.AddSingleton<DdaiPlanService>();
        builder.Services.AddSingleton<DdaiMapInspectionService>();
        builder.Services.AddSingleton<DdaiCapabilityService>();
        builder.Services.AddSingleton<IUniversalPlanContextProvider, LiveUniversalPlanContextProvider>();
        builder.Services.AddSingleton<DdaiUniversalPlanService>();
        builder.Services.AddSingleton(new DdaiMcpRuntimeOptions(options.Timeout));
        builder.Services
            .AddMcpServer(server => server.ServerInfo = new Implementation
            {
                Name = "ddai",
                Version = "0.1.0",
                Description = "Local Dungeondraft AI connector",
            })
            .WithStdioServerTransport()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, token) =>
            {
                ValidatePlanToolArguments(request.Params);
                return await next(request, token).ConfigureAwait(false);
            }))
            .WithTools<DdaiTools>(CreatePlanToolJsonOptions())
            .WithTools<DdaiCapabilityTools>()
            .WithTools<DdaiAssetTools>()
            .WithTools<DdaiMapTools>();

        await builder.Build().RunAsync(cancellationToken);
    }

    internal static void ValidatePlanToolArguments(CallToolRequestParams? parameters)
    {
        if (parameters?.Name is not ("ddai_validate_plan" or "ddai_apply_plan"))
        {
            return;
        }

        if (parameters.Arguments is null ||
            !parameters.Arguments.TryGetValue("plan", out var plan))
        {
            throw new JsonException("The plan tool requires a plan argument.");
        }

        try
        {
            _ = MapPlanJson.Deserialize(plan.GetRawText());
        }
        catch (MapPlanValidationException)
        {
            // Wire shape is strict here; semantic issues belong to the tool's structured result.
        }
    }

    internal static JsonSerializerOptions CreatePlanToolJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            AllowOutOfOrderMetadataProperties = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
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
    [Description("Validate a legacy rectangular-room plan locally, or validate a universal all-category plan against the live map, catalog revision, and certified Dungeondraft executors, without changing the map.")]
    public static async Task<string> ValidatePlan(
        MapPlan plan,
        DdaiPlanService planService,
        DdaiUniversalPlanService universalPlanService,
        DdaiMcpRuntimeOptions runtimeOptions,
        CancellationToken cancellationToken)
    {
        object result = StringComparer.Ordinal.Equals(plan.SchemaVersion, MapPlan.LegacySchemaVersion)
            ? planService.Validate(plan)
            : await universalPlanService.ValidateAsync(
                plan,
                runtimeOptions.RequestTimeout,
                cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(
        Name = "ddai_apply_plan",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Apply a legacy rectangular room or revision-locked universal all-category plan to the open Dungeondraft map. The same request_id and plan are idempotent.")]
    public static async Task<string> ApplyPlanAsync(
        MapPlan plan,
        DdaiPlanService planService,
        DdaiUniversalPlanService universalPlanService,
        DdaiMcpRuntimeOptions runtimeOptions,
        CancellationToken cancellationToken) =>
        JsonSerializer.Serialize(
            StringComparer.Ordinal.Equals(plan.SchemaVersion, MapPlan.LegacySchemaVersion)
                ? await planService.ApplyAsync(plan, runtimeOptions.RequestTimeout, cancellationToken).ConfigureAwait(false)
                : await universalPlanService.ApplyAsync(plan, runtimeOptions.RequestTimeout, cancellationToken).ConfigureAwait(false),
            JsonOptions);
}
