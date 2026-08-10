using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DDAI.Core.Assets;

public static class AssetReference
{
    public static string Create(string? packId, string category, string resourceIdentity)
    {
        if (!AssetCategory.IsCanonical(category))
        {
            throw new ArgumentException("Category must be an exact canonical asset category.", nameof(category));
        }

        ArgumentNullException.ThrowIfNull(resourceIdentity);

        var normalizedPackId = packId?.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant() ?? string.Empty;
        var input = string.Concat(normalizedPackId, "\n", category, "\n", resourceIdentity);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }
}

public static class AssetCatalogJson
{
    public const int MaximumJsonBytes = 1024 * 1024;

    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    public static string SerializeManifest(AssetCatalogManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateManifest(manifest);
        return JsonSerializer.Serialize(manifest, SerializerOptions);
    }

    public static AssetCatalogManifest DeserializeManifest(string json)
    {
        using var document = ParseBounded(json);
        ValidateManifestWireShape(document.RootElement);

        var manifest = JsonSerializer.Deserialize<AssetCatalogManifest>(json, SerializerOptions)
            ?? throw new JsonException("Asset catalog manifest cannot be JSON null.");
        ValidateManifest(manifest);
        return manifest;
    }

    public static string SerializeEntries(IReadOnlyList<AssetCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ValidateEntries(entries);
        return JsonSerializer.Serialize(entries, SerializerOptions);
    }

    public static IReadOnlyList<AssetCatalogEntry> DeserializeEntries(string json)
    {
        using var document = ParseBounded(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Asset catalog entries must be a JSON array.");
        }

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            EnsureObjectShape(
                entry,
                "asset catalog entry",
                ["asset_ref", "category", "display_name", "resource_fingerprint", "pack_id", "pack_name", "search_terms", "tags", "preview_hash", "allow_third_party_use", "generated"]);
        }

