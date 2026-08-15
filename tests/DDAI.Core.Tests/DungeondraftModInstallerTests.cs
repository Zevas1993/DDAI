using System.Diagnostics;
using System.Text.Json;

namespace DDAI.Core.Tests;

public sealed class DungeondraftModInstallerTests
{
    [Fact]
    public void Install_CopiesOnlyDDAIOwnedModAndIsIdempotent()
    {
        using var sandbox = new InstallerSandbox();
        var untouchedMod = Path.Combine(sandbox.ModsDirectory, "existing-mod", "sentinel.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(untouchedMod)!);
        File.WriteAllText(untouchedMod, "do-not-touch");

        var first = RunInstaller(sandbox.RepositoryRoot, sandbox.ModsDirectory, sandbox.UserDataDirectory);
        Assert.True(first.ExitCode == 0, $"Installer failed: {first.StandardOutput} {first.StandardError}");
        Assert.Equal("installed", ReadJson(first.StandardOutput).GetProperty("state").GetString());
        Assert.True(File.Exists(Path.Combine(sandbox.ModsDirectory, "DDAI", "ddai_bridge.ddmod")));
        Assert.Equal("do-not-touch", File.ReadAllText(untouchedMod));

        var second = RunInstaller(sandbox.RepositoryRoot, sandbox.ModsDirectory, sandbox.UserDataDirectory);
        Assert.True(second.ExitCode == 0, $"Installer failed: {second.StandardOutput} {second.StandardError}");
        Assert.Equal("already_current", ReadJson(second.StandardOutput).GetProperty("state").GetString());
        Assert.Equal("do-not-touch", File.ReadAllText(untouchedMod));
    }

    [Fact]
    public void Diagnose_ReportsInstalledButUnobservedWhenRuntimeReceiptIsMissing()
    {
        using var sandbox = new InstallerSandbox();
        var install = RunInstaller(sandbox.RepositoryRoot, sandbox.ModsDirectory, sandbox.UserDataDirectory);
        Assert.True(install.ExitCode == 0, $"Installer failed: {install.StandardOutput} {install.StandardError}");

        var diagnosis = RunInstaller(sandbox.RepositoryRoot, sandbox.ModsDirectory, sandbox.UserDataDirectory, diagnose: true);

        Assert.True(diagnosis.ExitCode == 0, $"Diagnosis failed: {diagnosis.StandardOutput} {diagnosis.StandardError}");
        var json = ReadJson(diagnosis.StandardOutput);
        Assert.Equal("installed_not_observed", json.GetProperty("state").GetString());
        Assert.Equal("runtime_heartbeat_missing", json.GetProperty("code").GetString());
        Assert.False(json.GetProperty("runtime_receipt_present").GetBoolean());
    }

    [Fact]
    public void Install_ReplacesOwnedTargetWhenAnUnexpectedScriptIsPresent()
    {
        using var sandbox = new InstallerSandbox();
        Assert.Equal(0, RunInstaller(sandbox.RepositoryRoot, sandbox.ModsDirectory, sandbox.UserDataDirectory).ExitCode);
        var unexpectedScript = Path.Combine(sandbox.ModsDirectory, "DDAI", "scripts", "unexpected.gd");
        File.WriteAllText(unexpectedScript, "var script_class = \"tool\"");

        var repair = RunInstaller(sandbox.RepositoryRoot, sandbox.ModsDirectory, sandbox.UserDataDirectory);

        Assert.Equal(0, repair.ExitCode);
        var json = ReadJson(repair.StandardOutput);
        Assert.Equal("repaired", json.GetProperty("state").GetString());
        Assert.False(File.Exists(unexpectedScript));
        Assert.True(Directory.Exists(json.GetProperty("backup").GetString()!));
    }

    [Fact]
    public void Diagnose_ReportsRunningOnlyForAFreshHeartbeatFromTheInstalledMod()
    {
        using var sandbox = new InstallerSandbox();
        Assert.Equal(0, RunInstaller(sandbox.RepositoryRoot, sandbox.ModsDirectory, sandbox.UserDataDirectory).ExitCode);
        WriteHeartbeat(sandbox.UserDataDirectory, DateTimeOffset.UtcNow, "0.3.0");

        var diagnosis = RunInstaller(sandbox.RepositoryRoot, sandbox.ModsDirectory, sandbox.UserDataDirectory, diagnose: true);

        Assert.True(diagnosis.ExitCode == 0, $"Diagnosis failed: {diagnosis.StandardOutput} {diagnosis.StandardError}");
        var json = ReadJson(diagnosis.StandardOutput);
        Assert.Equal("running", json.GetProperty("state").GetString());
        Assert.Equal("runtime_heartbeat_fresh", json.GetProperty("code").GetString());
    }

    [Fact]
    public void Diagnose_DoesNotCallAStaleHeartbeatRunning()
    {
        using var sandbox = new InstallerSandbox();
        Assert.Equal(0, RunInstaller(sandbox.RepositoryRoot, sandbox.ModsDirectory, sandbox.UserDataDirectory).ExitCode);
        WriteHeartbeat(sandbox.UserDataDirectory, DateTimeOffset.UtcNow.AddMinutes(-2), "0.3.0");

        var diagnosis = RunInstaller(sandbox.RepositoryRoot, sandbox.ModsDirectory, sandbox.UserDataDirectory, diagnose: true);

        Assert.True(diagnosis.ExitCode == 0, $"Diagnosis failed: {diagnosis.StandardOutput} {diagnosis.StandardError}");
        var json = ReadJson(diagnosis.StandardOutput);
        Assert.Equal("installed_not_observed", json.GetProperty("state").GetString());
        Assert.Equal("runtime_heartbeat_stale", json.GetProperty("code").GetString());
    }

    [Fact]
    public void Diagnose_DoesNotCallAFutureHeartbeatRunning()
    {
        using var sandbox = new InstallerSandbox();
        Assert.Equal(0, RunInstaller(sandbox.RepositoryRoot, sandbox.ModsDirectory, sandbox.UserDataDirectory).ExitCode);
        WriteHeartbeat(sandbox.UserDataDirectory, DateTimeOffset.UtcNow.AddMinutes(2), "0.3.0");
        var diagnosis = RunInstaller(sandbox.RepositoryRoot, sandbox.ModsDirectory, sandbox.UserDataDirectory, diagnose: true);
        Assert.True(diagnosis.ExitCode == 0, $"Diagnosis failed: {diagnosis.StandardOutput} {diagnosis.StandardError}");
        Assert.Equal("installed_not_observed", ReadJson(diagnosis.StandardOutput).GetProperty("state").GetString());
    }

    [Fact]
    public void Diagnose_IgnoresUnlimitedLegacyFilesAndFindsFreshCanonicalSlot()
    {
        using var sandbox = new InstallerSandbox();
        Assert.Equal(0, RunInstaller(sandbox.RepositoryRoot, sandbox.ModsDirectory, sandbox.UserDataDirectory).ExitCode);
        var heartbeatDirectory = Path.Combine(sandbox.UserDataDirectory, "ddai", "runtime-heartbeats");
        Directory.CreateDirectory(heartbeatDirectory);
        for (var index = 0; index < 32; index++)
        {
            var legacy = Path.Combine(heartbeatDirectory, $"legacy-attacker-{index:D2}.json");
            File.WriteAllText(legacy, "{ malformed");
            File.SetLastWriteTimeUtc(legacy, DateTime.UtcNow.AddMinutes(5));
        }
        WriteHeartbeat(sandbox.UserDataDirectory, DateTimeOffset.UtcNow, "0.3.0");

        var diagnosis = RunInstaller(sandbox.RepositoryRoot, sandbox.ModsDirectory, sandbox.UserDataDirectory, diagnose: true);

        Assert.True(diagnosis.ExitCode == 0, $"Diagnosis failed: {diagnosis.StandardOutput} {diagnosis.StandardError}");
        var json = ReadJson(diagnosis.StandardOutput);
        Assert.Equal("running", json.GetProperty("state").GetString());
        Assert.Equal("runtime_heartbeat_fresh", json.GetProperty("code").GetString());
    }

    [Fact]
    public void Diagnose_RejectsCanonicalSlotWithMismatchedSchema()
    {
        using var sandbox = new InstallerSandbox();
        Assert.Equal(0, RunInstaller(sandbox.RepositoryRoot, sandbox.ModsDirectory, sandbox.UserDataDirectory).ExitCode);
        WriteHeartbeat(sandbox.UserDataDirectory, DateTimeOffset.UtcNow, "0.3.0", schemaVersion: "2.0");

        var diagnosis = RunInstaller(sandbox.RepositoryRoot, sandbox.ModsDirectory, sandbox.UserDataDirectory, diagnose: true);

        Assert.True(diagnosis.ExitCode == 0, $"Diagnosis failed: {diagnosis.StandardOutput} {diagnosis.StandardError}");
        var json = ReadJson(diagnosis.StandardOutput);
        Assert.Equal("installed_not_observed", json.GetProperty("state").GetString());
        Assert.Equal("runtime_heartbeat_malformed_or_mismatched", json.GetProperty("code").GetString());
    }

    private static void WriteHeartbeat(string userDataDirectory, DateTimeOffset timestamp, string modVersion, string schemaVersion = "1.0")
    {
        var heartbeatPath = Path.Combine(userDataDirectory, "ddai", "runtime-heartbeats", "heartbeat-slot-0.json");
        Directory.CreateDirectory(Path.GetDirectoryName(heartbeatPath)!);
        File.WriteAllText(heartbeatPath, JsonSerializer.Serialize(new { schema_version = schemaVersion, session_id = "test-session", timestamp, mod_version = modVersion }));
    }

    private static ProcessResult RunInstaller(string repositoryRoot, string modsDirectory, string userDataDirectory, bool diagnose = false)
    {
        var scriptPath = Path.Combine(repositoryRoot, "tools", "Install-DDAIMod.ps1");
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-SourceModDirectory");
        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "mods", "DDAI"));
        startInfo.ArgumentList.Add("-ModsDirectory");
        startInfo.ArgumentList.Add(modsDirectory);
        startInfo.ArgumentList.Add("-UserDataDirectory");
        startInfo.ArgumentList.Add(userDataDirectory);
        if (diagnose)
        {
            startInfo.ArgumentList.Add("-Diagnose");
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start PowerShell.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static JsonElement ReadJson(string output) => JsonDocument.Parse(output.Trim()).RootElement.Clone();

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class InstallerSandbox : IDisposable
    {
        public InstallerSandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "ddai-installer-tests", Guid.NewGuid().ToString("N"));
            ModsDirectory = Path.Combine(Root, "chosen-mods");
            UserDataDirectory = Path.Combine(Root, "user-data");
            Directory.CreateDirectory(ModsDirectory);
            Directory.CreateDirectory(UserDataDirectory);
            RepositoryRoot = FindRepositoryRoot();
        }

        public string Root { get; }

        public string ModsDirectory { get; }

        public string UserDataDirectory { get; }

        public string RepositoryRoot { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static string FindRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "README.md")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "mods", "DDAI")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Could not locate the DDAI repository root.");
        }
    }
}
