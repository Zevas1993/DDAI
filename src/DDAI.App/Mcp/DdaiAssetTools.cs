using System.ComponentModel;
using System.Text.Json;
using DDAI.App.Assets;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DDAI.App.Mcp;

[McpServerToolType]
public sealed class DdaiAssetTools
{
    [McpServerTool(
        Name = "ddai_search_assets",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Search the accepted local asset catalog with bounded filters and opaque pagination. Third-party-use metadata is preserved without filtering.")]
    public static CallToolResult SearchAssets(AssetSearchQuery query, AssetSearchService service)
    {
        try
        {
            return DdaiToolResults.FromSearch(service.Search(query));
        }
        catch (InvalidOperationException)
        {
            return DdaiToolResults.Error("catalog_unavailable");
        }
        catch (ArgumentException)
        {
            return DdaiToolResults.Error("invalid_request");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DdaiToolResults.Error("search_unavailable");
        }
    }
}

internal static class DdaiToolResults
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public static CallToolResult FromSearch(AssetSearchResult result)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = json }],
            StructuredContent = JsonSerializer.Deserialize<JsonElement>(json, JsonOptions),
        };
    }

    public static CallToolResult Error(string errorCode)
    {
        var json = JsonSerializer.Serialize(new { error = errorCode }, JsonOptions);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = json }],
            IsError = true,
        };
    }
}
