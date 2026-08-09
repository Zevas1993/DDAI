using System.Text.Json;

namespace DDAI.Core.Tests;

public sealed class DungeondraftModPackageTests
{
    [Fact]
    public void Package_TargetsDungeondraft1201AndImplementsMailboxStatusBridge()
    {
        var modRoot = Path.Combine(FindRepositoryRoot(), "mods", "DDAI");
        var manifestPath = Path.Combine(modRoot, "ddai_bridge.ddmod");
        var scriptPath = Path.Combine(modRoot, "scripts", "ddai_bridge.gd");

        Assert.True(File.Exists(manifestPath), "The redistributable DDAI .ddmod manifest must be present.");
        Assert.True(File.Exists(scriptPath), "The redistributable DDAI GDScript tool must be present.");

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.Equal("DDAI Status Bridge", manifest.RootElement.GetProperty("name").GetString());
        Assert.Equal("org.ddai.status_bridge", manifest.RootElement.GetProperty("unique_id").GetString());
        Assert.Equal("1.2.0.1", manifest.RootElement.GetProperty("dd_version").GetString());

        var script = File.ReadAllText(scriptPath);
        Assert.Contains("func start():", script, StringComparison.Ordinal);
        Assert.Contains("func update(delta):", script, StringComparison.Ordinal);
        Assert.Contains("user://ddai", script, StringComparison.Ordinal);
        Assert.Contains("unsupported_command", script, StringComparison.Ordinal);
        Assert.Contains("malformed_request", script, StringComparison.Ordinal);
        Assert.DoesNotContain("TCPServer", script, StringComparison.Ordinal);
        Assert.DoesNotContain("HTTPClient", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PacketPeer", script, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "README.md")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src", "DDAI.Core")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the DDAI repository root.");
    }
}
