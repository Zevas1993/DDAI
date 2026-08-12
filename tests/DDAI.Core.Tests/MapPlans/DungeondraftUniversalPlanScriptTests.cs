using System.Diagnostics;
using System.Globalization;
using DDAI.Core.MapPlans;
using DDAI.Core.MapPlans.Operations;

namespace DDAI.Core.Tests.MapPlans;

public sealed class DungeondraftUniversalPlanScriptTests
{
    [Fact]
    public void V2PlansRouteToTheUniversalStateMachineBeforeLegacyValidation()
    {
        var script = ReadBridge();
        var body = FunctionBody(script, "_advance_apply_plan_claim");

        Assert.Contains("request.payload.get(\"schema_version\", \"\") == \"2.0\"", body, StringComparison.Ordinal);
        Assert.Contains("_advance_universal_plan_claim", body, StringComparison.Ordinal);
        Assert.True(
            body.IndexOf("_advance_universal_plan_claim", StringComparison.Ordinal) <
            body.IndexOf("_validate_rectangular_room_plan", StringComparison.Ordinal));
    }

    [Fact]
    public void UniversalPlanValidationIsBoundedStrictAndRevisionLocked()
    {
        var script = ReadBridge();
        var body = FunctionBody(script, "_validate_universal_plan");

        foreach (var token in new[]
                 {
                     "MAXIMUM_UNIVERSAL_OPERATIONS", "MAXIMUM_UNIVERSAL_POINTS",
                     "expected_map_id", "base_revision", "expected_catalog_revision",
                     "coordinate_system", "operations", "operation_type", "wall_polyline",
                     "asset_ref", "level_id", "path", "color_rgba",
                 })
        {
            Assert.Contains(token, body, StringComparison.Ordinal);
        }

        Assert.Contains("_dictionary_has_exact_keys", body, StringComparison.Ordinal);
        Assert.Contains("operations.size() > MAXIMUM_UNIVERSAL_OPERATIONS", body, StringComparison.Ordinal);
        Assert.Contains("operations.size() == 0", body, StringComparison.Ordinal);
        Assert.Contains("points.size() > MAXIMUM_UNIVERSAL_POINTS", body, StringComparison.Ordinal);
    }

