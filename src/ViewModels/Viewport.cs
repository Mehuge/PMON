using CommunityToolkit.Mvvm.ComponentModel;
using PMON.Data;

namespace PMON.ViewModels;

public sealed record SpanOption(string Label, TimeSpan Span);

/// <summary>Shared view state for every chart and the timeline: which store, which time window, and the hover cursor.</summary>
public sealed partial class Viewport : ObservableObject
{
    public static readonly IReadOnlyList<SpanOption> SpanOptions =
    [
        new("15 sec", TimeSpan.FromSeconds(15)),
        new("30 sec", TimeSpan.FromSeconds(30)),
        new("1 min", TimeSpan.FromMinutes(1)),
        new("2 min", TimeSpan.FromMinutes(2)),
        new("5 min", TimeSpan.FromMinutes(5)),
        new("10 min", TimeSpan.FromMinutes(10)),
        new("15 min", TimeSpan.FromMinutes(15)),
        new("30 min", TimeSpan.FromMinutes(30)),
        new("1 hour", TimeSpan.FromHours(1)),
        new("2 hours", TimeSpan.FromHours(2)),
        new("6 hours", TimeSpan.FromHours(6)),
    ];

    private static readonly TimeSpan FollowTolerance = TimeSpan.FromSeconds(1);

    [ObservableProperty]
    private SampleStore _store;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewStart))]
    private DateTime _viewEnd = DateTime.Now;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewStart))]
    private TimeSpan _span = TimeSpan.FromMinutes(5);

    [ObservableProperty]
    private DateTime? _cursorTime;

    [ObservableProperty]
    private int _version;

    [ObservableProperty]
    private bool _isLive = true;

    [ObservableProperty]
    private bool _followLive = true;

    /// <summary>Key of the process shown by single-process charts; null when none is selected.</summary>
    [ObservableProperty]
    private string? _selectedKey;

    public Viewport(SampleStore store)
    {
        _store = store;
    }

    public DateTime ViewStart => ViewEnd - Span;

    /// <summary>Longest distance between two samples that is still drawn as one continuous line.</summary>
    public double GapSeconds => Math.Max(2.5, Store.NominalIntervalSeconds() * 3);

    public void Invalidate() => Version++;

    public void DataAppended()
    {
        if (IsLive && FollowLive && !Store.IsEmpty)
            ViewEnd = Store.End;

        Invalidate();
    }

    public void Show(SampleStore store, bool live)
    {
        Store = store;
        IsLive = live;
        FollowLive = live;
        CursorTime = null;

        if (store.IsEmpty)
            ViewEnd = DateTime.Now;
        else
            ViewEnd = live ? store.End : Min(store.Start + Span, store.End);

        Invalidate();
    }

    public void Seek(DateTime end)
    {
        if (Store.IsEmpty)
            return;

        var lowest = Min(Store.Start + Span, Store.End);
        ViewEnd = end < lowest ? lowest : end > Store.End ? Store.End : end;
        FollowLive = IsLive && Store.End - ViewEnd <= FollowTolerance;
    }

    public void ResumeFollowing()
    {
        FollowLive = true;
        if (!Store.IsEmpty)
            ViewEnd = Store.End;
    }

    public void Zoom(int direction)
    {
        var current = SpanOptions.ToList().FindIndex(o => o.Span >= Span);
        var index = Math.Clamp((current < 0 ? SpanOptions.Count - 1 : current) + direction, 0, SpanOptions.Count - 1);
        SetSpan(SpanOptions[index].Span);
    }

    public void SetSpan(TimeSpan span)
    {
        Span = span;
        if (!Store.IsEmpty)
            Seek(ViewEnd);
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
