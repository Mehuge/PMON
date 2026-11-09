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
    string WindowTitle,
    int Instances = 1,
    int ParentPid = 0,
    int RootPid = 0) : ITimedSample
{
    public string Key => $"{Name}:{Pid}";

    /// <summary>Folds another instance's sample (same moment) into this one, producing a combined group sample.</summary>
    public ProcessSample Plus(ProcessSample other)
    {
        var window = WindowRank(other) > WindowRank(this) ? other : this;
        return this with
        {
            Pid = 0,
            CpuPercent = CpuPercent + other.CpuPercent,
            Threads = Threads + other.Threads,
            Handles = Handles + other.Handles,
            WorkingSetMb = WorkingSetMb + other.WorkingSetMb,
            PrivateMb = PrivateMb + other.PrivateMb,
            PagefileMb = PagefileMb + other.PagefileMb,
            VirtualMb = VirtualMb + other.VirtualMb,
            PeakWorkingSetMb = PeakWorkingSetMb + other.PeakWorkingSetMb,
            PageFaultsPerSec = PageFaultsPerSec + other.PageFaultsPerSec,
            IoReadBytesPerSec = IoReadBytesPerSec + other.IoReadBytesPerSec,
            IoWriteBytesPerSec = IoWriteBytesPerSec + other.IoWriteBytesPerSec,
            IoOpsPerSec = IoOpsPerSec + other.IoOpsPerSec,
            GdiObjects = GdiObjects + other.GdiObjects,
            UserObjects = UserObjects + other.UserObjects,
            Foreground = Foreground || other.Foreground,
            Responding = Responding && other.Responding,
            Window = window.Window,
            WindowX = window.WindowX,
            WindowY = window.WindowY,
            WindowWidth = window.WindowWidth,
            WindowHeight = window.WindowHeight,
            WindowTitle = window.WindowTitle,
            Instances = Instances + other.Instances,
        };
    }

    private static int WindowRank(ProcessSample s) =>
        (s.Foreground ? 4 : 0) + (s.Window != WindowState.None ? 2 : 0) + (s.WindowTitle.Length > 0 ? 1 : 0);
}

public static class HogReason
{
    public const string Cpu = "Cpu";
    public const string Memory = "Memory";
    public const string Disk = "Disk";

    /// <summary>An untracked process the user is inspecting; sampled in full every cycle.</summary>
    public const string Pinned = "Pinned";
}

/// <summary>A process that was among the top consumers of one resource while not being tracked by a pattern.</summary>
public sealed record HogSample(string Reason, ProcessSample Sample);

public sealed record Snapshot(DateTime Time, SystemSample System, IReadOnlyList<ProcessSample> Processes, IReadOnlyList<HogSample> Hogs);
