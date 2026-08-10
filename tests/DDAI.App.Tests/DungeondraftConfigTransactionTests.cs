using System.Text;

namespace DDAI.App.Tests;

public sealed class DungeondraftConfigTransactionTests
{
    [Fact]
    public void Apply_RequiredModOverloadPersistsBothIdsAndIsIdempotent()
    {
        using var sandbox = new ConfigTransactionSandbox();
        File.WriteAllText(
            sandbox.ConfigPath,
            "[Mods]\nactive_mods=[ ]\nmods_directory=\"D:\\\\DungeonDraft\\\\Dungeondraft\\\\mods\\\\custom_snap\"\n");
        var transaction = new DungeondraftConfigTransaction(new FixedTimeProvider());
        string[] required =
        [
            DungeondraftConfigEditor.CustomSnapModId,
            DungeondraftConfigEditor.DdaiModId,
        ];

        var first = transaction.Apply(
            transaction.PlanSetup(sandbox.ConfigPath, sandbox.ManagedModsDirectory, required));
        var second = transaction.Apply(
            transaction.PlanSetup(sandbox.ConfigPath, sandbox.ManagedModsDirectory, required));
        var text = File.ReadAllText(sandbox.ConfigPath);

        Assert.Equal("updated", first.State);
        Assert.Contains("Lievven.Snappy_Mod", text);
        Assert.Contains("org.ddai.status_bridge", text);
        Assert.Equal("already_current", second.State);
        Assert.False(second.Changed);
    }

    [Fact]
    public void Apply_CreatesExactSiblingBackupAndIsIdempotent()
    {
        using var sandbox = new ConfigTransactionSandbox();
        var original = Encoding.UTF8.GetBytes(
            "[Mods]\r\nactive_mods=[ \"Lievven.Snappy_Mod\" ]\r\nmods_directory=\"D:\\\\Old\"\r\n");
        File.WriteAllBytes(sandbox.ConfigPath, original);
        var transaction = new DungeondraftConfigTransaction(new FixedTimeProvider());

        var update = transaction.Apply(transaction.PlanSetup(sandbox.ConfigPath, sandbox.ManagedModsDirectory));

        Assert.True(update.Changed);
        Assert.Equal("updated", update.State);
        Assert.NotNull(update.BackupPath);
        Assert.Equal(original, File.ReadAllBytes(update.BackupPath!));
        Assert.Equal(Path.GetDirectoryName(sandbox.ConfigPath), Path.GetDirectoryName(update.BackupPath));
        Assert.True(DungeondraftConfigEditor.IsConfigured(File.ReadAllBytes(sandbox.ConfigPath), sandbox.ManagedModsDirectory));

        var second = transaction.Apply(transaction.PlanSetup(sandbox.ConfigPath, sandbox.ManagedModsDirectory));

        Assert.False(second.Changed);
        Assert.Equal("already_current", second.State);
        Assert.Null(second.BackupPath);
        Assert.Single(Directory.GetFiles(sandbox.Root, "*.ddai-backup-*.ini"));
        AssertNoStages(sandbox.Root);
    }

    [Fact]
    public void Apply_RejectsConcurrentChangeWithoutReplacingIt()
    {
        using var sandbox = new ConfigTransactionSandbox();
        File.WriteAllText(sandbox.ConfigPath, "[Mods]\nactive_mods=[ ]\n");
        var transaction = new DungeondraftConfigTransaction(new FixedTimeProvider());
        var plan = transaction.PlanSetup(sandbox.ConfigPath, sandbox.ManagedModsDirectory);
        File.WriteAllText(sandbox.ConfigPath, "user changed this");

        Assert.Throws<DungeondraftConfigException>(() => transaction.Apply(plan));

        Assert.Equal("user changed this", File.ReadAllText(sandbox.ConfigPath));
        Assert.Empty(Directory.GetFiles(sandbox.Root, "*.ddai-backup-*.ini"));
        AssertNoStages(sandbox.Root);
    }

    [Fact]
    public void Apply_ConcurrentChangeAfterFirstCheckRemovesItsBackup()
    {
        using var sandbox = new ConfigTransactionSandbox();
        File.WriteAllText(sandbox.ConfigPath, "[Mods]\nactive_mods=[ ]\n");
        var transaction = new DungeondraftConfigTransaction(new CallbackTimeProvider(() =>
            File.WriteAllText(sandbox.ConfigPath, "late user change")));
        var plan = transaction.PlanSetup(sandbox.ConfigPath, sandbox.ManagedModsDirectory);

        Assert.Throws<DungeondraftConfigException>(() => transaction.Apply(plan));

        Assert.Equal("late user change", File.ReadAllText(sandbox.ConfigPath));
        Assert.Empty(Directory.GetFiles(sandbox.Root, "*.ddai-backup-*.ini"));
        AssertNoStages(sandbox.Root);
    }

    [Fact]
    public void Apply_IdempotentPlanStillRejectsAConcurrentChange()
    {
        using var sandbox = new ConfigTransactionSandbox();
        var configured = Encoding.UTF8.GetBytes(
            $"[Mods]\nactive_mods=[ \"{DungeondraftConfigEditor.DdaiModId}\" ]\nmods_directory={EncodePath(sandbox.ManagedModsDirectory)}\n");
        File.WriteAllBytes(sandbox.ConfigPath, configured);
        var transaction = new DungeondraftConfigTransaction(new FixedTimeProvider());
        var plan = transaction.PlanSetup(sandbox.ConfigPath, sandbox.ManagedModsDirectory);
        Assert.Equal(plan.OriginalBytes, plan.ReplacementBytes);
        File.WriteAllText(sandbox.ConfigPath, "late user change");

        Assert.Throws<DungeondraftConfigException>(() => transaction.Apply(plan));

        Assert.Equal("late user change", File.ReadAllText(sandbox.ConfigPath));
        Assert.Empty(Directory.GetFiles(sandbox.Root, "*.ddai-backup-*.ini"));
        AssertNoStages(sandbox.Root);
    }

