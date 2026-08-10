using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DDAI.App.Assets;
using Microsoft.Win32.SafeHandles;
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
    public async Task ImportAsync_NormalizesARealStaticWebpToPng()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var input = CreateWebp(SKColors.Red);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(input, 0, 4));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(input, 8, 4));

        var result = await sandbox.Store.ImportAsync(Request("static-webp", input));

        var content = File.ReadAllBytes(sandbox.ContentPath(result.ContentHash));
        AssertPngDimensions(content, 1, 1);
        Assert.Equal("\u0089PNG\r\n\u001a\n", Encoding.Latin1.GetString(content, 0, 8));
        using var output = SKBitmap.Decode(content);
        AssertColorNear(SKColors.Red, output.GetPixel(0, 0));
    }

    [Fact]
    public async Task ImportAsync_StripsExifIccAndXmpWhileApplyingOrientation()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var input = CreateJpegWithExifIccAndXmp(6);
        var inputText = Encoding.Latin1.GetString(input);
        Assert.Contains("Exif\0\0", inputText, StringComparison.Ordinal);
        Assert.Contains("ICC_PROFILE\0", inputText, StringComparison.Ordinal);
        Assert.Contains("http://ns.adobe.com/xap/1.0/\0", inputText, StringComparison.Ordinal);

        var result = await sandbox.Store.ImportAsync(Request("structured-metadata", input));

        var content = File.ReadAllBytes(sandbox.ContentPath(result.ContentHash));
        Assert.Equal(32, result.Width);
        Assert.Equal(64, result.Height);
        Assert.DoesNotContain("eXIf", ReadChunkTypes(content));
        Assert.DoesNotContain("iCCP", ReadChunkTypes(content));
        Assert.DoesNotContain("iTXt", ReadChunkTypes(content));
        var outputText = Encoding.Latin1.GetString(content);
        Assert.DoesNotContain("Exif\0\0", outputText, StringComparison.Ordinal);
        Assert.DoesNotContain("ICC_PROFILE\0", outputText, StringComparison.Ordinal);
        Assert.DoesNotContain("http://ns.adobe.com/xap/1.0/\0", outputText, StringComparison.Ordinal);
        using var output = SKBitmap.Decode(content);
        AssertColorNear(SKColors.Blue, output.GetPixel(2, 2));
        AssertColorNear(SKColors.Red, output.GetPixel(output.Width - 3, 2));
        AssertColorNear(SKColors.Yellow, output.GetPixel(2, output.Height - 3));
        AssertColorNear(SKColors.Lime, output.GetPixel(output.Width - 3, output.Height - 3));
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
    [InlineData("tab\tname")]
    [InlineData("carriage\rreturn")]
    [InlineData("line\nfeed")]
    [InlineData("unicode\u2028line")]
    [InlineData("unicode\u2029paragraph")]
    [InlineData("zero\u200bwidth")]
    public async Task ImportAsync_RejectsControlledOriginalNamesBeforeWhitespaceNormalization(string name)
    {
        using var sandbox = new GeneratedAssetSandbox();
        var exception = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("controlled-name", CreatePng(1, 1)) with { Name = name }));
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

    [Theory]
    [InlineData("tab\ttag")]
    [InlineData("carriage\rreturn")]
    [InlineData("line\nfeed")]
    [InlineData("unicode\u2028line")]
    [InlineData("unicode\u2029paragraph")]
    [InlineData("zero\u200bwidth")]
    public async Task ImportAsync_RejectsControlledOriginalTagsBeforeWhitespaceNormalization(string tag)
    {
        using var sandbox = new GeneratedAssetSandbox();
        var exception = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("controlled-tag", CreatePng(1, 1)) with { Tags = [tag] }));
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
    public async Task ImportAsync_RealChildProcessesSerializeExactRetriesAndClassifyKeyConflicts()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var gatePath = Path.Combine(sandbox.Root, "child-start.gate");
        var children = Enumerable.Range(0, 8)
            .Select(index => StartChildImport(sandbox.Root, "cross-process-key", 1, gatePath, index.ToString()))
            .ToArray();
        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => children.All(child => File.Exists(child.ReadyPath)),
                TimeSpan.FromSeconds(30)), "The generated-asset child processes did not reach the shared start gate.");
            File.WriteAllBytes(gatePath, []);
            var outcomes = await Task.WhenAll(children.Select(CompleteChildImportAsync));

            Assert.All(outcomes, outcome => Assert.True(outcome.Success, outcome.Code));
            Assert.Single(outcomes, outcome => !outcome.Duplicate);
            Assert.Equal(7, outcomes.Count(outcome => outcome.Duplicate));
            Assert.Single(outcomes.Select(outcome => outcome.GeneratedAssetId).Distinct(StringComparer.Ordinal));

            var exactRetry = await CompleteChildImportAsync(StartChildImport(
                sandbox.Root, "cross-process-key", 1, gatePath, "retry"));
            Assert.True(exactRetry.Success, exactRetry.Code);
            Assert.True(exactRetry.Duplicate);

            var conflict = await CompleteChildImportAsync(StartChildImport(
                sandbox.Root, "cross-process-key", 2, gatePath, "conflict"));
            Assert.False(conflict.Success);
            Assert.Equal("request_conflict", conflict.Code);
        }
        finally
        {
            foreach (var child in children) child.Dispose();
        }
    }

    [Fact]
    public async Task ImportAsync_RealChildProcessCrashLeavesAnAbandonedTransactionRecoverable()
    {
        using var sandbox = new GeneratedAssetSandbox();
        PausedCrashTopology? topology = null;
        try
        {
            topology = StartPausedCrashTopology(sandbox);
            File.WriteAllBytes(topology.AllowOwnerCrashPath, []);

            var crashResult = await CompleteChildProcessAsync(topology.Owner);
            Assert.NotEqual(0, crashResult.ExitCode);
            Assert.Contains("Injected generated-asset child crash after Manifest.", crashResult.StandardError, StringComparison.Ordinal);
            Assert.False(File.Exists(topology.Owner.ResultPath));

            var recovered = await CompleteChildImportAsync(topology.Waiter);
            Assert.True(File.Exists(sandbox.ReceiptPath("cross-process-crash")));
            var replay = await CompleteChildImportAsync(StartChildImport(
                sandbox.Root, "cross-process-crash", 1, topology.GatePath, "replay"));
            Assert.True(recovered.Success, recovered.Code);
            Assert.True(recovered.AbandonedObserved);
            Assert.True(recovered.Duplicate);
            Assert.True(replay.Success, replay.Code);
            Assert.False(replay.AbandonedObserved);
            Assert.Equal(recovered.GeneratedAssetId, replay.GeneratedAssetId);
            Assert.True(replay.Duplicate);
            Assert.Empty(Directory.EnumerateFiles(sandbox.Root, "*.next", SearchOption.AllDirectories));
        }
        finally
        {
            topology?.Dispose();
        }
    }

    [Fact]
    public void AbandonedOwnerChildTopology_EarlyFailureTerminatesBothProcessesBeforeSandboxCleanup()
    {
        var sandbox = new GeneratedAssetSandbox();
        var root = sandbox.Root;
        PausedCrashTopology? topology = null;
        Exception? cleanupFailure = null;
        var cleanupElapsed = new Stopwatch();
        try
        {
            topology = StartPausedCrashTopology(sandbox);
            throw new InjectedChildTopologyException();
        }
        catch (InjectedChildTopologyException)
        {
        }
        finally
        {
            try
            {
                cleanupElapsed.Start();
                topology?.Dispose();
                cleanupElapsed.Stop();
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
            finally
            {
                sandbox.Dispose();
            }
        }

        Assert.Null(cleanupFailure);
        Assert.NotNull(topology);
        Assert.True(topology.Owner.ProcessTreeExitConfirmed);
        Assert.True(topology.Waiter.ProcessTreeExitConfirmed);
        Assert.True(topology.Owner.CapturedDescendantCount > 0);
        Assert.True(topology.Waiter.CapturedDescendantCount > 0);
        Assert.False(Directory.Exists(root));
        Assert.True(cleanupElapsed.Elapsed < TimeSpan.FromSeconds(15), $"Child cleanup took {cleanupElapsed.Elapsed}.");
    }

    [Fact]
    public async Task ChildProcessImportEntryPoint()
    {
        var root = Environment.GetEnvironmentVariable("DDAI_GENERATED_CHILD_ROOT");
        if (string.IsNullOrEmpty(root)) return;

        var readyPath = Environment.GetEnvironmentVariable("DDAI_GENERATED_CHILD_READY")
            ?? throw new InvalidOperationException("The generated-asset child ready path is missing.");
        var gatePath = Environment.GetEnvironmentVariable("DDAI_GENERATED_CHILD_GATE")
            ?? throw new InvalidOperationException("The generated-asset child gate path is missing.");
        var resultPath = Environment.GetEnvironmentVariable("DDAI_GENERATED_CHILD_RESULT")
            ?? throw new InvalidOperationException("The generated-asset child result path is missing.");
        var key = Environment.GetEnvironmentVariable("DDAI_GENERATED_CHILD_KEY")
            ?? throw new InvalidOperationException("The generated-asset child idempotency key is missing.");
        var gridWidth = int.Parse(
            Environment.GetEnvironmentVariable("DDAI_GENERATED_CHILD_GRID") ?? "1",
            System.Globalization.CultureInfo.InvariantCulture);
        var crashMove = Environment.GetEnvironmentVariable("DDAI_GENERATED_CHILD_CRASH_MOVE");
        var durableBoundaryPath = Environment.GetEnvironmentVariable("DDAI_GENERATED_CHILD_DURABLE_BOUNDARY");
        var retainedHandlePath = Environment.GetEnvironmentVariable("DDAI_GENERATED_CHILD_RETAINED_MUTEX");
        var allowOwnerCrashPath = Environment.GetEnvironmentVariable("DDAI_GENERATED_CHILD_ALLOW_CRASH");
        var waitForAbandoned = string.Equals(
            Environment.GetEnvironmentVariable("DDAI_GENERATED_CHILD_WAIT_ABANDONED"),
            "1",
            StringComparison.Ordinal);
        File.WriteAllBytes(readyPath, []);
        Assert.True(SpinWait.SpinUntil(() => File.Exists(gatePath), TimeSpan.FromSeconds(30)));

        var request = Request(key, CreatePng(64, 32)) with { GridWidth = gridWidth };
        if (waitForAbandoned)
        {
            Assert.False(string.IsNullOrEmpty(retainedHandlePath));
            var mutexMethod = typeof(GeneratedAssetStore).GetMethod(
                "MutexName",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var mutexName = Assert.IsType<string>(mutexMethod!.Invoke(null, [Path.Combine(root, "generated-assets")]));
            using var retainedMutex = Mutex.OpenExisting(mutexName);
            File.WriteAllBytes(retainedHandlePath, []);
            var entered = false;
            var abandonedObserved = false;
            try
            {
                try { entered = retainedMutex.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (AbandonedMutexException)
                {
                    entered = true;
                    abandonedObserved = true;
                }
                Assert.True(entered, "The recovery child did not acquire the abandoned mutex.");
                var recovered = ImportSynchronouslyWhileHoldingMutex(root, request);
                File.WriteAllText(resultPath, JsonSerializer.Serialize(new ChildImportOutcome(
                    true, recovered.Duplicate, recovered.GeneratedAssetId, null, abandonedObserved)));
            }
            finally
            {
                if (entered) retainedMutex.ReleaseMutex();
            }
            return;
        }

        IGeneratedAssetDurability durability = crashMove is null
            ? GeneratedAssetDurability.Instance
            : new FailFastAfterMove(
                Enum.Parse<GeneratedAssetDurableMove>(crashMove),
                durableBoundaryPath,
                retainedHandlePath,
                allowOwnerCrashPath);
        try
        {
            var result = await new GeneratedAssetStore(root, durability).ImportAsync(request);
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new ChildImportOutcome(
                true, result.Duplicate, result.GeneratedAssetId, null, false)));
        }
        catch (GeneratedAssetImportException exception)
        {
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new ChildImportOutcome(
                false, false, null, exception.Code, false)));
        }
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

    [Fact]
    public async Task ImportAsync_ClassifiesAnExactReceiptCollisionAsAReplay()
    {
        var request = Request("exact-receipt-collision", CreatePng(2, 2));
        byte[] receipt;
        using (var seed = new GeneratedAssetSandbox())
        {
            await seed.Store.ImportAsync(request);
            receipt = File.ReadAllBytes(seed.ReceiptPath(request.IdempotencyKey));
        }
        using var sandbox = new GeneratedAssetSandbox(
            root => new InsertReceiptAfterManifest(root, request.IdempotencyKey, receipt));

        var result = await sandbox.Store.ImportAsync(request);

        Assert.True(result.Duplicate);
    }

    [Fact]
    public async Task ImportAsync_ClassifiesAConflictingReceiptCollisionAsARequestConflict()
    {
        var request = Request("conflicting-receipt-collision", CreatePng(2, 2));
        var conflictingReceipt = JsonSerializer.SerializeToUtf8Bytes(new
        {
            request_fingerprint = new string('a', 64),
            result = (object?)null,
        });
        using var sandbox = new GeneratedAssetSandbox(
            root => new InsertReceiptAfterManifest(root, request.IdempotencyKey, conflictingReceipt));

        var exception = await Assert.ThrowsAsync<GeneratedAssetImportException>(() => sandbox.Store.ImportAsync(request));

        Assert.Equal("request_conflict", exception.Code);
    }

    [Fact]
    public void GeneratedAssetMutexName_UsesTheGlobalPerUserNamespaceAcrossWindowsSessions()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var mutexMethod = typeof(GeneratedAssetStore).GetMethod(
            "MutexName",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        var mutexName = Assert.IsType<string>(mutexMethod!.Invoke(null, [Path.Combine(sandbox.Root, "generated-assets")]));

        Assert.StartsWith("Global\\DDAI.GeneratedAssets.", mutexName, StringComparison.Ordinal);
        Assert.DoesNotContain("Local\\", mutexName, StringComparison.Ordinal);
        using var first = new Mutex(false, mutexName, out _);
        using var reopened = Mutex.OpenExisting(mutexName);
        Assert.True(first.WaitOne(TimeSpan.FromSeconds(1)));
        first.ReleaseMutex();
    }

    [Fact]
    public async Task ImportAsync_FailsClosedWhenAForeignPrincipalOwnsTheGlobalMutexObject()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var mutexMethod = typeof(GeneratedAssetStore).GetMethod(
            "MutexName",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var mutexName = Assert.IsType<string>(mutexMethod!.Invoke(null, [Path.Combine(sandbox.Root, "generated-assets")]));
        using var foreignMutex = CreateMutexWithSddl(
            mutexName,
            "D:P(A;;GA;;;S-1-5-21-0-0-0-9999)");

        var exception = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("foreign-mutex", CreatePng(1, 1))));

        Assert.Equal("storage_unavailable", exception.Code);
    }

    [Fact]
    public async Task ImportAsync_FailsClosedWhenAPrecreatedGlobalMutexHasAPermissiveDacl()
    {
        using var sandbox = new GeneratedAssetSandbox();
        var mutexMethod = typeof(GeneratedAssetStore).GetMethod(
            "MutexName",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var mutexName = Assert.IsType<string>(mutexMethod!.Invoke(null, [Path.Combine(sandbox.Root, "generated-assets")]));
        using var permissiveMutex = CreateMutexWithSddl(mutexName, "D:P(A;;GA;;;WD)");

        var exception = await Assert.ThrowsAsync<GeneratedAssetImportException>(
            () => sandbox.Store.ImportAsync(Request("permissive-mutex", CreatePng(1, 1))));

        Assert.Equal("storage_unavailable", exception.Code);
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

    private static byte[] CreateJpegWithExifIccAndXmp(int orientation)
    {
        var jpeg = CreateJpegWithOrientation(orientation);
        var iccProfile = CreateSrgbIccProfile();
        var iccPayload = new byte[14 + iccProfile.Length];
        Encoding.ASCII.GetBytes("ICC_PROFILE\0").CopyTo(iccPayload, 0);
        iccPayload[12] = 1;
        iccPayload[13] = 1;
        iccProfile.CopyTo(iccPayload, 14);
        jpeg = AddJpegAppSegment(jpeg, 0xe2, iccPayload);

        var xmpHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        var xmpBody = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"/></x:xmpmeta>");
        return AddJpegAppSegment(jpeg, 0xe1, [.. xmpHeader, .. xmpBody]);
    }

    private static byte[] AddJpegAppSegment(byte[] jpeg, byte marker, byte[] payload)
    {
        var segment = new byte[payload.Length + 4];
        segment[0] = 0xff;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2, 2), checked((ushort)(payload.Length + 2)));
        payload.CopyTo(segment, 4);
        return [.. jpeg.AsSpan(0, 2), .. segment, .. jpeg.AsSpan(2)];
    }

    private static byte[] CreateSrgbIccProfile()
    {
        var profilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "spool",
            "drivers",
            "color",
            "sRGB Color Space Profile.icm");
        Assert.True(File.Exists(profilePath), "The Windows sRGB ICC fixture is unavailable.");
        var bytes = File.ReadAllBytes(profilePath);
        using var verified = SKColorSpaceIccProfile.Create(bytes);
        Assert.NotNull(verified);
        return bytes;
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

    private static PausedCrashTopology StartPausedCrashTopology(GeneratedAssetSandbox sandbox)
    {
        var gatePath = Path.Combine(sandbox.Root, "crash-start.gate");
        var durableBoundaryPath = Path.Combine(sandbox.Root, "owner-manifest-durable.ready");
        var retainedHandlePath = Path.Combine(sandbox.Root, "waiter-mutex-open.ready");
        var allowOwnerCrashPath = Path.Combine(sandbox.Root, "parent-allows-owner-crash.ready");
        File.WriteAllBytes(gatePath, []);
        RunningChildImport? owner = null;
        RunningChildImport? waiter = null;
        try
        {
            owner = StartChildImport(
                sandbox.Root,
                "cross-process-crash",
                1,
                gatePath,
                "crash",
                GeneratedAssetDurableMove.Manifest.ToString(),
                durableBoundaryPath,
                retainedHandlePath,
                allowOwnerCrashPath);
            Assert.True(SpinWait.SpinUntil(
                () => File.Exists(durableBoundaryPath) || owner.Process.HasExited,
                TimeSpan.FromSeconds(30)), "The crash owner did not reach the manifest durability boundary.");
            Assert.False(owner.Process.HasExited, "The crash owner exited before a second process retained the mutex object.");
            Assert.Single(Directory.EnumerateFiles(
                Path.Combine(sandbox.Root, "generated-assets", "manifest"),
                "*.json"));

            waiter = StartChildImport(
                sandbox.Root,
                "cross-process-crash",
                1,
                gatePath,
                "abandoned-waiter",
                waitForAbandoned: true,
                retainedHandlePath: retainedHandlePath);
            Assert.True(SpinWait.SpinUntil(
                () => File.Exists(retainedHandlePath) || waiter.Process.HasExited,
                TimeSpan.FromSeconds(30)), "The recovery child did not open and retain the owner's mutex object.");
            Assert.False(waiter.Process.HasExited, "The recovery child exited before the owner crash.");
            return new PausedCrashTopology(owner, waiter, gatePath, allowOwnerCrashPath);
        }
        catch
        {
            DisposeChildProcesses(waiter, owner);
            throw;
        }
    }

    private static void DisposeChildProcesses(params RunningChildImport?[] children)
    {
        Exception? failure = null;
        foreach (var child in children)
        {
            if (child is null) continue;
            try
            {
                child.Dispose();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }
        if (failure is not null)
        {
            throw new InvalidOperationException("A generated-asset child process could not be cleaned up.", failure);
        }
    }

    private static RunningChildImport StartChildImport(
        string root,
        string key,
        int gridWidth,
        string gatePath,
        string suffix,
        string? crashMove = null,
        string? durableBoundaryPath = null,
        string? retainedHandlePath = null,
        string? allowOwnerCrashPath = null,
        bool waitForAbandoned = false)
    {
        var testAssembly = typeof(GeneratedAssetStoreTests).Assembly.Location;
        var resultPath = Path.Combine(root, "child-" + suffix + ".json");
        var readyPath = Path.Combine(root, "child-" + suffix + ".ready");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(testAssembly)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("vstest");
        startInfo.ArgumentList.Add(testAssembly);
        startInfo.ArgumentList.Add("--Tests:" + typeof(GeneratedAssetStoreTests).FullName + "." + nameof(ChildProcessImportEntryPoint));
        startInfo.Environment["DDAI_GENERATED_CHILD_ROOT"] = root;
        startInfo.Environment["DDAI_GENERATED_CHILD_RESULT"] = resultPath;
        startInfo.Environment["DDAI_GENERATED_CHILD_READY"] = readyPath;
        startInfo.Environment["DDAI_GENERATED_CHILD_GATE"] = gatePath;
        startInfo.Environment["DDAI_GENERATED_CHILD_KEY"] = key;
        startInfo.Environment["DDAI_GENERATED_CHILD_GRID"] = gridWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (crashMove is not null) startInfo.Environment["DDAI_GENERATED_CHILD_CRASH_MOVE"] = crashMove;
        if (durableBoundaryPath is not null) startInfo.Environment["DDAI_GENERATED_CHILD_DURABLE_BOUNDARY"] = durableBoundaryPath;
        if (retainedHandlePath is not null) startInfo.Environment["DDAI_GENERATED_CHILD_RETAINED_MUTEX"] = retainedHandlePath;
        if (allowOwnerCrashPath is not null) startInfo.Environment["DDAI_GENERATED_CHILD_ALLOW_CRASH"] = allowOwnerCrashPath;
        if (waitForAbandoned) startInfo.Environment["DDAI_GENERATED_CHILD_WAIT_ABANDONED"] = "1";
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the generated-asset child process.");
        return new RunningChildImport(
            process,
            process.StandardOutput.ReadToEndAsync(),
            process.StandardError.ReadToEndAsync(),
            resultPath,
            readyPath);
    }

    private static async Task<ChildImportOutcome> CompleteChildImportAsync(RunningChildImport child)
    {
        using (child)
        {
            var result = await CompleteChildProcessAsync(child);
            Assert.True(result.ExitCode == 0, $"Generated-asset child failed.\nstdout:\n{result.StandardOutput}\nstderr:\n{result.StandardError}");
            Assert.True(File.Exists(child.ResultPath), "The generated-asset child did not write its result.");
            return JsonSerializer.Deserialize<ChildImportOutcome>(File.ReadAllText(child.ResultPath))
                ?? throw new InvalidDataException("The generated-asset child result is invalid.");
        }
    }

    private static async Task<ChildProcessResult> CompleteChildProcessAsync(RunningChildImport child)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await child.Process.WaitForExitAsync(deadline.Token);
        return new ChildProcessResult(child.Process.ExitCode, await child.StandardOutput, await child.StandardError);
    }

    private static GeneratedAssetImportResult ImportSynchronouslyWhileHoldingMutex(
        string root,
        GeneratedAssetImportRequest request) =>
        new GeneratedAssetStore(root).ImportAsync(request).GetAwaiter().GetResult();

    private static IReadOnlyList<Process> CaptureDescendantProcesses(int rootProcessId)
    {
        using var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot.IsInvalid)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        var entry = new ProcessEntry32
        {
            Size = checked((uint)Marshal.SizeOf<ProcessEntry32>()),
            ExecutableFile = string.Empty,
        };
        var childrenByParent = new Dictionary<int, List<int>>();
        if (Process32First(snapshot, ref entry))
        {
            do
            {
                var parent = checked((int)entry.ParentProcessId);
                var process = checked((int)entry.ProcessId);
                if (!childrenByParent.TryGetValue(parent, out var children))
                {
                    children = [];
                    childrenByParent.Add(parent, children);
                }
                children.Add(process);
                entry.Size = checked((uint)Marshal.SizeOf<ProcessEntry32>());
            }
            while (Process32Next(snapshot, ref entry));
            var error = Marshal.GetLastWin32Error();
            if (error != 18)
            {
                throw new System.ComponentModel.Win32Exception(error);
            }
        }
        else
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        var result = new List<Process>();
        var pending = new Queue<int>();
        pending.Enqueue(rootProcessId);
        try
        {
            while (pending.TryDequeue(out var parent))
            {
                if (!childrenByParent.TryGetValue(parent, out var children)) continue;
                foreach (var childId in children)
                {
                    pending.Enqueue(childId);
                    try
                    {
                        var process = Process.GetProcessById(childId);
                        try
                        {
                            _ = process.SafeHandle;
                            result.Add(process);
                        }
                        catch
                        {
                            process.Dispose();
                            throw;
                        }
                    }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // The exact child exited between the snapshot and eagerly opening its native handle.
                    }
                }
            }
            return result;
        }
        catch
        {
            foreach (var process in result) process.Dispose();
            throw;
        }
    }

    private static bool WaitForExactProcessTreeExit(
        Process root,
        IReadOnlyList<Process> descendants,
        TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        if (!WaitForExactProcessExit(root, elapsed, timeout)) return false;
        foreach (var descendant in descendants)
        {
            if (!WaitForExactProcessExit(descendant, elapsed, timeout)) return false;
        }
        return true;
    }

    private static bool WaitForExactProcessExit(Process process, Stopwatch elapsed, TimeSpan timeout)
    {
        if (process.HasExited) return true;
        var remaining = timeout - elapsed.Elapsed;
        return remaining > TimeSpan.Zero && process.WaitForExit(checked((int)Math.Ceiling(remaining.TotalMilliseconds)));
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

    private static SafeWaitHandle CreateMutexWithSddl(string name, string sddl)
    {
        Assert.True(ConvertStringSecurityDescriptorToSecurityDescriptor(
            sddl,
            1,
            out var descriptor,
            out _));
        try
        {
            var attributes = new TestSecurityAttributes
            {
                Length = Marshal.SizeOf<TestSecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = false,
            };
            var handle = CreateMutexEx(ref attributes, name, 0, 0x001F0001);
            Assert.False(handle.IsInvalid);
            return handle;
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestSecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint UsageCount;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int PriorityBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSecurityDescriptorRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateMutexEx(
        ref TestSecurityAttributes mutexAttributes,
        string name,
        uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

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

        public GeneratedAssetSandbox(Func<string, IGeneratedAssetDurability> durabilityFactory)
        {
            Root = Path.Combine(Path.GetTempPath(), "ddai-generated-asset-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Store = new GeneratedAssetStore(Root, durabilityFactory(Root));
        }

        public string Root { get; }
        public GeneratedAssetStore Store { get; }
        public string ContentPath(string hash) => Path.Combine(Root, "generated-assets", "content", hash + ".png");
        public string PreviewPath(string hash) => Path.Combine(Root, "generated-assets", "preview", hash + ".png");
        public string ManifestPath(string id) => Path.Combine(Root, "generated-assets", "manifest", id + ".json");
        public string ReceiptPath(string key) => Path.Combine(
            Root,
            "generated-assets",
            "idempotency",
            Hash(Encoding.UTF8.GetBytes(key)) + ".json");

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

    private sealed class FailFastAfterMove(
        GeneratedAssetDurableMove target,
        string? durableBoundaryPath,
        string? retainedHandlePath,
        string? allowOwnerCrashPath) : IGeneratedAssetDurability
    {
        public void AfterDurableMove(GeneratedAssetDurableMove move)
        {
            if (move != target) return;
            if (durableBoundaryPath is not null) File.WriteAllBytes(durableBoundaryPath, []);
            if (retainedHandlePath is not null &&
                !SpinWait.SpinUntil(() => File.Exists(retainedHandlePath), TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("The recovery child did not retain the mutex before the injected crash.");
            }
            if (allowOwnerCrashPath is not null &&
                !SpinWait.SpinUntil(() => File.Exists(allowOwnerCrashPath), TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("The parent did not release the injected crash barrier.");
            }
            Environment.FailFast("Injected generated-asset child crash after " + move + ".");
        }
    }

    private sealed class InsertReceiptAfterManifest(string root, string key, byte[] bytes) : IGeneratedAssetDurability
    {
        private int inserted;

        public void AfterDurableMove(GeneratedAssetDurableMove move)
        {
            if (move != GeneratedAssetDurableMove.Manifest || Interlocked.Exchange(ref inserted, 1) != 0) return;
            var path = Path.Combine(root, "generated-assets", "idempotency", Hash(Encoding.UTF8.GetBytes(key)) + ".json");
            File.WriteAllBytes(path, bytes);
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
    private sealed class InjectedChildTopologyException : IOException;

    private sealed record ChildImportOutcome(
        bool Success,
        bool Duplicate,
        string? GeneratedAssetId,
        string? Code,
        bool AbandonedObserved);
    private sealed record ChildProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record RunningChildImport(
        Process Process,
        Task<string> StandardOutput,
        Task<string> StandardError,
        string ResultPath,
        string ReadyPath) : IDisposable
    {
        private int disposed;

        public int CapturedDescendantCount { get; private set; }
        public bool ProcessTreeExitConfirmed { get; private set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            IReadOnlyList<Process> descendants = [];
            try
            {
                descendants = CaptureDescendantProcesses(Process.Id);
                CapturedDescendantCount = descendants.Count;
                if (!Process.HasExited)
                {
                    try { Process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                }
                ProcessTreeExitConfirmed = WaitForExactProcessTreeExit(
                    Process,
                    descendants,
                    TimeSpan.FromSeconds(5));
                if (!ProcessTreeExitConfirmed)
                {
                    throw new TimeoutException($"Child process tree {Process.Id} did not exit after bounded cleanup.");
                }
            }
            finally
            {
                foreach (var descendant in descendants) descendant.Dispose();
                Process.Dispose();
            }
        }
    }

    private sealed class PausedCrashTopology(
        RunningChildImport owner,
        RunningChildImport waiter,
        string gatePath,
        string allowOwnerCrashPath) : IDisposable
    {
        private int disposed;

        public RunningChildImport Owner { get; } = owner;
        public RunningChildImport Waiter { get; } = waiter;
        public string GatePath { get; } = gatePath;
        public string AllowOwnerCrashPath { get; } = allowOwnerCrashPath;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            DisposeChildProcesses(Waiter, Owner);
        }
    }
}
