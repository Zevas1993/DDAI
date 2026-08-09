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
    public void Gdscript_TimestampValidatorCapsOffsetsAtFourteenHours()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "mods", "DDAI", "scripts", "ddai_bridge.gd"));
        var validator = FunctionBody(script, "_is_wire_timestamp");

        Assert.Contains("zone_hour_value > 14", validator, StringComparison.Ordinal);
        Assert.Contains("zone_hour_value == 14 and zone_minute_value != 0", validator, StringComparison.Ordinal);
        Assert.Contains("fraction.length() > 16", validator, StringComparison.Ordinal);
        Assert.Contains("fraction.substr(0, min(7, fraction.length()))", validator, StringComparison.Ordinal);
        Assert.Contains("zone_kind = \"local\"", validator, StringComparison.Ordinal);
        Assert.Contains("var zone_sign = 0", validator, StringComparison.Ordinal);
        Assert.Contains("if zone_hour_value == 0 and zone_minute_value == 0:", validator, StringComparison.Ordinal);
        Assert.Contains("zone_sign = 0", validator[validator.IndexOf("if zone_hour_value == 0 and zone_minute_value == 0:", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("var local_seconds = hour * 3600 + minute * 60 + second", validator, StringComparison.Ordinal);
        Assert.Contains("var offset_seconds = zone_hour_value * 3600 + zone_minute_value * 60", validator, StringComparison.Ordinal);
        Assert.Contains("local_seconds < offset_seconds", validator, StringComparison.Ordinal);
        Assert.Contains("local_seconds == offset_seconds and not fraction_nonzero", validator, StringComparison.Ordinal);
        Assert.Contains("local_seconds + offset_seconds >= 86400", validator, StringComparison.Ordinal);
        Assert.Contains("if zone_kind == \"local\"", validator, StringComparison.Ordinal);
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
        Assert.Contains("value.length() - zone_index == 3", validator, StringComparison.Ordinal);
        Assert.Contains("zone_minute = \"00\"", validator, StringComparison.Ordinal);
        Assert.Contains("main.length() != 16 and main.length() != 19", validator, StringComparison.Ordinal);
        Assert.True(
            validator.IndexOf("_is_ascii_decimal_digits(zone_hour)", StringComparison.Ordinal) <
            validator.IndexOf("int(zone_hour)", StringComparison.Ordinal));
        Assert.True(
            validator.IndexOf("_is_ascii_decimal_digits(fraction)", StringComparison.Ordinal) <
            validator.IndexOf("int(fraction.substr", StringComparison.Ordinal));
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
