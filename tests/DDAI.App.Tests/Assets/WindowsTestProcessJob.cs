using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DDAI.App.Tests.Assets;

internal readonly record struct WindowsJobAccounting(
    uint TotalProcesses,
    uint ActiveProcesses,
    uint TotalTerminatedProcesses);

internal sealed class WindowsTestProcessJob : IDisposable
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint ProcThreadAttributeHandleList = 0x00020002;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint StillActive = 259;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const uint TestCleanupExitCode = 0xDDA10001;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly SafeWaitHandle jobHandle;
    private readonly SafeWaitHandle processHandle;
    private readonly SafeWaitHandle threadHandle;
    private int disposed;

    private WindowsTestProcessJob(
        SafeWaitHandle jobHandle,
        SafeWaitHandle processHandle,
        SafeWaitHandle threadHandle,
        int processId,
        Task<string> standardOutput,
        Task<string> standardError)
    {
        this.jobHandle = jobHandle;
        this.processHandle = processHandle;
        this.threadHandle = threadHandle;
        ProcessId = processId;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public int ProcessId { get; }
    public Task<string> StandardOutput { get; }
    public Task<string> StandardError { get; }
    public bool LeaderExitConfirmed { get; private set; }
    public bool JobEmptyConfirmed { get; private set; }
    public bool TerminationRequested { get; private set; }
    public uint FinalActiveProcessCount { get; private set; } = uint.MaxValue;

    public bool HasExited
    {
        get
        {
            if (Volatile.Read(ref disposed) != 0) return LeaderExitConfirmed;
            return WaitForHandle(processHandle, 0);
        }
    }

    public int ExitCode
    {
        get
        {
            if (!GetExitCodeProcess(processHandle, out var exitCode)) ThrowLastWin32Error();
            if (exitCode == StillActive) throw new InvalidOperationException("The test-owned process is still active.");
            return unchecked((int)exitCode);
        }
    }

    public WindowsJobAccounting Accounting
    {
        get
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return new WindowsJobAccounting(0, FinalActiveProcessCount, 0);
            }
            return QueryAccounting(jobHandle);
        }
    }

    public static WindowsTestProcessJob StartDotNetVstest(
        string testAssembly,
        string testName,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        var dotnetHost = ResolveDotNetHost();
        return Start(
            dotnetHost,
            ["vstest", testAssembly, "--Tests:" + testName],
            workingDirectory,
            environment);
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        while (!HasExited)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

        Exception? failure = null;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var accounting = QueryAccounting(jobHandle);
            if (accounting.ActiveProcesses > 0)
            {
                TerminationRequested = true;
                if (!TerminateJobObject(jobHandle, TestCleanupExitCode)) ThrowLastWin32Error();
            }

            JobEmptyConfirmed = WaitForJobEmpty(elapsed, out var finalAccounting);
            FinalActiveProcessCount = finalAccounting.ActiveProcesses;
            if (!JobEmptyConfirmed)
            {
                throw new TimeoutException(
                    $"Test job for leader {ProcessId} still has {FinalActiveProcessCount} active process(es) after bounded cleanup.");
            }

            LeaderExitConfirmed = WaitForProcessExit(elapsed);
            if (!LeaderExitConfirmed)
            {
                throw new TimeoutException($"Test job leader {ProcessId} did not exit after bounded cleanup.");
            }

            WaitForRedirectedOutput(elapsed);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            threadHandle.Dispose();
            processHandle.Dispose();
            jobHandle.Dispose();
        }

        if (failure is not null)
        {
            throw new InvalidOperationException(
                $"The Windows test job for leader {ProcessId} could not be cleaned up.",
                failure);
        }
    }

    private static WindowsTestProcessJob Start(
        string applicationPath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environmentOverrides)
    {
        SafeWaitHandle? job = null;
        SafeWaitHandle? process = null;
        SafeWaitHandle? thread = null;
        SafeFileHandle? standardInputRead = null;
        SafeFileHandle? standardInputWrite = null;
        SafeFileHandle? standardOutputRead = null;
        SafeFileHandle? standardOutputWrite = null;
        SafeFileHandle? standardErrorRead = null;
        SafeFileHandle? standardErrorWrite = null;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr inheritedHandles = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        var processCreated = false;
        var assignedToJob = false;
        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid) ThrowLastWin32Error();
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            if (!SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformationClass,
                ref limits,
                checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>())))
            {
                ThrowLastWin32Error();
            }

            var inheritableAttributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                InheritHandle = true,
            };
            CreateAnonymousPipe(out standardInputRead, out standardInputWrite, ref inheritableAttributes);
            CreateAnonymousPipe(out standardOutputRead, out standardOutputWrite, ref inheritableAttributes);
            CreateAnonymousPipe(out standardErrorRead, out standardErrorWrite, ref inheritableAttributes);
            ClearInheritFlag(standardInputWrite);
            ClearInheritFlag(standardOutputRead);
            ClearInheritFlag(standardErrorRead);

            var attributeListSize = IntPtr.Zero;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
            if (attributeListSize == IntPtr.Zero) ThrowLastWin32Error();
            attributeList = Marshal.AllocHGlobal(attributeListSize);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize)) ThrowLastWin32Error();

            var handles = new[]
            {
                standardInputRead.DangerousGetHandle(),
                standardOutputWrite.DangerousGetHandle(),
                standardErrorWrite.DangerousGetHandle(),
            };
            inheritedHandles = Marshal.AllocHGlobal(checked(IntPtr.Size * handles.Length));
            Marshal.Copy(handles, 0, inheritedHandles, handles.Length);
            if (!UpdateProcThreadAttribute(
                attributeList,
                0,
                new UIntPtr(ProcThreadAttributeHandleList),
                inheritedHandles,
                new UIntPtr(checked((uint)(IntPtr.Size * handles.Length))),
                IntPtr.Zero,
                IntPtr.Zero))
            {
                ThrowLastWin32Error();
            }

            environment = CreateEnvironmentBlock(environmentOverrides);
            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StandardInput = standardInputRead.DangerousGetHandle(),
                    StandardOutput = standardOutputWrite.DangerousGetHandle(),
                    StandardError = standardErrorWrite.DangerousGetHandle(),
                },
                AttributeList = attributeList,
            };
            var commandLine = new StringBuilder(BuildCommandLine(applicationPath, arguments));
            if (!CreateProcess(
                applicationPath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent | CreateNoWindow,
                environment,
                workingDirectory,
                ref startupInfo,
                out var processInformation))
            {
                ThrowLastWin32Error();
            }

            processCreated = true;
            process = new SafeWaitHandle(processInformation.ProcessHandle, ownsHandle: true);
            thread = new SafeWaitHandle(processInformation.ThreadHandle, ownsHandle: true);
            standardInputRead.Dispose();
            standardInputRead = null;
            standardInputWrite.Dispose();
            standardInputWrite = null;
            standardOutputWrite.Dispose();
            standardOutputWrite = null;
            standardErrorWrite.Dispose();
            standardErrorWrite = null;

            if (!AssignProcessToJobObject(job, process)) ThrowLastWin32Error();
            assignedToJob = true;

            var outputTask = ReadPipeToEndAsync(standardOutputRead);
            standardOutputRead = null;
            var errorTask = ReadPipeToEndAsync(standardErrorRead);
            standardErrorRead = null;
            if (ResumeThread(thread) == uint.MaxValue) ThrowLastWin32Error();

            var result = new WindowsTestProcessJob(
                job,
                process,
                thread,
                checked((int)processInformation.ProcessId),
                outputTask,
                errorTask);
            job = null;
            process = null;
            thread = null;
            return result;
        }
        catch (Exception launchFailure)
        {
            Exception? cleanupFailure = null;
            try
            {
                if (processCreated && process is not null && !process.IsInvalid && !HasProcessExited(process))
                {
                    var terminated = assignedToJob
                        ? TerminateJobObject(job!, TestCleanupExitCode)
                        : TerminateProcess(process, TestCleanupExitCode);
                    if (!terminated) ThrowLastWin32Error();
                    if (!WaitForHandle(process, checked((uint)CleanupTimeout.TotalMilliseconds)))
                    {
                        throw new TimeoutException("The suspended test child did not exit after failed startup cleanup.");
                    }
                }
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    "The test child failed to start and its exact retained process could not be confirmed exited.",
                    launchFailure,
                    cleanupFailure);
            }
            throw;
        }
        finally
        {
            if (attributeList != IntPtr.Zero) DeleteProcThreadAttributeList(attributeList);
            if (inheritedHandles != IntPtr.Zero) Marshal.FreeHGlobal(inheritedHandles);
            if (attributeList != IntPtr.Zero) Marshal.FreeHGlobal(attributeList);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            standardInputRead?.Dispose();
            standardInputWrite?.Dispose();
            standardOutputRead?.Dispose();
            standardOutputWrite?.Dispose();
            standardErrorRead?.Dispose();
            standardErrorWrite?.Dispose();
            thread?.Dispose();
            process?.Dispose();
            job?.Dispose();
        }
    }

    private bool WaitForJobEmpty(System.Diagnostics.Stopwatch elapsed, out WindowsJobAccounting accounting)
    {
        do
        {
            accounting = QueryAccounting(jobHandle);
            if (accounting.ActiveProcesses == 0) return true;
            Thread.Sleep(TimeSpan.FromMilliseconds(10));
        }
        while (elapsed.Elapsed < CleanupTimeout);

        accounting = QueryAccounting(jobHandle);
        return accounting.ActiveProcesses == 0;
    }

    private bool WaitForProcessExit(System.Diagnostics.Stopwatch elapsed)
    {
        var remaining = CleanupTimeout - elapsed.Elapsed;
        if (remaining <= TimeSpan.Zero) return HasProcessExited(processHandle);
        return WaitForHandle(processHandle, checked((uint)Math.Ceiling(remaining.TotalMilliseconds)));
    }

    private void WaitForRedirectedOutput(System.Diagnostics.Stopwatch elapsed)
    {
        var remaining = CleanupTimeout - elapsed.Elapsed;
        if (remaining <= TimeSpan.Zero ||
            !Task.WaitAll([StandardOutput, StandardError], remaining))
        {
            throw new TimeoutException($"Redirected output for test job leader {ProcessId} did not close after bounded cleanup.");
        }
    }

    private static async Task<string> ReadPipeToEndAsync(SafeFileHandle handle)
    {
        using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync();
    }

    private static WindowsJobAccounting QueryAccounting(SafeWaitHandle job)
    {
        if (!QueryInformationJobObject(
            job,
            JobObjectBasicAccountingInformation,
            out var accounting,
            checked((uint)Marshal.SizeOf<JobObjectBasicAccounting>()),
            IntPtr.Zero))
        {
            ThrowLastWin32Error();
        }
        return new WindowsJobAccounting(
            accounting.TotalProcesses,
            accounting.ActiveProcesses,
            accounting.TotalTerminatedProcesses);
    }

    private static bool WaitForHandle(SafeWaitHandle handle, uint milliseconds)
    {
        var result = WaitForSingleObject(handle, milliseconds);
        return result switch
        {
            WaitObject0 => true,
            WaitTimeout => false,
            WaitFailed => throw new Win32Exception(Marshal.GetLastWin32Error()),
            _ => throw new InvalidOperationException($"Unexpected Windows wait result 0x{result:x8}."),
        };
    }

    private static bool HasProcessExited(SafeWaitHandle process) => WaitForHandle(process, 0);

    private static void CreateAnonymousPipe(
        out SafeFileHandle read,
        out SafeFileHandle write,
        ref SecurityAttributes attributes)
    {
        if (!CreatePipe(out read, out write, ref attributes, 0)) ThrowLastWin32Error();
    }

    private static void ClearInheritFlag(SafeFileHandle handle)
    {
        if (!SetHandleInformation(handle, HandleFlagInherit, 0)) ThrowLastWin32Error();
    }

    private static IntPtr CreateEnvironmentBlock(IReadOnlyDictionary<string, string> overrides)
    {
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value) values[name] = value;
        }
        foreach (var (name, value) in overrides)
        {
            if (string.IsNullOrEmpty(name) || name.Contains('=') || name.Contains('\0') || value.Contains('\0'))
            {
                throw new ArgumentException("A child-process environment override is invalid.", nameof(overrides));
            }
            values[name] = value;
        }

        var text = string.Join('\0', values.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
        var bytes = Encoding.Unicode.GetBytes(text);
        var block = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, block, bytes.Length);
        return block;
    }

    private static string ResolveDotNetHost()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);

        var runtimeDirectory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot = runtimeDirectory.Parent?.Parent?.Parent;
        var host = dotnetRoot is null ? null : Path.Combine(dotnetRoot.FullName, "dotnet.exe");
        if (host is not null && File.Exists(host)) return host;
        throw new FileNotFoundException("The exact dotnet host for the test child could not be resolved.", host);
    }

    private static string BuildCommandLine(string applicationPath, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { applicationPath }.Concat(arguments).Select(QuoteCommandLineArgument));

    private static string QuoteCommandLineArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var result = new StringBuilder(argument.Length + 2);
        result.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', checked(backslashes * 2 + 1));
                result.Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }
        result.Append('\\', checked(backslashes * 2));
        result.Append('"');
        return result.ToString();
    }

    private static void ThrowLastWin32Error() => throw new Win32Exception(Marshal.GetLastWin32Error());

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccounting
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeWaitHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeWaitHandle job,
        int informationClass,
        out JobObjectBasicAccounting information,
        uint informationLength,
        IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeWaitHandle job, SafeWaitHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeWaitHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeWaitHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeWaitHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        ref SecurityAttributes pipeAttributes,
        int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        uint flags,
        ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        UIntPtr attribute,
        IntPtr value,
        UIntPtr size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);
}
