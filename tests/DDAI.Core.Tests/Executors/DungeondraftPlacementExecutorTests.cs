using System.IO;

namespace DDAI.Core.Tests.Executors;

public sealed class DungeondraftPlacementExecutorTests
{
    [Fact]
    public void ObjectPlacementUsesTheContainerRouteAndNeverATool()
    {
        var body = FunctionBody(ReadBridge(), "_execute_object_placement");

        // Tool APIs drive the interactive UI and do nothing unless the tool is the
        // user's selected one, which a mod cannot arrange from a background handler.
        // Objects.CreateObject parents a Prop directly and needs no tool at all.
        Assert.Contains("level.Objects.CreateObject(", body, StringComparison.Ordinal);
        Assert.Contains("prop.SetTexture(texture)", body, StringComparison.Ordinal);
        Assert.Contains("prop.position", body, StringComparison.Ordinal);
        Assert.Contains("prop.rotation_degrees", body, StringComparison.Ordinal);
        Assert.Contains("prop.scale", body, StringComparison.Ordinal);
        Assert.Contains("prop.HasShadow", body, StringComparison.Ordinal);
        Assert.Contains("prop.SetBlockLight(", body, StringComparison.Ordinal);

        // CreateObject does not register the Prop in the search index.
        Assert.Contains("AddToSearchTable(", body, StringComparison.Ordinal);

        foreach (var toolCall in new[] { "OnSelectTool", "OnDeselectTool", ".Enable()", ".Disable()", ".Next()", ".Confirm()" })
        {
            Assert.DoesNotContain(toolCall, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ObjectPlacementObservesExactlyOneNewChild()
    {
        var body = FunctionBody(ReadBridge(), "_execute_object_placement");

        Assert.Contains("objects_before", body, StringComparison.Ordinal);
        Assert.Contains("created_nodes.size() != 1", body, StringComparison.Ordinal);
        Assert.Contains("_ensure_registered_node(", body, StringComparison.Ordinal);
        Assert.Contains("untracked_change", body, StringComparison.Ordinal);
    }

    [Fact]
    public void NodeRegistrationProvesIdentityByLookupNotByMagnitude()
    {
        var body = FunctionBody(ReadBridge(), "_ensure_registered_node");

        // Zero is a valid node id. World.AssignNodeID hands out nextNodeID then
        // increments, so the first node registered in a session legitimately gets
        // 0, and HasNodeID(0) resolves it. Live evidence: assign returned 0 with
        // nextNodeID advancing to 1 and the lookup round trip succeeding.
        // Rejecting ids by magnitude would refuse the first object on a fresh map.
        Assert.DoesNotContain("node_id.value) > 0", body, StringComparison.Ordinal);
        Assert.DoesNotContain("node_id.value) <= 0", body, StringComparison.Ordinal);

        // Identity is proven by the round trip instead.
        Assert.Contains("Global.World.HasNodeID(int(node_id.value))", body, StringComparison.Ordinal);
        Assert.Contains("Global.World.GetNodeByID(int(node_id.value)) == node", body, StringComparison.Ordinal);
        Assert.Contains("persisted.value != node_id.value", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectPlacementIsRegisteredAndReversible()
    {
        var bridge = ReadBridge();

        Assert.Contains(
            "\"object_placement\": funcref(self, \"_execute_object_placement\")",
            bridge,
            StringComparison.Ordinal);

        var reverse = FunctionBody(bridge, "_reverse_operation");
        Assert.Contains("object_placement", reverse, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyLiveProvenOperationsArePublished()
    {
        var bridge = ReadBridge();

        // object_placement was certified on 2026-08-16 against Dungeondraft 1.2.0.1:
        // applied at the exact requested grid position, observed with its node id
        // recorded, committed, then reversed with the map fingerprint returning to
        // its pre-apply value. Everything else stays withheld until it has the same
        // create, observe and reverse proof on a disposable map.
        Assert.Contains(
            "var _runtime_certified_operation_types = [\"wall_polyline\", \"object_placement\"]",
            bridge,
            StringComparison.Ordinal);

        foreach (var uncertified in new[]
                 {
                     "terrain_stroke", "pattern_region", "colorable_pattern_region", "cave_region",
                     "roof_region", "material_stroke", "portal_placement", "path_polyline",
                     "light_placement", "simple_tile_region", "smart_tile_region", "smart_tile_double_region",
                 })
        {
            Assert.DoesNotContain(
                $"\"{uncertified}\"]",
                FunctionBody(bridge, "_certified_operation_types"),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PathPolylineUsesTheDocumentedPathToolAndPathwayRoute()
    {
        var body = FunctionBody(ReadBridge(), "_execute_path_polyline");

        Assert.Contains("path_tool.Texture = texture", body, StringComparison.Ordinal);
        Assert.Contains("path_tool.Width", body, StringComparison.Ordinal);
        Assert.Contains("path_tool.Smoothness", body, StringComparison.Ordinal);
        Assert.Contains("path_tool.SetLayer(", body, StringComparison.Ordinal);
        Assert.Contains("path_tool.SetSorting(", body, StringComparison.Ordinal);
        Assert.Contains("path_tool.SetFadeIn(", body, StringComparison.Ordinal);
        Assert.Contains("path_tool.SetFadeOut(", body, StringComparison.Ordinal);

        // Points are set on the active Pathway in world space, then Smooth()
        // regenerates the visual, then the path is ended with the loop flag.
        Assert.Contains("path_tool.StartPath()", body, StringComparison.Ordinal);
        Assert.Contains("path_tool.ActivePath", body, StringComparison.Ordinal);
        Assert.Contains("SetEditPoints(", body, StringComparison.Ordinal);
        Assert.Contains("Smooth()", body, StringComparison.Ordinal);
        Assert.Contains("path_tool.EndPath(", body, StringComparison.Ordinal);

        Assert.True(
            body.IndexOf("path_tool.StartPath()", StringComparison.Ordinal)
                < body.IndexOf("SetEditPoints(", StringComparison.Ordinal),
            "The path must be started before its edit points are set.");
        Assert.True(
            body.IndexOf("SetEditPoints(", StringComparison.Ordinal)
                < body.IndexOf("path_tool.EndPath(", StringComparison.Ordinal),
            "Edit points must be set before the path is ended.");
    }

    [Fact]
    public void PathPolylineRejectsABusyToolAndObservesExactlyOneNewPathway()
    {
        var body = FunctionBody(ReadBridge(), "_execute_path_polyline");

        Assert.Contains("path_tool.isDrawing", body, StringComparison.Ordinal);
        Assert.Contains("pathways_before", body, StringComparison.Ordinal);
        Assert.Contains("pathways_after", body, StringComparison.Ordinal);
        Assert.Contains("_single_new_node(pathways_before, pathways_after)", body, StringComparison.Ordinal);
        Assert.Contains("_ensure_registered_node(", body, StringComparison.Ordinal);
        Assert.Contains("untracked_change", body, StringComparison.Ordinal);
    }

    [Fact]
    public void PathPolylineIsRegisteredAndReversible()
    {
        var bridge = ReadBridge();

        Assert.Contains(
            "\"path_polyline\": funcref(self, \"_execute_path_polyline\")",
            bridge,
            StringComparison.Ordinal);
        Assert.Contains("path_polyline", FunctionBody(bridge, "_reverse_operation"), StringComparison.Ordinal);
    }

    [Fact]
    public void FloorShapeRegionSelectsTheTilesetAndUsesTheShapeToolRoute()
    {
        var body = FunctionBody(ReadBridge(), "_execute_floor_shape_region");

        // FloorShapeTool extends ShapeTool, the same base as RoofTool, whose Mode
        // plus DrawRect/FinishShape route is already proven live here.
        Assert.Contains("floor_tool.SmartTileId", body, StringComparison.Ordinal);
        Assert.Contains("floor_tool.Mode", body, StringComparison.Ordinal);
        Assert.Contains("floor_tool.DrawRect(", body, StringComparison.Ordinal);
        Assert.Contains("floor_tool.FinishShape()", body, StringComparison.Ordinal);
        Assert.Contains("Global.WorldUI.AddPolyPoint(", body, StringComparison.Ordinal);
        Assert.Contains("floor_tool.isDragging", body, StringComparison.Ordinal);

        Assert.Contains("shapes_before", body, StringComparison.Ordinal);
        Assert.Contains("shapes_after", body, StringComparison.Ordinal);
        Assert.Contains("_single_new_node(shapes_before, shapes_after)", body, StringComparison.Ordinal);
        Assert.Contains("untracked_change", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AllThreeTileCategoriesAreRegisteredAndReversible()
    {
        var bridge = ReadBridge();

        // Three distinct categories share one executor but must never be
        // interchangeable at the plan layer; each registers its own entry.
        foreach (var category in new[] { "simple_tile_region", "smart_tile_region", "smart_tile_double_region" })
        {
            Assert.Contains(
                $"\"{category}\": funcref(self, \"_execute_floor_shape_region\")",
                bridge,
                StringComparison.Ordinal);
            Assert.Contains(category, FunctionBody(bridge, "_reverse_operation"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryRegisteredExecutorAlsoDeclaresItsOperationFieldSchema()
    {
        var bridge = ReadBridge();
        var validation = FunctionBody(bridge, "_validate_universal_operation");

        // Registering an executor is not enough. An operation whose fields are not
        // declared in expected_keys is rejected as unsupported_operation, which
        // reads like the executor is missing rather than its schema. Live proof of
        // object_placement failed on exactly this before the schemas were added.
        var registered = System.Text.RegularExpressions.Regex
            .Matches(bridge, @"""(?<op>[a-z_]+)"": funcref\(self, ""_execute_")
            .Select(match => match.Groups["op"].Value)
            .Distinct();

        foreach (var operation in registered)
        {
            Assert.True(
                validation.Contains($"\"{operation}\": [", StringComparison.Ordinal),
                $"Executor '{operation}' is registered but declares no field schema, so every plan using it is refused.");
        }
    }

    private static string ReadBridge() =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "mods", "DDAI", "scripts", "ddai_bridge.gd"));

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
