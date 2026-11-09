using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMON.Data;
using PMON.Models;

namespace PMON.ViewModels;

public sealed partial class ConsumerRow(Action<ProcessSample> activate) : ObservableObject
{
    private ProcessSample? _sample;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private string? _tooltip;

    [ObservableProperty]
    private bool _hasProcess;

    public void SetSample(ProcessSample? sample)
    {
        _sample = sample;
        HasProcess = sample is not null;
        Tooltip = sample is null ? null : Describe(sample);
    }

    [RelayCommand]
    private void Activate()
    {
        if (_sample is not null)
            activate(_sample);
    }

    private static string Describe(ProcessSample s)
    {
        var lines = new List<string>
        {
            $"{s.Name}  ·  PID {s.Pid}",
            $"CPU {ValueFormatter.Format(ValueKind.Percent, s.CpuPercent)}   ·   Memory {ValueFormatter.Format(ValueKind.Megabytes, s.WorkingSetMb)}   ·   Threads {s.Threads}",
            $"Disk read {ValueFormatter.Format(ValueKind.BytesPerSecond, s.IoReadBytesPerSec)}   ·   write {ValueFormatter.Format(ValueKind.BytesPerSecond, s.IoWriteBytesPerSec)}",
        };

        if (s.WindowTitle.Length > 0)
            lines.Add($"“{s.WindowTitle}”");

        return string.Join('\n', lines);
    }
}

/// <summary>A fixed-size ranking of processes by one metric; rows are updated in place so the panel never resizes.</summary>
public sealed class ConsumerListViewModel
{
    public const int Size = 3;

    private readonly Func<ProcessSample, double> _metric;
    private readonly ValueKind _kind;

    public ConsumerListViewModel(string title, ValueKind kind, Func<ProcessSample, double> metric, Action<ProcessSample> activate)
    {
        Title = title;
        _kind = kind;
        _metric = metric;
        Rows = Enumerable.Range(0, Size).Select(_ => new ConsumerRow(activate)).ToArray();
    }

    public string Title { get; }
    public IReadOnlyList<ConsumerRow> Rows { get; }

    public void Show(IEnumerable<ProcessSample> samples)
    {
        var top = samples.Where(s => _metric(s) > 0).OrderByDescending(_metric).Take(Size).ToList();
        for (var i = 0; i < Rows.Count; i++)
        {
            if (i < top.Count)
            {
                Rows[i].Name = top[i].Name;
                Rows[i].Value = ValueFormatter.Format(_kind, _metric(top[i]));
                Rows[i].SetSample(top[i]);
                continue;
            }

            Rows[i].Name = i == 0 ? "Idle" : string.Empty;
            Rows[i].Value = string.Empty;
            Rows[i].SetSample(null);
        }
    }
}
