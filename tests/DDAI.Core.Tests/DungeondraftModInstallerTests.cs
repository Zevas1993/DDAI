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

        Assert.Equal(0, diagnosis.ExitCode);
        var json = ReadJson(diagnosis.StandardOutput);
        Assert.Equal("installed_not_observed", json.GetProperty("state").GetString());
        Assert.Equal("runtime_receipt_missing", json.GetProperty("code").GetString());
        Assert.False(json.GetProperty("runtime_receipt_present").GetBoolean());
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
