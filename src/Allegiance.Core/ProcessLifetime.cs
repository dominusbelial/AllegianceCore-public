using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Allegiance.Core;

/// <summary>OS ownership for local bot processes. Unix children share a new session;
/// Windows children inherit a kill-on-close job owned by the game.</summary>
public sealed class ProcessLifetime : IDisposable
{
    private IntPtr _job;

    public ProcessLifetime()
    {
        if (!OperatingSystem.IsWindows()) return;
        _job = CreateJobObject(IntPtr.Zero, null);
        if (_job == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        int size = Marshal.SizeOf<ExtendedLimits>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, memory, false);
            if (!SetInformationJobObject(_job, 9, memory, (uint)size))
                throw new System.ComponentModel.Win32Exception();
        }
        catch { Dispose(); throw; }
        finally { Marshal.FreeHGlobal(memory); }
    }

    public void Attach(Process child)
    {
        if (_job != IntPtr.Zero && !AssignProcessToJobObject(_job, child.Handle))
            throw new System.ComponentModel.Win32Exception();
    }

    /// <summary>Call in the supervisor before starting workers, so crash cleanup includes every child.</summary>
    public static void WatchOwner(int parent, CancellationToken stop)
    {
        bool group = !OperatingSystem.IsWindows() && setsid() >= 0;
        _ = Task.Run(async () =>
        {
            try
            {
                using var owner = Process.GetProcessById(parent);
                await owner.WaitForExitAsync(stop);
            }
            catch (ArgumentException) { }
            catch (OperationCanceledException) { return; }
            if (group) kill(-Environment.ProcessId, 9);
            Process.GetCurrentProcess().Kill(entireProcessTree: true);
        });
    }

    /// <summary>After the supervisor exits, join even children it started before its ready
    /// message. Unix workers share the supervisor's session and watch their parent.</summary>
    public void WaitForChildren(int supervisorPid)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!TerminateJobObject(_job, 1)) throw new System.ComponentModel.Win32Exception();
            for (;;)
            {
                if (!QueryInformationJobObject(_job, 1, out var accounting, Marshal.SizeOf<JobAccounting>(), IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception();
                if (accounting.ActiveProcesses == 0) return;
                Thread.Sleep(20);
            }
        }

        // Signal zero only tests existence. A custom driver may have no private group.
        while (kill(-supervisorPid, 0) == 0)
        {
            // A container init may keep dead adopted processes as zombies indefinitely.
            if (OperatingSystem.IsLinux() && !Directory.EnumerateDirectories("/proc").Any(entry =>
                int.TryParse(Path.GetFileName(entry), out int pid) && getpgid(pid) == supervisorPid && LinuxStart(pid) is not null))
                break;
            Thread.Sleep(25);
        }
    }

    // Capture identity when the supervisor reports its workers. An ungrouped custom
    // driver can close every pipe and exit before its last worker.
    public static Action CaptureWorkerExit(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            string? start = LinuxStart(pid);
            return () =>
            {
                while (start is not null && LinuxStart(pid) == start) Thread.Sleep(25);
            };
        }
        var exit = ObserveExitAsync(pid);
        return exit.GetAwaiter().GetResult;
    }

    private static string? LinuxStart(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            // Field 2 is a parenthesized name which can itself contain ')'.
            // Fields 3 and 22 are state and the process birth time in clock ticks.
            string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return fields[0] is "Z" or "X" ? null : fields[19];
        }
        catch (IOException) { return null; } // Exited during the snapshot.
    }

    private static async Task ObserveExitAsync(int pid)
    {
        try
        {
            using var worker = Process.GetProcessById(pid);
            await worker.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (ArgumentException) { } // Already gone when ready was read.
    }

    public void Dispose()
    {
        var job = Interlocked.Exchange(ref _job, IntPtr.Zero);
        if (job != IntPtr.Zero) CloseHandle(job);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out JobAccounting info, int length, IntPtr returned);
    [DllImport("libc")] private static extern int getpgid(int pid);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public UIntPtr MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("libc")] private static extern int setsid();
    [DllImport("libc")] private static extern int kill(int pid, int signal);
}
