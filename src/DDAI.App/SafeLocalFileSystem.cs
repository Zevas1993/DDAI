using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DDAI.App;

internal sealed class SafeLocalFileSystem
{
    private readonly string root;

    public SafeLocalFileSystem(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root))
        {
            throw new InvalidDataException("The private filesystem root must be absolute.");
        }

        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        RequireOrdinaryDirectory(this.root);
    }

    public string EnsureDirectory(params string[] segments)
    {
        var current = root;
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                segment.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            {
                throw new InvalidDataException("A private directory segment is invalid.");
            }

            current = Path.GetFullPath(Path.Combine(current, segment));
            RequireBeneath(root, current);
            if (File.Exists(current))
            {
                throw new InvalidDataException("A private directory path is an existing file.");
            }

            Directory.CreateDirectory(current);
            RequireOrdinaryDirectory(current);
        }

        return current;
    }

    public IReadOnlyList<string> EnumerateFiles(string directory, string pattern, int maximumCandidates)
    {
        RequireBeneath(root, directory);
        RequireOrdinaryDirectory(directory);
        var files = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
            .Take(maximumCandidates)
            .Order(StringComparer.Ordinal)
            .ToArray();
        RequireOrdinaryDirectory(directory);
        return files;
    }

    public byte[] ReadBounded(string path, int maximumBytes)
    {
        RequireBeneath(root, path);
        using var handle = OpenOrdinaryHandle(path, directory: false);
        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
        if (stream.Length > maximumBytes)
        {
            throw new InvalidDataException("Private data exceeds its byte limit.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public void WriteImmutable<T>(string destinationPath, T value, System.Text.Json.JsonSerializerOptions options)
    {
        RequireBeneath(root, destinationPath);
        var parent = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException("The private destination has no parent.");
        RequireOrdinaryDirectory(parent);
        var temporaryPath = Path.Combine(parent, "." + Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                System.Text.Json.JsonSerializer.Serialize(stream, value, options);
                stream.Flush(flushToDisk: true);
            }

            using (OpenOrdinaryHandle(temporaryPath, directory: false))
            {
            }
            RequireOrdinaryDirectory(parent);
            File.Move(temporaryPath, destinationPath, overwrite: false);
            using (OpenOrdinaryHandle(destinationPath, directory: false))
            {
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public void DeleteOrdinaryOrLink(string path)
    {
        RequireBeneath(root, path);
        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("The private file has no parent.");
        RequireOrdinaryDirectory(parent);
        if (!File.Exists(path))
        {
            return;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
        {
            using (OpenOrdinaryHandle(path, directory: false))
            {
            }
        }

        File.Delete(path);
        RequireOrdinaryDirectory(parent);
    }

    public void QuarantineOrdinaryOrDeleteLink(string path, string quarantineDirectory)
    {
        RequireBeneath(root, path);
        RequireBeneath(root, quarantineDirectory);
        RequireOrdinaryDirectory(quarantineDirectory);
        if (!File.Exists(path))
        {
            return;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            DeleteOrdinaryOrLink(path);
            return;
        }

        using (OpenOrdinaryHandle(path, directory: false))
        {
        }
        var destination = Path.Combine(
            quarantineDirectory,
            Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".invalid");
        RequireBeneath(root, destination);
        File.Move(path, destination, overwrite: false);
        using (OpenOrdinaryHandle(destination, directory: false))
        {
        }
    }

    private static void RequireOrdinaryDirectory(string path)
    {
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Private paths must use ordinary directories.");
        }

        using var handle = OpenOrdinaryHandle(path, directory: true);
    }

    private static SafeFileHandle OpenOrdinaryHandle(string path, bool directory)
    {
        var handle = CreateFile(
            path,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | (directory ? FileFlagBackupSemantics : 0),
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "A private path could not be opened.");
        }

        try
        {
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileInfoByHandleClass.FileAttributeTagInfo,
                    out var attributes,
                    (uint)Marshal.SizeOf<FileAttributeTagInfo>()) ||
                (attributes.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ||
                !PathsEqual(GetFinalPath(handle), path))
            {
                throw new InvalidDataException("Private paths cannot traverse reparse points.");
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
                throw new Win32Exception(Marshal.GetLastWin32Error(), "A private final path could not be read.");
            }

            if (length < buffer.Capacity)
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

    private static void RequireBeneath(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        if (!normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A private path escapes its expected root.");
        }
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
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
