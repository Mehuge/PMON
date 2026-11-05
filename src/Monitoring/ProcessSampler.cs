using System.ComponentModel;
using System.Diagnostics;
using PMON.Models;

namespace PMON.Monitoring;

internal sealed class ProcessSampler
{
    private const double BytesPerMb = 1024.0 * 1024.0;
    private static readonly uint CountersSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.ProcessMemoryCountersEx>();

    private readonly record struct Previous(ulong CpuTicks, ulong ReadBytes, ulong WriteBytes, ulong Operations, uint PageFaults, long Stamp);

    private readonly record struct WindowInfo(nint Handle, string Title, WindowState State, NativeMethods.Rect Bounds, bool Responding);

    private readonly record struct Rates(double Cpu, double ReadBytes, double WriteBytes, double Operations, double Faults);

    private readonly record struct Candidate(Process Process, string Name, Rates Rates);

    private readonly Dictionary<int, Previous> _previous = new();

    /// <summary>Instance root for each live PID, fixed when first seen so charts do not re-split when a parent exits.</summary>
    private readonly Dictionary<int, int> _roots = new();

    private const int MaxAncestors = 64;

    /// <summary>Samples tracked processes in full and, when <paramref name="topCount"/> is positive, ranks the untracked ones.</summary>
    public (List<ProcessSample> Tracked, List<HogSample> Hogs) Sample(DateTime time, ProcessMatcher matcher, int topCount)
    {
        var tracked = new List<ProcessSample>();
        var candidates = new List<Candidate>();
        var stamp = Stopwatch.GetTimestamp();
        var foregroundPid = GetForegroundPid();
        var windows = EnumerateWindows();
        var seen = new HashSet<int>();
        var processes = Process.GetProcesses();
        var hogs = new List<HogSample>();
        var parents = ReadParents();
        var names = new Dictionary<int, string>();

        foreach (var process in processes)
        {
            try
            {
                names[process.Id] = process.ProcessName;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Exited while listing; it cannot be a useful ancestor either.
            }
        }

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var pid = process.Id;
                    if (pid == 0)
                        continue;

                    var name = process.ProcessName;
                    if (matcher.IsMatch(name))
                    {
                        windows.TryGetValue(pid, out var windowHandle);
                        var root = ResolveRoot(pid, name, parents, names);
                        tracked.Add(Read(process, time, stamp, foregroundPid, windowHandle, parents.GetValueOrDefault(pid), root));
                        seen.Add(pid);
                    }
                    else if (topCount > 0)
                    {
                        candidates.Add(new Candidate(process, name, MeasureLight(pid, stamp)));
                        seen.Add(pid);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Process exited mid-sample or is inaccessible; skip it this cycle.
                }
            }