        var entries = JsonSerializer.Deserialize<IReadOnlyList<AssetCatalogEntry>>(json, SerializerOptions)
            ?? throw new JsonException("Asset catalog entries cannot be JSON null.");
        ValidateEntries(entries);
        return entries;
    }

    public static string ComputeCatalogFingerprint(AssetCatalogManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var builder = new StringBuilder();
        AppendString(builder, "schema_version", manifest.SchemaVersion);
        AppendString(builder, "session_id", manifest.SessionId);
        AppendInteger(builder, "catalog_revision", manifest.CatalogRevision);
        AppendString(builder, "snapshot_at", manifest.SnapshotAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        AppendBoolean(builder, "complete", manifest.Complete);

        foreach (var category in AssetCategory.All)
        {
            var count = manifest.CategoryCounts is not null && manifest.CategoryCounts.TryGetValue(category, out var value)
                ? value
                : -1;
            AppendInteger(builder, $"category[{category}]", count);
        }

        AppendInteger(builder, "chunks_count", manifest.Chunks?.Count ?? -1);
        if (manifest.Chunks is not null)
        {
            for (var index = 0; index < manifest.Chunks.Count; index++)
            {
                var chunk = manifest.Chunks[index];
                AppendString(builder, $"chunk[{index}].file_name", chunk?.FileName);
                AppendString(builder, $"chunk[{index}].sha256", chunk?.Sha256);
                AppendInteger(builder, $"chunk[{index}].entry_count", chunk?.EntryCount ?? -1);
                AppendInteger(builder, $"chunk[{index}].byte_count", chunk?.ByteCount ?? -1);
            }
        }

        AppendInteger(builder, "errors_count", manifest.Errors?.Count ?? -1);
        if (manifest.Errors is not null)
        {
            for (var index = 0; index < manifest.Errors.Count; index++)
            {
                var error = manifest.Errors[index];
                AppendString(builder, $"error[{index}].code", error?.Code);
                AppendString(builder, $"error[{index}].message", error?.Message);
                AppendString(builder, $"error[{index}].category", error?.Category);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static JsonDocument ParseBounded(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
        {
            throw new JsonException("Asset catalog JSON exceeds the 1 MiB limit.");
        }

        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        }
        catch (JsonException)
        {
            throw;
        }
    }

    private static void ValidateManifestWireShape(JsonElement root)
    {
        EnsureObjectShape(
            root,
            "asset catalog manifest",
            ["schema_version", "session_id", "catalog_revision", "catalog_fingerprint", "snapshot_at", "complete", "category_counts", "chunks", "errors"]);

        EnsureObjectShape(root.GetProperty("category_counts"), "category counts", AssetCategory.All);
        EnsureArrayObjects(root.GetProperty("chunks"), "asset catalog chunk", ["file_name", "sha256", "entry_count", "byte_count"]);
        EnsureArrayObjects(root.GetProperty("errors"), "asset catalog error", ["code", "message", "category"]);
    }

    private static void EnsureArrayObjects(JsonElement element, string name, IReadOnlyList<string> expectedNames)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"{name} collection must be an array.");
        }

        foreach (var item in element.EnumerateArray())
        {
            EnsureObjectShape(item, name, expectedNames);
        }
    }

    private static void EnsureObjectShape(JsonElement element, string name, IReadOnlyList<string> expectedNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"{name} must be a JSON object.");
        }

        var expected = new HashSet<string>(expectedNames, StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name))
            {
                throw new JsonException($"{name} contains unsupported property '{property.Name}'.");
            }

            if (!observed.Add(property.Name))
            {
                throw new JsonException($"{name} contains duplicate property '{property.Name}'.");
            }
        }

        if (observed.Count != expected.Count)
        {
            var missing = expected.First(property => !observed.Contains(property));
            throw new JsonException($"{name} is missing required property '{missing}'.");
        }
    }

    private static void ValidateManifest(AssetCatalogManifest manifest)
    {
        RequireExact(manifest.SchemaVersion, AssetCatalogManifest.CurrentSchemaVersion, "schema version");
        RequireNonBlank(manifest.SessionId, "session id");
        if (manifest.CatalogRevision < 0)
        {
            throw new JsonException("Catalog revision cannot be negative.");
        }

        RequireHash(manifest.CatalogFingerprint, "catalog fingerprint");
        if (manifest.SnapshotAt == DateTimeOffset.MinValue || manifest.SnapshotAt.Offset != TimeSpan.Zero)
        {
            throw new JsonException("Snapshot timestamp must be a non-minimum UTC timestamp.");
        }

        ArgumentNullException.ThrowIfNull(manifest.CategoryCounts);
        if (manifest.CategoryCounts.Count != AssetCategory.All.Count ||
            manifest.CategoryCounts.Keys.Any(category => !AssetCategory.IsCanonical(category)))
        {
            throw new JsonException("Category counts must include each canonical category exactly once.");
        }

        foreach (var category in AssetCategory.All)
        {
            if (!manifest.CategoryCounts.TryGetValue(category, out var count) || count < 0)
            {
                throw new JsonException("Category counts must include non-negative counts for every canonical category.");
            }
        }

        ArgumentNullException.ThrowIfNull(manifest.Chunks);
        var fileNames = new HashSet<string>(StringComparer.Ordinal);
        long chunkEntryTotal = 0;
        foreach (var chunk in manifest.Chunks)
        {
            if (chunk is null)
            {
                throw new JsonException("Catalog chunks cannot contain null.");
            }

            RequireSafeChunkFileName(chunk.FileName);
            if (!fileNames.Add(chunk.FileName))
            {
                throw new JsonException("Catalog chunks cannot contain duplicate file names.");
            }

            RequireHash(chunk.Sha256, "chunk SHA-256");
            if (chunk.EntryCount < 0 || chunk.ByteCount < 0)
            {
                throw new JsonException("Chunk entry and byte counts cannot be negative.");
            }

            try
            {
                chunkEntryTotal = checked(chunkEntryTotal + chunk.EntryCount);
            }
            catch (OverflowException exception)
            {
                throw new JsonException("Chunk entry counts overflow.", exception);
            }
        }

        var categoryEntryTotal = manifest.CategoryCounts.Values.Aggregate(0L, static (total, count) => checked(total + count));
        if (chunkEntryTotal != categoryEntryTotal)
        {
            throw new JsonException("Category counts must equal the declared chunk entry count.");
        }

        ArgumentNullException.ThrowIfNull(manifest.Errors);
        foreach (var error in manifest.Errors)
        {
            if (error is null)
            {
                throw new JsonException("Catalog errors cannot contain null.");
            }

            RequireNonBlank(error.Code, "catalog error code");
            RequireNonBlank(error.Message, "catalog error message");
            if (error.Category is not null && !AssetCategory.IsCanonical(error.Category))
            {
                throw new JsonException("Catalog error category must be canonical when present.");
            }
        }

        if (!string.Equals(manifest.CatalogFingerprint, ComputeCatalogFingerprint(manifest), StringComparison.Ordinal))
        {
            throw new JsonException("Catalog fingerprint does not match the canonical manifest content.");
        }
    }

    private static void ValidateEntries(IReadOnlyList<AssetCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var assetReferences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                throw new JsonException("Asset catalog entries cannot contain null.");
            }

            RequireAssetReference(entry.AssetRef);
            if (!assetReferences.Add(entry.AssetRef))
            {
                throw new JsonException("Asset catalog entries cannot contain duplicate asset references.");
            }

            if (!AssetCategory.IsCanonical(entry.Category))
            {
                throw new JsonException("Asset category must be an exact canonical category.");
            }

            RequireNonBlank(entry.DisplayName, "display name");
            RequireHash(entry.ResourceFingerprint, "resource fingerprint");
            RequireOptionalNonBlank(entry.PackId, "pack id");
            RequireOptionalNonBlank(entry.PackName, "pack name");
            RequireStrings(entry.SearchTerms, "search terms");
            RequireStrings(entry.Tags, "tags");
            if (entry.PreviewHash is not null)
            {
                RequireHash(entry.PreviewHash, "preview hash");
            }
        }
    }

    private static void RequireAssetReference(string? value)
    {
        if (value is null || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal) || !IsCanonicalHash(value.AsSpan(7)))
        {
            throw new JsonException("Asset reference must be a sha256 prefix followed by 64 lowercase hexadecimal characters.");
        }
    }

    private static void RequireSafeChunkFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) ||
            value.Contains("..", StringComparison.Ordinal) ||
            !value.EndsWith(".json", StringComparison.Ordinal) ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new JsonException("Chunk file name must be a safe relative JSON file name.");
        }
    }

    private static void RequireHash(string? value, string name)
    {
        if (value is null || !IsCanonicalHash(value.AsSpan()))
        {
            throw new JsonException($"{name} must contain 64 lowercase hexadecimal characters.");
        }
    }

    private static bool IsCanonicalHash(ReadOnlySpan<char> value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if ((character < '0' || character > '9') && (character < 'a' || character > 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static void RequireNonBlank(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException($"{name} cannot be null, empty, or whitespace.");
        }
    }

    private static void RequireOptionalNonBlank(string? value, string name)
    {
        if (value is not null)
        {
            RequireNonBlank(value, name);
        }
    }

    private static void RequireStrings(IReadOnlyList<string>? values, string name)
    {
        if (values is null || values.Any(string.IsNullOrWhiteSpace))
        {
            throw new JsonException($"{name} cannot be null or contain null, empty, or whitespace values.");
        }
    }

    private static void RequireExact(string? value, string expected, string name)
    {
        if (!string.Equals(value, expected, StringComparison.Ordinal))
        {
            throw new JsonException($"Unsupported {name}.");
        }
    }

    private static void AppendString(StringBuilder builder, string name, string? value)
    {
        builder.Append(name);
        builder.Append('=');
        if (value is null)
        {
            builder.Append("null");
        }
        else
        {
            builder.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(value);
        }

        builder.Append('\n');
    }

    private static void AppendInteger(StringBuilder builder, string name, long value)
    {
        builder.Append(name);
        builder.Append('=');
        builder.Append(value.ToString(CultureInfo.InvariantCulture));
        builder.Append('\n');
    }

    private static void AppendBoolean(StringBuilder builder, string name, bool value)
    {
        builder.Append(name);
        builder.Append('=');
        builder.Append(value ? "true" : "false");
        builder.Append('\n');
    }
}
