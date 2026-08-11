using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DDAI.Core.Assets;
using Microsoft.Win32.SafeHandles;
using SkiaSharp;

namespace DDAI.App.Assets;

internal enum GeneratedAssetDurableMove
{
    Inbox,
    Content,
    Preview,
    Manifest,
    IdempotencyReceipt,
}

internal interface IGeneratedAssetDurability
{
    void AfterDurableMove(GeneratedAssetDurableMove move);
}

internal sealed class GeneratedAssetDurability : IGeneratedAssetDurability
{
    public static GeneratedAssetDurability Instance { get; } = new();
    public void AfterDurableMove(GeneratedAssetDurableMove move)
    {
    }
}

public sealed class GeneratedAssetStore
{
    public const int MaximumDecodedBytes = 8_388_608;
    public const long MaximumPixels = 16_777_216;
    public const int MaximumEdge = 4096;
    public const int MaximumPreviewEdge = 256;
    public const int MaximumPreviewBytes = 262_144;

    private const int MaximumPersistedImageBytes = 83_886_080;
    internal const int MaximumManifestBytes = 65_536;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    private readonly SafeLocalFileSystem fileSystem;
    private readonly SemaphoreSlim gate;
    private readonly string generatedRoot;
    private readonly string contentRoot;
    private readonly string previewRoot;
    private readonly string manifestRoot;
    private readonly string idempotencyRoot;
    private readonly string inboxRoot;
    private readonly IGeneratedAssetDurability durability;

    public GeneratedAssetStore(string dataRoot) : this(dataRoot, GeneratedAssetDurability.Instance)
    {
    }

