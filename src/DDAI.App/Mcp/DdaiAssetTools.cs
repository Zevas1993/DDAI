using System.ComponentModel;
using System.Text.Json;
using DDAI.App.Assets;
using DDAI.Core.Assets;
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

    [McpServerTool(
        Name = "ddai_get_asset_preview",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Return one bounded PNG preview for an exact opaque asset reference in the accepted local catalog.")]
    public static CallToolResult GetAssetPreview(string assetRef, AssetCatalogRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (string.IsNullOrWhiteSpace(assetRef))
        {
            return DdaiToolResults.Error("invalid_request");
        }

        _ = repository.TryRefresh();
        var catalog = repository.GetCurrent();
        if (catalog is null)
        {
            return DdaiToolResults.Error("catalog_unavailable");
        }

        var entry = catalog.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.AssetRef, assetRef, StringComparison.Ordinal));
        if (entry?.PreviewHash is null)
        {
            return DdaiToolResults.Error(entry is null ? "asset_not_found" : "preview_unavailable");
        }

        var preview = repository.OpenPreview(entry.PreviewHash);
        return preview is null
            ? DdaiToolResults.Error("preview_unavailable")
            : DdaiToolResults.FromPreview(entry, catalog, preview);
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

    public static CallToolResult FromPreview(
        AssetCatalogEntry entry,
        AcceptedAssetCatalog catalog,
        byte[] preview)
    {
        var json = JsonSerializer.Serialize(new AssetPreviewResult(
            entry.AssetRef,
            entry.Category,
            entry.PreviewHash!,
            "image/png",
            preview.Length,
            catalog.Manifest.CatalogRevision,
            catalog.Manifest.CatalogFingerprint,
            catalog.Manifest.SnapshotAt,
            catalog.Live), JsonOptions);
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = json },
                ImageContentBlock.FromBytes(preview, "image/png"),
            ],
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
            StructuredContent = JsonSerializer.Deserialize<JsonElement>(json, JsonOptions),
        };
    }

    private sealed record AssetPreviewResult(
        string AssetRef,
        string Category,
        string PreviewHash,
        string MimeType,
        int ByteCount,
        long CatalogRevision,
        string CatalogFingerprint,
        DateTimeOffset SnapshotAt,
        bool Live);
}
