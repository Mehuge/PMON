using Avalonia.Media;
using PMON.Models;

namespace PMON.Data;

public sealed class ProcessSeries(string key, string name, int pid, Color color)
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public int Pid { get; } = pid;
    public Color Color { get; } = color;
    public bool Visible { get; set; } = true;

    /// <summary>Set for top-consumer series: the resource (see <see cref="HogReason"/>) that put the process in the ranking.</summary>
    public string? Reason { get; init; }
    public List<ProcessSample> Samples { get; } = [];
}

/// <summary>In-memory time series for one data source (live capture or a loaded recording). UI-thread only.</summary>
public sealed class SampleStore
{
    private static readonly Color[] Palette =
    [
        Color.Parse("#4C9AFF"), Color.Parse("#F97066"), Color.Parse("#2DD4BF"), Color.Parse("#FBBF24"),
        Color.Parse("#A78BFA"), Color.Parse("#F472B6"), Color.Parse("#84CC16"), Color.Parse("#FB923C"),
        Color.Parse("#22D3EE"), Color.Parse("#E879F9"), Color.Parse("#94A3B8"), Color.Parse("#F43F5E"),
    ];

    private readonly Dictionary<string, ProcessSeries> _byKey = new();
    private readonly Dictionary<string, ProcessSeries> _hogByKey = new();

    public List<SystemSample> System { get; } = [];
    public List<ProcessSeries> Series { get; } = [];

    /// <summary>Untracked processes that were top CPU/memory/disk users, one series per (reason, process).</summary>
    public List<ProcessSeries> HogSeries { get; } = [];

    public bool IsEmpty => System.Count == 0 && Series.Count == 0;

    public DateTime Start { get; private set; } = DateTime.MaxValue;
    public DateTime End { get; private set; } = DateTime.MinValue;

    public void Add(Snapshot snapshot)
    {
        AddSystem(snapshot.System);
        foreach (var process in snapshot.Processes)
            AddProcess(process);

        foreach (var hog in snapshot.Hogs)
            AddHog(hog.Reason, hog.Sample);
    }

    public void AddSystem(SystemSample sample)
    {
        System.Add(sample);
        Extend(sample.Time);
    }

    public void AddProcess(ProcessSample sample)
    {
        var key = sample.Key;
        if (!_byKey.TryGetValue(key, out var series))
        {
            series = new ProcessSeries(key, sample.Name, sample.Pid, Palette[Series.Count % Palette.Length]);
            _byKey[key] = series;
            Series.Add(series);
        }

        series.Samples.Add(sample);
        Extend(sample.Time);
    }

    /// <summary>Records a top-consumer sample for a process that is not matched by any tracked pattern.</summary>
    public void AddHog(string reason, ProcessSample sample)
    {
        var key = $"{reason}|{sample.Key}";
        if (!_hogByKey.TryGetValue(key, out var series))
        {
            series = new ProcessSeries(key, sample.Name, sample.Pid, Palette[HogSeries.Count % Palette.Length]) { Reason = reason };
            _hogByKey[key] = series;
            HogSeries.Add(series);
        }

        series.Samples.Add(sample);
        Extend(sample.Time);
    }

    /// <summary>Drops everything older than <paramref name="cutoff"/> to bound live memory use.</summary>
    public void TrimBefore(DateTime cutoff)
    {
        var systemCount = TimeSearch.FirstAtOrAfter(System, cutoff);
        if (systemCount > 0)
            System.RemoveRange(0, systemCount);

        Trim(Series, _byKey, cutoff);
        Trim(HogSeries, _hogByKey, cutoff);

        Start = System.Count > 0 ? System[0].Time : cutoff;
    }

    private static void Trim(List<ProcessSeries> series, Dictionary<string, ProcessSeries> index, DateTime cutoff)
    {
        foreach (var item in series)
        {
            var count = TimeSearch.FirstAtOrAfter(item.Samples, cutoff);
            if (count > 0)
                item.Samples.RemoveRange(0, count);
        }

        foreach (var empty in series.Where(s => s.Samples.Count == 0).ToList())
        {
            series.Remove(empty);
            index.Remove(empty.Key);
        }
    }

    /// <summary>Typical spacing between samples, used to decide where chart lines should break.</summary>
    public double NominalIntervalSeconds()
    {
        var count = Math.Min(System.Count, 64);
        if (count < 2)
            return 1;

        var gaps = new double[count - 1];
        for (var i = 1; i < count; i++)
            gaps[i - 1] = (System[i].Time - System[i - 1].Time).TotalSeconds;

        Array.Sort(gaps);
        return Math.Max(0.1, gaps[gaps.Length / 2]);
    }

    private void Extend(DateTime time)
    {
        if (time < Start)
            Start = time;
        if (time > End)
            End = time;
    }
}
