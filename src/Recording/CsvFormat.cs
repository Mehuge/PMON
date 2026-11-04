using System.Globalization;
using System.Text;
using PMON.Models;

namespace PMON.Recording;

/// <summary>Single-file recording format: one row per system sample and one row per process sample.</summary>
public static class CsvFormat
{
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";
    public const string SystemScope = "System";
    public const string ProcessScope = "Process";
    public const string HogScope = "Hog";

    public static readonly string[] Columns =
    [
        "Time", "Scope", "Pid", "Name",
        "CpuPct", "MemPct", "MemUsedMB", "MemTotalMB", "CommitUsedMB", "CommitLimitMB",
        "DiskBusyPct", "DiskReadBps", "DiskWriteBps",
        "Threads", "Handles", "WorkingSetMB", "PrivateMB", "PagefileMB", "VirtualMB", "PeakWorkingSetMB",
        "PageFaultsPerSec", "IoReadBps", "IoWriteBps", "IoOpsPerSec", "GdiObjects", "UserObjects",
        "Foreground", "WindowState", "Responding", "WinX", "WinY", "WinW", "WinH", "BasePriority", "WindowTitle", "HogBy",
    ];

    public static string Header => string.Join(',', Columns);

    private const int SystemColumnCount = 13;

    public static string FormatSystem(SystemSample s) => string.Join(',',
        Time(s.Time), SystemScope, "", "",
        N(s.CpuPercent), N(s.MemoryPercent), N(s.MemoryUsedMb), N(s.MemoryTotalMb), N(s.CommitUsedMb), N(s.CommitLimitMb),
        N(s.DiskBusyPercent), N(s.DiskReadBytesPerSec, 0), N(s.DiskWriteBytesPerSec, 0))
        + new string(',', Columns.Length - SystemColumnCount);

    public static string FormatHog(HogSample hog) => FormatProcess(hog.Sample, HogScope, hog.Reason);

    public static string FormatProcess(ProcessSample p, string scope = ProcessScope, string hogBy = "") => string.Join(',',
        Time(p.Time), scope, p.Pid.ToString(CultureInfo.InvariantCulture), Escape(p.Name),
        N(p.CpuPercent), "", "", "", "", "",
        "", "", "",
        p.Threads.ToString(CultureInfo.InvariantCulture), p.Handles.ToString(CultureInfo.InvariantCulture),
        N(p.WorkingSetMb), N(p.PrivateMb), N(p.PagefileMb), N(p.VirtualMb), N(p.PeakWorkingSetMb),
        N(p.PageFaultsPerSec, 1), N(p.IoReadBytesPerSec, 0), N(p.IoWriteBytesPerSec, 0), N(p.IoOpsPerSec, 1),
        p.GdiObjects.ToString(CultureInfo.InvariantCulture), p.UserObjects.ToString(CultureInfo.InvariantCulture),
        p.Foreground ? "1" : "0", p.Window.ToString(), p.Responding ? "1" : "0",
        p.WindowX.ToString(CultureInfo.InvariantCulture), p.WindowY.ToString(CultureInfo.InvariantCulture),
        p.WindowWidth.ToString(CultureInfo.InvariantCulture), p.WindowHeight.ToString(CultureInfo.InvariantCulture),
        p.BasePriority.ToString(CultureInfo.InvariantCulture), Escape(p.WindowTitle), hogBy);

    public static List<string> SplitLine(string line)
    {
        var fields = new List<string>(40);
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c != '"')
                    current.Append(c);
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                    quoted = false;

                continue;
            }

            if (c == '"')
                quoted = true;
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
                current.Append(c);
        }

        fields.Add(current.ToString());
        return fields;
    }

    private static string Time(DateTime time) => time.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static string N(double value, int decimals = 2) =>
        value.ToString("F" + decimals, CultureInfo.InvariantCulture);

    private static string Escape(string value)
    {
        var flat = value.ReplaceLineEndings(" ");
        return flat.AsSpan().IndexOfAny(",\"") < 0 ? flat : "\"" + flat.Replace("\"", "\"\"") + "\"";
    }
}
