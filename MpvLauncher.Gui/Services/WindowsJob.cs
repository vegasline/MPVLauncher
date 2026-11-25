// Original implementation, MIT licensed.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// A Windows job object that holds the relay helper and everything it starts.
    ///
    /// The helper spawns a browser, and a browser spawns a small army of child
    /// processes. Killing the helper alone leaves those behind, so the launcher
    /// puts the helper in a job with KILL_ON_JOB_CLOSE: when the launcher dies -
    /// crash included - the job handle closes and Windows tears the whole tree
    /// down. It also carries a memory cap so a runaway page cannot eat the
    /// machine.
    ///
    /// Deliberately a background job rather than a service: no admin rights, no
    /// install, nothing that survives the launcher.
    /// </summary>
    public sealed class WindowsJob : IDisposable
    {
        private const int JobObjectExtendedLimitInformationClass = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;
        private const uint JobObjectLimitProcessMemory = 0x0100;

        private IntPtr _handle;

        private WindowsJob(IntPtr handle) => _handle = handle;

        /// <summary>
        /// Creates the job and adds <paramref name="process"/>. Returns null when
        /// the platform refuses, in which case the caller falls back to killing
        /// the process tree on its own.
        /// </summary>
        public static WindowsJob? TryCreate(Process process, long memoryLimitBytes)
        {
            if (!OperatingSystem.IsWindows() || process == null) return null;

            IntPtr handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) return null;

            try
            {
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose
                    }
                };

                if (memoryLimitBytes > 0)
                {
                    info.BasicLimitInformation.LimitFlags |= JobObjectLimitProcessMemory;
                    info.ProcessMemoryLimit = (UIntPtr)(ulong)memoryLimitBytes;
                }

                int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, buffer, false);
                    if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, buffer, (uint)length))
                    {
                        CloseHandle(handle);
                        return null;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                if (!AssignProcessToJobObject(handle, process.Handle))
                {
                    CloseHandle(handle);
                    return null;
                }

                return new WindowsJob(handle);
            }
            catch
            {
                CloseHandle(handle);
                return null;
            }
        }

        public void Dispose()
        {
            IntPtr handle = _handle;
            _handle = IntPtr.Zero;
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
