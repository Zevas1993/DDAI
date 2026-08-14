using System.Diagnostics;

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
        Assert.Contains("discovery", probe, StringComparison.Ordinal);
        Assert.Contains("is_dictionary", probe, StringComparison.Ordinal);
        Assert.Contains("ddai_key_present", probe, StringComparison.Ordinal);
        Assert.Contains("stored_uuid_valid", probe, StringComparison.Ordinal);
        Assert.Contains("foreign_key_count", probe, StringComparison.Ordinal);
        Assert.Contains("reason", probe, StringComparison.Ordinal);

        foreach (var forbidden in new[] { "erase(", "clear(", ".Save(", "queue_free" })
        {
            Assert.DoesNotContain(forbidden, probe, StringComparison.Ordinal);
        }

        // GDScript Dictionaries are reference types, so the natural mutation
        // vector for Global.ModMapData is subscript/dot assignment into the
        // fetched dictionary itself ("data[...] = " or "data.foo = "), not a
        // named method call. Guard the fetched dictionary ("data") and the
        // DDAI-owned nested dictionary ("owned") without flagging legitimate
        // reads like "data.has(...)" or "var owned = data[...]".
        foreach (var variable in new[] { "data", "owned" })
        {
            Assert.DoesNotMatch(variable + @"\s*(\[[^\]]*\]|\.[A-Za-z_][A-Za-z0-9_]*)\s*=(?!=)", probe);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task PinnedGodot353ProbesMapDataShapesReadOnly()
    {
        var repositoryRoot = FindRepositoryRoot();
        var godotPath = Path.Combine(
            repositoryRoot,
            "tools", "godot-3.5.3",
            "Godot_v3.5.3-stable_win64.exe");
        var fixtureRoot = Path.Combine(
            repositoryRoot,
            "tests", "DDAI.Core.Tests", "Executors", "GodotFixtures", "MapIdentity");
        Assert.True(File.Exists(godotPath), $"Pinned Godot 3.5.3 runtime not found: {godotPath}");

        var temporaryRoot = Path.Combine(Path.GetTempPath(), "ddai-map-identity-" + Guid.NewGuid().ToString("N"));
        var applicationName = "DDAI Map Identity " + Guid.NewGuid().ToString("N");
        var userDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Godot", "app_userdata", applicationName);
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            foreach (var fixture in Directory.EnumerateFiles(fixtureRoot))
            {
                var destination = Path.Combine(temporaryRoot, Path.GetFileName(fixture));
                if (Path.GetFileName(fixture).Equals("project.godot", StringComparison.Ordinal))
                {
                    File.WriteAllText(
                        destination,
                        File.ReadAllText(fixture).Replace(
                            "config/name=\"DDAI Map Identity\"",
                            $"config/name=\"{applicationName}\"",
                            StringComparison.Ordinal));
                }
                else
                {
                    File.Copy(fixture, destination);
                }
            }

            File.Copy(
                Path.Combine(repositoryRoot, "mods", "DDAI", "scripts", "ddai_operation_certifier.gd"),
                Path.Combine(temporaryRoot, "ddai_operation_certifier.gd"));

            using var process = Process.Start(new ProcessStartInfo(godotPath)
            {
                WorkingDirectory = temporaryRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "--path", temporaryRoot, "--no-window", "--scene", "res://main.tscn" },
            });
            Assert.NotNull(process);
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var exited = process.WaitForExit(30_000);
            if (!exited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            var output = await outputTask;
            var error = await errorTask;
            Assert.True(exited, "Pinned Godot map identity harness timed out.");
            Assert.True(process.ExitCode == 0, $"Godot exited {process.ExitCode}.\nstdout:\n{output}\nstderr:\n{error}");
            Assert.True(string.IsNullOrEmpty(error), $"Godot stderr:\n{error}");
            Assert.Contains("DDAI_MAP_IDENTITY_ABSENT_CLOSED:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_MAP_IDENTITY_NON_DICTIONARY_CLOSED:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_MAP_IDENTITY_FOREIGN_ONLY_COUNTED:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_MAP_IDENTITY_DDAI_KEY_COUNTED:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_MAP_IDENTITY_STRING_OWNED_INVALID:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_MAP_IDENTITY_MALFORMED_UUID_REJECTED:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_MAP_IDENTITY_DIRECT_GET_DISCOVERY:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_MAP_IDENTITY_READ_ONLY:True", output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
            if (Directory.Exists(userDataRoot)) Directory.Delete(userDataRoot, recursive: true);
        }
    }

    [Fact]
    public void CertifyOperationRoutesCapturesMapDataProbeGuardedByHasMethod()
    {
        var bridge = ReadBridge();
        var certifyRoutes = FunctionBody(bridge, "_certify_operation_routes");

        // The certifier instance must be asked for the map-data probe before
        // _certify_operation_routes returns, guarded by has_method so bridges
        // paired with an older certifier script (no probe_map_data) do not
        // crash on a missing method.
        Assert.Matches(
            @"if\s+certifier\.has_method\(""probe_map_data""\)\s*:\s*\n\s*_map_data_probe\s*=\s*certifier\.probe_map_data\(Global\)",
            certifyRoutes);

        Assert.Contains("var _map_data_probe = {}", bridge, StringComparison.Ordinal);
        Assert.Contains("\"map_data_probe\": _map_data_probe.duplicate(true),", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void MapDataProbeStateIsNeverFabricatedWithADefaultRecord()
    {
        var bridge = ReadBridge();

        // _map_data_probe must start empty. An empty dictionary means "the
        // probe did not run" -- a different fact from "the probe ran and
        // found nothing" -- so no fallback dictionary containing a literal
        // "available": false may ever be assigned to it.
        Assert.Contains("var _map_data_probe = {}", bridge, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"_map_data_probe\s*=\s*\{[^}]*""available""\s*:\s*false", bridge);
    }

    private static string ReadCertifier()
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(root, "mods", "DDAI", "scripts", "ddai_operation_certifier.gd"));
    }

    private static string ReadBridge()
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(root, "mods", "DDAI", "scripts", "ddai_bridge.gd"));
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
