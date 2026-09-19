using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

// Per-operation containment for the vendor CLIs LIMISAW spawns to read quotas.
//
// Two facts drive the shape. First, killing a process does NOT kill what it
// started: Cli.Run's timeout path calls proc.Kill() and RpcSession.Dispose
// kills only its own child, so a vendor helper or grandchild survives and
// accumulates across retries. Second, a refresh sweep runs on a ThreadPool
// (background) thread, so an exit mid-sweep tears that thread down WITHOUT
// running any finally at all — the code that kills the child never executes.
//
// The OS answers both with one primitive: a Job with KILL_ON_JOB_CLOSE
// terminates every process inside it when the last handle closes. Closing the
// handle deliberately ends a timed-out tree; process exit closes every handle
// the process held, so an abnormal exit ends it too, with no cooperation from
// our threads.
//
// The job is per OPERATION, never process-wide. LIMISAW itself must stay out of
// any kill-on-close job: the vendor installer (a visible PowerShell window) and
// the user's ini editor are launched by the same process, and an app-wide job
// would terminate an installation mid-write when the user quits the tray icon.
// Those launches never open a Scope, so they are structurally outside.
//
// Everything here is best-effort by design: if the calls fail (ancient Windows,
// odd sandbox), Adopt returns false and the child processes behave exactly as
// they did before — worst case unchanged, best case no orphans.
namespace Limisaw
{
    static class ChildSweeper
    {
        // CREATE_BREAKAWAY would let a child escape; we never pass it, and
        // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE does the sweeping.
        const int JobObjectExtendedLimitInformation = 9;
        const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int cbInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool IsProcessInJob(IntPtr hProcess, IntPtr hJob, out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateJobObject(IntPtr hJob, uint exitCode);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        // One probe operation's containment: a CLI invocation, or one pooled
        // app-server session for as long as the pool keeps it.
        internal sealed class Scope : IDisposable
        {
            IntPtr Job;
            int Adopted;

            internal Scope(IntPtr job) { Job = job; }

            // True when the OS gave us a real job. False means every method
            // here is a no-op and the caller's own Kill remains the only
            // containment — the behaviour LIMISAW had before this existed.
            public bool Armed { get { return Job != IntPtr.Zero; } }

            public int AdoptedCount { get { return Adopted; } }

            // Adopt as early as possible after Process.Start. A child that
            // spawns its own children before this call would leave them
            // outside the job.
            // ponytail: closing that window needs CreateProcess with
            // CREATE_SUSPENDED or PROC_THREAD_ATTRIBUTE_JOB_LIST, which means
            // owning the stdio pipe plumbing by hand instead of using
            // ProcessStartInfo. The vendor CLIs are Node programs that take
            // >100ms to reach their own spawn calls, so adopt-after-start is
            // the whole tree in practice. Upgrade if a vendor ever ships a
            // native launcher that forks immediately.
            public bool Adopt(Process proc)
            {
                if (Job == IntPtr.Zero || proc == null) return false;
                try
                {
                    if (!AssignProcessToJobObject(Job, proc.Handle)) return false;
                    Adopted++;
                    return true;
                }
                catch { return false; }
            }

            public bool Contains(Process proc)
            {
                if (Job == IntPtr.Zero || proc == null) return false;
                try
                {
                    bool inJob;
                    if (!IsProcessInJob(proc.Handle, Job, out inJob)) return false;
                    return inJob;
                }
                catch { return false; }
            }

            // Ends the whole tree now. TerminateJobObject is the immediate
            // half; CloseHandle is the half that also fires when this process
            // dies without ever getting here.
            public void Dispose()
            {
                IntPtr job = Job;
                Job = IntPtr.Zero;
                if (job == IntPtr.Zero) return;
                try { TerminateJobObject(job, 1); } catch { }
                try { CloseHandle(job); } catch { }
            }
        }

        // A scope is always returned, armed or not, so callers never branch.
        internal static Scope Open()
        {
            try
            {
                IntPtr handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero) return new Scope(IntPtr.Zero);
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation,
                        ref info, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))))
                {
                    CloseHandle(handle);
                    return new Scope(IntPtr.Zero);
                }
                return new Scope(handle);
            }
            catch { return new Scope(IntPtr.Zero); }
        }

        // Proof surface for the harness: LIMISAW itself must never be inside a
        // probe scope, or quitting the tray would take a visible installer with it.
        internal static bool SelfInside(Scope scope)
        {
            if (scope == null || !scope.Armed) return false;
            try
            {
                var self = Process.GetCurrentProcess();
                return scope.Contains(self);
            }
            catch { return false; }
        }
    }
}
