using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DDAI.Core.Mailbox;
using DDAI.Core.MapPlans;
using DDAI.Core.Maps;

namespace DDAI.Core.Tests.Maps;

public sealed class MapSnapshotContractTests
{
    [Fact]
    public void InspectionQuery_SerializesEveryMailboxFieldAndEnforcesBounds()
    {
        var query = new MapInspectionQuery(new MapInspectionRegion(1.25, 2.5, 10, 8), Level: 7, Limit: 500);

        var payload = MapSnapshotJson.SerializeQueryToElement(query);

        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.Equal(4, payload.EnumerateObject().Count());
        Assert.Equal(1.25, payload.GetProperty("region").GetProperty("x").GetDouble());
        Assert.Equal(2.5, payload.GetProperty("region").GetProperty("y").GetDouble());
        Assert.Equal(10, payload.GetProperty("region").GetProperty("width").GetDouble());
        Assert.Equal(8, payload.GetProperty("region").GetProperty("height").GetDouble());
        Assert.Equal(7, payload.GetProperty("level").GetInt32());
        Assert.Equal(500, payload.GetProperty("limit").GetInt32());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("cursor").ValueKind);

        foreach (var invalid in new[]
                 {
                     query with { Limit = 0 },
                     query with { Limit = 501 },
                     query with { Level = -1 },
                     query with { Region = new MapInspectionRegion(-1, 0, 1, 1) },
                     query with { Region = new MapInspectionRegion(0, -1, 1, 1) },
                     query with { Region = new MapInspectionRegion(0, 0, 0, 1) },
                     query with { Region = new MapInspectionRegion(0, 0, 1, double.NaN) },
                     query with { Region = new MapInspectionRegion(double.MaxValue, 0, double.MaxValue, 1) },
                     query with { Cursor = "not-a-correlated-cursor" },
                 })
        {
            Assert.ThrowsAny<ArgumentException>(() => MapSnapshotJson.ValidateQuery(invalid));
        }
    }

    [Fact]
    public void MailboxFactory_EmitsInspectMapWithExactEnvelopeAndPayload()
    {
        var timestamp = new DateTimeOffset(2026, 8, 11, 14, 0, 0, TimeSpan.Zero);
        var query = new MapInspectionQuery(Limit: 25);

        var request = MailboxRequest.CreateInspectMap("inspect-request-001", query, timestamp);

        Assert.Equal(MailboxRequest.CurrentSchemaVersion, request.SchemaVersion);
        Assert.Equal("inspect-request-001", request.RequestId);
        Assert.Equal("inspect_map", request.Command);
        Assert.Equal(timestamp, request.Timestamp);
        Assert.Equal(25, request.Payload.GetProperty("limit").GetInt32());
    }

    [Fact]
    public void SnapshotPage_RoundTripsStrictSnakeCaseContract()
    {
        var page = ValidPage();

        var json = MapSnapshotJson.SerializePage(page);
        var roundTrip = MapSnapshotJson.DeserializePage(json);

        Assert.Equal(json, MapSnapshotJson.SerializePage(roundTrip));
        Assert.True(Encoding.UTF8.GetByteCount(json) <= MapSnapshotJson.MaximumJsonBytes);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(9, document.RootElement.EnumerateObject().Count());
        Assert.Equal(41, document.RootElement.GetProperty("map_revision").GetInt64());
        Assert.Equal(7, document.RootElement.GetProperty("items")[0].GetProperty("node_id").GetInt64());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("items")[0].GetProperty("asset_ref").ValueKind);
        Assert.Equal(["object", "portal", "light", "text", "material", "floor_shape"],
            document.RootElement.GetProperty("unsupported_kinds").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void SnapshotPage_RejectsUnknownMissingDuplicateAndSemanticallyInvalidContent()
    {
        var valid = JsonNode.Parse(MapSnapshotJson.SerializePage(ValidPage()))!.AsObject();

        var unknown = valid.DeepClone().AsObject();
        unknown["private_scene_path"] = "/root/private";
        Assert.Throws<JsonException>(() => MapSnapshotJson.DeserializePage(unknown.ToJsonString()));

        var missing = valid.DeepClone().AsObject();
        missing.Remove("map_revision");
        Assert.Throws<JsonException>(() => MapSnapshotJson.DeserializePage(missing.ToJsonString()));

        var duplicate = MapSnapshotJson.SerializePage(ValidPage()).Replace(
            "\"map_id\":",
            "\"map_id\":\"" + new string('b', 64) + "\",\"map_id\":",
            StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => MapSnapshotJson.DeserializePage(duplicate));

        var wrongRevision = valid.DeepClone().AsObject();
        wrongRevision["map_revision"] = -1;
        Assert.Throws<JsonException>(() => MapSnapshotJson.DeserializePage(wrongRevision.ToJsonString()));

        var uncorrelatedCursor = valid.DeepClone().AsObject();
        uncorrelatedCursor["next_cursor"] = null;
        Assert.Throws<JsonException>(() => MapSnapshotJson.DeserializePage(uncorrelatedCursor.ToJsonString()));

        var duplicateNode = valid.DeepClone().AsObject();
        duplicateNode["items"]!.AsArray().Add(duplicateNode["items"]![0]!.DeepClone());
        Assert.Throws<JsonException>(() => MapSnapshotJson.DeserializePage(duplicateNode.ToJsonString()));
    }

    [Fact]
    public void SnapshotPage_RejectsOverOneMiBBeforeDeserialization()
    {
        var oversized = "{\"map_id\":\"" + new string('x', MapSnapshotJson.MaximumJsonBytes) + "\"}";

        Assert.Throws<JsonException>(() => MapSnapshotJson.DeserializePage(oversized));
    }

    [Fact]
    public void GdscriptInspection_UsesOnlyFixedDocumentedContainersAndReportsUnsupportedKinds()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "mods", "DDAI", "scripts", "ddai_bridge.gd"));
        var inspection = FunctionBody(script, "_inspect_map_payload") +
            FunctionBody(script, "_append_inspection_container") +
            FunctionBody(script, "_inspection_item");

        Assert.Contains("\"inspect_map\"", script, StringComparison.Ordinal);
        foreach (var documentedPath in new[] { "level.Walls", "level.Pathways", "level.Roofs", "level.PatternShapes" })
        {
            Assert.Contains(documentedPath, inspection, StringComparison.Ordinal);
        }

        Assert.Contains("GetNodeID()", inspection, StringComparison.Ordinal);
        Assert.Contains("GlobalRect", inspection, StringComparison.Ordinal);
        Assert.Contains("unsupported_kinds", inspection, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "get_property_list", "find_node", "get_node(", "get_tree(", "NodeLookup", ".Data", "Directory.new", "File.new", ".Save(" })
        {
            Assert.DoesNotContain(forbidden, inspection, StringComparison.Ordinal);
        }

        var cursorFingerprint = FunctionBody(script, "_inspection_cursor_fingerprint");
        foreach (var correlationField in new[] { "map_id", "map_revision", "level", "region_x", "region_y", "region_width", "region_height" })
        {
            Assert.Contains(correlationField, cursorFingerprint, StringComparison.Ordinal);
        }

        Assert.Contains(
            "fingerprint != expected_fingerprint",
            FunctionBody(script, "_inspection_cursor_offset"),
            StringComparison.Ordinal);
    }

    [Fact(Timeout = 60_000)]
    public async Task GdscriptBridge_ParsesWithPinnedGodot353()
    {
        var repositoryRoot = FindRepositoryRoot();
        var godotPath = Path.Combine(
            repositoryRoot,
            "artifacts", "rectangular-room", "tooling", "godot-3.5.3",
            "Godot_v3.5.3-stable_win64.exe");
        Assert.True(File.Exists(godotPath), $"Pinned Godot 3.5.3 runtime not found: {godotPath}");

        var fixtureRoot = Path.Combine(repositoryRoot, "artifacts", "rectangular-room", "parser-harness");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "ddai-map-parser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            foreach (var name in new[] { "project.godot", "main.tscn", "main.gd", "global.gd" })
            {
                File.Copy(Path.Combine(fixtureRoot, name), Path.Combine(temporaryRoot, name));
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
                ArgumentList =
                {
                    "--path", temporaryRoot,
                    "--no-window",
                    "--scene", "res://main.tscn",
                },
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
            Assert.True(exited, "Pinned Godot 3.5.3 parser harness timed out.");
            Assert.True(
                process.ExitCode == 0,
                $"Pinned Godot parser exited {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{output}{Environment.NewLine}stderr:{Environment.NewLine}{error}");
            Assert.Equal(string.Empty, error);
            Assert.Contains("DDAI_PARSE_OK:True", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static MapSnapshotPage ValidPage() => new(
        new string('a', 64),
        41,
        new MapCanvas(40, 30),
        256,
        [new MapSnapshotLevel(3, "Ground", true)],
        [new MapSnapshotItem(7, "wall", new MapSnapshotBounds(1, 2, 3, 4), 3, null)],
        new string('c', 64) + ":1",
        true,
        ["object", "portal", "light", "text", "material", "floor_shape"]);

    private static string FunctionBody(string script, string functionName)
    {
        var marker = $"func {functionName}(";
        var start = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing GDScript function {functionName}.");
        var next = script.IndexOf("\nfunc ", start + marker.Length, StringComparison.Ordinal);
        return next < 0 ? script[start..] : script[start..next];
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DDAI.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate DDAI repository root.");
    }
}
