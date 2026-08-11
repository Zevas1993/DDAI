using System.Buffers.Binary;
using System.Collections.Immutable;
using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DDAI.Core.Assets;
using Microsoft.Win32.SafeHandles;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DDAI.App.Tests")]

namespace DDAI.App.Assets;

public sealed class AcceptedAssetCatalog
{
    private readonly TimeProvider timeProvider;

    internal AcceptedAssetCatalog(
        AssetCatalogManifest manifest,
        ImmutableArray<AssetCatalogEntry> entries,
        TimeProvider timeProvider)
    {
        Manifest = manifest;
        Entries = entries;
        this.timeProvider = timeProvider;
    }

    public AssetCatalogManifest Manifest { get; }

    public IReadOnlyList<AssetCatalogEntry> Entries { get; }

    public TimeSpan Age
    {
        get
        {
            var age = timeProvider.GetUtcNow() - Manifest.SnapshotAt;
            return age > TimeSpan.Zero ? age : TimeSpan.Zero;
        }
    }

    public bool Live => Age <= AssetCatalogRepository.MaximumLiveAge;
}

public sealed class AssetCatalogRepository
{
    public const int MaximumPreviewBytes = 262_144;
    public static readonly TimeSpan MaximumLiveAge = TimeSpan.FromSeconds(45);

    private readonly string catalogRoot;
    private readonly string snapshotsRoot;
    private readonly string previewsRoot;
    private readonly string? generatedDataRoot;
    private readonly string? generatedRoot;
    private readonly string? generatedManifestRoot;
    private readonly TimeProvider timeProvider;
    private AcceptedAssetCatalog? current;

    public AssetCatalogRepository(string catalogRoot, TimeProvider timeProvider)
        : this(catalogRoot, null, timeProvider, hasGeneratedAssetRoot: false)
    {
    }

    public AssetCatalogRepository(string catalogRoot, string generatedAssetDataRoot, TimeProvider timeProvider)
        : this(catalogRoot, generatedAssetDataRoot, timeProvider, hasGeneratedAssetRoot: true)
    {
    }

    private AssetCatalogRepository(
        string catalogRoot,
        string? generatedAssetDataRoot,
        TimeProvider timeProvider,
        bool hasGeneratedAssetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogRoot);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (hasGeneratedAssetRoot &&
            (string.IsNullOrWhiteSpace(generatedAssetDataRoot) || !Path.IsPathFullyQualified(generatedAssetDataRoot)))
        {
            throw new InvalidDataException("The generated asset data root must be absolute.");
        }

