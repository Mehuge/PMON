using System.Diagnostics;
using PMON.Models;

namespace PMON.Monitoring;

internal sealed class SystemSampler : IDisposable
{
    private const double BytesPerMb = 1024.0 * 1024.0;

    private readonly PerformanceCounter? _diskIdle;
    private readonly PerformanceCounter? _diskRead;
    private readonly PerformanceCounter? _diskWrite;
    private ulong _previousIdle;
    private ulong _previousTotal;

    public SystemSampler()
    {
        try
        {
            _diskIdle = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total", readOnly: true);
            _diskRead = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", readOnly: true);
            _diskWrite = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", readOnly: true);
            _diskIdle.NextValue();
            _diskRead.NextValue();
            _diskWrite.NextValue();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _diskIdle?.Dispose();
            _diskRead?.Dispose();
            _diskWrite?.Dispose();
            _diskIdle = _diskRead = _diskWrite = null;
        }

        ReadCpuTimes(out _previousIdle, out _previousTotal);
    }

    public SystemSample Sample(DateTime time)
    {
        var cpu = ReadCpuPercent();

        var status = new NativeMethods.MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
        NativeMethods.GlobalMemoryStatusEx(ref status);
        var totalMb = status.TotalPhys / BytesPerMb;
        var usedMb = (status.TotalPhys - status.AvailPhys) / BytesPerMb;
        var commitUsedMb = (status.TotalPageFile - status.AvailPageFile) / BytesPerMb;
        var commitLimitMb = status.TotalPageFile / BytesPerMb;

        var diskBusy = 0.0;
        var diskRead = 0.0;
        var diskWrite = 0.0;
        if (_diskIdle is not null && _diskRead is not null && _diskWrite is not null)
        {
            try
            {
                diskBusy = Math.Clamp(100.0 - _diskIdle.NextValue(), 0, 100);
                diskRead = _diskRead.NextValue();
                diskWrite = _diskWrite.NextValue();
            }
            catch (InvalidOperationException)
            {
                // Counter instance vanished (e.g. disk topology change); report zeros until restart.
            }
        }

        return new SystemSample(
            time,
            cpu,
            totalMb <= 0 ? 0 : usedMb / totalMb * 100.0,
            usedMb,
            totalMb,
            commitUsedMb,
            commitLimitMb,
            diskBusy,
            diskRead,
            diskWrite);
    }

    public void Dispose()
    {
        _diskIdle?.Dispose();
        _diskRead?.Dispose();
        _diskWrite?.Dispose();
    }

    private double ReadCpuPercent()
    {
        ReadCpuTimes(out var idle, out var total);
        var deltaTotal = total - _previousTotal;
        var deltaIdle = idle - _previousIdle;
        _previousIdle = idle;
        _previousTotal = total;

        if (deltaTotal == 0)
            return 0;

        return Math.Clamp((deltaTotal - deltaIdle) * 100.0 / deltaTotal, 0, 100);
    }

    private static void ReadCpuTimes(out ulong idle, out ulong total)
    {
        if (!NativeMethods.GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            idle = 0;
            total = 0;
            return;
        }

        idle = idleTime.Value;
        total = kernelTime.Value + userTime.Value;
    }
}
