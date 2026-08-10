using System.Security.Cryptography;
using System.Text;

namespace DDAI.App.Tests;

public sealed class DungeondraftModConsolidatorTests
{
    [Fact]
    public void Apply_CopiesCompatibilityPatchedSourcePreservesItAndIsIdempotent()
    {
        using var sandbox = new ConsolidationSandbox();
        var sourceBefore = Snapshot(sandbox.SourceDirectory);
        var consolidator = new DungeondraftModConsolidator();
        var plan = consolidator.PlanCustomSnap(sandbox.SourceDirectory, sandbox.ManagedRoot);

        var first = consolidator.Apply(plan);
        var second = consolidator.Apply(plan);

        Assert.Equal("copied", first.State);
        Assert.True(first.Changed);
        Assert.NotNull(first.Receipt);
        Assert.Equal("already_current", second.State);
        Assert.False(second.Changed);
        Assert.Equal(sourceBefore, Snapshot(sandbox.SourceDirectory));
        Assert.NotEqual(sourceBefore, Snapshot(sandbox.DestinationDirectory));
        Assert.Contains("data.empty()", File.ReadAllText(sandbox.SourceScriptPath), StringComparison.Ordinal);
        Assert.DoesNotContain("data.empty()", File.ReadAllText(sandbox.DestinationScriptPath), StringComparison.Ordinal);
        Assert.Contains("data.size() == 0", File.ReadAllText(sandbox.DestinationScriptPath), StringComparison.Ordinal);
        Assert.True(consolidator.IsCurrent(first.Receipt!));
        AssertNoStages(sandbox.ManagedRoot);
    }

    [Fact]
    public void Apply_UpgradesExactLegacyCopyToCompatibilityPatchedCopy()
    {
        using var sandbox = new ConsolidationSandbox();
        var sourceBefore = Snapshot(sandbox.SourceDirectory);
        CopyTree(sandbox.SourceDirectory, sandbox.DestinationDirectory);
        Assert.Equal(sourceBefore, Snapshot(sandbox.DestinationDirectory));
        var consolidator = new DungeondraftModConsolidator();

        var first = consolidator.Apply(
            consolidator.PlanCustomSnap(sandbox.SourceDirectory, sandbox.ManagedRoot));
        var second = consolidator.Apply(
            consolidator.PlanCustomSnap(sandbox.SourceDirectory, sandbox.ManagedRoot));

        Assert.Equal("updated", first.State);
        Assert.True(first.Changed);
        Assert.Equal("already_current", second.State);
        Assert.Equal(sourceBefore, Snapshot(sandbox.SourceDirectory));
        Assert.Contains("data.size() == 0", File.ReadAllText(sandbox.DestinationScriptPath), StringComparison.Ordinal);
        AssertNoStages(sandbox.ManagedRoot);
        Assert.Empty(Directory.GetDirectories(sandbox.ManagedRoot, ".custom_snap.ddai-backup-*"));
    }

    [Fact]
    public void PlanCustomSnap_RejectsMissingAmbiguousAndForeignManifests()
    {
        using var sandbox = new ConsolidationSandbox();
        var consolidator = new DungeondraftModConsolidator();
        File.Delete(sandbox.ManifestPath);
        Assert.Throws<DungeondraftConfigException>(() =>
            consolidator.PlanCustomSnap(sandbox.SourceDirectory, sandbox.ManagedRoot));

        sandbox.WriteManifest(DungeondraftConfigEditor.CustomSnapModId);
        File.WriteAllText(
            Path.Combine(sandbox.SourceDirectory, "second.ddmod"),
            "{\"unique_id\":\"another.mod\"}");
        Assert.Throws<DungeondraftConfigException>(() =>
            consolidator.PlanCustomSnap(sandbox.SourceDirectory, sandbox.ManagedRoot));

        File.Delete(Path.Combine(sandbox.SourceDirectory, "second.ddmod"));
        sandbox.WriteManifest("foreign.mod");
        Assert.Throws<DungeondraftConfigException>(() =>
            consolidator.PlanCustomSnap(sandbox.SourceDirectory, sandbox.ManagedRoot));
    }

    [Fact]
    public void PlanCustomSnap_RejectsUnsupportedOrAmbiguousCompatibilityTarget()
    {
        using var sandbox = new ConsolidationSandbox();
        var consolidator = new DungeondraftModConsolidator();
        File.WriteAllText(sandbox.SourceScriptPath, "extends Node\n");
        Assert.Throws<DungeondraftConfigException>(() =>
            consolidator.PlanCustomSnap(sandbox.SourceDirectory, sandbox.ManagedRoot));

        File.WriteAllText(
            sandbox.SourceScriptPath,
            "func load_local_settings():\n    data.empty()\n    data.empty()\n");
        Assert.Throws<DungeondraftConfigException>(() =>
            consolidator.PlanCustomSnap(sandbox.SourceDirectory, sandbox.ManagedRoot));
    }

    [Fact]
    public void Apply_RejectsConflictingDestinationWithoutChangingEitherTree()
    {
        using var sandbox = new ConsolidationSandbox();
        var consolidator = new DungeondraftModConsolidator();
        var sourceBefore = Snapshot(sandbox.SourceDirectory);
        Directory.CreateDirectory(sandbox.DestinationDirectory);
        File.WriteAllText(Path.Combine(sandbox.DestinationDirectory, "foreign.txt"), "keep");
        var destinationBefore = Snapshot(sandbox.DestinationDirectory);
        Assert.Throws<DungeondraftConfigException>(() =>
            consolidator.PlanCustomSnap(sandbox.SourceDirectory, sandbox.ManagedRoot));

        Assert.Equal(sourceBefore, Snapshot(sandbox.SourceDirectory));
        Assert.Equal(destinationBefore, Snapshot(sandbox.DestinationDirectory));
        AssertNoStages(sandbox.ManagedRoot);
    }