    [Fact]
    public void PreflightCorrelatesLiveMapCatalogAndCertifiedAssets()
    {
        var script = ReadBridge();
        var body = FunctionBody(script, "_preflight_universal_plan");

        foreach (var token in new[]
                 {
                     "_current_map_id()", "_map_job_revision", "_read_accepted_catalog",
                     "catalog_revision", "catalog_fingerprint", "resource_fingerprint",
                     "_resolve_live_asset", "_certified_operation_executors",
                     "Global.World.GetLevelByID", "Global.Editor.Tools",
                 })
        {
            Assert.Contains(token, body, StringComparison.Ordinal);
        }
        Assert.Contains("_current_level_ids().has(operation.level_id)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptedCatalogRecomputesTheCompleteCanonicalManifestFingerprint()
    {
        var script = ReadBridge();
        var body = FunctionBody(script, "_read_accepted_catalog");
        var validator = FunctionBody(script, "_catalog_manifest_is_valid");

        Assert.Contains("_read_current_catalog_identity", body, StringComparison.Ordinal);
        Assert.Contains("_dictionary_has_exact_keys(manifest", validator, StringComparison.Ordinal);
        Assert.Contains("schema_version", validator, StringComparison.Ordinal);
        Assert.Contains("snapshot_at", validator, StringComparison.Ordinal);
        Assert.Contains("category_counts", validator, StringComparison.Ordinal);
        Assert.Contains("errors", validator, StringComparison.Ordinal);
        Assert.Contains("_catalog_manifest_fingerprint(manifest)", validator, StringComparison.Ordinal);
        Assert.Contains("chunk_file_names", validator, StringComparison.Ordinal);

        var identity = FunctionBody(script, "_catalog_identity_matches");
        Assert.Contains("_read_current_catalog_identity", identity, StringComparison.Ordinal);
    }

    [Fact]
    public void UniversalJobsPersistEveryNativeBoundaryAndRecoverFailClosed()
    {
        var script = ReadBridge();
        var body = FunctionBody(script, "_advance_universal_plan_claim");

        foreach (var token in new[]
                 {
                     "map-jobs", "map-completed", "prepared", "operation_applied",
                     "operation_observed", "reversing", "reversed", "outcome_unknown",
                     "committed", "_newly_prepared_job_keys", "_pending_operation_results",
                     "_pending_observation_results", "_pending_reversal_results",
                 })
        {
            Assert.Contains(token, body, StringComparison.Ordinal);
        }

        Assert.True(
            body.IndexOf("_write_map_job_journal", StringComparison.Ordinal) <
            body.IndexOf("_execute_operation", StringComparison.Ordinal));
        Assert.True(
            body.IndexOf("job.map_id != _current_map_id()", StringComparison.Ordinal) <
            body.IndexOf("job.state == \"operation_applied\"", StringComparison.Ordinal));
    }

    [Fact]
    public void DispatcherUsesOnlyExplicitCertifiedOperationFunctions()
    {
        var script = ReadBridge();
        var dispatcher = FunctionBody(script, "_execute_operation");
        var reversal = FunctionBody(script, "_reverse_operation");
        var reversalObservation = FunctionBody(script, "_observe_reversal");

        Assert.Contains("_certified_operation_executors", dispatcher, StringComparison.Ordinal);
        Assert.Contains("call_func", dispatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("call(operation.operation_type", dispatcher, StringComparison.Ordinal);
        Assert.Contains("_reverse_wall_polyline", reversal, StringComparison.Ordinal);
        Assert.Contains("Global.World.HasNodeID", reversalObservation, StringComparison.Ordinal);
        Assert.Contains("Global.World.GetNodeByID", reversalObservation, StringComparison.Ordinal);
        var wallExecutor = FunctionBody(script, "_execute_wall_polyline");
        Assert.Contains("Global.WorldUI.AddPolyPoint", wallExecutor, StringComparison.Ordinal);
        Assert.Contains("wall_tool.EndWall(operation.closed)", wallExecutor, StringComparison.Ordinal);
        Assert.Contains("Global.Editor.ActiveToolName == \"WallTool\"", wallExecutor, StringComparison.Ordinal);
        Assert.Contains("_cleanup_wall_tool(wall_tool, not wall_tool_was_active)", wallExecutor, StringComparison.Ordinal);
        Assert.DoesNotContain("level.Walls.AddWall", wallExecutor, StringComparison.Ordinal);
        Assert.DoesNotContain("GetNodeID()", wallExecutor, StringComparison.Ordinal);
        var observation = FunctionBody(script, "_observe_operation");
        Assert.Contains("level.Walls.get_children()", observation, StringComparison.Ordinal);
        Assert.Contains("inspected_wall_count > MAXIMUM_INSPECTION_STATE_ITEMS", observation, StringComparison.Ordinal);
        Assert.Contains("observed_node_counts[wall_id.value]", observation, StringComparison.Ordinal);
        Assert.Contains("observed_node_counts.get(int(node_id), 0) != 1", observation, StringComparison.Ordinal);
        Assert.Contains("registered_node_ids[int(node_id)] = true", observation, StringComparison.Ordinal);
        Assert.DoesNotContain("wall == registered_node", observation, StringComparison.Ordinal);
        Assert.DoesNotContain("registered_nodes[int(node_id)] = registered_node", observation, StringComparison.Ordinal);
        Assert.DoesNotContain("Global.World.GetNodeByID", observation, StringComparison.Ordinal);
        var stateMachine = FunctionBody(script, "_advance_universal_plan_claim");
        Assert.Contains("var observation_result = _observe_operation(job.current_operation_node_ids)", stateMachine, StringComparison.Ordinal);
        Assert.Contains("_pending_observation_results[key] = observation_result", stateMachine, StringComparison.Ordinal);
        Assert.DoesNotContain("_pending_observation_results[key] = _observe_operation", stateMachine, StringComparison.Ordinal);
        Assert.DoesNotContain("registered_node.get_parent()", observation, StringComparison.Ordinal);
        Assert.True(
            observation.IndexOf("level.Walls.get_child_count()", StringComparison.Ordinal) <
            observation.IndexOf("level.Walls.get_children()", StringComparison.Ordinal));
    }

    [Fact]
    public void StatusAdvertisesOnlyTheActuallyCertifiedUniversalExecutor()
    {
        var script = ReadBridge();
        var body = FunctionBody(script, "_status_payload");

        Assert.Contains("\"map_id\": _current_map_id()", body, StringComparison.Ordinal);
        Assert.Contains("\"map_job_revision\": _map_job_revision", body, StringComparison.Ordinal);
        Assert.Contains("\"certified_operation_types\": [\"wall_polyline\"]", body, StringComparison.Ordinal);
        Assert.Contains("\"level_ids\": _current_level_ids()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedJobCleanupRequiresBoundResponseAndJournalEvidence()
    {
        var body = FunctionBody(ReadBridge(), "_cleanup_completed_map_job");

        foreach (var token in new[]
                 {
                     "_dictionary_has_exact_keys(completed", "response_fingerprint",
                     "journal_fingerprint",
                     "completed.response_text.sha256_text()", "job.get(\"canonical_response\"",
                     "_validate_response", "_response_text_matches",
                 })
        {
            Assert.Contains(token, body, StringComparison.Ordinal);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ExactGodot353ExecutesObservesReversesAndRecoversUniversalWallJob()
    {
        var repositoryRoot = FindRepositoryRoot();
        var godotPath = Path.Combine(
            repositoryRoot,
            "artifacts", "rectangular-room", "tooling", "godot-3.5.3",
            "Godot_v3.5.3-stable_win64.exe");
        var fixtureRoot = Path.Combine(
            repositoryRoot,
            "tests", "DDAI.Core.Tests", "MapPlans", "GodotFixtures", "UniversalPlanExecutor");
        Assert.True(File.Exists(godotPath), $"Pinned Godot 3.5.3 runtime not found: {godotPath}");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "ddai-universal-executor-" + Guid.NewGuid().ToString("N"));
        var applicationName = "DDAI Universal Executor Harness " + Guid.NewGuid().ToString("N");
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
                            "config/name=\"DDAI Universal Executor Harness\"",
                            $"config/name=\"{applicationName}\"",
                            StringComparison.Ordinal));
                }
                else
                {
                    File.Copy(fixture, destination);
                }
            }

            File.Copy(
                Path.Combine(repositoryRoot, "mods", "DDAI", "scripts", "ddai_bridge.gd"),
                Path.Combine(temporaryRoot, "ddai_bridge.gd"));
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
            Assert.True(exited, "Pinned Godot universal executor harness timed out.");
            Assert.True(process.ExitCode == 0, $"Godot exited {process.ExitCode}.\nstdout:\n{output}\nstderr:\n{error}");
            Assert.True(string.IsNullOrEmpty(error), $"Godot stderr:\n{error}");
            foreach (var receipt in new[]
                     {
                         "DDAI_UNIVERSAL_FINGERPRINT:True",
                         "DDAI_UNIVERSAL_VALIDATION_PREFLIGHT:True",
                         "DDAI_UNIVERSAL_APPLY_OBSERVE:True",
                         "DDAI_UNIVERSAL_COMPLETION_TAMPER_BLOCKED:True",
                         "DDAI_UNIVERSAL_REVERSAL:True",
                         "DDAI_UNIVERSAL_OBSERVE_FAILURE_REVERSED:True",
                         "DDAI_UNIVERSAL_DUPLICATE_AFTER_CLEANUP:True",
                         "DDAI_UNIVERSAL_REVERSAL_REVISION_STABLE:True",
                         "DDAI_UNIVERSAL_PARTIAL_REGISTRATION_UNKNOWN:True",
                         "DDAI_UNIVERSAL_WALL_TOOL_STATE_RESTORED:True",
                         "DDAI_UNIVERSAL_NODE_ID_COLLISION_RECOVERED:True",
                         "DDAI_UNIVERSAL_PREPARED_RECOVERY:True",
                         "DDAI_UNIVERSAL_STRICT_FAILURES:True",
                         "DDAI_UNIVERSAL_CATALOG_MANIFEST_INTEGRITY:True",
                         "DDAI_UNIVERSAL_UNIQUE_ASSET_RESOLUTION:True",
                         "DDAI_UNIVERSAL_PREFLIGHT_CORRELATION:True",
                         "DDAI_UNIVERSAL_CATALOG_RACE_CORRELATION:True",
                         "DDAI_UNIVERSAL_JOURNAL_INVARIANTS:True",
                     })
            {
                Assert.Contains(receipt, output, StringComparison.Ordinal);
            }

            var json = ReceiptValue(output, "DDAI_UNIVERSAL_PLAN_JSON:");
            var actualFingerprint = ReceiptValue(output, "DDAI_UNIVERSAL_PLAN_FINGERPRINT:");
            var plan = MapPlanJson.Deserialize(json);
            var wall = Assert.IsType<WallPolylineOperation>(Assert.Single(plan.Operations!));
            var exactPlan = plan with
            {
                Operations =
                [
                    wall with
                    {
                        Path = new GridPolyline(
                        [
                            new GridPoint(1.23456789012345, 1.0000001),
                            new GridPoint(8.125, 1),
                        ]),
                    },
                ],
            };
            Assert.Equal(MapPlanJson.Fingerprint(exactPlan), actualFingerprint);
            var numberTexts = ReceiptValue(output, "DDAI_UNIVERSAL_DOUBLE_BITS:");
            Assert.Equal(
                string.Join('|', new[] { 0d, 1e-300d, 1e-18d, 0.0000001d, 0.00001d, 0.1d, 1.0000001d, 1.23456789012345d, 40d, 2147483647d }
                    .Select(value => unchecked((ulong)BitConverter.DoubleToInt64Bits(value)).ToString("x16", CultureInfo.InvariantCulture))),
                numberTexts);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
            if (Directory.Exists(userDataRoot))
            {
                Directory.Delete(userDataRoot, recursive: true);
            }
        }
    }

    private static string ReadBridge() =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "mods", "DDAI", "scripts", "ddai_bridge.gd"));

    private static string FunctionBody(string script, string functionName)
    {
        var start = script.IndexOf("func " + functionName + "(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Function {functionName} was not found.");
        var next = script.IndexOf("\nfunc ", start + 1, StringComparison.Ordinal);
        return next < 0 ? script[start..] : script[start..next];
    }

    private static string ReceiptValue(string output, string prefix)
    {
        var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Single(value => value.StartsWith(prefix, StringComparison.Ordinal));
        return line[prefix.Length..];
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "DDAI.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
