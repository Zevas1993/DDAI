using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DDAI.App;

internal sealed class SafeLocalFileSystem
{
    private readonly string root;
    private readonly HashSet<string> trustedDirectories = new(StringComparer.OrdinalIgnoreCase);

    public SafeLocalFileSystem(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root))
        {
            throw new InvalidDataException("The private filesystem root must be absolute.");
        }

        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        RequireOrdinaryDirectory(this.root);
        trustedDirectories.Add(this.root);
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
            trustedDirectories.Add(current);
        }

        return current;
    }

    public IDisposable AcquireDirectoryLease(params string[] requiredDirectories)
    {
        var directories = new HashSet<string>(trustedDirectories, StringComparer.OrdinalIgnoreCase);
        foreach (var requiredDirectory in requiredDirectories)
        {
            var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requiredDirectory));
            if (!PathsEqual(current, root))
            {
                RequireBeneath(root, current);
            }

            while (true)
            {
                directories.Add(current);
                if (PathsEqual(current, root)) break;
                current = Path.GetDirectoryName(current)
                    ?? throw new InvalidDataException("A required private directory has no parent.");
            }
        }

        var handles = new List<SafeFileHandle>();
        try
        {
            foreach (var directory in directories.OrderBy(path => path.Length))
            {
                handles.Add(OpenValidatedHandle(directory, directory: true, GenericRead, shareDelete: false, rejectReparse: true));
            }
            return new DirectoryLease(handles.ToArray());
        }
        catch
        {
            foreach (var handle in handles) handle.Dispose();
            throw;
        }
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

    public bool EntryExists(string path)
    {
        RequireBeneath(root, path);
        var attributes = GetFileAttributes(path);
        return attributes != uint.MaxValue || Marshal.GetLastWin32Error() is not (2 or 3);
    }

    public byte[] ReadBounded(string path, int maximumBytes)
    {
        RequireBeneath(root, path);
        using var lease = AcquireDirectoryLease();
        using var handle = OpenValidatedHandle(path, directory: false, GenericRead, shareDelete: false, rejectReparse: true);
        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
        if (stream.Length > maximumBytes)
        {
            throw new InvalidDataException("Private data exceeds its byte limit.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public FileStream OpenReadLease(string path, int maximumBytes)
    {
        RequireBeneath(root, path);
        var handle = OpenValidatedHandle(path, directory: false, GenericRead, shareDelete: false, rejectReparse: true);
        try
        {
            var stream = new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
            if (stream.Length > maximumBytes)
            {
                stream.Dispose();
                throw new InvalidDataException("Private data exceeds its byte limit.");
            }
            return stream;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void WriteImmutable<T>(string destinationPath, T value, System.Text.Json.JsonSerializerOptions options)
    {
        WriteImmutableBytes(destinationPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, options));
    }

    public void WriteImmutableBytes(string destinationPath, ReadOnlySpan<byte> bytes)
    {
        RequireBeneath(root, destinationPath);
        var parent = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException("The private destination has no parent.");
        RequireOrdinaryDirectory(parent);
        using var lease = AcquireDirectoryLease();
        using var handle = CreateFile(
            destinationPath,
            GenericWrite | DeleteAccess,
            0,
            IntPtr.Zero,
            CreateNew,
            FileAttributeNormal | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "An immutable private file could not be created.");
        }

        try
        {
            ValidateOpenedHandle(handle, destinationPath, rejectReparse: true);
        }
        catch
        {
            try { SetDeleteDisposition(handle); }
            catch (Win32Exception) { }
            throw;
        }

        using (var stream = new FileStream(handle, FileAccess.Write, bufferSize: 4096, isAsync: false))
        {
            try
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            catch
            {
                try { SetDeleteDisposition(stream.SafeFileHandle); }
                catch (Win32Exception) { }
                throw;
            }
        }
    }

    public void ReplaceBytes(string destinationPath, ReadOnlySpan<byte> bytes)
    {
        RequireBeneath(root, destinationPath);
        var parent = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException("The replacement destination has no parent.");
        RequireOrdinaryDirectory(parent);
        using var lease = AcquireDirectoryLease();
        var temporary = Path.Combine(parent, "." + Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".next");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destinationPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public void DeleteOrdinaryOrLink(string path)
    {
        RequireBeneath(root, path);
        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("The private file has no parent.");
        RequireOrdinaryDirectory(parent);
        using var lease = AcquireDirectoryLease();
        if (!EntryExists(path))
        {
            return;
        }
        var attributes = GetEntryAttributes(path);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        using var handle = OpenValidatedHandle(path, isDirectory, DeleteAccess, shareDelete: true, rejectReparse: false);
        SetDeleteDisposition(handle);
        RequireOrdinaryDirectory(parent);
    }

    public void QuarantineOrdinaryOrDeleteLink(string path, string quarantineDirectory)
    {
        RequireBeneath(root, path);
        RequireBeneath(root, quarantineDirectory);
        RequireOrdinaryDirectory(quarantineDirectory);
        using var lease = AcquireDirectoryLease();
        if (!EntryExists(path))
        {
            return;
        }

        var attributes = GetEntryAttributes(path);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        var destinationName = Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".invalid";
        var destination = Path.Combine(quarantineDirectory, destinationName);
        RequireBeneath(root, destination);
        using var quarantine = OpenValidatedHandle(quarantineDirectory, directory: true, GenericRead, shareDelete: false, rejectReparse: true);
        var deleted = false;
        using (var source = OpenValidatedHandle(path, isDirectory, DeleteAccess, shareDelete: true, rejectReparse: false))
        {
            try
            {
                RenameByHandleWithRetries(source, destination);
            }
            catch (Win32Exception)
            {
                // Empty directory blockers and malformed files may be safely consumed even when
                // a filter driver refuses the handle rename. The already-opened object is deleted;
                // no path is re-resolved for the fallback.
                SetDeleteDisposition(source);
                deleted = true;
            }
        }
        if (!deleted)
        {
            using (OpenValidatedHandle(destination, isDirectory, GenericRead, shareDelete: false, rejectReparse: false))
            {
            }
        }
    }

    private static void RequireOrdinaryDirectory(string path)
    {
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Private paths must use ordinary directories.");
        }

        using var handle = OpenValidatedHandle(path, directory: true, GenericRead, shareDelete: true, rejectReparse: true);
    }

    private static SafeFileHandle OpenValidatedHandle(
        string path,
        bool directory,
        uint desiredAccess,
        bool shareDelete,
        bool rejectReparse)
    {
        var handle = CreateFile(
            path,
            desiredAccess,
            FileShareRead | FileShareWrite | (shareDelete ? FileShareDelete : 0),
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
            ValidateOpenedHandle(handle, path, rejectReparse);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void ValidateOpenedHandle(SafeFileHandle handle, string path, bool rejectReparse)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileAttributeTagInfo,
                out var attributes,
                (uint)Marshal.SizeOf<FileAttributeTagInfo>()) ||
            (rejectReparse && (attributes.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0) ||
            !PathsEqual(GetFinalPath(handle), path))
        {
            throw new InvalidDataException("Private paths cannot traverse reparse points.");
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

    private static FileAttributes GetEntryAttributes(string path)
    {
        var attributes = GetFileAttributes(path);
        if (attributes == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "A private entry could not be inspected.");
        }
        return (FileAttributes)attributes;
    }

    private static void SetDeleteDisposition(SafeFileHandle handle)
    {
        var disposition = new FileDispositionInfo { DeleteFile = true };
        if (!SetFileInformationByHandle(
                handle,
                FileInfoByHandleClass.FileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInfo>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "A private entry could not be deleted by handle.");
        }
    }

    private static void RenameByHandle(SafeFileHandle source, string destinationPath)
    {
        var nameBytes = Encoding.Unicode.GetBytes(destinationPath);
        var headerSize = IntPtr.Size == 8 ? 20 : 12;
        var buffer = Marshal.AllocHGlobal(headerSize + nameBytes.Length);
        try
        {
            for (var index = 0; index < headerSize + nameBytes.Length; index++)
            {
                Marshal.WriteByte(buffer, index, 0);
            }
            Marshal.WriteByte(buffer, 0, 0);
            Marshal.WriteIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4, IntPtr.Zero);
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 16 : 8, nameBytes.Length);
            Marshal.Copy(nameBytes, 0, IntPtr.Add(buffer, headerSize), nameBytes.Length);
            if (!SetFileInformationByHandle(
                    source,
                    FileInfoByHandleClass.FileRenameInfo,
                    buffer,
                    (uint)(headerSize + nameBytes.Length)))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "A private entry could not be quarantined by handle.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void RenameByHandleWithRetries(SafeFileHandle source, string destinationPath)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                RenameByHandle(source, destinationPath);
                return;
            }
            catch (Win32Exception) when (attempt < 3)
            {
                Thread.Sleep(1);
            }
        }
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint CreateNew = 1;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private enum FileInfoByHandleClass
    {
        FileRenameInfo = 3,
        FileDispositionInfo = 4,
        FileAttributeTagInfo = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool DeleteFile;
    }

    private sealed class DirectoryLease(IEnumerable<SafeFileHandle> handles) : IDisposable
    {
        private readonly SafeFileHandle[] handles = handles.ToArray();
        public void Dispose()
        {
            foreach (var handle in handles.Reverse())
            {
                handle.Dispose();
            }
        }
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributes(string fileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);
}
