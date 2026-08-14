using System.Diagnostics;
using System.Globalization;
using DDAI.Core.MapPlans;
using DDAI.Core.MapPlans.Operations;

namespace DDAI.Core.Tests.MapPlans;

public sealed class DungeondraftUniversalPlanScriptTests
{
    [Fact]
    public void FreshRequestsCannotStarveBehindARecoverableProcessingClaim()
    {
        var body = FunctionBody(ReadBridge(), "_process_one_request");

        Assert.True(
            body.IndexOf("_claim_next_request()", StringComparison.Ordinal) <
            body.IndexOf("_claim_next_processing()", StringComparison.Ordinal));
    }

    [Fact]
    public void ProcessingClaimsRoundRobinSoOneBlockedClaimCannotStarvePreparedJobs()
    {
        var script = ReadBridge();
        var claim = FunctionBody(script, "_claim_next_processing");

        Assert.Contains("_processing_claim_cursor", script, StringComparison.Ordinal);
        Assert.Contains("file_names.sort()", claim, StringComparison.Ordinal);
        Assert.Contains("MAXIMUM_PROCESSING_CLAIMS", claim, StringComparison.Ordinal);
        Assert.Contains("(_processing_claim_cursor + offset) % file_names.size()", claim, StringComparison.Ordinal);
        Assert.Contains("_processing_claim_cursor = (index + 1) % file_names.size()", claim, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusCapturesTheOpenMapBaselineAfterDungeondraftFinishesStartup()
    {
        var script = ReadBridge();
        var start = FunctionBody(script, "start");
        var update = FunctionBody(script, "update");
        var status = FunctionBody(script, "_status_payload");

        Assert.Contains("if _asset_list_provider == null:\n\t\t_asset_list_provider =", start, StringComparison.Ordinal);
        Assert.Contains("if _texture_loader == null:\n\t\t_texture_loader =", start, StringComparison.Ordinal);
        Assert.DoesNotContain("_capture_map_job_state_fingerprint()", start, StringComparison.Ordinal);
        Assert.DoesNotContain("_recover_processing_claims()", start, StringComparison.Ordinal);
        Assert.DoesNotContain("_capture_map_job_state_fingerprint()", update, StringComparison.Ordinal);
        Assert.Contains("_capture_map_job_state_fingerprint()", status, StringComparison.Ordinal);
        Assert.Contains("_map_job_state_fingerprint = current_state_fingerprint", status, StringComparison.Ordinal);
        Assert.Contains("_claim_next_processing()", FunctionBody(script, "_process_one_request"), StringComparison.Ordinal);

        var capture = FunctionBody(script, "_capture_map_job_state_fingerprint");
        Assert.Contains("_inspect_map_payload", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("_terrain_state_hash", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("_cave_state_hash", capture, StringComparison.Ordinal);
    }


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
                     "coordinate_system", "operations", "_validate_universal_operation",
                 })
        {
            Assert.Contains(token, body, StringComparison.Ordinal);
        }

        Assert.Contains("_dictionary_has_exact_keys", body, StringComparison.Ordinal);
        Assert.Contains("operations.size() > MAXIMUM_UNIVERSAL_OPERATIONS", body, StringComparison.Ordinal);
        Assert.Contains("operations.size() == 0", body, StringComparison.Ordinal);
        var operationValidation = FunctionBody(script, "_validate_universal_operation");
        Assert.Contains("operation_type", operationValidation, StringComparison.Ordinal);
        Assert.Contains("wall_polyline", operationValidation, StringComparison.Ordinal);
        Assert.Contains("asset_ref", operationValidation, StringComparison.Ordinal);
        Assert.Contains("points.size() > MAXIMUM_UNIVERSAL_POINTS", operationValidation, StringComparison.Ordinal);
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
        Assert.Contains("_map_job_revision += 1", body, StringComparison.Ordinal);
        Assert.Contains("_map_job_state_fingerprint = current_state_fingerprint", body, StringComparison.Ordinal);
        Assert.True(
            body.IndexOf("_map_job_state_fingerprint = current_state_fingerprint", StringComparison.Ordinal) <
            body.IndexOf("_map_job_revision != int(plan.base_revision)", StringComparison.Ordinal));
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
    public void RecoveryCanCorrelateAValidatedHistoricalCatalogAfterPointerRotation()
    {
        var script = ReadBridge();
        var body = FunctionBody(script, "_read_catalog_identity_for_revision");

        Assert.Contains("/catalog/snapshots", body, StringComparison.Ordinal);
        Assert.Contains("MAXIMUM_CATALOG_SNAPSHOTS", body, StringComparison.Ordinal);
        Assert.Contains("var revision_marker = \"-\" + str(expected_revision) + \"-\"", body, StringComparison.Ordinal);
        Assert.Contains("snapshot_name.find(revision_marker) != -1", body, StringComparison.Ordinal);
        Assert.Contains("_catalog_manifest_is_valid", body, StringComparison.Ordinal);
        Assert.Contains("int(manifest.catalog_revision) == expected_revision", body, StringComparison.Ordinal);
        Assert.Contains("matched_fingerprint != manifest.catalog_fingerprint", body, StringComparison.Ordinal);
    }

    [Fact]
    public void PreflightMayUseTheLastValidatedSnapshotWhileLiveResolutionStillFailsClosed()
    {
        var script = ReadBridge();
        var identity = FunctionBody(script, "_read_current_catalog_identity");
        var preflight = FunctionBody(script, "_preflight_universal_plan");

        Assert.DoesNotContain("pointer.session_id != _session_id", identity, StringComparison.Ordinal);
        Assert.DoesNotContain("manifest.session_id != _session_id", identity, StringComparison.Ordinal);
        Assert.Contains("_resolve_live_asset", preflight, StringComparison.Ordinal);
        Assert.Contains("resource_fingerprint", preflight, StringComparison.Ordinal);
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
    public void UniversalPreflightFailuresResumeTheirJournalInsteadOfBlockingTheMailbox()
    {
        var body = FunctionBody(ReadBridge(), "_advance_universal_plan_claim");
        var journalCheck = body.IndexOf("directory.file_exists(_journal_path)", StringComparison.Ordinal);
        var completionCheck = body.IndexOf("directory.file_exists(completed_path)", StringComparison.Ordinal);
        var newJobCheck = body.IndexOf("not directory.file_exists(job_path)", StringComparison.Ordinal);

        Assert.True(journalCheck >= 0);
        Assert.True(journalCheck < completionCheck);
        Assert.True(journalCheck < newJobCheck);
        Assert.Contains("_read_validated_journal(_journal_path, request, request_fingerprint)", body, StringComparison.Ordinal);
        Assert.Contains("_advance_journaled_response", body, StringComparison.Ordinal);
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
        Assert.Contains("_observe_addressable_surface_reversal", reversalObservation, StringComparison.Ordinal);
        var wallExecutor = FunctionBody(script, "_execute_wall_polyline");
        Assert.Contains("Global.WorldUI.AddPolyPoint", wallExecutor, StringComparison.Ordinal);
        Assert.Contains("wall_tool.EndWall(operation.closed)", wallExecutor, StringComparison.Ordinal);
        Assert.Contains("Global.Editor.ActiveToolName == \"WallTool\"", wallExecutor, StringComparison.Ordinal);
        Assert.Contains("_cleanup_wall_tool(wall_tool, not wall_tool_was_active)", wallExecutor, StringComparison.Ordinal);
        Assert.DoesNotContain("level.Walls.AddWall", wallExecutor, StringComparison.Ordinal);
        Assert.DoesNotContain("GetNodeID()", wallExecutor, StringComparison.Ordinal);
        var observation = FunctionBody(script, "_observe_addressable_surface");
        var addressableNodes = FunctionBody(script, "_addressable_surface_nodes");
        Assert.Contains("level.Walls.get_children()", addressableNodes, StringComparison.Ordinal);
        Assert.Contains("MAXIMUM_INSPECTION_STATE_ITEMS", addressableNodes, StringComparison.Ordinal);
        Assert.Contains("counts[node_id.value]", observation, StringComparison.Ordinal);
        Assert.Contains("counts.get(int(node_id), 0) != 1", observation, StringComparison.Ordinal);
        Assert.Contains("targets[int(node_id)] = true", observation, StringComparison.Ordinal);
        Assert.DoesNotContain("wall == registered_node", observation, StringComparison.Ordinal);
        Assert.DoesNotContain("registered_nodes[int(node_id)] = registered_node", observation, StringComparison.Ordinal);
        Assert.DoesNotContain("Global.World.GetNodeByID", observation, StringComparison.Ordinal);
        var stateMachine = FunctionBody(script, "_advance_universal_plan_claim");
        Assert.Contains("var observation_result = _observe_operation(plan.operations[int(job.next_operation_index)], job.current_operation_node_ids, key)", stateMachine, StringComparison.Ordinal);
        Assert.Contains("_pending_observation_results[key] = observation_result", stateMachine, StringComparison.Ordinal);
        Assert.DoesNotContain("_pending_observation_results[key] = _observe_operation", stateMachine, StringComparison.Ordinal);
        Assert.DoesNotContain("registered_node.get_parent()", observation, StringComparison.Ordinal);
        Assert.True(
            addressableNodes.IndexOf("level.Walls.get_child_count()", StringComparison.Ordinal) <
            addressableNodes.IndexOf("level.Walls.get_children()", StringComparison.Ordinal));
    }

    [Fact]
    public void StatusAdvertisesOnlyTheActuallyCertifiedUniversalExecutor()
    {
        var script = ReadBridge();
        var body = FunctionBody(script, "_status_payload");

        Assert.Contains("\"map_id\": _current_map_id()", body, StringComparison.Ordinal);
        Assert.Contains("\"map_job_revision\": _map_job_revision", body, StringComparison.Ordinal);
        Assert.Contains("\"certified_operation_types\": _certified_operation_types()", body, StringComparison.Ordinal);
        Assert.Contains("\"operation_certifications\": _operation_certifications.duplicate(true)", body, StringComparison.Ordinal);
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
            "tools", "godot-3.5.3",
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
            Assert.True(exited, $"Pinned Godot universal executor harness timed out.\nstdout:\n{output}\nstderr:\n{error}");
            Assert.True(process.ExitCode == 0, $"Godot exited {process.ExitCode}.\nstdout:\n{output}\nstderr:\n{error}");
            Assert.True(string.IsNullOrEmpty(error), $"Godot stderr:\n{error}");
            foreach (var receipt in new[]
                     {
                         "DDAI_UNIVERSAL_FINGERPRINT:True",
                         "DDAI_UNIVERSAL_VALIDATION_PREFLIGHT:True",
                         "DDAI_UNIVERSAL_UNCERTIFIED_EXECUTOR_REJECTED:True",
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
