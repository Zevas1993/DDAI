using System.Diagnostics;

namespace DDAI.Core.Tests.Executors;

public sealed class DungeondraftOperationCertifierTests
{
    [Fact(Timeout = 60_000)]
    public async Task ExactGodot353ProbesAllOperationsWithoutCallingMutationMethods()
    {
        var repositoryRoot = FindRepositoryRoot();
        var godotPath = Path.Combine(
            repositoryRoot,
            "artifacts", "rectangular-room", "tooling", "godot-3.5.3",
            "Godot_v3.5.3-stable_win64.exe");
        var fixtureRoot = Path.Combine(
            repositoryRoot,
            "tests", "DDAI.Core.Tests", "Executors", "GodotFixtures", "OperationCertifier");
        Assert.True(File.Exists(godotPath), $"Pinned Godot 3.5.3 runtime not found: {godotPath}");

        var temporaryRoot = Path.Combine(Path.GetTempPath(), "ddai-operation-certifier-" + Guid.NewGuid().ToString("N"));
        var applicationName = "DDAI Operation Certifier " + Guid.NewGuid().ToString("N");
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
                    File.WriteAllText(
                        destination,
                        File.ReadAllText(fixture).Replace(
                            "config/name=\"DDAI Operation Certifier\"",
                            $"config/name=\"{applicationName}\"",
                            StringComparison.Ordinal));
                }
                else
                {
                    File.Copy(fixture, destination);
                }
            }

            File.Copy(
                Path.Combine(repositoryRoot, "mods", "DDAI", "scripts", "ddai_operation_certifier.gd"),
                Path.Combine(temporaryRoot, "ddai_operation_certifier.gd"));

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
            Assert.True(exited, "Pinned Godot operation certifier harness timed out.");
            Assert.True(process.ExitCode == 0, $"Godot exited {process.ExitCode}.\nstdout:\n{output}\nstderr:\n{error}");
            Assert.True(string.IsNullOrEmpty(error), $"Godot stderr:\n{error}");
            Assert.Contains("DDAI_CERTIFIER_EXACT_FOURTEEN:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_CERTIFIER_ALL_AVAILABLE:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_CERTIFIER_ONLY_WALL_CERTIFIED:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_CERTIFIER_VERSION_BOUND:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_CERTIFIER_READ_ONLY:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_CERTIFIER_OPAQUE_PROXY_AVAILABLE:True", output, StringComparison.Ordinal);
            Assert.Contains("DDAI_CERTIFIER_MISMATCHED_IDENTITY_CLOSED:True", output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
            if (Directory.Exists(userDataRoot)) Directory.Delete(userDataRoot, recursive: true);
        }
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
