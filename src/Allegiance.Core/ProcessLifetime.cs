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

    public void Dispose()
    {
        var job = Interlocked.Exchange(ref _job, IntPtr.Zero);
        if (job != IntPtr.Zero) CloseHandle(job);
    }

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