            if (topCount > 0)
                RankHogs(candidates, topCount, time, foregroundPid, hogs);
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }

        foreach (var pid in _previous.Keys.Where(pid => !seen.Contains(pid)).ToList())
            _previous.Remove(pid);

        foreach (var pid in _roots.Keys.Where(pid => !names.ContainsKey(pid)).ToList())
            _roots.Remove(pid);

        return (tracked, hogs);
    }

    /// <summary>Finds the topmost same-named ancestor, looking through differently named intermediates; a process with none is its own root.</summary>
    private int ResolveRoot(int pid, string name, Dictionary<int, int> parents, Dictionary<int, string> names)
    {
        if (_roots.TryGetValue(pid, out var known))
            return known;

        // Provisional entry stops a malformed parent cycle from recursing forever.
        _roots[pid] = pid;
        var root = pid;
        var child = pid;

        for (var depth = 0; depth < MaxAncestors; depth++)
        {
            if (!parents.TryGetValue(child, out var parent) || parent == child || !names.TryGetValue(parent, out var parentName) || !IsOlder(parent, child))
                break;

            if (parentName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                root = ResolveRoot(parent, name, parents, names);
                break;
            }

            child = parent;
        }

        _roots[pid] = root;
        return root;
    }

    /// <summary>Guards against PID reuse: a real parent started no later than its child. Unreadable times are trusted.</summary>
    private static bool IsOlder(int parent, int child)
    {
        var parentStart = CreationTicks(parent);
        var childStart = CreationTicks(child);
        return parentStart == 0 || childStart == 0 || parentStart <= childStart;
    }

    private static ulong CreationTicks(int pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
        if (handle == 0)
            return 0;

        try
        {
            return NativeMethods.GetProcessTimes(handle, out var created, out _, out _, out _) ? created.Value : 0;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static Dictionary<int, int> ReadParents()
    {
        var parents = new Dictionary<int, int>();
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot is 0 or -1)
            return parents;

        try
        {
            var entry = new NativeMethods.ProcessEntry32 { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.ProcessEntry32>() };
            for (var found = NativeMethods.Process32First(snapshot, ref entry); found; found = NativeMethods.Process32Next(snapshot, ref entry))
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return parents;
    }

    private Rates MeasureLight(int pid, long stamp)
    {
        ulong cpuTicks = 0, readBytes = 0, writeBytes = 0, operations = 0;
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
        if (handle != 0)
        {
            try
            {
                if (NativeMethods.GetProcessTimes(handle, out _, out _, out var kernel, out var userTime))
                    cpuTicks = kernel.Value + userTime.Value;

                if (NativeMethods.GetProcessIoCounters(handle, out var io))
                {
                    readBytes = io.ReadBytes;
                    writeBytes = io.WriteBytes;
                    operations = io.ReadOperations + io.WriteOperations;
                }
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        return UpdateRates(pid, cpuTicks, readBytes, writeBytes, operations, 0, stamp);
    }

    private static void RankHogs(List<Candidate> candidates, int count, DateTime time, int foregroundPid, List<HogSample> output)
    {
        void Take(string reason, IEnumerable<Candidate> ordered)
        {
            foreach (var candidate in ordered.Take(count))
            {
                try
                {
                    output.Add(new HogSample(reason, ToHogSample(candidate, time, foregroundPid)));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Process exited between ranking and reading its details.
                }
            }
        }

        Take(HogReason.Cpu, candidates.Where(c => c.Rates.Cpu > 0).OrderByDescending(c => c.Rates.Cpu));
        Take(HogReason.Memory, candidates.OrderByDescending(c => c.Process.WorkingSet64));
        Take(HogReason.Disk, candidates
            .Where(c => c.Rates.ReadBytes + c.Rates.WriteBytes > 0)
            .OrderByDescending(c => c.Rates.ReadBytes + c.Rates.WriteBytes));
    }

    private static ProcessSample ToHogSample(Candidate candidate, DateTime time, int foregroundPid)
    {
        var process = candidate.Process;
        return new ProcessSample(
            time,
            process.Id,
            candidate.Name,
            candidate.Rates.Cpu,
            process.Threads.Count,
            process.HandleCount,
            process.WorkingSet64 / BytesPerMb,
            process.PrivateMemorySize64 / BytesPerMb,
            process.PagedMemorySize64 / BytesPerMb,
            process.VirtualMemorySize64 / BytesPerMb,
            process.PeakWorkingSet64 / BytesPerMb,
            0,
            candidate.Rates.ReadBytes,
            candidate.Rates.WriteBytes,
            candidate.Rates.Operations,
            0,
            0,
            foregroundPid == process.Id,
            WindowState.None,
            true,
            0,
            0,
            0,
            0,
            process.BasePriority,
            string.Empty);
    }

    private Rates UpdateRates(int pid, ulong cpuTicks, ulong readBytes, ulong writeBytes, ulong operations, uint pageFaults, long stamp)
    {
        var rates = default(Rates);
        if (_previous.TryGetValue(pid, out var previous))
        {
            var seconds = (stamp - previous.Stamp) / (double)Stopwatch.Frequency;
            if (seconds > 0)
            {
                // FILETIME ticks are 100ns; normalise against all logical cores so 100% == whole machine.
                rates = new Rates(
                    Math.Clamp((cpuTicks - previous.CpuTicks) / 1e7 / seconds / Environment.ProcessorCount * 100.0, 0, 100),
                    Delta(readBytes, previous.ReadBytes) / seconds,
                    Delta(writeBytes, previous.WriteBytes) / seconds,
                    Delta(operations, previous.Operations) / seconds,
                    Delta(pageFaults, previous.PageFaults) / seconds);
            }
        }

        _previous[pid] = new Previous(cpuTicks, readBytes, writeBytes, operations, pageFaults, stamp);
        return rates;
    }

    private ProcessSample Read(Process process, DateTime time, long stamp, int foregroundPid, nint windowHandle, int parentPid, int rootPid)
    {
        var pid = process.Id;
        ulong cpuTicks = 0, readBytes = 0, writeBytes = 0, operations = 0;
        uint pageFaults = 0;
        int gdi = 0, user = 0;

        var handle = Open(pid);
        if (handle != 0)
        {
            try
            {
                if (NativeMethods.GetProcessTimes(handle, out _, out _, out var kernel, out var userTime))
                    cpuTicks = kernel.Value + userTime.Value;

                if (NativeMethods.GetProcessIoCounters(handle, out var io))
                {
                    readBytes = io.ReadBytes;
                    writeBytes = io.WriteBytes;
                    operations = io.ReadOperations + io.WriteOperations;
                }

                if (NativeMethods.GetProcessMemoryInfo(handle, out var counters, CountersSize))
                    pageFaults = counters.PageFaultCount;

                gdi = (int)NativeMethods.GetGuiResources(handle, NativeMethods.GrGdiObjects);
                user = (int)NativeMethods.GetGuiResources(handle, NativeMethods.GrUserObjects);
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        var rates = UpdateRates(pid, cpuTicks, readBytes, writeBytes, operations, pageFaults, stamp);
        var window = windowHandle == 0 ? null : DescribeWindow(windowHandle);

        return new ProcessSample(
            time,
            pid,
            process.ProcessName,
            rates.Cpu,
            process.Threads.Count,
            process.HandleCount,
            process.WorkingSet64 / BytesPerMb,
            process.PrivateMemorySize64 / BytesPerMb,
            process.PagedMemorySize64 / BytesPerMb,
            process.VirtualMemorySize64 / BytesPerMb,
            process.PeakWorkingSet64 / BytesPerMb,
            rates.Faults,
            rates.ReadBytes,
            rates.WriteBytes,
            rates.Operations,
            gdi,
            user,
            foregroundPid == pid,
            window?.State ?? WindowState.None,
            window?.Responding ?? true,
            window?.Bounds.Left ?? 0,
            window?.Bounds.Top ?? 0,
            window is null ? 0 : window.Value.Bounds.Right - window.Value.Bounds.Left,
            window is null ? 0 : window.Value.Bounds.Bottom - window.Value.Bounds.Top,
            process.BasePriority,
            window?.Title ?? string.Empty,
            ParentPid: parentPid,
            RootPid: rootPid);
    }

    private static double Delta(ulong current, ulong previous) => current >= previous ? current - previous : 0;

    private static nint Open(int pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryInformation | NativeMethods.ProcessQueryLimitedInformation, false, pid);
        return handle != 0 ? handle : NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
    }

    private static int GetForegroundPid()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0)
            return -1;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    /// <summary>Picks one representative top-level window per process: the foreground one, else titled and largest.</summary>
    private static Dictionary<int, nint> EnumerateWindows()
    {
        var best = new Dictionary<int, (nint Handle, long Score)>();
        var foreground = NativeMethods.GetForegroundWindow();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner) != 0)
                return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
                return true;

            long score = (long)Math.Max(0, rect.Right - rect.Left) * Math.Max(0, rect.Bottom - rect.Top);
            if (NativeMethods.GetWindowTextLength(hwnd) > 0)
                score += 1L << 40;
            if (hwnd == foreground)
                score += 1L << 50;

            if (!best.TryGetValue((int)pid, out var existing) || score > existing.Score)
                best[(int)pid] = (hwnd, score);

            return true;
        }, 0);

        return best.ToDictionary(pair => pair.Key, pair => pair.Value.Handle);
    }

    private static WindowInfo? DescribeWindow(nint hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            return null;

        var state = NativeMethods.IsIconic(hwnd)
            ? WindowState.Minimized
            : NativeMethods.IsZoomed(hwnd) ? WindowState.Maximized : WindowState.Normal;

        return new WindowInfo(hwnd, NativeMethods.ReadWindowTitle(hwnd), state, rect, !NativeMethods.IsHungAppWindow(hwnd));
    }
}
