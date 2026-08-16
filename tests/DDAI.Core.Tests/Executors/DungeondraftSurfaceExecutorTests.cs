using System.Diagnostics;
using DDAI.Core.MapPlans;
using DDAI.Core.MapPlans.Operations;

namespace DDAI.Core.Tests.Executors;

public sealed class DungeondraftSurfaceExecutorTests
{
    [Fact]
    public void ManagedAdapterProbeIsReadOnlyAndWritesAClosedShapeReceipt()
    {
        var script = ReadBridge();
        var start = FunctionBody(script, "start");
        var probe = FunctionBody(script, "_probe_managed_adapter");

        Assert.Contains("_probe_managed_adapter()", start, StringComparison.Ordinal);
        Assert.Contains("ClassDB.class_exists(\"CSharpScript\")", probe, StringComparison.Ordinal);
        Assert.Contains("DdaiManagedAdapter", probe, StringComparison.Ordinal);
        Assert.Contains("Ping", probe, StringComparison.Ordinal);
        Assert.Contains("managed-adapter.json", probe, StringComparison.Ordinal);
        Assert.Contains("available", probe, StringComparison.Ordinal);
        Assert.Contains("reason", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("Terrain", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("Global.World", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void SurfaceExecutorsUseDocumentedRoutesAndExplicitRollbackEvidence()
    {
        var script = ReadBridge();
        var validation = FunctionBody(script, "_validate_universal_operation");
        var preflight = FunctionBody(script, "_preflight_universal_plan");

        foreach (var operation in new[]
                 {
                     "terrain_stroke", "pattern_region", "colorable_pattern_region", "cave_region", "roof_region",
                 })
        {
            Assert.Contains(operation, validation, StringComparison.Ordinal);
        }

        Assert.Contains("_surface_category", preflight, StringComparison.Ordinal);
        Assert.Contains("_resolve_live_asset", preflight, StringComparison.Ordinal);

        var terrain = FunctionBody(script, "_execute_terrain_stroke");
        Assert.Contains("terrain.CloneSplatImage()", terrain, StringComparison.Ordinal);
        Assert.Contains("terrain.CloneSplatImage2()", terrain, StringComparison.Ordinal);
        Assert.Contains("terrain.ExpandedSlots", terrain, StringComparison.Ordinal);
        Assert.Contains("expanded_slots", terrain, StringComparison.Ordinal);
        Assert.Contains("_terrain_texture_slot(terrain, context.resource_identity, texture)", terrain, StringComparison.Ordinal);
        Assert.Contains("_paint_terrain_splat", terrain, StringComparison.Ordinal);
        Assert.Contains("int(canvas.width)", terrain, StringComparison.Ordinal);
        Assert.Contains("int(canvas.height)", terrain, StringComparison.Ordinal);
        Assert.Contains("_terrain_brush_radius(terrain_tool.brush)", terrain, StringComparison.Ordinal);
        Assert.DoesNotContain("terrain_tool.BrushRadius", terrain, StringComparison.Ordinal);
        Assert.DoesNotContain("terrain_tool.GetBrushRadius", terrain, StringComparison.Ordinal);
        Assert.Contains("native_state_brush_radius", terrain, StringComparison.Ordinal);
        Assert.Contains("painted.stage", terrain, StringComparison.Ordinal);
        Assert.Contains("native_state_managed_adapter_required", FunctionBody(script, "_paint_terrain_splat"), StringComparison.Ordinal);
        Assert.DoesNotContain("terrain.Paint", FunctionBody(script, "_paint_terrain_splat"), StringComparison.Ordinal);
        Assert.DoesNotContain("terrain.SetTexture(texture, 0)", terrain, StringComparison.Ordinal);
        Assert.Contains("_persist_surface_rollback", terrain, StringComparison.Ordinal);
        Assert.Contains("terrain_tool.IsPainting", terrain, StringComparison.Ordinal);

        var terrainSlot = FunctionBody(script, "_terrain_texture_slot");
        Assert.Contains("for slot in range(8)", terrainSlot, StringComparison.Ordinal);
        Assert.Contains("terrain.GetTexture(slot)", terrainSlot, StringComparison.Ordinal);
        Assert.Contains("texture == requested_texture", terrainSlot, StringComparison.Ordinal);
        Assert.Contains("str(texture.resource_path) == resource_identity", terrainSlot, StringComparison.Ordinal);
        Assert.Contains("_texture_content_hash(requested_texture)", terrainSlot, StringComparison.Ordinal);
        Assert.Contains("_texture_content_hash(texture) == requested_hash", terrainSlot, StringComparison.Ordinal);
        Assert.Contains("matched_slot != -1", terrainSlot, StringComparison.Ordinal);
        Assert.Contains("terrain.Save(false)", terrainSlot, StringComparison.Ordinal);
        Assert.Contains("saved.get(\"texture_\" + str(slot + 1), null)", terrainSlot, StringComparison.Ordinal);
        Assert.Contains("_saved_terrain_texture_identity(saved_identity) == resource_identity", terrainSlot, StringComparison.Ordinal);

        var savedIdentity = FunctionBody(script, "_saved_terrain_texture_identity");
        Assert.Contains("typeof(value) == TYPE_STRING", savedIdentity, StringComparison.Ordinal);
        Assert.Contains("value is Resource", savedIdentity, StringComparison.Ordinal);
        Assert.Contains("str(value.resource_path)", savedIdentity, StringComparison.Ordinal);

        var textureHash = FunctionBody(script, "_texture_content_hash");
        Assert.Contains("texture.get_data()", textureHash, StringComparison.Ordinal);
        Assert.Contains("_sha256_bytes(image.get_data())", textureHash, StringComparison.Ordinal);

        var rollback = FunctionBody(script, "_reverse_surface_rollback");
        Assert.Contains("rollback.record.expanded_slots", rollback, StringComparison.Ordinal);
        Assert.Contains("_load_surface_rollback_payload", rollback, StringComparison.Ordinal);

        var persistence = FunctionBody(script, "_persist_surface_rollback");
        Assert.Contains("_save_surface_rollback_payload", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("ResourceSaver.save(primary_path, primary)", persistence, StringComparison.Ordinal);
        var savePayload = FunctionBody(script, "_save_surface_rollback_payload");
        Assert.Contains("value is Image", savePayload, StringComparison.Ordinal);
        Assert.Contains("value.save_png(image_path)", savePayload, StringComparison.Ordinal);
        Assert.Contains("value is BitMap", savePayload, StringComparison.Ordinal);
        Assert.Contains("ResourceSaver.save(resource_path, value)", savePayload, StringComparison.Ordinal);

        var pattern = FunctionBody(script, "_execute_pattern_region");
        Assert.Contains("level.PatternShapes.DrawPolygon", pattern, StringComparison.Ordinal);
        Assert.Contains("pattern_tool.Texture", pattern, StringComparison.Ordinal);
        Assert.Contains("pattern_tool.Rotation", pattern, StringComparison.Ordinal);
        Assert.Contains("pattern_tool.SetLayer", pattern, StringComparison.Ordinal);
        Assert.Contains("level.PatternShapes.GetShapes()", pattern, StringComparison.Ordinal);

        var cave = FunctionBody(script, "_execute_cave_region");
        Assert.Contains("cave_mesh.bitmap.duplicate()", cave, StringComparison.Ordinal);
        Assert.Contains("cave_mesh.SetCircle", cave, StringComparison.Ordinal);
        Assert.Contains("cave_mesh.OnDrawingBegin()", cave, StringComparison.Ordinal);
        Assert.Contains("cave_mesh.OnDrawingEnd()", cave, StringComparison.Ordinal);
        Assert.Contains("cave_mesh.IsMeshWorkerBusy", cave, StringComparison.Ordinal);
        Assert.Contains("_persist_surface_rollback", cave, StringComparison.Ordinal);

        var roof = FunctionBody(script, "_execute_roof_region");
        Assert.Contains("roof_tool.DrawRect", roof, StringComparison.Ordinal);
        Assert.Contains("roof_tool.FinishShape()", roof, StringComparison.Ordinal);
        Assert.Contains("Global.WorldUI.AddPolyPoint", roof, StringComparison.Ordinal);
        Assert.Contains("level.Roofs.get_children()", roof, StringComparison.Ordinal);
    }

    [Fact]
    public void SurfaceCleanupIsRetryableAndAddressableReversalPreservesManualSelection()
    {
        var script = ReadBridge();

        Assert.Contains("_select_tool_has_active_selection(select_tool)", script, StringComparison.Ordinal);
        Assert.Contains("selection_busy", script, StringComparison.Ordinal);
        Assert.Contains("_remove_directory_tree_bounded(rollback_path, 4096)", script, StringComparison.Ordinal);

        var rollbackRemoval = script.IndexOf("_remove_directory_tree_bounded(rollback_path, 4096)", StringComparison.Ordinal);
        var journalRemoval = script.IndexOf("_remove_file(job_path)", rollbackRemoval, StringComparison.Ordinal);
        Assert.True(rollbackRemoval >= 0 && journalRemoval > rollbackRemoval,
            "Rollback evidence must be removed before the journal so cleanup can be retried after a crash.");
    }

    [Fact]
    public void SurfaceObservationAndReversalAreTypeBoundAndFailClosed()
    {
        var script = ReadBridge();
        var observation = FunctionBody(script, "_observe_operation");
        var reversal = FunctionBody(script, "_reverse_operation");
        var reversed = FunctionBody(script, "_observe_reversal");

        Assert.Contains("operation.operation_type", observation, StringComparison.Ordinal);
        Assert.Contains("_observe_surface_rollback", observation, StringComparison.Ordinal);
        Assert.Contains("_observe_addressable_surface", observation, StringComparison.Ordinal);
        Assert.Contains("_reverse_surface_rollback", reversal, StringComparison.Ordinal);
        Assert.Contains("_reverse_addressable_surface", reversal, StringComparison.Ordinal);
        Assert.Contains("_observe_surface_rollback_reversal", reversed, StringComparison.Ordinal);
        Assert.Contains("_observe_addressable_surface_reversal", reversed, StringComparison.Ordinal);
    }

    [Fact]
    public void SurfaceFailuresWriteOnlyBoundedPathFreeStageEvidence()
    {
        var script = ReadBridge();
        var failure = FunctionBody(script, "_surface_failure");

        Assert.Contains("surface-stage.json", failure, StringComparison.Ordinal);
        Assert.Contains("operation_type", failure, StringComparison.Ordinal);
        Assert.Contains("stage", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("asset_ref", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("resource_identity", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("resource_path", failure, StringComparison.Ordinal);

        var terrain = FunctionBody(script, "_execute_terrain_stroke");
        var paint = FunctionBody(script, "_paint_terrain_splat");
        var failureStages = FunctionBody(script, "_surface_failure");
        foreach (var stage in new[]
                 {
                     "context", "native_state", "native_state_brush_radius", "native_state_pixel_input",
                     "native_state_pixel_bound", "native_state_pixel_coordinate", "native_state_pixel_coordinate_input",
                     "native_state_pixel_coordinate_image", "native_state_pixel_coordinate_position", "native_state_pixel_coordinate_world",
                     "native_state_pixel_coordinate_dimensions", "native_state_pixel_coordinate_missing", "native_state_pixel_coordinate_width",
                     "native_state_pixel_coordinate_height", "native_state_pixel_coordinate_grid", "native_state_pixel_coordinate_scale", "native_state_pixel_blend",
                     "rollback_persistence", "texture_load", "sampling",
                     "painting_busy", "after_state",
                 })
        {
            Assert.True(terrain.Contains($"\"{stage}\"", StringComparison.Ordinal) ||
                        paint.Contains($"\"{stage}\"", StringComparison.Ordinal) ||
                        failureStages.Contains($"\"{stage}\"", StringComparison.Ordinal));
        }

        var persistence = FunctionBody(script, "_persist_surface_rollback");
        Assert.Contains("\"stage\": \"rollback_primary_missing\"", persistence, StringComparison.Ordinal);
        Assert.Contains("\"stage\": \"rollback_hash_invalid\"", persistence, StringComparison.Ordinal);
        Assert.Contains("\"stage\": \"rollback_texture_missing\"", persistence, StringComparison.Ordinal);
        Assert.Contains("\"stage\": \"rollback_texture_identity_missing\"", persistence, StringComparison.Ordinal);
        Assert.Contains("\"stage\": \"rollback_directory\"", persistence, StringComparison.Ordinal);
        Assert.Contains("\"stage\": \"rollback_primary_write\"", persistence, StringComparison.Ordinal);
        Assert.Contains("\"stage\": \"rollback_secondary_write\"", persistence, StringComparison.Ordinal);
        Assert.Contains("\"stage\": \"rollback_hash\"", persistence, StringComparison.Ordinal);
        Assert.Contains("\"stage\": \"rollback_metadata\"", persistence, StringComparison.Ordinal);

        var terrainDiagnostic = FunctionBody(script, "_terrain_texture_diagnostic");
        Assert.Contains("terrain-slot.json", terrainDiagnostic, StringComparison.Ordinal);
        Assert.Contains("for slot in range(8)", terrainDiagnostic, StringComparison.Ordinal);
        Assert.Contains("typeof(texture)", terrainDiagnostic, StringComparison.Ordinal);
        Assert.Contains("same_object", terrainDiagnostic, StringComparison.Ordinal);
        Assert.Contains("path_present", terrainDiagnostic, StringComparison.Ordinal);
        Assert.Contains("hash_matches", terrainDiagnostic, StringComparison.Ordinal);
        Assert.Contains("saved_has_key", terrainDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("resource_identity\"", terrainDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("resource_path\"", terrainDiagnostic, StringComparison.Ordinal);

        var terrainSlot = FunctionBody(script, "_terrain_texture_slot");
        Assert.Contains("typeof(terrain.textures) == TYPE_ARRAY", terrainSlot, StringComparison.Ordinal);
        Assert.Contains("terrain.textures.size()", terrainSlot, StringComparison.Ordinal);
        Assert.Contains("terrain.textures[slot]", terrainSlot, StringComparison.Ordinal);

        var terrainExecutor = FunctionBody(script, "_execute_terrain_stroke");
        Assert.Contains("var brush_offset = Vector2.ONE * (-float(brush_radius) - 32.0)", terrainExecutor, StringComparison.Ordinal);
        Assert.Contains("_paint_terrain_splat", terrainExecutor, StringComparison.Ordinal);
        Assert.DoesNotContain("terrain.Paint", FunctionBody(script, "_paint_terrain_splat"), StringComparison.Ordinal);

        var terrainBlend = FunctionBody(script, "_paint_terrain_splat");
        Assert.Contains("MAXIMUM_TERRAIN_BLEND_PIXELS", terrainBlend, StringComparison.Ordinal);
        Assert.Contains("_terrain_world_to_texture(primary", terrainBlend, StringComparison.Ordinal);
        Assert.Contains("Vector2(float(point.x) * grid_size + float(brush_offset.x)", terrainBlend, StringComparison.Ordinal);
        Assert.DoesNotContain("terrain.WorldToTexture", terrainBlend, StringComparison.Ordinal);
        Assert.Contains("_blend_terrain_pixels", terrainBlend, StringComparison.Ordinal);
        Assert.Contains("native_state_managed_adapter_required", terrainBlend, StringComparison.Ordinal);
        Assert.Contains("native_state_pixel_bound", terrainBlend, StringComparison.Ordinal);
        Assert.Contains("destination_result.stage", terrainBlend, StringComparison.Ordinal);
        Assert.Contains("native_state_pixel_blend", terrainBlend, StringComparison.Ordinal);
        Assert.Contains("native_state_pixel_candidate_unchanged", terrainBlend, StringComparison.Ordinal);

        var liveTerrainBlend = FunctionBody(script, "_blend_terrain_pixels");
        Assert.Contains("brush.lock()", liveTerrainBlend, StringComparison.Ordinal);
        Assert.Contains("active.lock()", liveTerrainBlend, StringComparison.Ordinal);
        Assert.Contains("inactive.lock()", liveTerrainBlend, StringComparison.Ordinal);
        Assert.Contains("get_pixel", liveTerrainBlend, StringComparison.Ordinal);
        Assert.Contains("set_pixel", liveTerrainBlend, StringComparison.Ordinal);
        Assert.Contains("linear_interpolate", liveTerrainBlend, StringComparison.Ordinal);
        Assert.DoesNotContain("blend_towards_channel", liveTerrainBlend, StringComparison.Ordinal);


        var terrainBrushRadius = FunctionBody(script, "_terrain_brush_radius");
        Assert.Contains("brush.get_width()", terrainBrushRadius, StringComparison.Ordinal);
        Assert.Contains("brush.get_height()", terrainBrushRadius, StringComparison.Ordinal);
        Assert.Contains("* 16.0", terrainBrushRadius, StringComparison.Ordinal);

        var worldToTexture = FunctionBody(script, "_terrain_world_to_texture");
        Assert.Contains("primary.get_width()", worldToTexture, StringComparison.Ordinal);
        Assert.DoesNotContain("primary.get_width() - 1", worldToTexture, StringComparison.Ordinal);
        Assert.Contains("canvas_width", worldToTexture, StringComparison.Ordinal);
        Assert.DoesNotContain("Global.World.Width", worldToTexture, StringComparison.Ordinal);
        Assert.Contains("grid_size", worldToTexture, StringComparison.Ordinal);
        Assert.Contains("var map_width = float(canvas_width)", worldToTexture, StringComparison.Ordinal);
        Assert.Contains("map_width != floor(map_width)", worldToTexture, StringComparison.Ordinal);
        Assert.Contains("primary.get_width() != int(map_width) * 4", worldToTexture, StringComparison.Ordinal);
        Assert.Contains("world_position / 64.0 + Vector2.ONE * 0.5", worldToTexture, StringComparison.Ordinal);

    }

    [Fact(Timeout = 60_000)]
    public async Task ExactGodot353FailsTerrainClosedAndExecutesTheSupportedSurfaceFamilies()
    {
        var repositoryRoot = FindRepositoryRoot();
        var godotPath = Path.Combine(
            repositoryRoot, "tools", "godot-3.5.3",
            "Godot_v3.5.3-stable_win64.exe");
        var fixtureRoot = Path.Combine(
            repositoryRoot, "tests", "DDAI.Core.Tests", "Executors", "GodotFixtures", "SurfaceExecutors");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "ddai-surface-executors-" + Guid.NewGuid().ToString("N"));
        var applicationName = "DDAI Surface Executor Harness " + Guid.NewGuid().ToString("N");
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
                    File.WriteAllText(destination, File.ReadAllText(fixture).Replace(
                        "config/name=\"DDAI Surface Executor Harness\"",
                        $"config/name=\"{applicationName}\"", StringComparison.Ordinal));
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
            Assert.True(
                exited,
                $"Pinned Godot surface executor harness timed out.{Environment.NewLine}stdout:{Environment.NewLine}{output}{Environment.NewLine}stderr:{Environment.NewLine}{error}");
            Assert.True(process.ExitCode == 0, $"Godot exited {process.ExitCode}.\nstdout:\n{output}\nstderr:\n{error}");
            Assert.True(string.IsNullOrEmpty(error), $"Godot stderr:\n{error}");
            foreach (var receipt in new[]
                     {
                         "DDAI_SURFACE_TERRAIN_FAILS_CLOSED:True",
                         "DDAI_SURFACE_PATTERN:True",
                         "DDAI_SURFACE_COLORABLE_PATTERN:True",
                         "DDAI_SURFACE_CAVE:True",
                         "DDAI_SURFACE_ROOF:True",
                         "DDAI_SURFACE_TOOL_STATE:True",
                         "DDAI_SURFACE_ROLLBACK_TAMPER:True",
                         "DDAI_SURFACE_CAPABILITY_WITHHELD:True",
                         "DDAI_OBJECT_SAVE_PERSISTENCE:True",
                         "DDAI_LIGHT_SAVE_PERSISTENCE:True",
                         "DDAI_LIGHT_NUMERIC_BOUNDS:True",
                         "DDAI_LIGHT_FINGERPRINT_FIELDS:True",
                     })
            {
                Assert.Contains(receipt, output, StringComparison.Ordinal);
            }

            var fingerprintPrefix = "DDAI_LIGHT_PLAN_FINGERPRINT:";
            var actualFingerprint = Assert.Single(
                output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
                line => line.StartsWith(fingerprintPrefix, StringComparison.Ordinal))[fingerprintPrefix.Length..];
            var expectedPlan = new MapPlan
            {
                SchemaVersion = MapPlan.CurrentSchemaVersion,
                RequestId = "light-fingerprint-001",
                ExpectedMapId = "map-live",
                BaseRevision = 0,
                ExpectedCatalogRevision = 1,
                Mode = MapOperationMode.Add,
                CoordinateSystem = MapCoordinateSystem.Grid,
                Canvas = new MapCanvas(40, 30),
                Operations =
                [
                    new LightPlacementOperation(
                        "light-fingerprint",
                        "0",
                        "sha256:" + new string('a', 64),
                        new GridPoint(3, 4),
                        6,
                        0.75,
                        "#ffeeddcc",
                        true),
                ],
            };
            Assert.Equal(MapPlanJson.Fingerprint(expectedPlan), actualFingerprint);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
            if (Directory.Exists(userDataRoot))
            {
                Directory.Delete(userDataRoot, recursive: true);
            }
        }
    }

    private static string ReadBridge() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(), "mods", "DDAI", "scripts", "ddai_bridge.gd"));

    private static string FunctionBody(string script, string functionName)
    {
        var marker = $"func {functionName}(";
        var start = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing function {functionName}.");
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

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
