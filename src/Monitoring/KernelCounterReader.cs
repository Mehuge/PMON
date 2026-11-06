using System.Runtime.InteropServices;

namespace PMON.Monitoring;

internal readonly record struct KernelCounters(ulong CpuTicks, ulong ReadBytes, ulong WriteBytes, ulong Operations, uint PageFaults);

/// <summary>
/// Per-process counters from the kernel process list. Unlike process handles they stay readable for
/// tamper-protected processes (security software), which is what Task Manager relies on.
/// </summary>
internal static class KernelCounterReader
{
    private const int SystemProcessInformation = 5;
    private const int InfoLengthMismatch = unchecked((int)0xC0000004);
    private const int MaxAttempts = 6;

    // Field offsets within SYSTEM_PROCESS_INFORMATION (x64).
    private const int UserTimeOffset = 40;
    private const int KernelTimeOffset = 48;
    private const int ProcessIdOffset = 80;
    private const int PageFaultOffset = 128;
    private const int ReadOperationsOffset = 208;
    private const int WriteOperationsOffset = 216;
    private const int ReadBytesOffset = 232;
    private const int WriteBytesOffset = 240;

    public static Dictionary<int, KernelCounters> Read()
    {
        var result = new Dictionary<int, KernelCounters>();
        var size = 1 << 20;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NativeMethods.NtQuerySystemInformation(SystemProcessInformation, buffer, (uint)size, out var needed);
                if (status == InfoLengthMismatch)
                {
                    size = needed == 0 ? size * 2 : (int)needed + (1 << 16);
                    continue;
                }

                if (status < 0)
                    return result;

                Parse(buffer, result);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return result;
    }

    private static void Parse(nint buffer, Dictionary<int, KernelCounters> result)
    {
        var offset = 0;
        while (true)
        {
            var entry = buffer + offset;
            var pid = (int)Marshal.ReadIntPtr(entry, ProcessIdOffset);
            result[pid] = new KernelCounters(
                (ulong)(Marshal.ReadInt64(entry, UserTimeOffset) + Marshal.ReadInt64(entry, KernelTimeOffset)),
                (ulong)Marshal.ReadInt64(entry, ReadBytesOffset),
                (ulong)Marshal.ReadInt64(entry, WriteBytesOffset),
                (ulong)(Marshal.ReadInt64(entry, ReadOperationsOffset) + Marshal.ReadInt64(entry, WriteOperationsOffset)),
                (uint)Marshal.ReadInt32(entry, PageFaultOffset));

            var next = Marshal.ReadInt32(entry, 0);
            if (next == 0)
                return;

            offset += next;
        }
    }
}
