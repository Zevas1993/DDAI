namespace DDAI.App;

public sealed record DungeondraftConfigTransactionPlan(
    string Path,
    byte[] OriginalBytes,
    byte[] ReplacementBytes,
    DungeondraftConfigOwnership Ownership);

public sealed record DungeondraftConfigUpdate(
    string State,
    string Path,
    bool Changed,
    string? BackupPath,
    DungeondraftConfigOwnership? Ownership);

public sealed class DungeondraftConfigTransaction(TimeProvider timeProvider)
{
    public DungeondraftConfigTransactionPlan PlanSetup(string configPath, string managedModsDirectory) =>
        PlanSetup(configPath, managedModsDirectory, [DungeondraftConfigEditor.DdaiModId]);

    public DungeondraftConfigTransactionPlan PlanSetup(
        string configPath,
        string managedModsDirectory,
        IReadOnlyList<string> requiredModIds)
    {
        var path = RequireRegularFile(configPath);
        var original = ReadExact(path);
        var edit = DungeondraftConfigEditor.PlanSetup(original, managedModsDirectory, requiredModIds);
        return new DungeondraftConfigTransactionPlan(
            path,
            edit.OriginalBytes,
            edit.ReplacementBytes,
            edit.Ownership);
    }

    public DungeondraftConfigTransactionPlan PlanUninstall(
        string configPath,
        DungeondraftConfigOwnership ownership)
    {
        var path = RequireRegularFile(configPath);
        var original = ReadExact(path);
        var edit = DungeondraftConfigEditor.PlanUninstall(original, ownership);
        return new DungeondraftConfigTransactionPlan(
            path,
            edit.OriginalBytes,
            edit.ReplacementBytes,
            edit.Ownership);
    }

    public DungeondraftConfigUpdate Apply(DungeondraftConfigTransactionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlanPath(plan.Path);
        if (plan.OriginalBytes.AsSpan().SequenceEqual(plan.ReplacementBytes))
        {
            try
            {
                RequireCurrentBytes(plan.Path, plan.OriginalBytes);
            }
            catch (DungeondraftConfigException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new DungeondraftConfigException(
                    "Dungeondraft config.ini could not be verified as already current.",
                    exception);
            }

            return new DungeondraftConfigUpdate(
                "already_current",
                plan.Path,
                Changed: false,
                BackupPath: null,
                plan.Ownership);
        }

        var directory = Path.GetDirectoryName(plan.Path)!;
        var fileName = Path.GetFileName(plan.Path);
        var stage = Path.Combine(directory, $".{fileName}.ddai-stage-{Guid.NewGuid():N}.tmp");
        string? backup = null;
        var destinationReplaced = false;

        try
        {
            WriteDurableNew(stage, plan.ReplacementBytes);
            RequireCurrentBytes(plan.Path, plan.OriginalBytes);

            backup = $"{plan.Path}.ddai-backup-{timeProvider.GetUtcNow().UtcDateTime:yyyyMMdd'T'HHmmssfffffff'Z'}-{Guid.NewGuid():N}.ini";
            WriteDurableNew(backup, plan.OriginalBytes);

            // This second check narrows the race window after the durable backup.
            RequireCurrentBytes(plan.Path, plan.OriginalBytes);
            File.Move(stage, plan.Path, overwrite: true);
            destinationReplaced = true;

            var installed = ReadExact(plan.Path);
            if (!installed.AsSpan().SequenceEqual(plan.ReplacementBytes))
            {
                throw new IOException("The Dungeondraft configuration replacement did not verify byte-for-byte.");
            }

            return new DungeondraftConfigUpdate(
                "updated",
                plan.Path,
                Changed: true,
                backup,
                plan.Ownership);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DungeondraftConfigException)
        {
            Exception? rollbackFailure = null;
            try
            {
                if (destinationReplaced || DestinationMatches(plan.Path, plan.ReplacementBytes))
                {
                    RestoreOriginalBytes(plan.Path, plan.OriginalBytes);
                }
            }
            catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
            {
                rollbackFailure = rollbackException;
            }
            finally
            {
                TryDelete(stage);
                if (backup is not null)
                {
                    TryDelete(backup);
                }
            }

            if (rollbackFailure is not null)
            {
                throw new DungeondraftConfigException(
                    "Updating Dungeondraft config.ini failed and the original bytes could not be restored.",
                    new AggregateException(exception, rollbackFailure));
            }

            throw new DungeondraftConfigException(
                "Updating Dungeondraft config.ini failed; the original file was preserved.",
                exception);
        }
        finally
        {
            TryDelete(stage);
        }
    }

    public void Restore(DungeondraftConfigTransactionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlanPath(plan.Path);
        byte[] current;
        try
        {
            current = ReadExact(plan.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DungeondraftConfigException("Dungeondraft config.ini could not be read for restoration.", exception);
        }

        if (current.AsSpan().SequenceEqual(plan.OriginalBytes))
        {
            return;
        }

        if (!current.AsSpan().SequenceEqual(plan.ReplacementBytes))
        {
            throw new DungeondraftConfigException(
                "Dungeondraft config.ini changed after setup; refusing to overwrite the user's changes.");
        }

        try
        {
            RestoreOriginalBytes(plan.Path, plan.OriginalBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DungeondraftConfigException("Dungeondraft config.ini could not be restored.", exception);
        }
    }

    private static string RequireRegularFile(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            throw new DungeondraftConfigException("The Dungeondraft config.ini path is required.");
        }

        string path;
        try
        {
            path = Path.GetFullPath(configPath);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.Directory) != 0)
            {
                throw new DungeondraftConfigException("Dungeondraft config.ini must be an existing regular file.");
            }
        }
        catch (DungeondraftConfigException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DungeondraftConfigException("Dungeondraft config.ini must be an existing regular file.", exception);
        }

        return path;
    }

    private static void ValidatePlanPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || Path.GetDirectoryName(path) is null)
        {
            throw new DungeondraftConfigException("The transaction plan contains an invalid config.ini path.");
        }
    }

    private static byte[] ReadExact(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > int.MaxValue)
        {
            throw new IOException("Dungeondraft config.ini is too large.");
        }

        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void RequireCurrentBytes(string path, byte[] expected)
    {
        var current = ReadExact(path);
        if (!current.AsSpan().SequenceEqual(expected))
        {
            throw new DungeondraftConfigException(
                "Dungeondraft config.ini changed after it was planned; refusing to overwrite the user's changes.");
        }
    }

    private static bool DestinationMatches(string path, byte[] expected)
    {
        try
        {
            return File.Exists(path) && ReadExact(path).AsSpan().SequenceEqual(expected);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WriteDurableNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void RestoreOriginalBytes(string path, byte[] originalBytes)
    {
        var directory = Path.GetDirectoryName(path)!;
        var fileName = Path.GetFileName(path);
        var rollback = Path.Combine(directory, $".{fileName}.ddai-rollback-{Guid.NewGuid():N}.tmp");
        try
        {
            WriteDurableNew(rollback, originalBytes);
            File.Move(rollback, path, overwrite: true);
            if (!ReadExact(path).AsSpan().SequenceEqual(originalBytes))
            {
                throw new IOException("The restored Dungeondraft configuration did not verify byte-for-byte.");
            }
        }
        finally
        {
            TryDelete(rollback);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
