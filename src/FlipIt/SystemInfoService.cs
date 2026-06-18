using System;
using System.Runtime.InteropServices;

namespace ScreenSaver
{
    /// <summary>
    /// Cheap CPU + memory usage readings for the clock's info line, e.g. "CPU 12%  内存 48%".
    /// CPU usage comes from GetSystemTimes deltas (the same numbers Task Manager shows), memory from
    /// GlobalMemoryStatusEx. Both are simple syscalls — no PerformanceCounter, so no first-use stall
    /// and no dependency on the perf-counter registry being healthy. Samples at most once a second.
    /// </summary>
    internal static class SystemInfoService
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idleTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        private static ulong _prevIdle;
        private static ulong _prevTotal;   // kernel (includes idle) + user
        private static DateTime _lastSampleUtc = DateTime.MinValue;
        private static int _cpuPercent;
        private static int _memPercent;

        public static string GetDisplayText()
        {
            if ((DateTime.UtcNow - _lastSampleUtc).TotalMilliseconds >= 900)
                Sample();
            return $"CPU {_cpuPercent}%  内存 {_memPercent}%";
        }

        private static void Sample()
        {
            _lastSampleUtc = DateTime.UtcNow;
            try
            {
                if (GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
                {
                    var idle = ToUlong(idleFt);
                    var total = ToUlong(kernelFt) + ToUlong(userFt);

                    var idleDelta = idle - _prevIdle;
                    var totalDelta = total - _prevTotal;
                    // First call has no previous sample; leave the value at 0 until the next tick.
                    if (_prevTotal != 0 && totalDelta > 0)
                        _cpuPercent = (int)Math.Min(100, Math.Max(0,
                            100 - (idleDelta * 100.0 / totalDelta)));

                    _prevIdle = idle;
                    _prevTotal = total;
                }

                var mem = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(mem))
                    _memPercent = (int)mem.dwMemoryLoad;
            }
            catch
            {
                // Keep whatever the last good values were.
            }
        }

        private static ulong ToUlong(System.Runtime.InteropServices.ComTypes.FILETIME ft)
        {
            return ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
        }
    }
}
