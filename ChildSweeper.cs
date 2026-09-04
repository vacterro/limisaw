using System;
using System.Runtime.InteropServices;

// A Windows Job object that outlives the thread which spawned its children.
//
// The refresh sweep runs on a ThreadPool (background) thread. When the app exits
// mid-sweep, that thread is torn down WITHOUT running any finally: the only code
// that kills `codex app-server --stdio` (RpcSession.Dispose) never executes, and
// Cli.Run's finally only disposes the Process object, which does not kill. The
// vendor's children then outlive their parent — one orphan per Codex account plus
// `claude` / `agy`, and with a 3-minute timer over a sweep that can take 66
// seconds the window is roughly one exit in three.
//
// The OS already has the fix: a Job with KILL_ON_JOB_CLOSE terminates every
// process inside it when the last handle closes. A job handle created with that
// flag is a process-wide resource; the kernel sweeps the children whether or not
// our threads ever get to say goodbye.
//
// Everything here is best-effort by design: if the calls fail (ancient Windows,
// odd sandbox), the child processes behave exactly as they did before — worst
// case unchanged, best case no orphans.
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

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

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

        static IntPtr Job = IntPtr.Zero;
        static readonly object Gate = new object();

        // The process-wide job. Created once, never closed: the handle's lifetime
        // IS the kill switch, so holding it for the process lifetime is the point.
        public static void Arm()
        {
            if (Job != IntPtr.Zero) return;
            lock (Gate)
            {
                if (Job != IntPtr.Zero) return;
                try
                {
                    IntPtr handle = CreateJobObject(IntPtr.Zero, null);
                    if (handle == IntPtr.Zero) return;
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                    if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation,
                            ref info, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))))
                    {
                        CloseHandle(handle);
                        return;
                    }
                    // This process itself joins the job, so every child spawned
                    // after this point is inside it. Joining self is safe: the
                    // kill fires only when the handle closes, which on process
                    // exit is exactly "after the last child is dead".
                    if (!AssignProcessToJobObject(handle, GetCurrentProcess()))
                    {
                        CloseHandle(handle);
                        return;
                    }
                    Job = handle;
                }
                catch { Job = IntPtr.Zero; }
            }
        }
    }
}
