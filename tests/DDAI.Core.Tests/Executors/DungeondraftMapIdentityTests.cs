namespace DDAI.Core.Tests.Executors;

public sealed class DungeondraftMapIdentityTests
{
    [Fact]
    public void MapDataProbeIsReadOnlyAndReturnsAClosedShape()
    {
        var script = ReadCertifier();
        var probe = FunctionBody(script, "probe_map_data");

        Assert.Contains("ModMapData", probe, StringComparison.Ordinal);
        Assert.Contains("available", probe, StringComparison.Ordinal);
        Assert.Contains("is_dictionary", probe, StringComparison.Ordinal);
        Assert.Contains("ddai_key_present", probe, StringComparison.Ordinal);
        Assert.Contains("stored_uuid_valid", probe, StringComparison.Ordinal);
        Assert.Contains("foreign_key_count", probe, StringComparison.Ordinal);
        Assert.Contains("reason", probe, StringComparison.Ordinal);

        foreach (var forbidden in new[] { "erase(", "clear(", ".Save(", "queue_free" })
        {
            Assert.DoesNotContain(forbidden, probe, StringComparison.Ordinal);
        }
    }

    private static string ReadCertifier()
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(root, "mods", "DDAI", "scripts", "ddai_operation_certifier.gd"));
    }

    private static string FunctionBody(string script, string name)
    {
        var marker = "\nfunc " + name + "(";
        var start = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Function {name} not found.");
        var next = script.IndexOf("\nfunc ", start + marker.Length, StringComparison.Ordinal);
        return next < 0 ? script[start..] : script[start..next];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DDAI.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
