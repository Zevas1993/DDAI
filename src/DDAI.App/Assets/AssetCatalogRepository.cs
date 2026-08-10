using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DDAI.Core.Assets;
using Microsoft.Win32.SafeHandles;

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
    private readonly TimeProvider timeProvider;
    private AcceptedAssetCatalog? current;

    public AssetCatalogRepository(string catalogRoot, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogRoot);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.catalogRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(catalogRoot));
        snapshotsRoot = Path.Combine(this.catalogRoot, "snapshots");
        previewsRoot = Path.Combine(this.catalogRoot, "previews");
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
            or OverflowException)
        {
            return false;
        }
    }

    public AcceptedAssetCatalog? GetCurrent() => Volatile.Read(ref current);

    public byte[]? OpenPreview(string previewHash)
    {
        if (!IsCanonicalHash(previewHash))
        {
            return null;
        }

        try
        {
            RequireExistingOrdinaryDirectory(catalogRoot, "Catalog root");
            RequireExistingOrdinaryDirectory(previewsRoot, "Preview directory");
            var previewPath = Path.Combine(previewsRoot, previewHash + ".png");
            RequireBeneath(previewsRoot, previewPath);
            RequireOrdinaryPath(previewsRoot, previewPath);
            var bytes = ReadBoundedFile(previewPath, MaximumPreviewBytes);
            return string.Equals(Hash(bytes), previewHash, StringComparison.Ordinal) ? bytes : null;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException
            or InvalidDataException
            or CryptographicException)
        {
            return null;
        }
    }

    private AcceptedAssetCatalog ReadSnapshot()
    {
        RequireExistingOrdinaryDirectory(catalogRoot, "Catalog root");
        RequireExistingOrdinaryDirectory(snapshotsRoot, "Snapshots directory");

        var pointerPath = Path.Combine(catalogRoot, "current.json");
        RequireBeneath(catalogRoot, pointerPath);
        RequireOrdinaryPath(catalogRoot, pointerPath);
        var pointer = ReadPointer(ReadBoundedText(pointerPath, AssetCatalogJson.MaximumJsonBytes));

        var manifestPath = ResolveSnapshotPath(pointer.Manifest);
        var manifest = AssetCatalogJson.DeserializeManifest(
            ReadBoundedText(manifestPath, AssetCatalogJson.MaximumJsonBytes));
        if (!manifest.Complete || !string.Equals(manifest.SessionId, pointer.SessionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The catalog pointer does not identify a complete matching snapshot.");
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

    private static (string SessionId, string Manifest) ReadPointer(string json)
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
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!observed.Add(property.Name) || property.Value.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("The catalog pointer is malformed.");
            }

            switch (property.Name)
            {
                case "session_id":
                    sessionId = property.Value.GetString();
                    break;
                case "manifest":
                    manifest = property.Value.GetString();
                    break;
                default:
                    throw new JsonException("The catalog pointer contains an unsupported property.");
            }
        }

        if (observed.Count != 2 || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(manifest))
        {
            throw new JsonException("The catalog pointer is missing required values.");
        }

        return (sessionId, manifest);
    }

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

            if (length < buffer.Capacity - 1)
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
