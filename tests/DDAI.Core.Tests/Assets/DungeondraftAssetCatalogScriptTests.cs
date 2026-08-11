using DDAI.Core.Assets;
using System.Diagnostics;

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
    public async Task Script_AcceptsGodotStringArraysButStillRejectsOtherEnumerationTypes()
    {
        var repositoryRoot = FindRepositoryRoot();
        var godotPath = Path.Combine(
            repositoryRoot,
            "artifacts", "rectangular-room", "tooling", "godot-3.5.3",
            "Godot_v3.5.3-stable_win64.exe");
        Assert.True(
            File.Exists(godotPath),
            $"The pinned Godot 3.5.3 test runtime is required at '{godotPath}'.");

        var fixtureRoot = Path.Combine(
            repositoryRoot,
            "tests", "DDAI.Core.Tests", "Assets", "GodotFixtures", "AssetCatalogEnumeration");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "ddai-godot-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            foreach (var fixtureName in new[] { "project.godot", "main.tscn", "main.gd", "global.gd" })
            {
                File.Copy(Path.Combine(fixtureRoot, fixtureName), Path.Combine(temporaryRoot, fixtureName));
            }

            var productionScript = ReadCatalogScript();
            const string toolScriptDeclaration = "var script_class = \"tool\"\n";
            Assert.Contains(toolScriptDeclaration, productionScript, StringComparison.Ordinal);
            productionScript = productionScript.Replace(
                toolScriptDeclaration,
                toolScriptDeclaration + "var Script = null\nvar DdaiTestClock = null\n",
                StringComparison.Ordinal);
            const string wireTimestampFunction =
                "func _utc_wire_timestamp():\n\tvar current = OS.get_datetime_from_unix_time(OS.get_unix_time())";
            Assert.Contains(wireTimestampFunction, productionScript, StringComparison.Ordinal);
            productionScript = productionScript.Replace(
                wireTimestampFunction,
                "func _utc_wire_timestamp():\n\tif DdaiTestClock != null:\n\t\treturn DdaiTestClock.now()\n" +
                "\tvar current = OS.get_datetime_from_unix_time(OS.get_unix_time())",
                StringComparison.Ordinal);
            if (Environment.GetEnvironmentVariable("DDAI_GODOT_CATALOG_TEST_MUTATION") == "array-only")
            {
                const string fixedPredicate = "typeof(listed) != TYPE_ARRAY and typeof(listed) != TYPE_STRING_ARRAY";
                Assert.Contains(fixedPredicate, productionScript, StringComparison.Ordinal);
                productionScript = productionScript.Replace(
                    fixedPredicate,
                    "typeof(listed) != TYPE_ARRAY",
                    StringComparison.Ordinal);
            }

            File.WriteAllText(Path.Combine(temporaryRoot, "ddai_asset_catalog.gd"), productionScript);

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
            Assert.True(exited, "The pinned Godot behavior harness timed out.");

            Assert.True(
                process.ExitCode == 0,
                $"The pinned Godot behavior harness exited {process.ExitCode}.{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{output}{Environment.NewLine}stderr:{Environment.NewLine}{error}");
            Assert.Equal(string.Empty, error);
            Assert.Contains("DDAI_TYPED_ARRAY_ENUMERATION:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_WRONG_TYPE_FAILS_CLOSED:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_ENUMERATION_DIAGNOSTIC_CLOSED_WORLD:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_TOOL_SCOPE_LIVE_WIRING:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_SNAPSHOT_TIMESTAMP_FINALIZED_ONCE:True", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
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
    public void Script_StagesStrictOpaqueCandidateAndLeavesPublicPointerCommitToHelper()
    {
        var script = ReadCatalogScript();
        var entryBuilder = FunctionBody(script, "_build_catalog_entry");
        var publisher = FunctionBody(script, "_read_catalog_commit_response");

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
        Assert.Contains("catalog/previews", script, StringComparison.Ordinal);
        Assert.Contains("private/catalog-commit", script, StringComparison.Ordinal);
        Assert.Contains("candidate_fingerprint", script, StringComparison.Ordinal);
        Assert.Contains("request_content_hash", script, StringComparison.Ordinal);
        Assert.Contains("state_token", publisher, StringComparison.Ordinal);
        Assert.DoesNotContain("_replace_bytes_recoverably", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CURRENT_POINTER_FILE_NAME", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CURRENT_SLOT_FILE_NAMES", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_ImplementsEveryIncrementalPublicationState()
    {
        var script = ReadCatalogScript();

        foreach (var state in new[]
                 {
                     "waiting_for_receipt", "enumerating", "previewing", "writing_chunks", "publishing_candidate", "waiting_catalog_commit",
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
        Assert.Contains("helper_verification", script, StringComparison.Ordinal);
        Assert.DoesNotContain("waiting_publication_advice", script, StringComparison.Ordinal);
        Assert.Contains("asset-helper", script, StringComparison.Ordinal);
        Assert.Contains("OS.execute", script, StringComparison.Ordinal);
        Assert.Contains("HELPER_LAUNCH_INTERVAL_MSEC", script, StringComparison.Ordinal);
        Assert.Contains("HELPER_MAX_LAUNCH_ATTEMPTS", script, StringComparison.Ordinal);
        Assert.Contains("pack_normalization_failed", script, StringComparison.Ordinal);
        Assert.Contains("response.has(\"normalized_pack_id\")", script, StringComparison.Ordinal);
        Assert.Contains("_pack_normalization_values", script, StringComparison.Ordinal);
        Assert.Contains("_private_request_is_pending", script, StringComparison.Ordinal);
        Assert.Contains("if not _private_request_is_pending(_catalog_commit_root(), _commit_request_id)", script, StringComparison.Ordinal);
        Assert.Contains("if not _private_request_is_pending(_pack_normalization_root(), _normalization_active_request_id)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("_publication_slot_index", script, StringComparison.Ordinal);
        Assert.DoesNotContain("func _select_current_slot", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_BindsExactCommitRequestBytesAndVerifiesThePublishedSnapshotBeforeAcknowledgement()
    {
        var script = ReadCatalogScript();
        var requester = FunctionBody(script, "_advance_catalog_commit_request_state");
        var verifier = FunctionBody(script, "_read_catalog_commit_response");

        Assert.Contains("_commit_request_hash = _sha256_bytes(payload)", requester, StringComparison.Ordinal);
        Assert.Contains("_commit_request_id = _commit_request_hash", requester, StringComparison.Ordinal);
        Assert.DoesNotContain("\"request_id\"", requester, StringComparison.Ordinal);
        Assert.DoesNotContain("\"request_content_hash\"", requester, StringComparison.Ordinal);
        Assert.Contains("session_id", verifier, StringComparison.Ordinal);
        Assert.Contains("manifest_path", verifier, StringComparison.Ordinal);
        Assert.Contains("current.json", verifier, StringComparison.Ordinal);
        Assert.Contains("current-slot-", verifier, StringComparison.Ordinal);
        Assert.Contains("/snapshots/", verifier, StringComparison.Ordinal);
        Assert.Contains("_catalog_fingerprint(fingerprint_manifest)", verifier, StringComparison.Ordinal);
        Assert.Contains("_catalog_commit_state_token", verifier, StringComparison.Ordinal);
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
