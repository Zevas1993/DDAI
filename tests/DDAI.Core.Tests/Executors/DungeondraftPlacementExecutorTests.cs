using System.IO;

namespace DDAI.Core.Tests.Executors;

public sealed class DungeondraftPlacementExecutorTests
{
    [Fact]
    public void ObjectPlacementUsesTheDocumentedObjectToolRoute()
    {
        var body = FunctionBody(ReadBridge(), "_execute_object_placement");

        // Property casing is taken from the published ObjectTool reference: the
        // texture property is lowercase, unlike every other tool in the matrix.
        Assert.Contains("object_tool.texture = texture", body, StringComparison.Ordinal);
        Assert.Contains("object_tool.Rotation.value", body, StringComparison.Ordinal);
        Assert.Contains("object_tool.Scale.value", body, StringComparison.Ordinal);
        Assert.Contains("object_tool.SetLayer(", body, StringComparison.Ordinal);
        Assert.Contains("object_tool.SetSorting(", body, StringComparison.Ordinal);
        Assert.Contains("object_tool.SetShadow(", body, StringComparison.Ordinal);
        Assert.Contains("object_tool.SetBlockLight(", body, StringComparison.Ordinal);

        // Confirm() internally calls Record() then Next(), so the preview must be
        // created and positioned before it, never after.
        Assert.Contains("object_tool.Next()", body, StringComparison.Ordinal);
        Assert.Contains("object_tool.Preview", body, StringComparison.Ordinal);
        Assert.Contains("object_tool.Confirm()", body, StringComparison.Ordinal);
        Assert.True(
            body.IndexOf("object_tool.Next()", StringComparison.Ordinal)
                < body.IndexOf("object_tool.Confirm()", StringComparison.Ordinal),
            "The preview must be created before Confirm().");
    }

    [Fact]
    public void ObjectPlacementRestoresToolStateAndObservesExactlyOneNewObject()
    {
        var body = FunctionBody(ReadBridge(), "_execute_object_placement");

        foreach (var prior in new[]
                 {
                     "prior_texture", "prior_rotation", "prior_scale",
                     "prior_layer", "prior_sorting", "prior_shadow", "prior_block_light",
                 })
        {
            Assert.Contains(prior, body, StringComparison.Ordinal);
        }

        // A returned method call is not success. Confirm() also creates a fresh
        // preview, so the observation must pin down exactly one new placed object.
        Assert.Contains("objects_before", body, StringComparison.Ordinal);
        Assert.Contains("objects_after", body, StringComparison.Ordinal);
        Assert.Contains("_single_new_node(objects_before, objects_after)", body, StringComparison.Ordinal);
        Assert.Contains("_ensure_registered_node(", body, StringComparison.Ordinal);
        Assert.Contains("untracked_change", body, StringComparison.Ordinal);
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
    public void ObjectPlacementRemainsUncertifiedUntilLiveProof()
    {
        var bridge = ReadBridge();

        // Implementing an executor must never publish it. Certification requires a
        // disposable-map create, observe, reverse, save and reopen proof.
        Assert.Contains(
            "var _runtime_certified_operation_types = [\"wall_polyline\"]",
            bridge,
            StringComparison.Ordinal);
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
