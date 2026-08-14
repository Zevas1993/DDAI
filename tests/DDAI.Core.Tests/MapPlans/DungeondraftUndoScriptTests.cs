using System.Diagnostics;

namespace DDAI.Core.Tests.MapPlans;

public sealed class DungeondraftUndoScriptTests
{
    [Fact]
    public void UndoIsASeparateDurableCommandAndNeverUsesGenericEditorUndo()
    {
        var script = ReadBridge();

        Assert.Contains("\"undo_last_job\"", script, StringComparison.Ordinal);
        Assert.Contains("func _advance_undo_last_job_claim(", script, StringComparison.Ordinal);
        Assert.Contains("map-undoable", script, StringComparison.Ordinal);
        Assert.Contains("undo-jobs", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Global.Editor.Undo", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Input.action_press", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactGodot353ReversesOnlyTheBoundCompletedJobAndRejectsInterveningEdits()
    {
        var repositoryRoot = FindRepositoryRoot();
        var godot = Path.Combine(repositoryRoot, "tools", "godot-3.5.3", "Godot_v3.5.3-stable_win64.exe");
        var fixture = Path.Combine(repositoryRoot, "tests", "DDAI.Core.Tests", "MapPlans", "GodotFixtures", "UniversalPlanExecutor");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "ddai-undo-godot-" + Guid.NewGuid().ToString("N"));
        var userDataRoot = Path.Combine(Path.GetTempPath(), "ddai-undo-user-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        Directory.CreateDirectory(userDataRoot);
        try
        {
            File.Copy(Path.Combine(repositoryRoot, "mods", "DDAI", "scripts", "ddai_bridge.gd"), Path.Combine(temporaryRoot, "ddai_bridge.gd"));
            foreach (var name in new[] { "project.godot", "main.tscn", "global.gd", "main.gd" })
            {
                File.Copy(Path.Combine(fixture, name), Path.Combine(temporaryRoot, name));
            }

            var start = new ProcessStartInfo(godot)
            {
                WorkingDirectory = temporaryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            start.ArgumentList.Add("--path");
            start.ArgumentList.Add(temporaryRoot);
            start.ArgumentList.Add("--no-window");
            start.ArgumentList.Add("--scene");
            start.ArgumentList.Add("res://main.tscn");
            start.Environment["APPDATA"] = userDataRoot;
            using var process = Process.Start(start)!;
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
            Assert.True(exited, $"Pinned Godot undo harness timed out.\nstdout:\n{output}\nstderr:\n{error}");
            Assert.True(process.ExitCode == 0, $"Godot exited {process.ExitCode}.\nstdout:\n{output}\nstderr:\n{error}");
            Assert.True(string.IsNullOrWhiteSpace(error), error);
            Assert.Contains("DDAI_UNDO_EXACT_COMPLETED_JOB:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_UNDO_INTERVENING_EDIT_REJECTED:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_UNDO_DUPLICATE_IDEMPOTENT:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_UNDO_MISSING_NODE_REJECTED:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_UNDO_INTERRUPTED_BEFORE_REVERSAL_UNKNOWN:True", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
            if (Directory.Exists(userDataRoot)) Directory.Delete(userDataRoot, recursive: true);
        }
    }

    private static string ReadBridge() => File.ReadAllText(Path.Combine(FindRepositoryRoot(), "mods", "DDAI", "scripts", "ddai_bridge.gd"));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "DDAI.slnx"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