    internal GeneratedAssetStore(string dataRoot, IGeneratedAssetDurability durability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        if (!Path.IsPathFullyQualified(dataRoot))
        {
            throw new InvalidDataException("The generated asset data root must be absolute.");
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        Directory.CreateDirectory(normalizedRoot);
        fileSystem = new SafeLocalFileSystem(normalizedRoot);
        generatedRoot = fileSystem.EnsureDirectory("generated-assets");
        contentRoot = fileSystem.EnsureDirectory("generated-assets", "content");
        previewRoot = fileSystem.EnsureDirectory("generated-assets", "preview");
        manifestRoot = fileSystem.EnsureDirectory("generated-assets", "manifest");
        idempotencyRoot = fileSystem.EnsureDirectory("generated-assets", "idempotency");
        inboxRoot = fileSystem.EnsureDirectory("import-inbox");
        gate = Gates.GetOrAdd(normalizedRoot, _ => new SemaphoreSlim(1, 1));
        this.durability = durability ?? throw new ArgumentNullException(nameof(durability));
    }

    public async Task<string> StageInboxAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        if (content.Length > MaximumDecodedBytes)
        {
            throw Failure("image_too_large", "The inbox image exceeds its decoded byte limit.");
        }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                var path = Path.Combine(inboxRoot, token + ".bin");
                if (fileSystem.EntryExists(path)) continue;
                PublishImmutable(path, content.ToArray(), MaximumDecodedBytes, GeneratedAssetDurableMove.Inbox);
                return token;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GeneratedAssetImportResult> ImportAsync(
        GeneratedAssetImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Import(request, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public byte[]? OpenPreview(string previewHash)
    {
        if (!IsHash(previewHash)) return null;
        var path = Path.Combine(previewRoot, previewHash + ".png");
        try
        {
            if (!fileSystem.EntryExists(path)) return null;
            var preview = fileSystem.ReadBounded(path, MaximumPreviewBytes);
            return string.Equals(Hash(preview), previewHash, StringComparison.Ordinal) ? preview : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private GeneratedAssetImportResult Import(GeneratedAssetImportRequest request, CancellationToken cancellationToken)
    {
        var metadata = ValidateAndCanonicalizeRequest(request);
        var source = request.ContentBase64 is not null
            ? DecodeBase64(request.ContentBase64)
            : ReadInbox(request.InboxToken!);
        using var decoded = DecodeAndNormalize(source);
        var content = EncodePng(decoded);
        VerifyNormalizedPng(content, decoded.Width, decoded.Height);
        var contentHash = Hash(content);
        var preview = EncodeBoundedPreview(decoded);
        var previewHash = Hash(preview);

        var canonicalBytes = SerializeCanonicalImport(contentHash, metadata);
        var generatedAssetId = Hash(canonicalBytes);
        var result = new GeneratedAssetImportResult(
            generatedAssetId,
            contentHash,
            previewHash,
            decoded.Width,
            decoded.Height,
            metadata.Category,
            "staged",
            false);
        var receiptPath = Path.Combine(idempotencyRoot, Hash(Encoding.UTF8.GetBytes(request.IdempotencyKey)) + ".json");
        var requestFingerprint = Hash(canonicalBytes);
        var manifestPath = Path.Combine(manifestRoot, generatedAssetId + ".json");
        var manifest = new GeneratedAssetManifest(
            1,
            generatedAssetId,
            contentHash,
            previewHash,
            decoded.Width,
            decoded.Height,
            metadata.Category,
            metadata.Name,
            metadata.Tags,
            metadata.GridWidth,
            metadata.GridHeight,
            "staged");
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        GeneratedAssetMutex mutex;
        try
        {
            mutex = CreateGeneratedAssetMutex(generatedRoot);
        }
        catch (Exception exception) when (exception is Win32Exception or UnauthorizedAccessException)
        {
            // Never fall back to the process-local gate. A foreign or inaccessible global
            // object would otherwise reopen the cross-process publication race.
            throw Failure("storage_unavailable", "The generated asset transaction lock is unavailable.", exception);
        }

        using var heldMutex = mutex;
        var entered = false;
        try
        {
            try { entered = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { entered = true; }
            if (!entered)
            {
                throw Failure("storage_busy", "The generated asset transaction lock timed out.");
            }

            if (TryReplayReceipt(
                    receiptPath,
                    requestFingerprint,
                    result,
                    contentHash,
                    content,
                    previewHash,
                    preview,
                    manifestPath,
                    manifestBytes,
                    out var replay))
            {
                return replay;
            }

            var duplicate = fileSystem.EntryExists(manifestPath);
            PublishPriorArtifacts(contentHash, content, previewHash, preview, manifestPath, manifestBytes);
            // This is the final rollback-safe cancellation boundary. Content, preview, and
            // manifest publication are immutable and recoverable without a receipt; once the
            // receipt is durable, the import is complete and later cancellation must not
            // misreport a completed idempotent operation as canceled.
            cancellationToken.ThrowIfCancellationRequested();
            var receiptBytes = JsonSerializer.SerializeToUtf8Bytes(new IdempotencyReceipt(requestFingerprint, result), JsonOptions);
            try
            {
                var receiptCreated = PublishImmutable(
                    receiptPath,
                    receiptBytes,
                    MaximumManifestBytes,
                    GeneratedAssetDurableMove.IdempotencyReceipt);
                if (!receiptCreated && TryReplayReceipt(
                        receiptPath,
                        requestFingerprint,
                        result,
                        contentHash,
                        content,
                        previewHash,
                        preview,
                        manifestPath,
                        manifestBytes,
                        out replay))
                {
                    return replay;
                }
            }
            catch (GeneratedAssetImportException exception) when (exception.Code == "stored_data_conflict")
            {
                // A receipt pathname collision must be classified by its canonical request,
                // not reported as a generic immutable-data conflict.
                if (TryReplayReceipt(
                        receiptPath,
                        requestFingerprint,
                        result,
                        contentHash,
                        content,
                        previewHash,
                        preview,
                        manifestPath,
                        manifestBytes,
                        out replay))
                {
                    return replay;
                }
                throw;
            }

            return result with { Duplicate = duplicate };
        }
        finally
        {
            if (entered) mutex.Release();
        }
    }

    private bool TryReplayReceipt(
        string receiptPath,
        string requestFingerprint,
        GeneratedAssetImportResult result,
        string contentHash,
        byte[] content,
        string previewHash,
        byte[] preview,
        string manifestPath,
        byte[] manifestBytes,
        out GeneratedAssetImportResult replay)
    {
        replay = default!;
        if (!fileSystem.EntryExists(receiptPath)) return false;

        IdempotencyReceipt receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<IdempotencyReceipt>(
                fileSystem.ReadBounded(receiptPath, MaximumManifestBytes),
                JsonOptions) ?? throw Failure("stored_data_invalid", "The idempotency receipt is invalid.");
        }
        catch (JsonException exception)
        {
            throw Failure("stored_data_invalid", "The idempotency receipt is invalid.", exception);
        }
        if (!IsHash(receipt.RequestFingerprint))
        {
            throw Failure("stored_data_invalid", "The idempotency receipt fingerprint is invalid.");
        }
        if (!string.Equals(receipt.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
        {
            throw Failure("request_conflict", "The idempotency key belongs to a different normalized import.");
        }
        if (receipt.Result is null)
        {
            throw Failure("stored_data_invalid", "The idempotency receipt result is missing.");
        }
        if (receipt.Result != result)
        {
            throw Failure("stored_data_conflict", "The idempotency receipt result does not match the normalized import.");
        }

        PublishPriorArtifacts(contentHash, content, previewHash, preview, manifestPath, manifestBytes);
        replay = result with { Duplicate = true };
        return true;
    }

    private void PublishPriorArtifacts(
        string contentHash,
        byte[] content,
        string previewHash,
        byte[] preview,
        string manifestPath,
        byte[] manifestBytes)
    {
        PublishImmutable(
            Path.Combine(contentRoot, contentHash + ".png"),
            content,
            MaximumPersistedImageBytes,
            GeneratedAssetDurableMove.Content);
        PublishImmutable(
            Path.Combine(previewRoot, previewHash + ".png"),
            preview,
            MaximumPreviewBytes,
            GeneratedAssetDurableMove.Preview);
        PublishImmutable(
            manifestPath,
            manifestBytes,
            MaximumManifestBytes,
            GeneratedAssetDurableMove.Manifest);
    }

    internal static AssetCatalogEntry ReadStagedCatalogEntry(byte[] manifestBytes, string manifestFileName)
    {
        ArgumentNullException.ThrowIfNull(manifestBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFileName);
        ValidateGeneratedManifestWireShape(manifestBytes);
        var manifest = JsonSerializer.Deserialize<GeneratedAssetManifest>(manifestBytes, JsonOptions)
            ?? throw Failure("stored_data_invalid", "A generated asset manifest cannot be JSON null.");
        if (manifest.SchemaVersion != 1 ||
            !IsHash(manifest.GeneratedAssetId) ||
            !IsHash(manifest.ContentHash) ||
            !IsHash(manifest.PreviewHash) ||
            manifest.Width is < 1 or > MaximumEdge ||
            manifest.Height is < 1 or > MaximumEdge ||
            (long)manifest.Width * manifest.Height > MaximumPixels ||
            !string.Equals(manifest.ActivationState, "staged", StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(manifestFileName), manifestFileName, StringComparison.Ordinal) ||
            !string.Equals(manifestFileName, manifest.GeneratedAssetId + ".json", StringComparison.Ordinal))
        {
            throw Failure("stored_data_invalid", "A generated asset manifest is invalid.");
        }

        var metadata = ValidateAndCanonicalizeMetadata(
            manifest.Category,
            manifest.Name,
            manifest.Tags,
            manifest.GridWidth,
            manifest.GridHeight);
        if (!string.Equals(metadata.Category, manifest.Category, StringComparison.Ordinal) ||
            !string.Equals(metadata.Name, manifest.Name, StringComparison.Ordinal) ||
            !metadata.Tags.SequenceEqual(manifest.Tags, StringComparer.Ordinal) ||
            !string.Equals(Hash(SerializeCanonicalImport(manifest.ContentHash, metadata)), manifest.GeneratedAssetId, StringComparison.Ordinal))
        {
            throw Failure("stored_data_invalid", "A generated asset manifest does not match its canonical identity.");
        }

        return new AssetCatalogEntry(
            "sha256:" + manifest.GeneratedAssetId,
            metadata.Category,
            metadata.Name,
            manifest.ContentHash,
            "ddai-generated",
            "DDAI Generated Assets",
            [],
            metadata.Tags.ToArray(),
            manifest.PreviewHash,
            true,
            true);
    }

    private static void ValidateGeneratedManifestWireShape(byte[] manifestBytes)
    {
        using var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A generated asset manifest must be a JSON object.");
        }

        var expected = new HashSet<string>(
            [
                "schema_version",
                "generated_asset_id",
                "content_hash",
                "preview_hash",
                "width",
                "height",
                "category",
                "name",
                "tags",
                "grid_width",
                "grid_height",
                "activation_state",
            ],
            StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!expected.Remove(property.Name))
            {
                throw new JsonException("A generated asset manifest has duplicate or unsupported properties.");
            }
        }

        if (expected.Count != 0)
        {
            throw new JsonException("A generated asset manifest is missing required properties.");
        }
    }

    private static CanonicalMetadata ValidateAndCanonicalizeRequest(GeneratedAssetImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw Failure("invalid_request", "An idempotency key is required.");
        }
        var metadata = ValidateAndCanonicalizeMetadata(
            request.Category,
            request.Name,
            request.Tags,
            request.GridWidth,
            request.GridHeight);
        if (request.ContentBase64 is null == (request.InboxToken is null))
        {
            throw Failure("invalid_input", "Exactly one generated asset input form is required.");
        }
        return metadata;
    }

    private static CanonicalMetadata ValidateAndCanonicalizeMetadata(
        string category,
        string? requestedName,
        IReadOnlyList<string>? requestedTags,
        int gridWidth,
        int gridHeight)
    {
        if (!AssetCategory.IsCanonical(category))
        {
            throw Failure("unsupported_category", "The generated asset category is unsupported.");
        }
        if (!IsWellFormedUtf16(requestedName) || ContainsControlledScalar(requestedName!))
        {
            throw Failure("invalid_name", "The generated asset name contains invalid or controlled Unicode.");
        }
        var name = NormalizeWhitespace(requestedName);
        if (string.IsNullOrWhiteSpace(name) || CountScalars(name) > 120 || IsPathLikeOrControlledName(name))
        {
            throw Failure("invalid_name", "The generated asset name is invalid or exceeds 120 Unicode scalars.");
        }
        if (requestedTags is null || requestedTags.Count > 32)
        {
            throw Failure("invalid_tags", "Generated asset tags are invalid or exceed 32 entries.");
        }
        if (requestedTags.Any(tag => !IsWellFormedUtf16(tag) || ContainsControlledScalar(tag!)))
        {
            throw Failure("invalid_tags", "A generated asset tag contains invalid or controlled Unicode.");
        }
        var tags = requestedTags.Select(NormalizeTag).ToArray();
        if (tags.Any(tag => tag.Length == 0 || CountScalars(tag) > 64 || IsPathLikeOrControlled(tag)))
        {
            throw Failure("invalid_tags", "A generated asset tag is invalid or exceeds 64 Unicode scalars.");
        }
        tags = tags.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (gridWidth is <= 0 or > MaximumEdge || gridHeight is <= 0 or > MaximumEdge)
        {
            throw Failure("invalid_grid", "Generated asset grid dimensions must be between 1 and 4096.");
        }
        return new CanonicalMetadata(category, name, tags, gridWidth, gridHeight);
    }

    private static byte[] SerializeCanonicalImport(string contentHash, CanonicalMetadata metadata) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new CanonicalImport(
                contentHash,
                metadata.Category,
                metadata.Name,
                metadata.Tags,
                metadata.GridWidth,
                metadata.GridHeight),
            JsonOptions);

    private byte[] ReadInbox(string token)
    {
        if (!IsCanonicalInboxToken(token))
        {
            throw Failure("invalid_inbox_token", "The inbox token is invalid.");
        }
        var path = Path.Combine(inboxRoot, token + ".bin");
        try
        {
            if (!fileSystem.EntryExists(path))
            {
                throw Failure("invalid_inbox_token", "The inbox token is unknown.");
            }
            return fileSystem.ReadBounded(path, MaximumDecodedBytes);
        }
        catch (GeneratedAssetImportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure("invalid_inbox_token", "The inbox token could not be read safely.", exception);
        }
    }

    private static bool IsCanonicalInboxToken(string? token) =>
        token is { Length: 32 } && token.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static byte[] DecodeBase64(string value)
    {
        var maximumCharacters = checked(((MaximumDecodedBytes + 2) / 3) * 4);
        if (value.Length > maximumCharacters || value.Any(char.IsWhiteSpace))
        {
            throw Failure("image_too_large", "The encoded generated image exceeds its byte limit.");
        }
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length > MaximumDecodedBytes)
            {
                throw Failure("image_too_large", "The decoded generated image exceeds its byte limit.");
            }
            return bytes;
        }
        catch (FormatException exception)
        {
            throw Failure("invalid_base64", "The generated image is not canonical base64.", exception);
        }
    }

    private static SKBitmap DecodeAndNormalize(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw Failure("invalid_image", "The generated content is not a decodable image.");
        if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp))
        {
            throw Failure("unsupported_image_format", "Only PNG, JPEG, and WebP generated images are supported.");
        }
        // Skia reports zero for a static/single-frame codec and a positive count only for
        // multi-frame codecs. Normalize that documented sentinel to one logical frame.
        var logicalFrameCount = Math.Max(1, codec.FrameCount);
        if (logicalFrameCount != 1)
        {
            throw Failure("animated_image", "Animated generated images are unsupported.");
        }
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || info.Width > MaximumEdge || info.Height > MaximumEdge ||
            (long)info.Width * info.Height > MaximumPixels)
        {
            throw Failure("image_dimensions_exceeded", "The generated image dimensions exceed their limits.");
        }

        var decoded = new SKBitmap(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var ownsDecoded = true;
        try
        {
            var decodeResult = codec.GetPixels(decoded.Info, decoded.GetPixels());
            if (decodeResult != SKCodecResult.Success)
            {
                throw Failure("invalid_image", "The generated image pixels could not be decoded.");
            }
            if (codec.EncodedOrigin == SKEncodedOrigin.TopLeft)
            {
                ownsDecoded = false;
                return decoded;
            }
            return Orient(decoded, codec.EncodedOrigin);
        }
        finally
        {
            if (ownsDecoded) decoded.Dispose();
        }
    }

    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return source;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or
            SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var destination = new SKBitmap(
            swap ? source.Height : source.Width,
            swap ? source.Width : source.Height,
            source.ColorType,
            source.AlphaType);
        var ownsDestination = true;
        try
        {
            var sourcePixels = source.GetPixelSpan();
            var destinationPixels = destination.GetPixelSpan();
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < source.Width; x++)
                {
                    var (destinationX, destinationY) = origin switch
                    {
                        SKEncodedOrigin.TopRight => (source.Width - 1 - x, y),
                        SKEncodedOrigin.BottomRight => (source.Width - 1 - x, source.Height - 1 - y),
                        SKEncodedOrigin.BottomLeft => (x, source.Height - 1 - y),
                        SKEncodedOrigin.LeftTop => (y, x),
                        SKEncodedOrigin.RightTop => (source.Height - 1 - y, x),
                        SKEncodedOrigin.RightBottom => (source.Height - 1 - y, source.Width - 1 - x),
                        SKEncodedOrigin.LeftBottom => (y, source.Width - 1 - x),
                        _ => throw Failure("invalid_image", "The generated image has an unsupported encoded origin."),
                    };
                    sourcePixels.Slice(y * source.RowBytes + x * 4, 4).CopyTo(
                        destinationPixels.Slice(destinationY * destination.RowBytes + destinationX * 4, 4));
                }
            }
            ownsDestination = false;
            return destination;
        }
        finally
        {
            if (ownsDestination) destination.Dispose();
        }
    }

    private static SKBitmap CreatePreview(SKBitmap source)
    {
        if (source.Width <= MaximumPreviewEdge && source.Height <= MaximumPreviewEdge)
        {
            return source.Copy();
        }
        var scale = Math.Min(MaximumPreviewEdge / (double)source.Width, MaximumPreviewEdge / (double)source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale, MidpointRounding.AwayFromZero));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale, MidpointRounding.AwayFromZero));
        return source.Resize(new SKImageInfo(width, height, source.ColorType, source.AlphaType), SKSamplingOptions.Default)
            ?? throw Failure("invalid_image", "The generated image preview could not be rendered.");
    }

    private static byte[] EncodeBoundedPreview(SKBitmap source)
    {
        var current = CreatePreview(source);
        try
        {
            while (true)
            {
                var encoded = EncodePng(current);
                if (encoded.Length <= MaximumPreviewBytes)
                {
                    VerifyNormalizedPng(encoded, current.Width, current.Height);
                    return encoded;
                }
                if (current.Width == 1 && current.Height == 1)
                {
                    throw Failure("image_preview_too_large", "The normalized preview exceeds its byte limit.");
                }
                var width = Math.Max(1, current.Width * 3 / 4);
                var height = Math.Max(1, current.Height * 3 / 4);
                var next = current.Resize(
                    new SKImageInfo(width, height, current.ColorType, current.AlphaType),
                    SKSamplingOptions.Default) ?? throw Failure("invalid_image", "The generated image preview could not be bounded.");
                current.Dispose();
                current = next;
            }
        }
        finally
        {
            current.Dispose();
        }
    }

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw Failure("invalid_image", "The generated image could not be normalized.");
        return data.ToArray();
    }

    private static void VerifyNormalizedPng(byte[] bytes, int expectedWidth, int expectedHeight)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw Failure("normalization_failed", "The normalized PNG could not be verified.");
        if (codec.EncodedFormat != SKEncodedImageFormat.Png || Math.Max(1, codec.FrameCount) != 1 ||
            codec.Info.Width != expectedWidth || codec.Info.Height != expectedHeight)
        {
            throw Failure("normalization_failed", "The normalized PNG contract could not be verified.");
        }
        using var bitmap = new SKBitmap(expectedWidth, expectedHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
        {
            throw Failure("normalization_failed", "The normalized PNG pixels could not be verified.");
        }
    }

    private static string NormalizeWhitespace(string? value)
    {
        if (value is null) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace) builder.Append(' ');
            builder.Append(rune.ToString());
            pendingSpace = false;
        }
        return builder.ToString();
    }

    private static string NormalizeTag(string? value) => NormalizeWhitespace(value).ToLowerInvariant();

    private static int CountScalars(string value) => value.EnumerateRunes().Count();

    private static bool IsPathLikeOrControlledName(string value) => IsPathLikeOrControlled(value);

    private static bool IsPathLikeOrControlled(string value) =>
        value is "." or ".." ||
        value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
        value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
        ContainsControlledScalar(value);

    private static bool ContainsControlledScalar(string value) => value.EnumerateRunes().Any(rune =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or
            UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate);

    private static bool IsWellFormedUtf16(string? value)
    {
        if (value is null) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) return false;
                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return false;
            }
        }
        return true;
    }

    private bool PublishImmutable(
        string destinationPath,
        byte[] bytes,
        int maximumExistingBytes,
        GeneratedAssetDurableMove move)
    {
        var parent = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException("The generated asset destination has no parent.");
        using var lease = fileSystem.AcquireDirectoryLease(parent);
        if (fileSystem.EntryExists(destinationPath))
        {
            RequireImmutableMatch(destinationPath, bytes, maximumExistingBytes);
            return false;
        }

        var stagingPath = Path.Combine(parent, "." + Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".next");
        try
        {
            fileSystem.WriteImmutableBytes(stagingPath, bytes);
            try
            {
                File.Move(stagingPath, destinationPath, overwrite: false);
            }
            catch (IOException) when (fileSystem.EntryExists(destinationPath))
            {
                RequireImmutableMatch(destinationPath, bytes, maximumExistingBytes);
                return false;
            }
            durability.AfterDurableMove(move);
            // Reopen through SafeLocalFileSystem's validated handle before success. This binds
            // the published pathname to the exact flushed bytes even if the staging pathname
            // was substituted after its original handle closed or the destination was raced.
            RequireImmutableMatch(destinationPath, bytes, maximumExistingBytes);
            return true;
        }
        finally
        {
            if (fileSystem.EntryExists(stagingPath)) fileSystem.DeleteOrdinaryOrLink(stagingPath);
        }
    }

    private void RequireImmutableMatch(string path, byte[] expected, int maximumBytes)
    {
        if (!fileSystem.ReadBounded(path, maximumBytes).AsSpan().SequenceEqual(expected))
        {
            throw Failure("stored_data_conflict", "Immutable generated asset data does not match its identifier.");
        }
    }

    private static string MutexName(string generatedRoot)
    {
        var userHash = Hash(Encoding.UTF8.GetBytes(CurrentUserSid()));
        var rootHash = Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(generatedRoot).ToLowerInvariant()));
        return "Global\\DDAI.GeneratedAssets." + userHash + "." + rootHash;
    }

    private static GeneratedAssetMutex CreateGeneratedAssetMutex(string generatedRoot)
    {
        var sid = CurrentUserSid();
        var securityDescriptor = IntPtr.Zero;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                "D:P(A;;GA;;;SY)(A;;GA;;;" + sid + ")",
                SecurityDescriptorRevision,
                out securityDescriptor,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = securityDescriptor,
                InheritHandle = false,
            };
            var handle = CreateMutexEx(
                ref attributes,
                MutexName(generatedRoot),
                0,
                ReadControl | Synchronize | MutexModifyState);
            if (!handle.IsInvalid)
            {
                try
                {
                    ValidateGeneratedAssetMutexSecurity(handle);
                    return new GeneratedAssetMutex(handle);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            var nativeFailure = new Win32Exception(error);
            if (error == ErrorAccessDenied)
            {
                throw new UnauthorizedAccessException(
                    "The global generated-asset mutex is owned by another principal.",
                    nativeFailure);
            }
            throw nativeFailure;
        }
        finally
        {
            _ = LocalFree(securityDescriptor);
        }
    }

    private static string CurrentUserSid() => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new UnauthorizedAccessException("The current Windows user has no security identifier.");

    private static void ValidateGeneratedAssetMutexSecurity(SafeWaitHandle handle)
    {
        var error = GetSecurityInfo(
            handle,
            SeKernelObject,
            OwnerSecurityInformation | DaclSecurityInformation,
            out _,
            out _,
            out _,
            out _,
            out var securityDescriptor);
        if (error != 0) throw new Win32Exception(checked((int)error));

        try
        {
            var length = GetSecurityDescriptorLength(securityDescriptor);
            if (length == 0) throw new UnauthorizedAccessException("The global generated-asset mutex has no security descriptor.");
            var bytes = new byte[checked((int)length)];
            Marshal.Copy(securityDescriptor, bytes, 0, bytes.Length);
            var descriptor = new RawSecurityDescriptor(bytes, 0);
            using var identity = WindowsIdentity.GetCurrent();
            var currentUser = identity.User
                ?? throw new UnauthorizedAccessException("The current Windows user has no security identifier.");
            var expectedOwner = identity.Owner ?? currentUser;
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            if (descriptor.Owner is null ||
                (!descriptor.Owner.Equals(expectedOwner) &&
                 !descriptor.Owner.Equals(currentUser) &&
                 !descriptor.Owner.Equals(system)))
            {
                throw new UnauthorizedAccessException("The global generated-asset mutex has an unexpected owner.");
            }
            if ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 ||
                descriptor.DiscretionaryAcl is null)
            {
                throw new UnauthorizedAccessException("The global generated-asset mutex DACL is not protected.");
            }

            var requiredPrincipals = new HashSet<string>(StringComparer.Ordinal)
            {
                currentUser.Value,
                system.Value,
            };
            var observedPrincipals = new HashSet<string>(StringComparer.Ordinal);
            foreach (GenericAce ace in descriptor.DiscretionaryAcl)
            {
                if (ace is not CommonAce common ||
                    common.AceQualifier != AceQualifier.AccessAllowed ||
                    common.AceFlags != AceFlags.None ||
                    common.SecurityIdentifier is null ||
                    !requiredPrincipals.Contains(common.SecurityIdentifier.Value) ||
                    !GrantsMutexAllAccess(common.AccessMask))
                {
                    throw new UnauthorizedAccessException("The global generated-asset mutex DACL is not restricted to the current user and SYSTEM.");
                }
                observedPrincipals.Add(common.SecurityIdentifier.Value);
            }
            if (!observedPrincipals.SetEquals(requiredPrincipals))
            {
                throw new UnauthorizedAccessException("The global generated-asset mutex DACL is missing a required principal.");
            }
        }
        finally
        {
            _ = LocalFree(securityDescriptor);
        }
    }

    private static bool GrantsMutexAllAccess(int accessMask) =>
        (accessMask & GenericAll) != 0 || (accessMask & MutexAllAccess) == MutexAllAccess;

    private const uint SecurityDescriptorRevision = 1;
    private const uint MutexModifyState = 0x0001;
    private const uint ReadControl = 0x00020000;
    private const uint Synchronize = 0x00100000;
    private const int MutexAllAccess = 0x001F0001;
    private const int GenericAll = 0x10000000;
    private const int ErrorAccessDenied = 5;
    private const int SeKernelObject = 6;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    private sealed class GeneratedAssetMutex : WaitHandle
    {
        private readonly SafeWaitHandle ownedHandle;

        public GeneratedAssetMutex(SafeWaitHandle handle)
        {
            SafeWaitHandle = handle;
            ownedHandle = handle;
        }

        public void Release()
        {
            if (!ReleaseMutexNative(ownedHandle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSecurityDescriptorRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateMutexEx(
        ref SecurityAttributes mutexAttributes,
        string name,
        uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll", EntryPoint = "ReleaseMutex", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseMutexNative(SafeWaitHandle mutex);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(
        SafeWaitHandle handle,
        int objectType,
        uint securityInfo,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsHash(string? value) => value is not null && value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static GeneratedAssetImportException Failure(string code, string message, Exception? innerException = null) =>
        new(code, message, innerException);

    private sealed record CanonicalImport(
        string ContentHash,
        string Category,
        string Name,
        IReadOnlyList<string> Tags,
        int GridWidth,
        int GridHeight);

    private sealed record CanonicalMetadata(
        string Category,
        string Name,
        IReadOnlyList<string> Tags,
        int GridWidth,
        int GridHeight);

    private sealed record GeneratedAssetManifest(
        int SchemaVersion,
        string GeneratedAssetId,
        string ContentHash,
        string PreviewHash,
        int Width,
        int Height,
        string Category,
        string Name,
        IReadOnlyList<string> Tags,
        int GridWidth,
        int GridHeight,
        string ActivationState);

    private sealed record IdempotencyReceipt(
        string RequestFingerprint,
        GeneratedAssetImportResult Result);
}
