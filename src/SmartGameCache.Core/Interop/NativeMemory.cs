using System;
using System.Runtime.InteropServices;

namespace SmartGameCache.Core.Interop;

public static class NativeMemory
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MEMORYSTATUSEX()
        {
            dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    public record SystemMemoryStatus(
        ulong TotalPhysicalBytes,
        ulong AvailablePhysicalBytes,
        ulong UsedPhysicalBytes,
        uint MemoryLoadPercent,
        bool IsUnderPressure
    );

    public static SystemMemoryStatus GetMemoryStatus()
    {
        var memStatus = new MEMORYSTATUSEX();
        if (GlobalMemoryStatusEx(memStatus))
        {
            ulong used = memStatus.ullTotalPhys > memStatus.ullAvailPhys 
                ? memStatus.ullTotalPhys - memStatus.ullAvailPhys 
                : 0;

            // System is under memory pressure if memory load exceeds 85% or available physical RAM is less than 1.5 GB
            bool underPressure = memStatus.dwMemoryLoad >= 85 || memStatus.ullAvailPhys < (1536UL * 1024 * 1024);

            return new SystemMemoryStatus(
                TotalPhysicalBytes: memStatus.ullTotalPhys,
                AvailablePhysicalBytes: memStatus.ullAvailPhys,
                UsedPhysicalBytes: used,
                MemoryLoadPercent: memStatus.dwMemoryLoad,
                IsUnderPressure: underPressure
            );
        }

        // Fallback estimate if API fails
        var total = 16UL * 1024 * 1024 * 1024;
        return new SystemMemoryStatus(total, total / 2, total / 2, 50, false);
    }
}