    [Fact]
    public void Apply_LockedFileLeavesOriginalAndNoOrphans()
    {
        using var sandbox = new ConfigTransactionSandbox();
        var original = Encoding.UTF8.GetBytes("[Mods]\nactive_mods=[ ]\n");
        File.WriteAllBytes(sandbox.ConfigPath, original);
        var transaction = new DungeondraftConfigTransaction(new FixedTimeProvider());
        var plan = transaction.PlanSetup(sandbox.ConfigPath, sandbox.ManagedModsDirectory);

        using (new FileStream(sandbox.ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<DungeondraftConfigException>(() => transaction.Apply(plan));
        }

        Assert.Equal(original, File.ReadAllBytes(sandbox.ConfigPath));
        Assert.Empty(Directory.GetFiles(sandbox.Root, "*.ddai-backup-*.ini"));
        AssertNoStages(sandbox.Root);
    }

    [Fact]
    public void Restore_ReturnsExactOriginalAndRejectsAnUnrelatedDestination()
    {
        using var sandbox = new ConfigTransactionSandbox();
        var original = Encoding.Unicode.GetPreamble().Concat(
            Encoding.Unicode.GetBytes("[Mods]\r\nactive_mods=[ \"Lievven.Snappy_Mod\" ]\r\n")).ToArray();
        File.WriteAllBytes(sandbox.ConfigPath, original);
        var transaction = new DungeondraftConfigTransaction(new FixedTimeProvider());
        var plan = transaction.PlanSetup(sandbox.ConfigPath, sandbox.ManagedModsDirectory);

        _ = transaction.Apply(plan);
        transaction.Restore(plan);

        Assert.Equal(original, File.ReadAllBytes(sandbox.ConfigPath));
        AssertNoStages(sandbox.Root);

        File.WriteAllText(sandbox.ConfigPath, "unrelated user edit");
        Assert.Throws<DungeondraftConfigException>(() => transaction.Restore(plan));
        Assert.Equal("unrelated user edit", File.ReadAllText(sandbox.ConfigPath));
        AssertNoStages(sandbox.Root);
    }

    [Fact]
    public void Apply_UninstallBacksUpActivatedBytesAndPreservesUnrelatedMods()
    {
        using var sandbox = new ConfigTransactionSandbox();
        var activated = Encoding.UTF8.GetBytes(
            "[Mods]\nactive_mods=[ \"Other.Mod\", \"org.ddai.status_bridge\", \"Other.Mod\" ]\nmods_directory=\"C:\\\\Managed\"\n");
        File.WriteAllBytes(sandbox.ConfigPath, activated);
        var transaction = new DungeondraftConfigTransaction(new FixedTimeProvider());
        var ownership = new DungeondraftConfigOwnership(
            @"C:\Managed",
            PreviousModsDirectoryPresent: true,
            PreviousModsDirectoryLiteral: "\"D:\\\\Previous\"",
            DungeondraftConfigEditor.DdaiModId);

        var update = transaction.Apply(transaction.PlanUninstall(sandbox.ConfigPath, ownership));
        var result = Encoding.UTF8.GetString(File.ReadAllBytes(sandbox.ConfigPath));

        Assert.Equal(activated, File.ReadAllBytes(update.BackupPath!));
        Assert.Contains("active_mods=[ \"Other.Mod\", \"Other.Mod\" ]", result);
        Assert.Contains("mods_directory=\"D:\\\\Previous\"", result);
        AssertNoStages(sandbox.Root);
    }

    [Fact]
    public void PlanSetup_RequiresAnExistingRegularConfigFile()
    {
        using var sandbox = new ConfigTransactionSandbox();
        var transaction = new DungeondraftConfigTransaction(new FixedTimeProvider());

        Assert.Throws<DungeondraftConfigException>(() =>
            transaction.PlanSetup(sandbox.ConfigPath, sandbox.ManagedModsDirectory));

        Directory.CreateDirectory(sandbox.ConfigPath);
        Assert.Throws<DungeondraftConfigException>(() =>
            transaction.PlanSetup(sandbox.ConfigPath, sandbox.ManagedModsDirectory));
    }

    private static void AssertNoStages(string directory)
    {
        Assert.Empty(Directory.GetFiles(directory, "*.ddai-stage-*.tmp"));
        Assert.Empty(Directory.GetFiles(directory, "*.ddai-rollback-*.tmp"));
    }

    private static string EncodePath(string path) =>
        "\"" + Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)
            .Replace("\\", "\\\\", StringComparison.Ordinal) + "\"";

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 8, 9, 20, 0, 0, TimeSpan.Zero);
    }

    private sealed class CallbackTimeProvider(Action callback) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            callback();
            return new DateTimeOffset(2026, 8, 9, 20, 0, 0, TimeSpan.Zero);
        }
    }

    private sealed class ConfigTransactionSandbox : IDisposable
    {
        public ConfigTransactionSandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "ddai-config-transaction-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            ConfigPath = Path.Combine(Root, "config.ini");
            ManagedModsDirectory = Path.Combine(Root, "DungeondraftMods");
        }

        public string Root { get; }
        public string ConfigPath { get; }
        public string ManagedModsDirectory { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
