using CommunityToolkit.Mvvm.ComponentModel;
using PMON.Data;
using PMON.Models;

namespace PMON.ViewModels;

public sealed partial class ConsumerRow : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;
}

/// <summary>A fixed-size ranking of processes by one metric; rows are updated in place so the panel never resizes.</summary>
public sealed class ConsumerListViewModel
{
    public const int Size = 3;

    private readonly Func<ProcessSample, double> _metric;
    private readonly ValueKind _kind;

    public ConsumerListViewModel(string title, ValueKind kind, Func<ProcessSample, double> metric)
    {
        Title = title;
        _kind = kind;
        _metric = metric;
        Rows = Enumerable.Range(0, Size).Select(_ => new ConsumerRow()).ToArray();
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
                continue;
            }

            Rows[i].Name = i == 0 ? "Idle" : string.Empty;
            Rows[i].Value = string.Empty;
        }
    }
}
