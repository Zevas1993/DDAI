using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DDAI.App.Assets;
using SkiaSharp;

namespace DDAI.App.Tests.Assets;

public sealed class GeneratedAssetStoreTests
{
    [Fact]
    public async Task ImportAsync_DecodesNormalizesAndPersistsARealTwoByTwoPng()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var input = CreatePng(2, 2);

        var result = await sandbox.Store.ImportAsync(Request("first", input));

        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);
        Assert.Equal("Objects", result.Category);
        Assert.Equal("staged", result.ActivationState);
        Assert.False(result.Duplicate);
        var content = File.ReadAllBytes(sandbox.ContentPath(result.ContentHash));
        Assert.Equal(result.ContentHash, Hash(content));
        AssertPngDimensions(content, 2, 2);
        Assert.True(File.Exists(sandbox.ManifestPath(result.GeneratedAssetId)));
    }

    [Fact]
    public async Task ImportAsync_BoundsPreviewToTwoHundredFiftySixPixels()
    {
        using var sandbox = new GeneratedAssetSandbox();

        var result = await sandbox.Store.ImportAsync(Request("preview", CreatePng(512, 256)));

        var preview = File.ReadAllBytes(sandbox.PreviewPath(result.PreviewHash));
        Assert.Equal(result.PreviewHash, Hash(preview));
        AssertPngDimensions(preview, 256, 128);
        Assert.True(preview.Length <= 262_144);
    }

    [Fact]
    public async Task ImportAsync_StripsAncillaryMetadataByRenderingNormalizedPixels()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var input = AddPngTextChunk(CreatePng(2, 2), "Comment", "private prompt");
        Assert.Contains("tEXt", ReadChunkTypes(input));

        var result = await sandbox.Store.ImportAsync(Request("metadata", input));

        var content = File.ReadAllBytes(sandbox.ContentPath(result.ContentHash));
        Assert.DoesNotContain("tEXt", ReadChunkTypes(content));
        Assert.DoesNotContain("private prompt", Encoding.Latin1.GetString(content), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAsync_DeduplicatesExactNormalizedContentAndMetadata()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var input = CreatePng(2, 2);
        var first = await sandbox.Store.ImportAsync(Request("dedupe-a", input));

        var second = await sandbox.Store.ImportAsync(Request("dedupe-b", input));

        Assert.False(first.Duplicate);
        Assert.True(second.Duplicate);
        Assert.Equal(first.GeneratedAssetId, second.GeneratedAssetId);
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal(first.PreviewHash, second.PreviewHash);
    }

    [Theory]
    [InlineData("%%%", "invalid_base64")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=", "invalid_base64")]
    [InlineData("bm90IGFuIGltYWdl", "invalid_image")]
    public async Task ImportAsync_RejectsInvalidBase64MimeWrappersAndNonImages(string content, string expectedCode)
    {
        using var sandbox = new GeneratedAssetSandbox();
        var exception = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("hostile", Convert.FromBase64String("aQ==")) with { ContentBase64 = content }));
        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public async Task ImportAsync_PreBoundsBase64AndEnforcesDecodedByteLimit()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var maximumBase64Characters = ((GeneratedAssetStore.MaximumDecodedBytes + 2) / 3) * 4;
        var encodedTooLong = new string('A', maximumBase64Characters + 4);
        var decodedTooLarge = Convert.ToBase64String(new byte[GeneratedAssetStore.MaximumDecodedBytes + 1]);

        var encodedFailure = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("encoded-overflow", [1]) with { ContentBase64 = encodedTooLong }));
        var decodedFailure = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("decoded-overflow", [1]) with { ContentBase64 = decodedTooLarge }));

        Assert.Equal("image_too_large", encodedFailure.Code);
        Assert.Equal("image_too_large", decodedFailure.Code);
    }

    [Theory]
    [InlineData(4097, 1)]
    [InlineData(4096, 4097)]
    public async Task ImportAsync_RejectsEdgeAndPixelOverflowBeforeDecodeAllocation(int width, int height)
    {
        using var sandbox = new GeneratedAssetSandbox();
        var hostileHeader = RewritePngDimensions(CreatePng(2, 2), width, height);

        var exception = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("dimensions-" + width + "-" + height, hostileHeader)));

        Assert.Equal("image_dimensions_exceeded", exception.Code);
    }

    [Fact]
    public async Task ImportAsync_RejectsMalformedAndAnimatedAllowedFormatImages()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var malformed = CreatePng(2, 2)[..32];
        var animatedWebp = CreateAnimatedWebp();

        var malformedFailure = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("malformed", malformed)));
        var animatedFailure = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("animated", animatedWebp)));

        Assert.Equal("invalid_image", malformedFailure.Code);
        Assert.Equal("animated_image", animatedFailure.Code);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("folder\\escape")]
    [InlineData("bad\u0001name")]
    public async Task ImportAsync_RejectsPathLikeAndControlNames(string name)
    {
        using var sandbox = new GeneratedAssetSandbox();
        var exception = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("bad-name", CreatePng(1, 1)) with { Name = name }));
        Assert.Equal("invalid_name", exception.Code);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("folder\\escape")]
    [InlineData("bad\u0001tag")]
    public async Task ImportAsync_RejectsPathLikeAndControlTags(string tag)
    {
        using var sandbox = new GeneratedAssetSandbox();
        var exception = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("bad-tag", CreatePng(1, 1)) with { Tags = [tag] }));
        Assert.Equal("invalid_tags", exception.Code);
    }

    [Fact]
    public async Task ImportAsync_CountsNameAndTagLimitsInUnicodeScalars()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var emoji = "\U0001F409";

        var accepted = await sandbox.Store.ImportAsync(
            Request("scalar-accepted", CreatePng(1, 1)) with { Name = string.Concat(Enumerable.Repeat(emoji, 120)), Tags = [string.Concat(Enumerable.Repeat(emoji, 64))] });
        var nameFailure = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("name-overflow", CreatePng(1, 1)) with { Name = string.Concat(Enumerable.Repeat(emoji, 121)) }));
        var tagFailure = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("tag-overflow", CreatePng(1, 1)) with { Tags = [string.Concat(Enumerable.Repeat(emoji, 65))] }));

        Assert.Equal(1, accepted.Width);
        Assert.Equal("invalid_name", nameFailure.Code);
        Assert.Equal("invalid_tags", tagFailure.Code);
    }

    [Fact]
    public async Task ImportAsync_RejectsMoreThanThirtyTwoTags()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var exception = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("too-many-tags", CreatePng(1, 1)) with { Tags = Enumerable.Range(0, 33).Select(index => "tag-" + index).ToArray() }));
        Assert.Equal("invalid_tags", exception.Code);
    }

    [Theory]
    [InlineData("Unknown", "unsupported_category")]
    [InlineData("objects", "unsupported_category")]
    public async Task ImportAsync_RejectsUnsupportedCategories(string category, string expectedCode)
    {
        using var sandbox = new GeneratedAssetSandbox();
        var exception = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("category", CreatePng(1, 1)) with { Category = category }));
        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public async Task ImportAsync_RequiresExactlyOneInputForm()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var neither = Request("neither", CreatePng(1, 1)) with { ContentBase64 = null };
        var both = Request("both", CreatePng(1, 1)) with { InboxToken = new string('a', 32) };

        var neitherFailure = await Assert.ThrowsAsync<GeneratedAssetImportException>(() => sandbox.Store.ImportAsync(neither));
        var bothFailure = await Assert.ThrowsAsync<GeneratedAssetImportException>(() => sandbox.Store.ImportAsync(both));

        Assert.Equal("invalid_input", neitherFailure.Code);
        Assert.Equal("invalid_input", bothFailure.Code);
    }

    [Fact]
    public async Task ImportAsync_CanonicalizesTagsDeterministicallyInManifestAndIdentity()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var input = CreatePng(1, 1);
        var first = await sandbox.Store.ImportAsync(Request("canonical-a", input) with { Tags = [" Ruin ", "STONE", "stone"] });
        var second = await sandbox.Store.ImportAsync(Request("canonical-b", input) with { Tags = ["stone", "ruin"] });

        using var manifest = JsonDocument.Parse(File.ReadAllBytes(sandbox.ManifestPath(first.GeneratedAssetId)));
        Assert.Equal(["ruin", "stone"], manifest.RootElement.GetProperty("tags").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(first.GeneratedAssetId, second.GeneratedAssetId);
        Assert.True(second.Duplicate);
    }

    [Fact]
    public async Task ImportAsync_ReusedKeyWithChangedCanonicalMetadataReturnsRequestConflict()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var input = CreatePng(1, 1);
        await sandbox.Store.ImportAsync(Request("same-key", input));

        var exception = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("same-key", input) with { GridWidth = 2 }));

        Assert.Equal("request_conflict", exception.Code);
    }

    [Fact]
    public async Task ImportAsync_AppliesEveryEncodedOriginTransformBeforeHashingAndSizing()
    {
        var expectedCorners = new Dictionary<int, SKColor[]>
        {
            [1] = [SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow],
            [2] = [SKColors.Lime, SKColors.Red, SKColors.Yellow, SKColors.Blue],
            [3] = [SKColors.Yellow, SKColors.Blue, SKColors.Lime, SKColors.Red],
            [4] = [SKColors.Blue, SKColors.Yellow, SKColors.Red, SKColors.Lime],
            [5] = [SKColors.Red, SKColors.Blue, SKColors.Lime, SKColors.Yellow],
            [6] = [SKColors.Blue, SKColors.Red, SKColors.Yellow, SKColors.Lime],
            [7] = [SKColors.Yellow, SKColors.Lime, SKColors.Blue, SKColors.Red],
            [8] = [SKColors.Lime, SKColors.Yellow, SKColors.Red, SKColors.Blue],
        };

        foreach (var (orientation, corners) in expectedCorners)
        {
            using var sandbox = new GeneratedAssetSandbox();
            var result = await sandbox.Store.ImportAsync(Request("orientation-" + orientation, CreateJpegWithOrientation(orientation)));
            var expectedWidth = orientation >= 5 ? 32 : 64;
            var expectedHeight = orientation >= 5 ? 64 : 32;
            Assert.Equal(expectedWidth, result.Width);
            Assert.Equal(expectedHeight, result.Height);
            using var output = SKBitmap.Decode(File.ReadAllBytes(sandbox.ContentPath(result.ContentHash)));
            AssertColorNear(corners[0], output.GetPixel(2, 2));
            AssertColorNear(corners[1], output.GetPixel(output.Width - 3, 2));
            AssertColorNear(corners[2], output.GetPixel(2, output.Height - 3));
            AssertColorNear(corners[3], output.GetPixel(output.Width - 3, output.Height - 3));
        }
    }

    [Fact]
    public async Task StageInboxAsync_IssuesA128BitLowercaseTokenThatImportAsyncCanConsume()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var token = await sandbox.Store.StageInboxAsync(CreatePng(2, 2));
        Assert.Matches("^[0-9a-f]{32}$", token);

        var result = await sandbox.Store.ImportAsync(
            Request("inbox", [1]) with { ContentBase64 = null, InboxToken = token });

        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("../0123456789abcdef0123456789abcdef")]
    [InlineData("https://example.test/image.png")]
    [InlineData("00000000000000000000000000000000")]
    public async Task ImportAsync_RejectsMalformedOrForeignInboxTokens(string token)
    {
        using var sandbox = new GeneratedAssetSandbox();
        var exception = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("foreign-token", [1]) with { ContentBase64 = null, InboxToken = token }));
        Assert.Equal("invalid_inbox_token", exception.Code);
    }

    [Fact]
    public async Task StageInboxAsync_RejectsDecodedByteOverflow()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var exception = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.StageInboxAsync(new byte[GeneratedAssetStore.MaximumDecodedBytes + 1]));
        Assert.Equal("image_too_large", exception.Code);
    }

    [Fact]
    public async Task StageInboxAsync_RejectsAForeignJunctionThatReplacedTheInbox()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var inbox = Path.Combine(sandbox.Root, "import-inbox");
        var outside = Path.Combine(sandbox.Root, "outside");
        Directory.CreateDirectory(outside);
        Directory.Delete(inbox);
        CreateDirectoryJunction(inbox, outside);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => sandbox.Store.StageInboxAsync(CreatePng(1, 1)));
            Assert.Empty(Directory.EnumerateFiles(outside));
        }
        finally
        {
            Directory.Delete(inbox);
        }
    }

    [Fact]
    public void Constructor_RejectsGeneratedAssetsJunctionEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "ddai-generated-asset-tests", Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        var generated = Path.Combine(root, "generated-assets");
        CreateDirectoryJunction(generated, outside);
        try
        {
            Assert.ThrowsAny<Exception>(() => new GeneratedAssetStore(root));
            Assert.Empty(Directory.EnumerateFiles(outside));
        }
        finally
        {
            Directory.Delete(generated);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Content")]
    [InlineData("Preview")]
    [InlineData("Manifest")]
    [InlineData("IdempotencyReceipt")]
    public async Task ImportAsync_RecoversIdempotentlyFromAFaultAfterEveryDurableMove(string moveName)
    {
        var move = Enum.Parse<GeneratedAssetDurableMove>(moveName);
        using var sandbox = new GeneratedAssetSandbox(new ThrowOnceAfterMove(move));
        var request = Request("crash-" + move, CreatePng(300, 150));

        await Assert.ThrowsAsync<InjectedDurabilityException>(() => sandbox.Store.ImportAsync(request));

        var recovered = await new GeneratedAssetStore(sandbox.Root).ImportAsync(request);
        var replay = await new GeneratedAssetStore(sandbox.Root).ImportAsync(request);
        Assert.Equal(recovered.GeneratedAssetId, replay.GeneratedAssetId);
        Assert.True(replay.Duplicate);
        Assert.True(File.Exists(sandbox.ContentPath(recovered.ContentHash)));
        Assert.True(File.Exists(sandbox.PreviewPath(recovered.PreviewHash)));
        Assert.True(File.Exists(sandbox.ManifestPath(recovered.GeneratedAssetId)));
        Assert.Empty(Directory.EnumerateFiles(sandbox.Root, "*.next", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ImportAsync_ConcurrentExactDuplicatesPublishOneIdentityAndOneNonDuplicate()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var content = CreatePng(2, 2);
        var tasks = Enumerable.Range(0, 16)
            .Select(index => new GeneratedAssetStore(sandbox.Root).ImportAsync(Request("concurrent-" + index, content)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Single(results, result => !result.Duplicate);
        Assert.Equal(15, results.Count(result => result.Duplicate));
        Assert.Single(results.Select(result => result.GeneratedAssetId).Distinct(StringComparer.Ordinal));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(sandbox.Root, "generated-assets", "manifest"), "*.json"));
    }

    [Fact]
    public async Task ImportAsync_RejectsAReparsePointAtAnImmutableContentDestination()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var input = CreatePng(2, 2);
        var initial = await sandbox.Store.ImportAsync(Request("link-seed", input));
        var contentPath = sandbox.ContentPath(initial.ContentHash);
        var outside = Path.Combine(sandbox.Root, "outside-content");
        Directory.CreateDirectory(outside);
        var sentinelPath = Path.Combine(outside, "sentinel.bin");
        var sentinel = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(sentinelPath, sentinel);
        File.Delete(contentPath);
        CreateDirectoryJunction(contentPath, outside);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => sandbox.Store.ImportAsync(Request("link-attack", input)));
            Assert.Equal(sentinel, File.ReadAllBytes(sentinelPath));
            Assert.Single(Directory.EnumerateFiles(outside));
        }
        finally
        {
            Directory.Delete(contentPath);
        }
    }

    [Fact]
    public async Task ImportAsync_RejectsBytesSubstitutedAfterAtomicMoveBeforeReportingSuccess()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var substitutingStore = new GeneratedAssetStore(
            sandbox.Root,
            new SubstituteAfterMove(sandbox.Root, GeneratedAssetDurableMove.Content));

        var exception = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => substitutingStore.ImportAsync(Request("publication-substitution", CreatePng(2, 2))));

        Assert.Equal("stored_data_conflict", exception.Code);
    }

    [Fact]
    public async Task ImportAsync_DownscalesIncompressiblePreviewUntilTheEncodedCapIsMet()
    {
        using var sandbox = new GeneratedAssetSandbox();

        var result = await sandbox.Store.ImportAsync(Request("incompressible-preview", CreateIncompressiblePng()));

        var preview = File.ReadAllBytes(sandbox.PreviewPath(result.PreviewHash));
        Assert.True(preview.Length <= GeneratedAssetStore.MaximumPreviewBytes);
        using var bitmap = SKBitmap.Decode(preview);
        Assert.True(bitmap.Width <= GeneratedAssetStore.MaximumPreviewEdge);
        Assert.True(bitmap.Height <= GeneratedAssetStore.MaximumPreviewEdge);
    }

    [Fact]
    public async Task ImportAsync_RejectsIllFormedUtf16InsteadOfCanonicalizingReplacementCharacters()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var invalid = new string(['x', '\ud800']);

        var nameFailure = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("invalid-utf-name", CreatePng(1, 1)) with { Name = invalid }));
        var tagFailure = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("invalid-utf-tag", CreatePng(1, 1)) with { Tags = [invalid] }));

        Assert.Equal("invalid_name", nameFailure.Code);
        Assert.Equal("invalid_tags", tagFailure.Code);
    }

    [Fact]
    public async Task ImportAsync_ReceiptReplayRepairsAMissingEarlierArtifactBeforeReturning()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var request = Request("repair-replay", CreatePng(2, 2));
        var first = await sandbox.Store.ImportAsync(request);
        File.Delete(sandbox.ContentPath(first.ContentHash));

        var replay = await new GeneratedAssetStore(sandbox.Root).ImportAsync(request);

        Assert.True(replay.Duplicate);
        Assert.True(File.Exists(sandbox.ContentPath(first.ContentHash)));
        Assert.Equal(first.ContentHash, Hash(File.ReadAllBytes(sandbox.ContentPath(first.ContentHash))));
    }

    [Fact]
    public async Task ImportAsync_RejectsAReceiptWhoseStoredResultDoesNotMatchTheCanonicalImport()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var request = Request("tampered-receipt", CreatePng(2, 2));
        await sandbox.Store.ImportAsync(request);
        var receiptName = Hash(Encoding.UTF8.GetBytes(request.IdempotencyKey)) + ".json";
        var receiptPath = Path.Combine(sandbox.Root, "generated-assets", "idempotency", receiptName);
        var json = File.ReadAllText(receiptPath).Replace("\"width\":2", "\"width\":9", StringComparison.Ordinal);
        File.WriteAllText(receiptPath, json);

        var exception = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => new GeneratedAssetStore(sandbox.Root).ImportAsync(request));

        Assert.Equal("stored_data_conflict", exception.Code);
    }

    private static GeneratedAssetImportRequest Request(string key, byte[] content) => new(
        key,
        "Objects",
        "Ancient Statue",
        ["stone", "ruin"],
        1,
        1,
        Convert.ToBase64String(content),
        null);

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(new SKColor(18, 52, 86, 255));
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static byte[] CreateIncompressiblePng()
    {
        using var bitmap = new SKBitmap(256, 256, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var random = new Random(20260810);
        random.NextBytes(bitmap.GetPixelSpan());
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var result = encoded.ToArray();
        Assert.True(result.Length > GeneratedAssetStore.MaximumPreviewBytes);
        return result;
    }

    private static byte[] AddPngTextChunk(byte[] png, string key, string value)
    {
        var iendOffset = png.Length - 12;
        var data = Encoding.Latin1.GetBytes(key + "\0" + value);
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), (uint)data.Length);
        Encoding.ASCII.GetBytes("tEXt").CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length, 4), Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return [.. png.AsSpan(0, iendOffset), .. chunk, .. png.AsSpan(iendOffset)];
    }

    private static byte[] RewritePngDimensions(byte[] png, int width, int height)
    {
        var result = png.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20, 4), checked((uint)height));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(29, 4), Crc32(result.AsSpan(12, 17)));
        return result;
    }

    private static byte[] CreateJpegWithOrientation(int orientation)
    {
        using var bitmap = new SKBitmap(64, 32, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Black);
            using var paint = new SKPaint { IsAntialias = false };
            paint.Color = SKColors.Red;
            canvas.DrawRect(0, 0, 32, 16, paint);
            paint.Color = SKColors.Lime;
            canvas.DrawRect(32, 0, 32, 16, paint);
            paint.Color = SKColors.Blue;
            canvas.DrawRect(0, 16, 32, 16, paint);
            paint.Color = SKColors.Yellow;
            canvas.DrawRect(32, 16, 32, 16, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        var jpeg = data.ToArray();
        var exif = new byte[]
        {
            0xff, 0xe1, 0x00, 0x22,
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'I', (byte)'I', 0x2a, 0, 8, 0, 0, 0,
            1, 0,
            0x12, 0x01, 3, 0, 1, 0, 0, 0, checked((byte)orientation), 0, 0, 0,
            0, 0, 0, 0,
        };
        return [.. jpeg.AsSpan(0, 2), .. exif, .. jpeg.AsSpan(2)];
    }

    private static byte[] CreateAnimatedWebp()
    {
        var first = ExtractWebpImageChunk(CreateWebp(SKColors.Red));
        var second = ExtractWebpImageChunk(CreateWebp(SKColors.Blue));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(0u);
        writer.Write(Encoding.ASCII.GetBytes("WEBP"));
        WriteRiffChunk(writer, "VP8X", [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        WriteRiffChunk(writer, "ANIM", [0, 0, 0, 0, 0, 0]);
        WriteAnimationFrame(writer, first);
        WriteAnimationFrame(writer, second);
        writer.Flush();
        var result = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), checked((uint)result.Length - 8));
        return result;
    }

    private static byte[] CreateWebp(SKColor color)
    {
        using var bitmap = new SKBitmap(1, 1);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, 100);
        return data.ToArray();
    }

    private static byte[] ExtractWebpImageChunk(byte[] webp) => webp[12..];

    private static void WriteAnimationFrame(BinaryWriter writer, byte[] imageChunk)
    {
        var frame = new byte[16 + imageChunk.Length];
        frame[6] = 0;
        frame[9] = 0;
        frame[12] = 100;
        imageChunk.CopyTo(frame, 16);
        WriteRiffChunk(writer, "ANMF", frame);
    }

    private static void WriteRiffChunk(BinaryWriter writer, string type, byte[] data)
    {
        writer.Write(Encoding.ASCII.GetBytes(type));
        writer.Write(checked((uint)data.Length));
        writer.Write(data);
        if ((data.Length & 1) != 0) writer.Write((byte)0);
    }

    private static void AssertColorNear(SKColor expected, SKColor actual)
    {
        Assert.InRange(Math.Abs(expected.Red - actual.Red), 0, 20);
        Assert.InRange(Math.Abs(expected.Green - actual.Green), 0, 20);
        Assert.InRange(Math.Abs(expected.Blue - actual.Blue), 0, 20);
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("cmd.exe could not create the junction fixture.");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && Directory.Exists(linkPath), "The Windows junction fixture could not be created.");
    }

    private static IReadOnlyList<string> ReadChunkTypes(byte[] png)
    {
        var result = new List<string>();
        for (var offset = 8; offset + 12 <= png.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
            result.Add(Encoding.ASCII.GetString(png, offset + 4, 4));
            offset = checked(offset + 12 + length);
        }
        return result;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320u;
            }
        }
        return ~crc;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void AssertPngDimensions(byte[] bytes, int width, int height)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Png, codec.EncodedFormat);
        Assert.Equal(width, codec.Info.Width);
        Assert.Equal(height, codec.Info.Height);
    }

    private sealed class GeneratedAssetSandbox : IDisposable
    {
        public GeneratedAssetSandbox(IGeneratedAssetDurability? durability = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "ddai-generated-asset-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Store = durability is null ? new GeneratedAssetStore(Root) : new GeneratedAssetStore(Root, durability);
        }

        public string Root { get; }
        public GeneratedAssetStore Store { get; }
        public string ContentPath(string hash) => Path.Combine(Root, "generated-assets", "content", hash + ".png");
        public string PreviewPath(string hash) => Path.Combine(Root, "generated-assets", "preview", hash + ".png");
        public string ManifestPath(string id) => Path.Combine(Root, "generated-assets", "manifest", id + ".json");

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class ThrowOnceAfterMove(GeneratedAssetDurableMove target) : IGeneratedAssetDurability
    {
        private int thrown;

        public void AfterDurableMove(GeneratedAssetDurableMove move)
        {
            if (move == target && Interlocked.Exchange(ref thrown, 1) == 0)
            {
                throw new InjectedDurabilityException();
            }
        }
    }

    private sealed class SubstituteAfterMove(string root, GeneratedAssetDurableMove target) : IGeneratedAssetDurability
    {
        public void AfterDurableMove(GeneratedAssetDurableMove move)
        {
            if (move != target) return;
            var directory = Path.Combine(root, "generated-assets", "content");
            var destination = Assert.Single(Directory.EnumerateFiles(directory, "*.png"));
            File.WriteAllBytes(destination, Encoding.ASCII.GetBytes("substituted-after-move"));
        }
    }

    private sealed class InjectedDurabilityException : IOException;
}