        this.catalogRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(catalogRoot));
        snapshotsRoot = Path.Combine(this.catalogRoot, "snapshots");
        previewsRoot = Path.Combine(this.catalogRoot, "previews");
        if (hasGeneratedAssetRoot)
        {
            this.generatedDataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(generatedAssetDataRoot!));
            generatedRoot = Path.Combine(this.generatedDataRoot, "generated-assets");
            generatedManifestRoot = Path.Combine(generatedRoot, "manifest");
        }
        this.timeProvider = timeProvider;
    }

    public bool TryRefresh()
    {
        try
        {
            var accepted = ReadSnapshot();
            Interlocked.Exchange(ref current, accepted);
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException
            or JsonException
            or InvalidDataException
            or CryptographicException
            or Win32Exception
            or OverflowException)
        {
            return false;
        }
    }

    public AcceptedAssetCatalog? GetCurrent() => Volatile.Read(ref current);

    public IReadOnlyList<AssetCatalogEntry> GetStagedGeneratedEntries()
    {
        if (generatedDataRoot is null || generatedRoot is null || generatedManifestRoot is null)
        {
            return [];
        }

        try
        {
            RequireExistingOrdinaryDirectory(generatedDataRoot, "Generated asset data root");
            RequireExistingOrdinaryDirectory(generatedRoot, "Generated asset root");
            RequireExistingOrdinaryDirectory(generatedManifestRoot, "Generated asset manifest directory");
            var entries = new List<AssetCatalogEntry>();
            var assetReferences = new HashSet<string>(StringComparer.Ordinal);
            foreach (var manifestPath in Directory.EnumerateFiles(
                         generatedManifestRoot,
                         "*.json",
                         SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                try
                {
                    var entry = ReadStagedGeneratedEntry(manifestPath);
                    if (assetReferences.Add(entry.AssetRef))
                    {
                        entries.Add(entry);
                    }
                }
                catch (Exception exception) when (IsRecoverableCatalogFailure(exception))
                {
                    // A malformed staged record never weakens or invalidates the accepted live snapshot.
                }
            }

            return entries.ToImmutableArray();
        }
        catch (Exception exception) when (IsRecoverableCatalogFailure(exception))
        {
            return [];
        }
    }

    public byte[]? OpenPreview(string previewHash)
    {
        if (!IsCanonicalHash(previewHash))
        {
            return null;
        }

        try
        {
            RequireExistingOrdinaryDirectory(catalogRoot, "Catalog root");
            var previewPath = Path.Combine(previewsRoot, previewHash + ".png");
            RequireBeneath(previewsRoot, previewPath);
            var bytes = ReadBoundedFile(previewPath, MaximumPreviewBytes);
            if (!IsBoundedPng(bytes) || !string.Equals(Hash(bytes), previewHash, StringComparison.Ordinal))
            {
                return null;
            }

            return HasDecodableImageData(bytes) ? bytes : null;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException
            or InvalidDataException
            or CryptographicException
            or Win32Exception)
        {
            return null;
        }
    }

    private AcceptedAssetCatalog ReadSnapshot()
    {
        var validPointers = InspectPointers()
            .Where(pointer => pointer.Catalog is not null)
            .Select(pointer => pointer.Catalog!)
            .ToArray();
        EnsureNoRevisionConflict(validPointers);
        return validPointers
            .OrderByDescending(candidate => candidate.Manifest.CatalogRevision)
            .FirstOrDefault()
            ?? throw new InvalidDataException("No complete recoverable catalog pointer is available.");
    }

    internal IReadOnlyList<CatalogPointerInspection> InspectPointers()
    {
        RequireExistingOrdinaryDirectory(catalogRoot, "Catalog root");
        RequireExistingOrdinaryDirectory(snapshotsRoot, "Snapshots directory");

        var inspections = new List<CatalogPointerInspection>();
        foreach (var pointer in new[]
                 {
                     new CatalogPointerLocation(-1, Path.Combine(catalogRoot, "current.json")),
                     new CatalogPointerLocation(0, Path.Combine(catalogRoot, "current-slot-0.json")),
                     new CatalogPointerLocation(1, Path.Combine(catalogRoot, "current-slot-1.json")),
                 })
        {
            try
            {
                inspections.Add(new CatalogPointerInspection(pointer.SlotIndex, ReadSnapshot(pointer.Path)));
            }
            catch (Exception exception) when (IsRecoverableCatalogFailure(exception))
            {
                inspections.Add(new CatalogPointerInspection(pointer.SlotIndex, null));
            }
        }

        return inspections;
    }

    internal static void EnsureNoRevisionConflict(IEnumerable<AcceptedAssetCatalog> candidates)
    {
        foreach (var revision in candidates.GroupBy(candidate => candidate.Manifest.CatalogRevision))
        {
            if (revision.Select(candidate => candidate.Manifest.CatalogFingerprint).Distinct(StringComparer.Ordinal).Skip(1).Any())
            {
                throw new InvalidDataException("Catalog pointers conflict at the same revision.");
            }
        }
    }

    private AcceptedAssetCatalog ReadSnapshot(string pointerPath)
    {
        RequireBeneath(catalogRoot, pointerPath);
        RequireOrdinaryPath(catalogRoot, pointerPath);
        var pointer = ReadPointer(ReadBoundedText(pointerPath, AssetCatalogJson.MaximumJsonBytes));
        var accepted = ReadImmutableSnapshot(pointer.Manifest);
        var manifest = accepted.Manifest;
        if (!manifest.Complete || !string.Equals(manifest.SessionId, pointer.SessionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The catalog pointer does not identify a complete matching snapshot.");
        }

        if (pointer.CatalogRevision is not null && pointer.CatalogRevision != manifest.CatalogRevision)
        {
            throw new InvalidDataException("The catalog pointer revision does not match its manifest.");
        }

        return accepted;
    }

    internal AcceptedAssetCatalog ReadImmutableSnapshot(string manifestRelativePath)
    {
        RequireExistingOrdinaryDirectory(catalogRoot, "Catalog root");
        RequireExistingOrdinaryDirectory(snapshotsRoot, "Snapshots directory");
        var manifestPath = ResolveSnapshotPath(manifestRelativePath);
        var manifest = AssetCatalogJson.DeserializeManifest(
            ReadBoundedText(manifestPath, AssetCatalogJson.MaximumJsonBytes));
        if (!manifest.Complete)
        {
            throw new InvalidDataException("The immutable catalog snapshot is incomplete.");
        }

        var entries = new List<AssetCatalogEntry>();
        var assetReferences = new HashSet<string>(StringComparer.Ordinal);
        var categoryCounts = AssetCategory.All.ToDictionary(category => category, _ => 0, StringComparer.Ordinal);
        var manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("The manifest directory is unavailable.");

        foreach (var chunk in manifest.Chunks)
        {
            var chunkPath = Path.Combine(manifestDirectory, chunk.FileName);
            RequireBeneath(manifestDirectory, chunkPath);
            RequireOrdinaryPath(manifestDirectory, chunkPath);
            var chunkBytes = ReadBoundedFile(chunkPath, AssetCatalogJson.MaximumJsonBytes);
            if (chunkBytes.LongLength != chunk.ByteCount ||
                !string.Equals(Hash(chunkBytes), chunk.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A catalog chunk does not match its manifest receipt.");
            }

            var chunkEntries = AssetCatalogJson.DeserializeChunk(Encoding.UTF8.GetString(chunkBytes));
            if (chunkEntries.Count != chunk.EntryCount)
            {
                throw new InvalidDataException("A catalog chunk record count does not match its manifest receipt.");
            }

            foreach (var entry in chunkEntries)
            {
                if (!assetReferences.Add(entry.AssetRef))
                {
                    throw new InvalidDataException("Catalog snapshots cannot contain duplicate asset references.");
                }

                categoryCounts[entry.Category] = checked(categoryCounts[entry.Category] + 1);
                entries.Add(FreezeEntry(entry));
            }
        }

        foreach (var category in AssetCategory.All)
        {
            if (manifest.CategoryCounts[category] != categoryCounts[category])
            {
                throw new InvalidDataException("Catalog category totals do not match the published records.");
            }
        }

        return new AcceptedAssetCatalog(FreezeManifest(manifest), entries.ToImmutableArray(), timeProvider);
    }

    private string ResolveSnapshotPath(string manifestRelativePath)
    {
        if (string.IsNullOrWhiteSpace(manifestRelativePath) || Path.IsPathFullyQualified(manifestRelativePath))
        {
            throw new InvalidDataException("The catalog manifest path is not a safe relative path.");
        }

        var manifestPath = Path.GetFullPath(Path.Combine(snapshotsRoot, manifestRelativePath));
        RequireBeneath(snapshotsRoot, manifestPath);
        RequireOrdinaryPath(snapshotsRoot, manifestPath);
        return manifestPath;
    }

    private static (string SessionId, string Manifest, long? CatalogRevision) ReadPointer(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The catalog pointer must be a JSON object.");
        }

        string? sessionId = null;
        string? manifest = null;
        long? catalogRevision = null;
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!observed.Add(property.Name))
            {
                throw new JsonException("The catalog pointer is malformed.");
            }

            switch (property.Name)
            {
                case "session_id":
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new JsonException("The catalog pointer session is malformed.");
                    }
                    sessionId = property.Value.GetString();
                    break;
                case "manifest":
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new JsonException("The catalog pointer manifest is malformed.");
                    }
                    manifest = property.Value.GetString();
                    break;
                case "catalog_revision":
                    if (!property.Value.TryGetInt64(out var revision) || revision < 0)
                    {
                        throw new JsonException("The catalog pointer revision is malformed.");
                    }
                    catalogRevision = revision;
                    break;
                default:
                    throw new JsonException("The catalog pointer contains an unsupported property.");
            }
        }

        if (observed.Count is < 2 or > 3 || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(manifest))
        {
            throw new JsonException("The catalog pointer is missing required values.");
        }

        return (sessionId, manifest, catalogRevision);
    }

    private static bool IsRecoverableCatalogFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException
        or ArgumentException
        or NotSupportedException
        or PathTooLongException
        or JsonException
        or InvalidDataException
        or CryptographicException
        or Win32Exception
        or OverflowException;

    private static AssetCatalogManifest FreezeManifest(AssetCatalogManifest manifest) => manifest with
    {
        CategoryCounts = manifest.CategoryCounts.ToImmutableDictionary(StringComparer.Ordinal),
        Chunks = manifest.Chunks.ToImmutableArray(),
        Errors = manifest.Errors.Select(error => new AssetCatalogError(error.Code, error.Message, error.Category)).ToImmutableArray(),
    };

    private static AssetCatalogEntry FreezeEntry(AssetCatalogEntry entry) => entry with
    {
        SearchTerms = entry.SearchTerms.ToImmutableArray(),
        Tags = entry.Tags.ToImmutableArray(),
    };

    private AssetCatalogEntry ReadStagedGeneratedEntry(string manifestPath)
    {
        if (generatedManifestRoot is null)
        {
            throw new InvalidOperationException("The generated manifest root is unavailable.");
        }

        RequireBeneath(generatedManifestRoot, manifestPath);
        RequireOrdinaryPath(generatedManifestRoot, manifestPath);
        return GeneratedAssetStore.ReadStagedCatalogEntry(
            ReadBoundedFile(manifestPath, GeneratedAssetStore.MaximumManifestBytes),
            Path.GetFileName(manifestPath));
    }

    private static string ReadBoundedText(string path, int maximumBytes) =>
        Encoding.UTF8.GetString(ReadBoundedFile(path, maximumBytes));

    private static byte[] ReadBoundedFile(string path, int maximumBytes)
    {
        using var handle = OpenOrdinaryReadHandle(path);
        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 81_920, isAsync: false);
        if (stream.Length > maximumBytes)
        {
            throw new InvalidDataException("The catalog file exceeds its byte limit.");
        }

        using var output = new MemoryStream((int)stream.Length);
        var buffer = new byte[81_920];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (output.Length > maximumBytes - read)
            {
                throw new InvalidDataException("The catalog file exceeds its byte limit.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static SafeFileHandle OpenOrdinaryReadHandle(string path)
    {
        var handle = CreateFile(
            path,
            GenericRead,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Catalog file could not be opened.");
        }

        try
        {
            if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileAttributeTagInfo,
                out var attributes,
                (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Catalog file attributes could not be read.");
            }

            if ((attributes.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ||
                !PathsEqual(GetFinalPath(handle), path))
            {
                throw new InvalidDataException("Catalog paths cannot traverse reparse points.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Catalog file final path could not be read.");
            }

            if (FinalPathFitsBuffer(length, buffer.Capacity))
            {
                var finalPath = buffer.ToString();
                return finalPath.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)
                    ? "\\\\" + finalPath[8..]
                    : finalPath.StartsWith("\\\\?\\", StringComparison.Ordinal)
                        ? finalPath[4..]
                        : finalPath;
            }

            capacity = checked((int)length + 1);
        }
    }

    private static void RequireExistingOrdinaryDirectory(string path, string name)
    {
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{name} must be an ordinary directory.");
        }
    }

    private static void RequireOrdinaryPath(string root, string candidate)
    {
        RequireBeneath(root, candidate);
        for (var current = candidate; ; current = Path.GetDirectoryName(current)
            ?? throw new InvalidDataException("The catalog path has no parent."))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Catalog paths cannot traverse reparse points.");
            }

            if (PathsEqual(current, root))
            {
                return;
            }
        }
    }

    private static void RequireBeneath(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A catalog path escapes its expected directory.");
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    internal static bool FinalPathFitsBuffer(uint returnedLength, int bufferCapacity) =>
        returnedLength < bufferCapacity;

    private static bool IsBoundedPng(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < signature.Length || !bytes[..signature.Length].SequenceEqual(signature))
        {
            return false;
        }

        var offset = signature.Length;
        var hasHeader = false;
        var hasPalette = false;
        var hasImageData = false;
        PngHeader header = default;
        using var imageData = new MemoryStream();
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 12)
            {
                return false;
            }

            var dataLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (dataLength > int.MaxValue || dataLength > bytes.Length - offset - 12)
            {
                return false;
            }

            var type = bytes.Slice(offset + 4, 4);
            var data = bytes.Slice(offset + 8, (int)dataLength);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + (int)dataLength, 4));
            if (ComputePngCrc(type, data) != expectedCrc)
            {
                return false;
            }

            if (!hasHeader)
            {
                if (!type.SequenceEqual("IHDR"u8) || data.Length != 13 || !IsValidHeader(data, out header))
                {
                    return false;
                }

                hasHeader = true;
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (hasPalette || hasImageData || data.Length == 0 || data.Length % 3 != 0 || data.Length > 768 ||
                    header.ColorType is 0 or 4)
                {
                    return false;
                }

                hasPalette = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (data.Length == 0 || header.ColorType == 3 && !hasPalette)
                {
                    return false;
                }

                hasImageData = true;
                imageData.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                return data.Length == 0 && hasImageData && offset + 12 + (int)dataLength == bytes.Length &&
                    HasDecodableImageData(imageData.ToArray(), header);
            }
            else if ((type[0] & 0x20) == 0)
            {
                return false;
            }

            offset += 12 + (int)dataLength;
        }

        return false;
    }

    private static bool IsValidHeader(ReadOnlySpan<byte> header, out PngHeader parsedHeader)
    {
        var width = BinaryPrimitives.ReadUInt32BigEndian(header);
        var height = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4));
        var bitDepth = header[8];
        var colorType = header[9];
        parsedHeader = default;
        if (width is 0 or > 256 || height is 0 or > 256 || header[10] != 0 || header[11] != 0 || header[12] > 1)
        {
            return false;
        }

        var valid = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            4 or 6 => bitDepth is 8 or 16,
            _ => false,
        };
        if (valid)
        {
            parsedHeader = new PngHeader((int)width, (int)height, bitDepth, colorType, header[12] == 1);
        }

        return valid;
    }

    private static bool HasDecodableImageData(ReadOnlySpan<byte> pngBytes)
    {
        if (!TryReadPngData(pngBytes, out var imageData, out var header))
        {
            return false;
        }

        return HasDecodableImageData(imageData, header);
    }

    private static bool TryReadPngData(ReadOnlySpan<byte> bytes, out byte[] imageData, out PngHeader header)
    {
        imageData = [];
        header = default;
        var offset = 8;
        using var output = new MemoryStream();
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 12)
            {
                return false;
            }

            var dataLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (dataLength > int.MaxValue || dataLength > bytes.Length - offset - 12)
            {
                return false;
            }

            var type = bytes.Slice(offset + 4, 4);
            var data = bytes.Slice(offset + 8, (int)dataLength);
            if (type.SequenceEqual("IHDR"u8))
            {
                if (data.Length != 13 || !IsValidHeader(data, out header))
                {
                    return false;
                }
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                output.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                imageData = output.ToArray();
                return data.Length == 0 && offset + 12 + (int)dataLength == bytes.Length;
            }

            offset += 12 + (int)dataLength;
        }

        return false;
    }

    private static bool HasDecodableImageData(byte[] imageData, PngHeader header)
    {
        if (!TryGetDecodedByteCount(header, out var decodedByteCount))
        {
            return false;
        }

        try
        {
            using var compressed = new MemoryStream(imageData, writable: false);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            var decoded = new byte[decodedByteCount];
            var read = 0;
            while (read < decoded.Length)
            {
                var count = zlib.Read(decoded, read, decoded.Length - read);
                if (count == 0)
                {
                    return false;
                }

                read += count;
            }

            if (zlib.ReadByte() != -1)
            {
                return false;
            }

            return HasValidScanlineFilters(decoded, header);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool TryGetDecodedByteCount(PngHeader header, out int byteCount)
    {
        try
        {
            var bitsPerPixel = header.ColorType switch
            {
                0 or 3 => header.BitDepth,
                2 => checked(header.BitDepth * 3),
                4 => checked(header.BitDepth * 2),
                6 => checked(header.BitDepth * 4),
                _ => 0,
            };
            long total = 0;
            foreach (var pass in EnumeratePasses(header))
            {
                var rowBytes = checked((pass.Width * bitsPerPixel + 7) / 8);
                total = checked(total + (long)pass.Height * (rowBytes + 1));
            }

            byteCount = checked((int)total);
            return true;
        }
        catch (OverflowException)
        {
            byteCount = 0;
            return false;
        }
    }

    private static bool HasValidScanlineFilters(ReadOnlySpan<byte> decoded, PngHeader header)
    {
        var bitsPerPixel = header.ColorType switch
        {
            0 or 3 => header.BitDepth,
            2 => header.BitDepth * 3,
            4 => header.BitDepth * 2,
            6 => header.BitDepth * 4,
            _ => 0,
        };
        var offset = 0;
        foreach (var pass in EnumeratePasses(header))
        {
            var rowBytes = (pass.Width * bitsPerPixel + 7) / 8;
            for (var row = 0; row < pass.Height; row++)
            {
                if (offset >= decoded.Length || decoded[offset] > 4)
                {
                    return false;
                }

                offset += rowBytes + 1;
            }
        }

        return offset == decoded.Length;
    }

    private static IEnumerable<PngPass> EnumeratePasses(PngHeader header)
    {
        if (!header.Interlaced)
        {
            yield return new PngPass(header.Width, header.Height);
            yield break;
        }

        int[] startsX = [0, 4, 0, 2, 0, 1, 0];
        int[] startsY = [0, 0, 4, 0, 2, 0, 1];
        int[] stepsX = [8, 8, 4, 4, 2, 2, 1];
        int[] stepsY = [8, 8, 8, 4, 4, 2, 2];
        for (var pass = 0; pass < startsX.Length; pass++)
        {
            var width = PassLength(header.Width, startsX[pass], stepsX[pass]);
            var height = PassLength(header.Height, startsY[pass], stepsY[pass]);
            if (width > 0 && height > 0)
            {
                yield return new PngPass(width, height);
            }
        }
    }

    private static int PassLength(int length, int start, int step) =>
        length <= start ? 0 : (length - start + step - 1) / step;

    private static uint ComputePngCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in type)
        {
            crc = UpdatePngCrc(crc, value);
        }

        foreach (var value in data)
        {
            crc = UpdatePngCrc(crc, value);
        }

        return ~crc;
    }

    private static uint UpdatePngCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320u;
        }

        return crc;
    }

    private static bool IsCanonicalHash(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        return value.All(character => (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'));
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private readonly record struct PngHeader(
        int Width,
        int Height,
        int BitDepth,
        int ColorType,
        bool Interlaced);

    private readonly record struct PngPass(int Width, int Height);

    private readonly record struct CatalogPointerLocation(int SlotIndex, string Path);

    private enum FileInfoByHandleClass
    {
        FileAttributeTagInfo = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint flags);
}

internal sealed record CatalogPointerInspection(int SlotIndex, AcceptedAssetCatalog? Catalog);
