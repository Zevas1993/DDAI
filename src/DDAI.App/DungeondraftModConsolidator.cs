using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DDAI.App;

public sealed record DungeondraftModConsolidationReceipt(
    string SourceDirectory,
    string DestinationDirectory,
    string ModId);

public sealed record DungeondraftModFile(string RelativePath, long Length, string Sha256);

public sealed record DungeondraftModConsolidationPlan(
    string SourceDirectory,
    string DestinationDirectory,
    string ModId,
    IReadOnlyList<DungeondraftModFile> Files)
{
    public string ManagedModsDirectory { get; init; } = Path.GetDirectoryName(DestinationDirectory) ?? string.Empty;
}

public sealed record DungeondraftModConsolidationUpdate(
    string State,
    bool Changed,
    DungeondraftModConsolidationReceipt? Receipt);

public sealed class DungeondraftModConsolidator
{
    private const string DestinationName = "custom_snap";

    public DungeondraftModConsolidationPlan PlanCustomSnap(
        string sourceDirectory,
        string managedModsDirectory)
    {
        var source = NormalizeExistingDirectory(sourceDirectory, "Custom Snap source");
        var managedRoot = NormalizeManagedRoot(managedModsDirectory);
        RejectOverlappingTrees(source, managedRoot);
        var destination = Path.GetFullPath(Path.Combine(managedRoot, DestinationName));
        RequireCanonicalDestination(managedRoot, destination);

        var files = SnapshotDirectory(source);
        var manifests = files
            .Where(file => !file.RelativePath.Contains(Path.DirectorySeparatorChar) &&
                string.Equals(Path.GetExtension(file.RelativePath), ".ddmod", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (manifests.Length != 1)
        {
            throw new DungeondraftConfigException(
                "The selected Custom Snap directory must contain exactly one top-level .ddmod manifest.");
        }

        var manifestPath = Path.Combine(source, manifests[0].RelativePath);
        try
        {
            var root = JsonNode.Parse(File.ReadAllBytes(manifestPath));
            if (root?["unique_id"]?.GetValue<string>() != DungeondraftConfigEditor.CustomSnapModId)
            {
                throw new DungeondraftConfigException(
                    "The selected mod directory is not the expected Custom Snap mod.");
            }
        }
        catch (DungeondraftConfigException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or IOException or UnauthorizedAccessException)
        {
            throw new DungeondraftConfigException("The Custom Snap manifest is invalid or unreadable.", exception);
        }

        if (Directory.Exists(destination))
        {
            RequireSnapshotsEqual(
                files,
                SnapshotDirectory(destination),
                "The managed Custom Snap destination contains different files; refusing to overwrite it.");
        }
        else if (File.Exists(destination))
        {
            throw new DungeondraftConfigException(
                "The managed Custom Snap destination is an existing file; refusing to overwrite it.");
        }

        return new DungeondraftModConsolidationPlan(
            source,
            destination,
            DungeondraftConfigEditor.CustomSnapModId,
            files)
        {
            ManagedModsDirectory = managedRoot,
        };
    }

    public DungeondraftModConsolidationUpdate Apply(DungeondraftModConsolidationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlan(plan);
        var receipt = new DungeondraftModConsolidationReceipt(
            plan.SourceDirectory,
            plan.DestinationDirectory,
            plan.ModId);

        try
        {
            var sourceFiles = SnapshotDirectory(plan.SourceDirectory);
            RequireSnapshotsEqual(plan.Files, sourceFiles, "Custom Snap changed after consolidation was planned.");

            if (Directory.Exists(plan.DestinationDirectory))
            {
                var destinationFiles = SnapshotDirectory(plan.DestinationDirectory);
                RequireSnapshotsEqual(
                    plan.Files,
                    destinationFiles,
                    "The managed Custom Snap destination contains different files; refusing to overwrite it.");
                return new DungeondraftModConsolidationUpdate("already_current", Changed: false, receipt);
            }

            if (File.Exists(plan.DestinationDirectory))
            {
                throw new DungeondraftConfigException(
                    "The managed Custom Snap destination is an existing file; refusing to overwrite it.");
            }

            Directory.CreateDirectory(plan.ManagedModsDirectory);
            var stage = Path.Combine(
                plan.ManagedModsDirectory,
                $".{DestinationName}.ddai-stage-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(stage);
                foreach (var file in plan.Files)
                {
                    var sourcePath = ResolveRelativePath(plan.SourceDirectory, file.RelativePath);
                    var stagePath = ResolveRelativePath(stage, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(stagePath)!);
                    CopyFileDurably(sourcePath, stagePath);
                }

                sourceFiles = SnapshotDirectory(plan.SourceDirectory);
                var stagedFiles = SnapshotDirectory(stage);
                RequireSnapshotsEqual(plan.Files, sourceFiles, "Custom Snap changed while it was being copied.");
                RequireSnapshotsEqual(plan.Files, stagedFiles, "The staged Custom Snap copy did not verify byte-for-byte.");
                Directory.Move(stage, plan.DestinationDirectory);
            }
            finally
            {
                TryDeleteDirectory(stage);
            }

            return new DungeondraftModConsolidationUpdate("copied", Changed: true, receipt);
        }
        catch (DungeondraftConfigException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DungeondraftConfigException(
                "Custom Snap could not be copied into the managed Dungeondraft Mods directory.",
                exception);
        }
    }

    public bool IsCurrent(DungeondraftModConsolidationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        try
        {
            if (!string.Equals(receipt.ModId, DungeondraftConfigEditor.CustomSnapModId, StringComparison.Ordinal))
            {
                return false;
            }

            var managedRoot = Path.GetDirectoryName(Path.GetFullPath(receipt.DestinationDirectory));
            if (managedRoot is null)
            {
                return false;
            }

            var plan = PlanCustomSnap(receipt.SourceDirectory, managedRoot);
            if (!PathsEqual(plan.DestinationDirectory, receipt.DestinationDirectory) ||
                !Directory.Exists(receipt.DestinationDirectory))
            {
                return false;
            }

            return SnapshotsEqual(plan.Files, SnapshotDirectory(receipt.DestinationDirectory));
        }
        catch (Exception exception) when (exception is DungeondraftConfigException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static void ValidatePlan(DungeondraftModConsolidationPlan plan)
    {
        var source = NormalizeExistingDirectory(plan.SourceDirectory, "Custom Snap source");
        var managedRoot = NormalizeManagedRoot(plan.ManagedModsDirectory);
        var destination = Path.GetFullPath(plan.DestinationDirectory);
        RequireCanonicalDestination(managedRoot, destination);
        RejectOverlappingTrees(source, managedRoot);
        if (!PathsEqual(source, plan.SourceDirectory) ||
            !PathsEqual(managedRoot, plan.ManagedModsDirectory) ||
            !PathsEqual(destination, plan.DestinationDirectory) ||
            !string.Equals(plan.ModId, DungeondraftConfigEditor.CustomSnapModId, StringComparison.Ordinal))
        {
            throw new DungeondraftConfigException("The Custom Snap consolidation plan is not canonical.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in plan.Files)
        {
            _ = ResolveRelativePath(source, file.RelativePath);
            if (file.Length < 0 || file.Sha256.Length != 64 ||
                !file.Sha256.All(character => char.IsAsciiHexDigit(character)) ||
                !seen.Add(file.RelativePath))
            {
                throw new DungeondraftConfigException("The Custom Snap consolidation plan contains an invalid file entry.");
            }
        }
    }

    private static IReadOnlyList<DungeondraftModFile> SnapshotDirectory(string directory)
    {
        var root = NormalizeExistingDirectory(directory, "Mod directory");
        var files = new List<DungeondraftModFile>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var fullPath = Path.GetFullPath(entry);
                RequireBeneath(root, fullPath);
                var attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new DungeondraftConfigException("Custom Snap contains a link or reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(fullPath);
                    continue;
                }

                var relative = Path.GetRelativePath(root, fullPath);
                _ = ResolveRelativePath(root, relative);
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                files.Add(new DungeondraftModFile(
                    relative,
                    stream.Length,
                    Convert.ToHexString(SHA256.HashData(stream))));
            }
        }

        return files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static string NormalizeExistingDirectory(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new DungeondraftConfigException($"{label} is required.");
        }

        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!Directory.Exists(fullPath) ||
                (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new DungeondraftConfigException($"{label} must be an existing ordinary directory.");
            }
        }
        catch (DungeondraftConfigException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DungeondraftConfigException($"{label} must be an existing ordinary directory.", exception);
        }

        return fullPath;
    }

    private static string NormalizeManagedRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new DungeondraftConfigException("The managed Mods directory must be an absolute path.");
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (File.Exists(fullPath))
        {
            throw new DungeondraftConfigException("The managed Mods directory is an existing file.");
        }

        for (var current = new DirectoryInfo(fullPath); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new DungeondraftConfigException("The managed Mods path contains a reparse point.");
            }
        }

        return fullPath;
    }

    private static void RequireCanonicalDestination(string managedRoot, string destination)
    {
        var expected = Path.GetFullPath(Path.Combine(managedRoot, DestinationName));
        if (!PathsEqual(expected, destination))
        {
            throw new DungeondraftConfigException("The Custom Snap destination must be the managed custom_snap sibling.");
        }

        RequireBeneath(managedRoot, destination);
    }

    private static void RejectOverlappingTrees(string source, string managedRoot)
    {
        if (PathsEqual(source, managedRoot) || IsBeneath(source, managedRoot) || IsBeneath(managedRoot, source))
        {
            throw new DungeondraftConfigException("The Custom Snap source and managed Mods directory cannot overlap.");
        }
    }

    private static string ResolveRelativePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative))
        {
            throw new DungeondraftConfigException("A Custom Snap file path is invalid.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, relative));
        RequireBeneath(root, fullPath);
        return fullPath;
    }

    private static void RequireBeneath(string root, string candidate)
    {
        if (!IsBeneath(root, candidate))
        {
            throw new DungeondraftConfigException("A Custom Snap path escapes its expected directory.");
        }
    }

    private static bool IsBeneath(string root, string candidate)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static bool SnapshotsEqual(
        IReadOnlyList<DungeondraftModFile> expected,
        IReadOnlyList<DungeondraftModFile> actual) =>
        expected.Count == actual.Count && expected.SequenceEqual(actual);

    private static void RequireSnapshotsEqual(
        IReadOnlyList<DungeondraftModFile> expected,
        IReadOnlyList<DungeondraftModFile> actual,
        string message)
    {
        if (!SnapshotsEqual(expected, actual))
        {
            throw new DungeondraftConfigException(message);
        }
    }

    private static void CopyFileDurably(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.WriteThrough);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
