using DDAI.Core.Assets;

namespace DDAI.Core.Tests.Assets;

public sealed class DungeondraftAssetCatalogScriptTests
{
    [Fact]
    public void Script_EnumeratesEveryCanonicalCategoryThroughTheDocumentedToolApi()
    {
        var script = ReadCatalogScript();
        string[] expectedCategories =
        [
            "Terrain", "Patterns", "Patterns Colorable", "Caves", "Roofs",
            "Objects", "Walls", "Materials", "Portals", "Paths", "Lights",
            "Simple Tiles", "Smart Tiles", "Smart Tiles Double",
        ];

        Assert.Equal(expectedCategories, AssetCategory.All);
        foreach (var category in expectedCategories)
        {
            Assert.Contains($"\"{category}\"", script, StringComparison.Ordinal);
        }

        Assert.Contains("Script.GetAssetList(category)", script, StringComparison.Ordinal);
        Assert.Contains("Global.Header.AssetManifest", script, StringComparison.Ordinal);
        Assert.Contains("AllowThirdPartyUse", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_BoundsPerFrameWorkPreviewsAndChunkPayloads()
    {
        var script = ReadCatalogScript();
        var previewPreparation = FunctionBody(script, "_prepare_preview");

        Assert.Equal("8", ConstantValue(script, "MAX_ASSETS_PER_TICK"));
        Assert.Equal("256", ConstantValue(script, "MAX_PREVIEW_EDGE"));
        Assert.Equal("262144", ConstantValue(script, "MAX_PREVIEW_BYTES"));
        Assert.Equal("900000", ConstantValue(script, "MAX_CHUNK_BYTES"));
        Assert.Contains("HashingContext.HASH_SHA256", script, StringComparison.Ordinal);
        Assert.Contains("var source_image = texture.get_data()", previewPreparation, StringComparison.Ordinal);
        Assert.Contains("source_image == null", previewPreparation, StringComparison.Ordinal);
        Assert.Contains("var image = source_image.duplicate()", previewPreparation, StringComparison.Ordinal);
        Assert.True(
            previewPreparation.IndexOf("source_image == null", StringComparison.Ordinal) <
            previewPreparation.IndexOf("source_image.duplicate()", StringComparison.Ordinal));
        Assert.Contains("Image.INTERPOLATE_LANCZOS", script, StringComparison.Ordinal);
        Assert.Contains("save_png_to_buffer()", script, StringComparison.Ordinal);
        Assert.Contains("func _advance_preview_publication_state():", script, StringComparison.Ordinal);
        Assert.Contains("func _advance_preview_finalization_state():", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_PublishesStrictOpaqueSnapshotWireContractWithCurrentPointerLast()
    {
        var script = ReadCatalogScript();
        var entryBuilder = FunctionBody(script, "_build_catalog_entry");
        var publisher = FunctionBody(script, "_advance_publishing_state");

        foreach (var field in new[]
                 {
                     "asset_ref", "category", "display_name", "resource_fingerprint", "pack_id",
                     "pack_name", "search_terms", "tags", "preview_hash", "allow_third_party_use", "generated",
                 })
        {
            Assert.Contains($"\"{field}\"", entryBuilder, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("\"resource_identity\"", entryBuilder, StringComparison.Ordinal);
        Assert.Contains("preview_hash = null", script, StringComparison.Ordinal);
        Assert.Contains("preview_not_available", script, StringComparison.Ordinal);
        Assert.Contains("catalog/snapshots", script, StringComparison.Ordinal);
        Assert.Contains("catalog/previews", script, StringComparison.Ordinal);
        Assert.Contains("catalog/current.json", script, StringComparison.Ordinal);
        Assert.Contains("current-slot-0.json", script, StringComparison.Ordinal);
        Assert.Contains("current-slot-1.json", script, StringComparison.Ordinal);
        Assert.True(
            publisher.IndexOf("MANIFEST_FILE_NAME", StringComparison.Ordinal) <
            publisher.IndexOf("CURRENT_SLOT_FILE_NAMES", StringComparison.Ordinal) &&
            publisher.IndexOf("CURRENT_SLOT_FILE_NAMES", StringComparison.Ordinal) <
            publisher.IndexOf("CURRENT_POINTER_FILE_NAME", StringComparison.Ordinal),
            "The immutable manifest and recoverable slot must be published before the mutable current pointer.");
    }

    [Fact]
    public void Script_ImplementsEveryIncrementalPublicationState()
    {
        var script = ReadCatalogScript();

        foreach (var state in new[]
                 {
                     "waiting_for_receipt", "enumerating", "previewing", "writing_chunks", "publishing",
                 })
        {
            Assert.Contains($"\"{state}\"", script, StringComparison.Ordinal);
        }

        Assert.Contains("runtime-receipt.json", script, StringComparison.Ordinal);
        Assert.Contains("category_counts", script, StringComparison.Ordinal);
        Assert.Contains("catalog_fingerprint", script, StringComparison.Ordinal);
        Assert.Contains("entry_count", script, StringComparison.Ordinal);
        Assert.Contains("byte_count", script, StringComparison.Ordinal);
        Assert.Contains("MAX_ERROR_RECORDS", script, StringComparison.Ordinal);
        Assert.Contains("MAX_MANIFEST_BYTES", script, StringComparison.Ordinal);
        Assert.Contains("errors_truncated", script, StringComparison.Ordinal);
        Assert.Contains("func _receipt_is_newer(", script, StringComparison.Ordinal);
        Assert.Contains("_manifest_text.to_utf8().size() > MAX_MANIFEST_BYTES", script, StringComparison.Ordinal);
        Assert.Contains("return catalog_root.get_base_dir() + \"/private/pack-normalization\"", script, StringComparison.Ordinal);
        Assert.Contains("+ \"/requests/\"", script, StringComparison.Ordinal);
        Assert.Contains("+ \"/responses/\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("non-ASCII IDs become null", script, StringComparison.Ordinal);
    }

    private static string ReadCatalogScript() => File.ReadAllText(
        Path.Combine(FindRepositoryRoot(), "mods", "DDAI", "scripts", "ddai_asset_catalog.gd"));

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
        var marker = "const " + constantName + " = ";
        var start = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected GDScript constant {constantName}.");
        start += marker.Length;
        var end = script.IndexOfAny(['\r', '\n'], start);
        return script[start..end].Trim();
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
