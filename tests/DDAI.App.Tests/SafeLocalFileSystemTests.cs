using System.Runtime.InteropServices;
using System.Text.Json;
using DDAI.App;

namespace DDAI.App.Tests;

public sealed class SafeLocalFileSystemTests
{
    [Fact]
    public void AcquireDirectoryLease_HoldsAnExplicitDynamicDirectoryAndEveryRequiredParent()
    {
        using var sandbox = new FileSystemSandbox();
        var fileSystem = new SafeLocalFileSystem(sandbox.Root);
        var candidate = fileSystem.EnsureDirectory("private", "catalog-commit", "candidates", "dynamic-candidate");
        var candidates = Path.GetDirectoryName(candidate)!;
        using var lease = fileSystem.AcquireDirectoryLease(candidate);

        AssertParentSwapBlocked(candidate, candidate + "-swapped");
        AssertParentSwapBlocked(candidates, candidates + "-swapped");
        Assert.Equal("external", File.ReadAllText(sandbox.ExternalSentinel));
    }

    [Fact]
    public void WriteImmutable_HoldsParentAgainstConcurrentSwap()
    {
        using var sandbox = new FileSystemSandbox();
        var fileSystem = new SafeLocalFileSystem(sandbox.Root);
        var responses = fileSystem.EnsureDirectory("private", "responses");
        var destination = Path.Combine(responses, "response.json");
        using var lease = fileSystem.AcquireDirectoryLease();

        AssertParentSwapBlocked(sandbox.PrivateRoot, sandbox.SwapPath);
        fileSystem.WriteImmutable(destination, new { value = "trusted" }, JsonOptions);

        Assert.Equal("trusted", JsonDocument.Parse(File.ReadAllText(destination)).RootElement.GetProperty("value").GetString());
        Assert.Equal("external", File.ReadAllText(sandbox.ExternalSentinel));
    }

    [Fact]
    public void WriteImmutable_DanglingTargetJunctionCannotReceiveBytesBeforeHandleValidation()
    {
        using var sandbox = new FileSystemSandbox();
        var fileSystem = new SafeLocalFileSystem(sandbox.Root);
        var responses = fileSystem.EnsureDirectory("private", "responses");
        var destination = Path.Combine(responses, "response.json");
        var externalTarget = Path.Combine(sandbox.ExternalRoot, "created-through-link");
        CreateDirectoryJunction(destination, externalTarget);

        try
        {
            _ = Record.Exception(() => fileSystem.WriteImmutableBytes(destination, "trusted"u8));

            Assert.False(File.Exists(externalTarget));
            Assert.Equal("external", File.ReadAllText(sandbox.ExternalSentinel));
        }
        finally
        {
            Directory.Delete(destination);
        }
    }

    [Fact]
    public async Task WriteImmutable_ConcurrentTargetHardLinkNeverReceivesTrustedBytes()
    {
        using var sandbox = new FileSystemSandbox();
        var fileSystem = new SafeLocalFileSystem(sandbox.Root);
        var responses = fileSystem.EnsureDirectory("private", "hard-link-race");
        var external = Path.Combine(sandbox.ExternalRoot, "hard-link-target.txt");
        File.WriteAllText(external, "external");

        for (var index = 0; index < 64; index++)
        {
            var destination = Path.Combine(responses, "response-" + index + ".json");
            using var start = new ManualResetEventSlim();
            var linker = Task.Run(() =>
            {
                start.Wait();
                _ = CreateHardLink(destination, external, IntPtr.Zero);
            });
            start.Set();
            _ = Record.Exception(() => fileSystem.WriteImmutableBytes(destination, "trusted"u8));
            await linker;

            Assert.Equal("external", File.ReadAllText(external));
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    [Fact]
    public void DeleteOrdinaryOrLink_HoldsParentAndDeletesOpenedEntryOnly()
    {
        using var sandbox = new FileSystemSandbox();
        var target = Path.Combine(sandbox.PrivateRoot, "delete.json");
        File.WriteAllText(target, "trusted");
        var fileSystem = new SafeLocalFileSystem(sandbox.Root);
        _ = fileSystem.EnsureDirectory("private");
        using var lease = fileSystem.AcquireDirectoryLease();

        AssertParentSwapBlocked(sandbox.PrivateRoot, sandbox.SwapPath);
        fileSystem.DeleteOrdinaryOrLink(target);

        Assert.False(File.Exists(target));
        Assert.Equal("external", File.ReadAllText(sandbox.ExternalSentinel));
    }

    [Fact]
    public void QuarantineOrdinaryOrDeleteLink_HoldsBothParentsAndRenamesOpenedEntryOnly()
    {
        using var sandbox = new FileSystemSandbox();
        var target = Path.Combine(sandbox.PrivateRoot, "poison.json");
        File.WriteAllText(target, "trusted");
        var fileSystem = new SafeLocalFileSystem(sandbox.Root);
        var quarantine = fileSystem.EnsureDirectory("private", "quarantine");
        using var lease = fileSystem.AcquireDirectoryLease();

        AssertParentSwapBlocked(sandbox.PrivateRoot, sandbox.SwapPath);
        fileSystem.QuarantineOrdinaryOrDeleteLink(target, quarantine);

        Assert.False(File.Exists(target));
        Assert.Single(Directory.GetFileSystemEntries(quarantine));
        Assert.Equal("external", File.ReadAllText(sandbox.ExternalSentinel));
    }

    private static void AssertParentSwapBlocked(string parent, string swapPath)
    {
        var exception = Record.Exception(() => Directory.Move(parent, swapPath));
        Assert.IsAssignableFrom<IOException>(exception);
        Assert.True(Directory.Exists(parent));
        Assert.False(Directory.Exists(swapPath));
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("cmd.exe could not create the junction fixture.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class FileSystemSandbox : IDisposable
    {
        public FileSystemSandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "ddai-safe-fs-tests", Guid.NewGuid().ToString("N"));
            PrivateRoot = Path.Combine(Root, "private");
            SwapPath = Path.Combine(Root, "private-swapped");
            var external = Path.Combine(Root, "external");
            Directory.CreateDirectory(PrivateRoot);
            Directory.CreateDirectory(external);
            ExternalRoot = external;
            ExternalSentinel = Path.Combine(external, "sentinel.txt");
            File.WriteAllText(ExternalSentinel, "external");
        }

        public string Root { get; }
        public string PrivateRoot { get; }
        public string SwapPath { get; }
        public string ExternalRoot { get; }
        public string ExternalSentinel { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
