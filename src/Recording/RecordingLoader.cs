using System.Globalization;
using PMON.Data;
using PMON.Models;

namespace PMON.Recording;

public static class RecordingLoader
{
    public static SampleStore Load(string path, CancellationToken cancellation = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        var headerLine = reader.ReadLine() ?? throw new InvalidDataException("The file is empty.");
        var index = CsvFormat.SplitLine(headerLine)
            .Select((name, position) => (name, position))
            .ToDictionary(pair => pair.name, pair => pair.position, StringComparer.OrdinalIgnoreCase);

        if (!index.ContainsKey("Time") || !index.ContainsKey("Scope"))
            throw new InvalidDataException("Not a PMON recording (missing Time/Scope columns).");

        var store = new SampleStore();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            cancellation.ThrowIfCancellationRequested();
            if (line.Length == 0)
                continue;

            var row = new Row(CsvFormat.SplitLine(line), index);
            if (!DateTime.TryParseExact(row.Text("Time"), CsvFormat.TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                continue;

            var scope = row.Text("Scope");
            if (scope.Equals(CsvFormat.SystemScope, StringComparison.OrdinalIgnoreCase))
            {
                store.AddSystem(ReadSystem(row, time));
                continue;
            }

            if (scope.Equals(CsvFormat.ProcessScope, StringComparison.OrdinalIgnoreCase))
            {
                store.AddProcess(ReadProcess(row, time));
                continue;
            }

            if (scope.Equals(CsvFormat.HogScope, StringComparison.OrdinalIgnoreCase))
                store.AddHog(row.Text("HogBy"), ReadProcess(row, time));
        }

        return store;
    }

    private static SystemSample ReadSystem(Row r, DateTime time) => new(
        time,
        r.Number("CpuPct"), r.Number("MemPct"), r.Number("MemUsedMB"), r.Number("MemTotalMB"),
        r.Number("CommitUsedMB"), r.Number("CommitLimitMB"),
        r.Number("DiskBusyPct"), r.Number("DiskReadBps"), r.Number("DiskWriteBps"));

    private static ProcessSample ReadProcess(Row r, DateTime time) => new(
        time,
        r.Integer("Pid"), r.Text("Name"),
        r.Number("CpuPct"), r.Integer("Threads"), r.Integer("Handles"),
        r.Number("WorkingSetMB"), r.Number("PrivateMB"), r.Number("PagefileMB"), r.Number("VirtualMB"), r.Number("PeakWorkingSetMB"),
        r.Number("PageFaultsPerSec"), r.Number("IoReadBps"), r.Number("IoWriteBps"), r.Number("IoOpsPerSec"),
        r.Integer("GdiObjects"), r.Integer("UserObjects"),
        r.Text("Foreground") == "1",
        Enum.TryParse<WindowState>(r.Text("WindowState"), out var state) ? state : WindowState.None,
        r.Text("Responding") != "0",
        r.Integer("WinX"), r.Integer("WinY"), r.Integer("WinW"), r.Integer("WinH"),
        r.Integer("BasePriority"), r.Text("WindowTitle"),
        ParentPid: r.Integer("ParentPid"), RootPid: r.Integer("RootPid"));

    private readonly struct Row(List<string> fields, Dictionary<string, int> index)
    {
        public string Text(string column) =>
            index.TryGetValue(column, out var i) && i < fields.Count ? fields[i] : string.Empty;

        public double Number(string column) =>
            double.TryParse(Text(column), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

        public int Integer(string column) =>
            int.TryParse(Text(column), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
