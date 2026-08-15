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

        // No reflection against the host, ever. Probing Dungeondraft 1.2.0.1 with
        // get_property_list() plus Object.get("ModMapData") crashed the application:
        // the dump showed a repeating GDScript interpreter cycle running the stack out
        // (access violation c0000005). Both calls can reach a host-defined _get() or
        // _get_property_list() handler and which one recursed was never isolated, so
        // the probe uses direct member access only -- the pattern the shipping
        // Lievven.Snappy_Mod proves works on this exact build.
        Assert.DoesNotContain("get_property_list", probe, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"global_object\s*\.\s*get\s*\(", probe);
        Assert.Contains("global_object.ModMapData", probe, StringComparison.Ordinal);

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
            Assert.Contains("DDAI_MAP_IDENTITY_NO_HOST_REFLECTION:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_MAP_IDENTITY_READ_ONLY:True", output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
            if (Directory.Exists(userDataRoot)) Directory.Delete(userDataRoot, recursive: true);
        }
    }

    [Fact]
    public void CurrentMapIdResolvesDurableIdentityAndNeverSessionState()
    {
        var bridge = ReadBridge();
        var body = FunctionBody(bridge, "_current_map_id");

        // Session-derived identity was the whole defect: map_id changed on every
        // mod reload while the map was byte-identical.
        Assert.DoesNotContain("_session_id", body, StringComparison.Ordinal);
        Assert.DoesNotContain("get_instance_id", body, StringComparison.Ordinal);
        Assert.Contains("_resolved_map_uuid", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveMapIdentityAdoptsAStoredUuidAndMintsWithoutWriting()
    {
        var bridge = ReadBridge();
        var body = FunctionBody(bridge, "_resolve_map_identity");

        Assert.Contains("_read_stored_map_uuid", body, StringComparison.Ordinal);
        Assert.Contains("\"bound\"", body, StringComparison.Ordinal);
        Assert.Contains("\"pending\"", body, StringComparison.Ordinal);
        Assert.Contains("\"unbound\"", body, StringComparison.Ordinal);

        // Resolution runs on read-only paths, so it must never write into the
        // host store. Binding is a separate, mutation-time step.
        Assert.DoesNotMatch(@"ModMapData\s*\[[^\]]*\]\s*=(?!=)", body);
    }

    [Fact]
    public void ReadStoredMapUuidUsesDirectMemberAccessOnly()
    {
        var bridge = ReadBridge();
        var body = FunctionBody(bridge, "_read_stored_map_uuid");

        // Same rule as the certifier probe: reflecting on the host crashed it.
        Assert.Contains("Global.ModMapData", body, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"Global\s*\.\s*get\s*\(", body);
        Assert.DoesNotContain("get_property_list", body, StringComparison.Ordinal);
    }

    [Fact]
    public void CertifyOperationRoutesDoesNotCaptureMapDataProbe()
    {
        var bridge = ReadBridge();
        var certifyRoutes = FunctionBody(bridge, "_certify_operation_routes");

        // _certify_operation_routes() runs exactly once per mod load, from
        // start() -- not once per status call. ModMapData is per-map state:
        // start() can run before any map is open, or against a different map
        // than the one currently open. Capturing the probe here would freeze
        // a pre-map or wrong-map reading for the entire process lifetime, so
        // this function must not reference probe_map_data at all -- the
        // probe belongs at status time (see
        // StatusPayloadComputesMapDataProbeFreshOnEveryCall below).
        Assert.DoesNotContain("probe_map_data", certifyRoutes, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusPayloadComputesMapDataProbeFreshOnEveryCall()
    {
        var bridge = ReadBridge();
        var statusPayload = FunctionBody(bridge, "_status_payload");

        // The probe must be recomputed on every status call, exactly like
        // map_id/dimensions/level_ids are computed via function calls in the
        // same returned dictionary rather than read from a field cached once
        // at mod load. Asserting a direct call here (not a duplicate() of a
        // stored field) is what would catch a regression back to the
        // capture-once-at-start bug.
        Assert.Contains("\"map_data_probe\": _current_map_data_probe(),", statusPayload, StringComparison.Ordinal);

        var probeFunction = FunctionBody(bridge, "_current_map_data_probe");
        Assert.Contains("has_method(\"probe_map_data\")", probeFunction, StringComparison.Ordinal);
        Assert.Contains("certifier.probe_map_data(Global)", probeFunction, StringComparison.Ordinal);

        // Every failure path (script fails to load, certifier fails to
        // instantiate, or an older certifier lacking probe_map_data) must
        // return an empty dictionary -- never a fabricated "available: false"
        // record -- because {} means "the probe did not run", a different
        // fact from "the probe ran and found nothing". Scanning the whole
        // function body (rather than matching only a direct
        // "_map_data_probe = {...}" assignment, as an earlier version of
        // this test did) also catches a two-step fabrication such as
        // "var default_record = {\"available\": false, ...}" followed by
        // "return default_record". This still would not catch a fabricated
        // record built in a *different* function and merely called from
        // here; guarding against that is left as a known residual gap rather
        // than contorting a source-text test further.
        Assert.DoesNotMatch(@"""available""\s*:\s*false", probeFunction);
        Assert.Contains("return {}", probeFunction, StringComparison.Ordinal);

        // No stale cached field should linger now that the probe is computed
        // fresh on every call instead of once at mod load.
        Assert.DoesNotContain("var _map_data_probe", bridge, StringComparison.Ordinal);
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
