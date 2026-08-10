using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DDAI.Core.Assets;
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
    private const int MaximumManifestBytes = 65_536;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    private readonly SafeLocalFileSystem fileSystem;
    private readonly SemaphoreSlim gate;
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
        fileSystem.EnsureDirectory("generated-assets");
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
            return Import(request);
        }
        finally
        {
            gate.Release();
        }
    }

    private GeneratedAssetImportResult Import(GeneratedAssetImportRequest request)
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

        var canonical = new CanonicalImport(
            contentHash,
            metadata.Category,
            metadata.Name,
            metadata.Tags,
            metadata.GridWidth,
            metadata.GridHeight);
        var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(canonical, JsonOptions);
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
            canonical.Category,
            canonical.Name,
            canonical.Tags,
            canonical.GridWidth,
            canonical.GridHeight,
            "staged");
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (fileSystem.EntryExists(receiptPath))
        {
            var receipt = JsonSerializer.Deserialize<IdempotencyReceipt>(
                fileSystem.ReadBounded(receiptPath, MaximumManifestBytes),
                JsonOptions) ?? throw Failure("stored_data_invalid", "The idempotency receipt is invalid.");
            if (!string.Equals(receipt.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
            {
                throw Failure("request_conflict", "The idempotency key belongs to a different normalized import.");
            }
            if (receipt.Result != result)
            {
                throw Failure("stored_data_conflict", "The idempotency receipt result does not match the normalized import.");
            }
            PublishPriorArtifacts(contentHash, content, previewHash, preview, manifestPath, manifestBytes);
            return result with { Duplicate = true };
        }

        var duplicate = fileSystem.EntryExists(manifestPath);
        PublishPriorArtifacts(contentHash, content, previewHash, preview, manifestPath, manifestBytes);
        PublishImmutable(
            receiptPath,
            JsonSerializer.SerializeToUtf8Bytes(new IdempotencyReceipt(requestFingerprint, result), JsonOptions),
            MaximumManifestBytes,
            GeneratedAssetDurableMove.IdempotencyReceipt);

        return result with { Duplicate = duplicate };
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

    private static CanonicalMetadata ValidateAndCanonicalizeRequest(GeneratedAssetImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw Failure("invalid_request", "An idempotency key is required.");
        }
        if (!AssetCategory.IsCanonical(request.Category))
        {
            throw Failure("unsupported_category", "The generated asset category is unsupported.");
        }
        if (!IsWellFormedUtf16(request.Name))
        {
            throw Failure("invalid_name", "The generated asset name contains invalid Unicode.");
        }
        var name = NormalizeWhitespace(request.Name);
        if (string.IsNullOrWhiteSpace(name) || CountScalars(name) > 120 || IsPathLikeOrControlledName(name))
        {
            throw Failure("invalid_name", "The generated asset name is invalid or exceeds 120 Unicode scalars.");
        }
        if (request.Tags is null || request.Tags.Count > 32)
        {
            throw Failure("invalid_tags", "Generated asset tags are invalid or exceed 32 entries.");
        }
        if (request.Tags.Any(tag => !IsWellFormedUtf16(tag)))
        {
            throw Failure("invalid_tags", "A generated asset tag contains invalid Unicode.");
        }
        var tags = request.Tags.Select(NormalizeTag).ToArray();
        if (tags.Any(tag => tag.Length == 0 || CountScalars(tag) > 64 || IsPathLikeOrControlled(tag)))
        {
            throw Failure("invalid_tags", "A generated asset tag is invalid or exceeds 64 Unicode scalars.");
        }
        tags = tags.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (request.GridWidth is <= 0 or > MaximumEdge || request.GridHeight is <= 0 or > MaximumEdge)
        {
            throw Failure("invalid_grid", "Generated asset grid dimensions must be between 1 and 4096.");
        }
        if (request.ContentBase64 is null == (request.InboxToken is null))
        {
            throw Failure("invalid_input", "Exactly one generated asset input form is required.");
        }
        return new CanonicalMetadata(request.Category, name, tags, request.GridWidth, request.GridHeight);
    }

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

    private void PublishImmutable(
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
            return;
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
                return;
            }
            durability.AfterDurableMove(move);
            // Reopen through SafeLocalFileSystem's validated handle before success. This binds
            // the published pathname to the exact flushed bytes even if the staging pathname
            // was substituted after its original handle closed or the destination was raced.
            RequireImmutableMatch(destinationPath, bytes, maximumExistingBytes);
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

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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
