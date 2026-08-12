using System.ComponentModel;
using System.Text;
using System.Text.Json;
using DDAI.Core.Maps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DDAI.App.Mcp;

[McpServerToolType]
public sealed class DdaiMapTools
{
    private static readonly HashSet<string> ClosedErrorCodes = new(
        [
            "invalid_request",
            "request_conflict",
            "inspect_timeout",
            "invalid_response",
            "catalog_revision_mismatch",
            "map_not_available",
            "active_level_unavailable",
            "unsupported_level",
            "invalid_region",
            "invalid_cursor",
            "inspection_state_invalid",
            "inspection_state_too_large",
            "response_too_large",
            "inspection_unavailable",
        ],
        StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    [McpServerTool(
        Name = "ddai_inspect_map",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Inspect a bounded page of native items from the current Dungeondraft level without changing the map.")]
    public static async Task<CallToolResult> InspectMapAsync(
        MapInspectionQuery query,
        DdaiMapInspectionService service,
        DdaiMcpRuntimeOptions runtimeOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(runtimeOptions);

        try
        {
            var result = await service.InspectAsync(
                query,
                runtimeOptions.RequestTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!result.Success || result.Page is null)
            {
                return DdaiToolResults.Error(CanonicalErrorCode(result.Error?.Code));
            }

            var page = result.Page;
            var json = JsonSerializer.Serialize(new
            {
                page.MapId,
                page.MapRevision,
                result.CatalogRevision,
                page.Canvas,
                page.GridSize,
                page.Levels,
                page.Items,
                page.NextCursor,
                page.Truncated,
                page.UnsupportedKinds,
            }, JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) > MapSnapshotJson.MaximumJsonBytes)
            {
                return DdaiToolResults.Error("response_too_large");
            }

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = json }],
                StructuredContent = JsonSerializer.Deserialize<JsonElement>(json, JsonOptions),
            };
        }
        catch (ArgumentException)
        {
            return DdaiToolResults.Error("invalid_request");
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return DdaiToolResults.Error("invalid_response");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DdaiToolResults.Error("inspection_unavailable");
        }
    }

    [McpServerTool(
        Name = "ddai_undo_last_job",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Reverse exactly one completed DDAI map job when its job ID, map identity, and current revision still match durable bridge evidence.")]
    public static async Task<string> UndoLastJobAsync(
        string requestId,
        string targetRequestId,
        string expectedMapId,
        long expectedMapRevision,
        DdaiUniversalPlanService service,
        DdaiMcpRuntimeOptions runtimeOptions,
        CancellationToken cancellationToken) =>
        JsonSerializer.Serialize(
            await service.UndoLastJobAsync(
                requestId,
                targetRequestId,
                expectedMapId,
                expectedMapRevision,
                runtimeOptions.RequestTimeout,
                cancellationToken).ConfigureAwait(false),
            JsonOptions);

    private static string CanonicalErrorCode(string? code) =>
        code is not null && ClosedErrorCodes.Contains(code) ? code : "inspection_unavailable";
}
