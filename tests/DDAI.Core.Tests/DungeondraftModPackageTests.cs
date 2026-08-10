using System.Text.Json;

namespace DDAI.Core.Tests;

public sealed class DungeondraftModPackageTests
{
    [Fact]
    public void Package_DeclaresRectangularRoomMutationBridgeVersionAndCommands()
    {
        var modRoot = Path.Combine(FindRepositoryRoot(), "mods", "DDAI");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(modRoot, "ddai_bridge.ddmod")));
        var script = File.ReadAllText(Path.Combine(modRoot, "scripts", "ddai_bridge.gd"));
        var readme = File.ReadAllText(Path.Combine(modRoot, "README.md"));

        Assert.Equal("0.2.1", manifest.RootElement.GetProperty("version").GetString());
        Assert.Equal("0.2.1", ConstantValue(script, "MOD_VERSION"));
        Assert.Contains("const SUPPORTED_COMMANDS = [\"status\", \"apply_plan\"]", script, StringComparison.Ordinal);
        Assert.Contains("apply_plan", readme, StringComparison.Ordinal);
        Assert.Contains("native rectangular wall", readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gdscript_ValidatesGodotJsonFloatsAsBoundedWholeInt32Values()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "mods", "DDAI", "scripts", "ddai_bridge.gd"));
        var validator = FunctionBody(script, "_validate_rectangular_room_plan");
        var int32 = FunctionBody(script, "_is_json_int32");

        Assert.Contains("typeof(value) == TYPE_REAL", int32, StringComparison.Ordinal);
        Assert.DoesNotContain("typeof(value) == TYPE_INT", int32, StringComparison.Ordinal);
        Assert.Contains("not is_nan(value)", int32, StringComparison.Ordinal);
        Assert.Contains("not is_inf(value)", int32, StringComparison.Ordinal);
        Assert.Contains("value == floor(value)", int32, StringComparison.Ordinal);
        Assert.Contains("value >= -2147483648.0", int32, StringComparison.Ordinal);
        Assert.Contains("value <= 2147483647.0", int32, StringComparison.Ordinal);
        foreach (var code in new[]
                 {
                     "unsupported_mode", "unsupported_base_revision", "invalid_room_count", "room_required",
                     "invalid_room_id", "invalid_room_x", "invalid_room_y", "invalid_room_width",
                     "invalid_room_height", "room_out_of_bounds",
                 })
        {
            Assert.Contains("\"" + code + "\"", validator, StringComparison.Ordinal);
        }
        Assert.Contains("canvas.width - room.x", validator, StringComparison.Ordinal);
        Assert.Contains("canvas.height - room.y", validator, StringComparison.Ordinal);
    }

    [Fact]
    public void Gdscript_UsesLanguageNeutralPlanFingerprintFraming()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "mods", "DDAI", "scripts", "ddai_bridge.gd"));
        var body = FunctionBody(script, "_plan_fingerprint_input");
        var orderedTags = new[]
        {
            "schema_version=", "request_id=", "base_revision=", "mode=", "canvas_width=",
            "canvas_height=", "rooms_count=", "room[0].id=", "room[0].x=", "room[0].y=",
            "room[0].width=", "room[0].height=",
        };
        var position = -1;
        foreach (var tag in orderedTags)
        {
            var next = body.IndexOf(tag, position + 1, StringComparison.Ordinal);
            Assert.True(next > position, $"Expected fingerprint tag in order: {tag}");
            position = next;
        }

        Assert.Contains("value.to_utf8().size()", FunctionBody(script, "_framed_string"), StringComparison.Ordinal);
        Assert.Contains("_plan_fingerprint_input(plan).sha256_text()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("to_json(plan).sha256_text()", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Gdscript_UsesImmutableNonReplayableMutationIntentRouting()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "mods", "DDAI", "scripts", "ddai_bridge.gd"));
        var advance = FunctionBody(script, "_advance_claim_state");
        var mutation = FunctionBody(script, "_advance_apply_plan_claim");
        var recovery = FunctionBody(script, "_recover_processing_claims");

        Assert.Contains("if request.command == \"apply_plan\":", advance, StringComparison.Ordinal);
        Assert.Contains("_advance_apply_plan_claim", advance, StringComparison.Ordinal);
        Assert.Contains("mutation-intents", recovery, StringComparison.Ordinal);
        Assert.Contains(".prepared.json", mutation, StringComparison.Ordinal);
        Assert.Contains(".confirmed.json", mutation, StringComparison.Ordinal);
        Assert.Contains(".ambiguous.json", mutation, StringComparison.Ordinal);
        Assert.Contains("_prepared_mutation_keys.has(key)", mutation, StringComparison.Ordinal);
        Assert.Contains("mutation_outcome_unknown", mutation, StringComparison.Ordinal);
        Assert.True(
            mutation.IndexOf("_write_mutation_intent(prepared_path", StringComparison.Ordinal) <
            mutation.IndexOf("_execute_rectangular_room", StringComparison.Ordinal));
        Assert.True(
            mutation.IndexOf("mutation_outcome_unknown", StringComparison.Ordinal) <
            mutation.IndexOf("_execute_rectangular_room", StringComparison.Ordinal));
        Assert.Contains("_mutation_active", mutation, StringComparison.Ordinal);
        Assert.Contains("mutation_busy", mutation, StringComparison.Ordinal);
    }

    [Fact]
    public void Gdscript_ExecutesExactlyOneDocumentedClosedNativeWallWithCleanup()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "mods", "DDAI", "scripts", "ddai_bridge.gd"));
        var advance = FunctionBody(script, "_advance_apply_plan_claim");
        var executor = FunctionBody(script, "_execute_rectangular_room");
        var cleanup = FunctionBody(script, "_cleanup_wall_tool");
        var preflight = FunctionBody(script, "_runtime_room_preflight");
        var orderedTokens = new[]
        {
            "Global.World.Width", "Global.World.Height", "Global.World.GridSize", "Global.World.CurrentLevelId",
            "Global.World.GetLevelByID", "level.Walls.get_children()", "Global.Editor.Tools[\"WallTool\"]",
            "wall_tool.Enable()", "Global.WorldUI.ClearPolyline()", "Global.WorldUI.AddPolyPoint",
            "wall_tool.EndWall(true)", "level.Walls.get_children()", "_cleanup_wall_tool(wall_tool)",
        };
        var position = -1;
        foreach (var token in orderedTokens)
        {
            var next = executor.IndexOf(token, position + 1, StringComparison.Ordinal);
            Assert.True(next > position, $"Expected native wall token in order: {token}");
            position = next;
        }

        Assert.Equal(4, CountOccurrences(executor, "Global.WorldUI.AddPolyPoint("));
        Assert.Equal(1, CountOccurrences(executor, "wall_tool.EndWall(true)"));
        Assert.Equal(1, CountOccurrences(executor, "Global.WorldUI.AddPolyPoint(point_1)"));
        Assert.DoesNotContain("wall_tool.Confirm()", executor, StringComparison.Ordinal);
        Assert.Contains("walls_after == walls_before + 1", executor, StringComparison.Ordinal);
        Assert.Contains("_runtime_room_preflight(plan)", executor, StringComparison.Ordinal);
        Assert.Contains("level.Walls.get_children().size()", preflight, StringComparison.Ordinal);
        foreach (var payloadField in new[]
                 {
                     "\"applied\": true", "\"created_walls\": 1", "\"room_id\": room.id",
                     "\"undo_available\": true", "\"undo_instruction\": \"Use Dungeondraft Undo once\"",
                     "\"plan_fingerprint\": plan_fingerprint",
                 })
        {
            Assert.Contains(payloadField, executor, StringComparison.Ordinal);
        }
        Assert.Contains("Global.WorldUI.ClearPolyline()", cleanup, StringComparison.Ordinal);
        Assert.Contains("wall_tool.Disable()", cleanup, StringComparison.Ordinal);
        Assert.True(
            advance.IndexOf("_runtime_room_preflight", StringComparison.Ordinal) <
            advance.IndexOf("_write_mutation_intent(prepared_path", StringComparison.Ordinal));
        Assert.Contains("_advance_journaled_response(claim, request, request_fingerprint, journal_path, response_path, key", advance, StringComparison.Ordinal);
        Assert.DoesNotContain("get_property_list", executor, StringComparison.Ordinal);
        Assert.DoesNotContain("Custom Snap", executor, StringComparison.Ordinal);
    }

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
        Assert.Contains("return _fail_claim_without_loss(claim, validation_error)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("_can_correlate_failure", script, StringComparison.Ordinal);
        Assert.Contains("request.payload == null", script, StringComparison.Ordinal);
        Assert.Contains("_is_wire_timestamp", script, StringComparison.Ordinal);
        Assert.Contains("runtime-heartbeats", script, StringComparison.Ordinal);
        Assert.Contains("func _write_heartbeat():", script, StringComparison.Ordinal);
        Assert.True(script.IndexOf("_heartbeat_elapsed += delta", StringComparison.Ordinal) < script.IndexOf("if _poll_elapsed < POLL_INTERVAL_SECONDS", StringComparison.Ordinal));
        Assert.Contains("_response_text_matches", script, StringComparison.Ordinal);
        Assert.Contains("response_conflict", script, StringComparison.Ordinal);
        Assert.DoesNotContain("func _prune_heartbeats():", script, StringComparison.Ordinal);
        Assert.Contains("func _prepare_response(request):", script, StringComparison.Ordinal);
        Assert.Contains("func _recover_processing_claims():", script, StringComparison.Ordinal);
        Assert.Contains("heartbeat-slot-", script, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime-heartbeats/\" + _session_id + \"-\" + str(OS.get_unix_time())", script, StringComparison.Ordinal);
        Assert.DoesNotContain("TCPServer", script, StringComparison.Ordinal);
        Assert.DoesNotContain("HTTPClient", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PacketPeer", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Gdscript_RoutesPollingAndStartupRecoveryThroughJournaledStateMachine()
    {
        var scriptPath = Path.Combine(FindRepositoryRoot(), "mods", "DDAI", "scripts", "ddai_bridge.gd");
        var script = File.ReadAllText(scriptPath);
        var processBody = FunctionBody(script, "_process_one_request");
        var recoveryBody = FunctionBody(script, "_recover_processing_claims");
        var advanceBody = FunctionBody(script, "_advance_claim_state");

        Assert.Contains("_run_claim_state_machine(claim)", processBody, StringComparison.Ordinal);
        Assert.Contains("_run_claim_state_machine({\"file_name\": file_name, \"path\": path})", recoveryBody, StringComparison.Ordinal);
        Assert.DoesNotContain("directory.rename(path, request_path)", recoveryBody, StringComparison.Ordinal);
        Assert.Contains("_reconcile_request_duplicate", advanceBody, StringComparison.Ordinal);
        Assert.Contains("request_fingerprint", advanceBody, StringComparison.Ordinal);
        Assert.Contains("response_text", advanceBody, StringComparison.Ordinal);
        Assert.Contains("_write_text_atomically(response_path, journal.response_text)", advanceBody, StringComparison.Ordinal);
        Assert.Contains("_response_text_matches(response_path, journal.response_text)", advanceBody, StringComparison.Ordinal);
        Assert.True(
            advanceBody.IndexOf("_response_text_matches(response_path, journal.response_text)", StringComparison.Ordinal) <
            advanceBody.LastIndexOf("_remove_file(claim.path)", StringComparison.Ordinal),
            "The processing claim may only be removed after the exact journaled response is verified.");
    }

    [Fact]
    public void Gdscript_AvoidsRuntimeCallsProvenToCrashDungeondraft1201()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "mods", "DDAI", "scripts", "ddai_bridge.gd"));
        var processBody = FunctionBody(script, "_process_one_request");
        var advanceBody = FunctionBody(script, "_advance_claim_state");
        var reconcileBody = FunctionBody(script, "_reconcile_request_duplicate");
        var activeModsBody = FunctionBody(script, "_active_mods_payload");

        Assert.DoesNotContain("claim.empty()", processBody, StringComparison.Ordinal);
        Assert.DoesNotContain("claim.size()", processBody, StringComparison.Ordinal);
        Assert.Contains("if not claim.has(\"path\"):", processBody, StringComparison.Ordinal);
        Assert.DoesNotContain("validation_error.empty()", advanceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("validation_error.size()", advanceBody, StringComparison.Ordinal);
        Assert.Contains("if validation_error.has(\"code\"):", advanceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_validate_request(duplicate, file_name).empty()", reconcileBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_validate_request(duplicate, file_name).size()", reconcileBody, StringComparison.Ordinal);
        Assert.Contains("not _validate_request(duplicate, file_name).has(\"code\")", reconcileBody, StringComparison.Ordinal);
        Assert.DoesNotContain("mods.keys()", activeModsBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Global", activeModsBody, StringComparison.Ordinal);
        Assert.Contains("return {\"available\": false, \"values\": []}", activeModsBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Gdscript_StatusPayloadDoesNotReflectOverDungeondraftRuntimeObjects()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "mods", "DDAI", "scripts", "ddai_bridge.gd"));
        var statusPayload = FunctionBody(script, "_status_payload");

        Assert.DoesNotContain("Global", statusPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("World", statusPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("_safe_property", statusPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("_first_safe_property", statusPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("get_property_list", statusPayload, StringComparison.Ordinal);
        Assert.Contains("\"map_loaded\": true", statusPayload, StringComparison.Ordinal);
        Assert.Contains("\"dungeondraft_version_available\": false", statusPayload, StringComparison.Ordinal);
        Assert.Contains("\"active_mods_available\": false", statusPayload, StringComparison.Ordinal);
    }

    [Fact]
    public void Gdscript_TimestampValidatorEnforcesCanonicalWireLanguageAndUtcBounds()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "mods", "DDAI", "scripts", "ddai_bridge.gd"));
        var validator = FunctionBody(script, "_is_wire_timestamp");

        Assert.Contains("zone_hour_value > 14", validator, StringComparison.Ordinal);
        Assert.Contains("zone_hour_value == 14 and zone_minute_value != 0", validator, StringComparison.Ordinal);
        Assert.Contains("fraction.length() > 7", validator, StringComparison.Ordinal);
        Assert.DoesNotContain("fraction.substr(0, min(7, fraction.length()))", validator, StringComparison.Ordinal);
        Assert.DoesNotContain("zone_kind = \"local\"", validator, StringComparison.Ordinal);
        Assert.Contains("var zone_sign = 0", validator, StringComparison.Ordinal);
        Assert.Contains("if zone_hour_value == 0 and zone_minute_value == 0:", validator, StringComparison.Ordinal);
        Assert.Contains("zone_sign = 0", validator[validator.IndexOf("if zone_hour_value == 0 and zone_minute_value == 0:", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("var local_seconds = hour * 3600 + minute * 60 + second", validator, StringComparison.Ordinal);
        Assert.Contains("var offset_seconds = zone_hour_value * 3600 + zone_minute_value * 60", validator, StringComparison.Ordinal);
        Assert.Contains("local_seconds < offset_seconds", validator, StringComparison.Ordinal);
        Assert.Contains("local_seconds == offset_seconds and not fraction_nonzero", validator, StringComparison.Ordinal);
        Assert.Contains("local_seconds + offset_seconds >= 86400", validator, StringComparison.Ordinal);
        Assert.DoesNotContain("if zone_kind == \"local\"", validator, StringComparison.Ordinal);
    }

    [Fact]
    public void Gdscript_TimestampValidatorRequiresAsciiDecimalDigitsBeforeConversion()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "mods", "DDAI", "scripts", "ddai_bridge.gd"));
        var validator = FunctionBody(script, "_is_wire_timestamp");
        var digitCheck = FunctionBody(script, "_is_ascii_decimal_digits");

        Assert.DoesNotContain(".is_valid_integer()", validator, StringComparison.Ordinal);
        Assert.Contains("_is_ascii_decimal_digits(zone_hour)", validator, StringComparison.Ordinal);
        Assert.Contains("_is_ascii_decimal_digits(zone_minute)", validator, StringComparison.Ordinal);
        Assert.Contains("_is_ascii_decimal_digits(fraction)", validator, StringComparison.Ordinal);
        Assert.Contains("_is_ascii_decimal_digits(digits)", validator, StringComparison.Ordinal);
        Assert.DoesNotContain("value.length() - zone_index == 3", validator, StringComparison.Ordinal);
        Assert.DoesNotContain("zone_minute = \"00\"", validator, StringComparison.Ordinal);
        Assert.Contains("main.length() != 19", validator, StringComparison.Ordinal);
        Assert.True(
            validator.IndexOf("_is_ascii_decimal_digits(zone_hour)", StringComparison.Ordinal) <
            validator.IndexOf("int(zone_hour)", StringComparison.Ordinal));
        Assert.True(
            validator.IndexOf("_is_ascii_decimal_digits(fraction)", StringComparison.Ordinal) <
            validator.IndexOf("int(fraction)", StringComparison.Ordinal));
        Assert.True(
            validator.IndexOf("_is_ascii_decimal_digits(digits)", StringComparison.Ordinal) <
            validator.IndexOf("int(main.substr(0, 4))", StringComparison.Ordinal));
        Assert.Contains("var code = value.ord_at(index)", digitCheck, StringComparison.Ordinal);
        Assert.Contains("code < 48 or code > 57", digitCheck, StringComparison.Ordinal);
    }

    [Fact]
    public void Gdscript_FailsClosedWhenReconciliationOrFailurePublicationCannotCommit()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "mods", "DDAI", "scripts", "ddai_bridge.gd"));
        var advance = FunctionBody(script, "_advance_claim_state");
        var reconcile = FunctionBody(script, "_reconcile_request_duplicate");
        var failClaim = FunctionBody(script, "_fail_claim_without_loss");
        var writeFailed = FunctionBody(script, "_write_failed_record");
        var moveFailed = FunctionBody(script, "_move_to_unique_failed");
        var remove = FunctionBody(script, "_remove_file");

        Assert.Contains("var reconciliation_result = _reconcile_request_duplicate", advance, StringComparison.Ordinal);
        Assert.Contains("if reconciliation_result != \"reconciled\"", advance, StringComparison.Ordinal);
        Assert.True(
            advance.IndexOf("if reconciliation_result != \"reconciled\"", StringComparison.Ordinal) <
            advance.IndexOf("_write_json_atomically(journal_path", StringComparison.Ordinal));
        Assert.Contains("return _fail_claim_without_loss", advance, StringComparison.Ordinal);
        Assert.Contains("return \"blocked\"", reconcile, StringComparison.Ordinal);
        Assert.Contains("if failed_result != \"created\"", failClaim, StringComparison.Ordinal);
        Assert.Contains("var remove_result = _remove_file(claim.path)", failClaim, StringComparison.Ordinal);
        Assert.Contains("return _write_json_atomically", writeFailed, StringComparison.Ordinal);
        Assert.Contains("return \"move_failed\"", moveFailed, StringComparison.Ordinal);
        Assert.Contains("return \"remove_failed\"", remove, StringComparison.Ordinal);
    }

    private static string FunctionBody(string script, string functionName)
    {
        var marker = "func " + functionName + "(";
        var start = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected GDScript function {functionName}.");
        var next = script.IndexOf("\nfunc ", start + marker.Length, StringComparison.Ordinal);
        return next < 0 ? script[start..] : script[start..next];
    }

    private static string ConstantValue(string script, string constantName)
    {
        var marker = "const " + constantName + " = \"";
        var start = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected GDScript constant {constantName}.");
        start += marker.Length;
        var end = script.IndexOf('"', start);
        return script[start..end];
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var position = 0;
        while ((position = value.IndexOf(token, position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += token.Length;
        }

        return count;
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