    [Fact]
    public void Apply_RejectsSourceChangedAfterPlanningAndCleansStage()
    {
        using var sandbox = new ConsolidationSandbox();
        var consolidator = new DungeondraftModConsolidator();
        var plan = consolidator.PlanCustomSnap(sandbox.SourceDirectory, sandbox.ManagedRoot);
        File.AppendAllText(Path.Combine(sandbox.SourceDirectory, "scripts", "snappy_mod.gd"), "\nchanged");

        Assert.Throws<DungeondraftConfigException>(() => consolidator.Apply(plan));

        Assert.False(Directory.Exists(sandbox.DestinationDirectory));
        AssertNoStages(sandbox.ManagedRoot);
    }

    [Fact]
    public void Apply_RejectsForgedDestinationAndTraversalEntry()
    {
        using var sandbox = new ConsolidationSandbox();
        var consolidator = new DungeondraftModConsolidator();
        var plan = consolidator.PlanCustomSnap(sandbox.SourceDirectory, sandbox.ManagedRoot);
        var outside = Path.Combine(sandbox.Root, "outside");

        Assert.Throws<DungeondraftConfigException>(() =>
            consolidator.Apply(plan with { DestinationDirectory = outside }));
        Assert.Throws<DungeondraftConfigException>(() =>
            consolidator.Apply(plan with
            {
                Files = [.. plan.Files, new DungeondraftModFile("..\\escape.txt", 1, new string('0', 64))],
            }));

        Assert.False(Directory.Exists(outside));
        AssertNoStages(sandbox.ManagedRoot);
    }

    [Fact]
    public void PlanCustomSnap_RejectsNestedReparsePointWhenSupported()
    {
        using var sandbox = new ConsolidationSandbox();
        var external = Path.Combine(sandbox.Root, "external.txt");
        var link = Path.Combine(sandbox.SourceDirectory, "linked.txt");
        File.WriteAllText(external, "external");
        try
        {
            File.CreateSymbolicLink(link, external);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        Assert.Throws<DungeondraftConfigException>(() =>
            new DungeondraftModConsolidator().PlanCustomSnap(sandbox.SourceDirectory, sandbox.ManagedRoot));
    }

    [Fact]
    public void IsCurrent_ReturnsFalseAfterCopiedContentChanges()
    {
        using var sandbox = new ConsolidationSandbox();
        var consolidator = new DungeondraftModConsolidator();
        var receipt = consolidator.Apply(
            consolidator.PlanCustomSnap(sandbox.SourceDirectory, sandbox.ManagedRoot)).Receipt!;
        File.AppendAllText(Path.Combine(sandbox.DestinationDirectory, "README.md"), "changed");

        Assert.False(consolidator.IsCurrent(receipt));
    }

    private static SortedDictionary<string, string> Snapshot(string directory) =>
        new(
            Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(
                path => Path.GetRelativePath(directory, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal),
            StringComparer.Ordinal);

    private static void AssertNoStages(string managedRoot)
    {
        Assert.Empty(Directory.GetDirectories(managedRoot, ".custom_snap.ddai-stage-*"));
        Assert.Empty(Directory.GetFiles(managedRoot, ".custom_snap.ddai-stage-*"));
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (var sourcePath in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destinationPath = Path.Combine(destination, Path.GetRelativePath(source, sourcePath));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }
    }

    private sealed class ConsolidationSandbox : IDisposable
    {
        public ConsolidationSandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "ddai-mod-consolidator-tests", Guid.NewGuid().ToString("N"));
            SourceDirectory = Path.Combine(Root, "source", "custom_snap");
            ManagedRoot = Path.Combine(Root, "managed");
            Directory.CreateDirectory(Path.Combine(SourceDirectory, "scripts"));
            Directory.CreateDirectory(Path.Combine(SourceDirectory, "icons"));
            Directory.CreateDirectory(ManagedRoot);
            ManifestPath = Path.Combine(SourceDirectory, "snappy_mod.ddmod");
            WriteManifest(DungeondraftConfigEditor.CustomSnapModId);
            File.WriteAllText(
                SourceScriptPath,
                "func load_local_settings():\n    var data = Global.ModMapData[TOOL_ID]\n    if data == null or data.empty():\n        return\n");
            File.WriteAllBytes(Path.Combine(SourceDirectory, "icons", "snap.png"), [0x89, 0x50, 0x4E, 0x47]);
            File.WriteAllText(Path.Combine(SourceDirectory, "README.md"), "Custom Snap", Encoding.UTF8);
        }

        public string Root { get; }
        public string SourceDirectory { get; }
        public string ManagedRoot { get; }
        public string ManifestPath { get; }
        public string DestinationDirectory => Path.Combine(ManagedRoot, "custom_snap");
        public string SourceScriptPath => Path.Combine(SourceDirectory, "scripts", "snappy_mod.gd");
        public string DestinationScriptPath => Path.Combine(DestinationDirectory, "scripts", "snappy_mod.gd");

        public void WriteManifest(string uniqueId) =>
            File.WriteAllText(
                ManifestPath,
                $"{{\"name\":\"Custom Snap Mod\",\"unique_id\":\"{uniqueId}\",\"dd_version\":\"1.1.0.6\"}}");

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
