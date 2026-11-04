using PMON.Data;

namespace PMON.Models;

public enum WindowState
{
    None,
    Normal,
    Maximized,
    Minimized,
}

public sealed record SystemSample(
    DateTime Time,
    double CpuPercent,
    double MemoryPercent,
    double MemoryUsedMb,
    double MemoryTotalMb,
    double CommitUsedMb,
    double CommitLimitMb,
    double DiskBusyPercent,
    double DiskReadBytesPerSec,
    double DiskWriteBytesPerSec) : ITimedSample
{
    public double CommitPercent => CommitLimitMb <= 0 ? 0 : CommitUsedMb / CommitLimitMb * 100.0;
}

public sealed record ProcessSample(
    DateTime Time,
    int Pid,
    string Name,
    double CpuPercent,
    int Threads,
    int Handles,
    double WorkingSetMb,
    double PrivateMb,
    double PagefileMb,
    double VirtualMb,
    double PeakWorkingSetMb,
    double PageFaultsPerSec,
    double IoReadBytesPerSec,
    double IoWriteBytesPerSec,
    double IoOpsPerSec,
    int GdiObjects,
    int UserObjects,
    bool Foreground,
    WindowState Window,
    bool Responding,
    int WindowX,
    int WindowY,
    int WindowWidth,
    int WindowHeight,
    int BasePriority,
    string WindowTitle) : ITimedSample
{
    public string Key => $"{Name}:{Pid}";
}

public static class HogReason
{
    public const string Cpu = "Cpu";
    public const string Memory = "Memory";
    public const string Disk = "Disk";
}

/// <summary>A process that was among the top consumers of one resource while not being tracked by a pattern.</summary>
public sealed record HogSample(string Reason, ProcessSample Sample);

public sealed record Snapshot(DateTime Time, SystemSample System, IReadOnlyList<ProcessSample> Processes, IReadOnlyList<HogSample> Hogs);
