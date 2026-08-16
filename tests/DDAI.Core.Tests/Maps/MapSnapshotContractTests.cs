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
                     query with { Region = new MapInspectionRegion(1.0000001, 0, 1, 1) },
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
        Assert.Equal(10, document.RootElement.EnumerateObject().Count());
        Assert.Equal(new string('b', 64), document.RootElement.GetProperty("map_revision").GetString());
        Assert.Equal(7, document.RootElement.GetProperty("items")[0].GetProperty("node_id").GetInt64());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("items")[0].GetProperty("asset_ref").ValueKind);
        Assert.Equal(["object", "portal", "light", "text", "material", "floor_shape"],
            document.RootElement.GetProperty("unsupported_kinds").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void SnapshotPage_AcceptsInternalObjectFingerprintButOmitsItAfterSanitization()
    {
        var fingerprint = new string('c', 64);
        var internalPage = ValidPage() with
        {
            Items = [new MapSnapshotItem(9, "object", new MapSnapshotBounds(1, 2, 3, 4), 3, null, fingerprint)],
        };

        var wire = MapSnapshotJson.SerializePage(internalPage);
        var parsed = MapSnapshotJson.DeserializePage(wire);
        var publicPage = parsed with
        {
            Items = [parsed.Items[0] with { AssetRef = "sha256:" + new string('d', 64), ResourceFingerprint = null }],
        };

        Assert.Equal(fingerprint, parsed.Items[0].ResourceFingerprint);
        Assert.Contains("resource_fingerprint", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("resource_fingerprint", MapSnapshotJson.SerializePage(publicPage), StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotPage_AcceptsInternalLightFingerprintButRejectsItOnOtherKinds()
    {
        var fingerprint = new string('e', 64);
        var lightPage = ValidPage() with
        {
            Items = [new MapSnapshotItem(10, "light", new MapSnapshotBounds(1, 2, 3, 4), 3, null, fingerprint)],
        };
        var wallPage = lightPage with
        {
            Items = [lightPage.Items[0] with { Kind = "wall" }],
        };

        Assert.Equal(fingerprint, MapSnapshotJson.DeserializePage(MapSnapshotJson.SerializePage(lightPage)).Items[0].ResourceFingerprint);
        Assert.Throws<JsonException>(() => MapSnapshotJson.DeserializePage(MapSnapshotJson.SerializePage(wallPage)));
    }

    [Fact]
    public void SnapshotPage_AcceptsAxisAlignedWallExtentsButRejectsPointBounds()
    {
        var horizontal = ValidPage() with
        {
            Items = [new MapSnapshotItem(7, "wall", new MapSnapshotBounds(1, 2, 3, 0), 3, null)],
        };
        var vertical = ValidPage() with
        {
            Items = [new MapSnapshotItem(7, "wall", new MapSnapshotBounds(1, 2, 0, 4), 3, null)],
        };
        var point = ValidPage() with
        {
            Items = [new MapSnapshotItem(7, "wall", new MapSnapshotBounds(1, 2, 0, 0), 3, null)],
        };

        Assert.Equal(0, MapSnapshotJson.DeserializePage(MapSnapshotJson.SerializePage(horizontal)).Items[0].Bounds.Height);
        Assert.Equal(0, MapSnapshotJson.DeserializePage(MapSnapshotJson.SerializePage(vertical)).Items[0].Bounds.Width);
        Assert.Throws<JsonException>(() => MapSnapshotJson.DeserializePage(MapSnapshotJson.SerializePage(point)));
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
        wrongRevision["map_revision"] = "allocation-token-not-a-state-revision";
        Assert.Throws<JsonException>(() => MapSnapshotJson.DeserializePage(wrongRevision.ToJsonString()));

        var uncorrelatedCursor = valid.DeepClone().AsObject();
        uncorrelatedCursor["next_cursor"] = null;
        Assert.Throws<JsonException>(() => MapSnapshotJson.DeserializePage(uncorrelatedCursor.ToJsonString()));

        var duplicateNode = valid.DeepClone().AsObject();
        duplicateNode["items"]!.AsArray().Add(duplicateNode["items"]![0]!.DeepClone());
        Assert.Throws<JsonException>(() => MapSnapshotJson.DeserializePage(duplicateNode.ToJsonString()));

        var primitiveItem = valid.DeepClone().AsObject();
        primitiveItem["items"]!.AsArray()[0] = "not-an-object";
        Assert.Throws<JsonException>(() => MapSnapshotJson.DeserializePage(primitiveItem.ToJsonString()));
    }

    [Fact]
    public void InspectionCursor_BindsExactMapStateFiltersAndOffset()
    {
        var mapId = new string('a', 64);
        var revision = new string('b', 64);
        var region = new MapInspectionRegion(1, 2, 10, 8);

        var cursor = MapSnapshotJson.CreateCursor(mapId, revision, 3, region, 5);

        Assert.Equal("9da31f0d36cb0547a1cf21211cc2d894fd9da2de4a94385331a7f156361d5265:5", cursor);
        Assert.Equal(5, MapSnapshotJson.GetCursorOffset(cursor));
        Assert.NotEqual(cursor, MapSnapshotJson.CreateCursor(mapId, revision, 4, region, 5));
        Assert.NotEqual(cursor, MapSnapshotJson.CreateCursor(mapId, revision, 3, region with { X = 2 }, 5));
        Assert.NotEqual(cursor, MapSnapshotJson.CreateCursor(mapId, revision, 3, region, 6));
    }

    [Fact]
    public void PageQueryCorrelation_RejectsLimitLevelRegionAndCursorDrift()
    {
        var query = new MapInspectionQuery(new MapInspectionRegion(0, 0, 10, 10), Level: 3, Limit: 1);
        var page = ValidPage() with
        {
            NextCursor = MapSnapshotJson.CreateCursor(
                ValidPage().MapId,
                ValidPage().MapRevision,
                3,
                query.Region,
                1),
        };

        MapSnapshotJson.ValidatePageForQuery(page, query);

        Assert.Throws<JsonException>(() => MapSnapshotJson.ValidatePageForQuery(
            page with { Items = [.. page.Items, page.Items[0] with { NodeId = 8 }] },
            query));
        Assert.Throws<JsonException>(() => MapSnapshotJson.ValidatePageForQuery(page, query with { Level = 4 }));
        Assert.Throws<JsonException>(() => MapSnapshotJson.ValidatePageForQuery(
            page,
            query with { Region = new MapInspectionRegion(20, 20, 2, 2) }));
        Assert.Throws<JsonException>(() => MapSnapshotJson.ValidatePageForQuery(
            page with { NextCursor = new string('c', 64) + ":1" },
            query));
        Assert.Throws<JsonException>(() => MapSnapshotJson.ValidatePageForQuery(
            page with { Truncated = false, NextCursor = null },
            query with { Cursor = new string('c', 64) + ":0" }));
    }

    [Fact]
    public void PageQueryCorrelation_RejectsEmptyPageOutsideCanvasAndAcceptsExactBoundary()
    {
        var emptyPage = ValidPage() with
        {
            Items = [],
            NextCursor = null,
            Truncated = false,
        };

        Assert.Throws<JsonException>(() => MapSnapshotJson.ValidatePageForQuery(
            emptyPage,
            new MapInspectionQuery(new MapInspectionRegion(100, 100, 1, 1), Level: 3, Limit: 1)));

        MapSnapshotJson.ValidatePageForQuery(
            emptyPage,
            new MapInspectionQuery(new MapInspectionRegion(39, 29, 1, 1), Level: 3, Limit: 1));
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
            FunctionBody(script, "_inspection_item") +
            FunctionBody(script, "_documented_object_rect");

        Assert.Contains("\"inspect_map\"", script, StringComparison.Ordinal);
        foreach (var documentedPath in new[] { "level.Walls", "level.Pathways", "level.Roofs", "level.PatternShapes", "level.Objects", "level.Lights" })
        {
            Assert.Contains(documentedPath, inspection, StringComparison.Ordinal);
        }

        Assert.Contains("_node_id_metadata(node)", inspection, StringComparison.Ordinal);
        Assert.Contains("get_meta(\"node_id\")", FunctionBody(script, "_node_id_metadata"), StringComparison.Ordinal);
        Assert.Contains("GlobalRect", inspection, StringComparison.Ordinal);
        Assert.Contains("_documented_object_rect(node)", inspection, StringComparison.Ordinal);
        Assert.Contains("_documented_light_rect(node)", inspection, StringComparison.Ordinal);
        Assert.Contains("node.Rect", FunctionBody(script, "_documented_object_rect"), StringComparison.Ordinal);
        Assert.Contains("node.texture_scale", FunctionBody(script, "_documented_light_rect"), StringComparison.Ordinal);
        Assert.Contains("node.Sprite", inspection, StringComparison.Ordinal);
        Assert.Contains("node.texture", inspection, StringComparison.Ordinal);
        Assert.Contains("var unsupported_kinds = [\"portal\", \"text\", \"material\", \"floor_shape\"]", inspection, StringComparison.Ordinal);
        Assert.Contains("unsupported_kinds", inspection, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "get_property_list", "find_node", "get_node(", "get_tree(", "NodeLookup", ".Data", "Directory.new", "File.new", ".Save(" })
        {
            Assert.DoesNotContain(forbidden, inspection, StringComparison.Ordinal);
        }

        var cursorFingerprint = FunctionBody(script, "_inspection_cursor_value");
        foreach (var correlationField in new[] { "map_id=", "map_revision=", "level=", "region=", "offset=" })
        {
            Assert.Contains(correlationField, cursorFingerprint, StringComparison.Ordinal);
        }

        Assert.Contains("_inspection_cursor_value", FunctionBody(script, "_inspection_cursor_offset"), StringComparison.Ordinal);
    }

    [Fact]
    public void GdscriptPackage_HasNoNetworkListenerOrProcessLaunchSurface()
    {
        var repositoryRoot = FindRepositoryRoot();
        var surface = File.ReadAllText(Path.Combine(repositoryRoot, "mods", "DDAI", "scripts", "ddai_bridge.gd")) +
            File.ReadAllText(Path.Combine(repositoryRoot, "mods", "DDAI", "ddai_bridge.ddmod"));

        Assert.Empty(ListenerSurfaceFindings(surface));
        foreach (var prohibited in new[]
                 {
                     "TCPServer.new(", "HTTPServer.new(", "PacketPeerUDP.new(", "WebSocketServer.new(",
                     "UDPServer.new(", ".listen(", ".bind(", "create_server(", "OS.execute(",
                 })
        {
            Assert.NotEmpty(ListenerSurfaceFindings("prefix " + prohibited + " suffix"));
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task GdscriptBridge_ParsesWithPinnedGodot353()
    {
        var repositoryRoot = FindRepositoryRoot();
        var godotPath = Path.Combine(
            repositoryRoot,
            "tools", "godot-3.5.3",
            "Godot_v3.5.3-stable_win64.exe");
        Assert.True(File.Exists(godotPath), $"Pinned Godot 3.5.3 runtime not found: {godotPath}");

        var fixtureRoot = Path.Combine(
            repositoryRoot, "tests", "DDAI.Core.Tests", "Maps", "GodotFixtures", "ParserHarness");
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

    [Fact(Timeout = 60_000)]
    public async Task GdscriptBridge_ExecutesExactBoundedReadOnlyInspectionBehaviorOnGodot353()
    {
        var repositoryRoot = FindRepositoryRoot();
        var godotPath = Path.Combine(
            repositoryRoot,
            "tools", "godot-3.5.3",
            "Godot_v3.5.3-stable_win64.exe");
        Assert.True(File.Exists(godotPath), $"Pinned Godot 3.5.3 runtime not found: {godotPath}");

        var fixtureRoot = Path.Combine(
            repositoryRoot,
            "tests", "DDAI.Core.Tests", "Maps", "GodotFixtures", "MapInspectionBehavior");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "ddai-map-behavior-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            foreach (var fixture in Directory.EnumerateFiles(fixtureRoot))
            {
                File.Copy(fixture, Path.Combine(temporaryRoot, Path.GetFileName(fixture)));
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
            Assert.True(exited, "Pinned Godot 3.5.3 behavior harness timed out.");
            Assert.True(
                process.ExitCode == 0,
                $"Pinned Godot behavior harness exited {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{output}{Environment.NewLine}stderr:{Environment.NewLine}{error}");
            Assert.True(string.IsNullOrEmpty(error), $"Pinned Godot behavior harness stderr:{Environment.NewLine}{error}");
            foreach (var receipt in new[]
                     {
                         "DDAI_INSPECTION_PAGINATION:True",
                         "DDAI_INSPECTION_STALE_CURSOR:True",
                         "DDAI_INSPECTION_BOUNDS_AND_CAP:True",
                         "DDAI_INSPECTION_CURSOR_VECTOR:True",
                          "DDAI_INSPECTION_MAP_IDENTITY:True",
                         "DDAI_INSPECTION_OBJECT_CORRELATION:True",
                         "DDAI_INSPECTION_LIGHT_CORRELATION:True",
                          "DDAI_INSPECTION_RELOADED_OBJECT_BOUNDS:True",
                          "DDAI_INSPECTION_NODE_ID_DIAGNOSTIC:True",
                          "DDAI_INSPECTION_READ_ONLY:True",
                     })
            {
                Assert.Contains(receipt, output, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static MapSnapshotPage ValidPage() => new(
        new string('a', 64),
        "bound",
        new string('b', 64),
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

    private static IReadOnlyList<string> ListenerSurfaceFindings(string surface)
    {
        string[] prohibited =
        [
            "tcpserver", "tcp_server", "httpserver", "http_server", "packetpeer", "packet_peer",
            "websocket", "web_socket", "udpserver", "udp_server", ".listen(", ".bind(",
            "create_server(", "os.execute(",
        ];
        var normalized = string.Concat(surface.Where(character => !char.IsWhiteSpace(character))).ToLowerInvariant();
        return prohibited.Where(token => normalized.Contains(token, StringComparison.Ordinal)).ToArray();
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
