using System.Text.Json.Serialization;

namespace DDAI.Core.Assets;

public sealed record AssetCatalogEntry(
    [property: JsonPropertyName("asset_ref")] string AssetRef,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("resource_fingerprint")] string ResourceFingerprint,
    [property: JsonPropertyName("pack_id")] string? PackId,
    [property: JsonPropertyName("pack_name")] string? PackName,
    [property: JsonPropertyName("search_terms")] IReadOnlyList<string> SearchTerms,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("preview_hash")] string? PreviewHash,
    [property: JsonPropertyName("allow_third_party_use")] bool AllowThirdPartyUse,
    [property: JsonPropertyName("generated")] bool Generated);

public sealed record AssetCatalogChunk(
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("entry_count")] int EntryCount,
    [property: JsonPropertyName("byte_count")] long ByteCount);

public sealed record AssetCatalogError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("category")] string? Category);

public sealed record AssetCatalogManifest(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("catalog_revision")] long CatalogRevision,
    [property: JsonPropertyName("catalog_fingerprint")] string CatalogFingerprint,
    [property: JsonPropertyName("snapshot_at")] DateTimeOffset SnapshotAt,
    [property: JsonPropertyName("complete")] bool Complete,
    [property: JsonPropertyName("category_counts")] IReadOnlyDictionary<string, int> CategoryCounts,
    [property: JsonPropertyName("chunks")] IReadOnlyList<AssetCatalogChunk> Chunks,
    [property: JsonPropertyName("errors")] IReadOnlyList<AssetCatalogError> Errors)
{
    public const string CurrentSchemaVersion = "1.0";
}
