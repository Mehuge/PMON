using Avalonia.Media;
using PMON.Data;
using PMON.Models;

namespace PMON.ViewModels;

public sealed record SystemSeriesSpec(string Name, Color Color, Func<SystemSample, double> Selector);

public sealed record ProcessMetricSpec(string Name, Color Color, Func<ProcessSample, double> Selector);

public sealed class ChartViewModel(string title, string description, ValueKind kind, Viewport viewport)
{
    public string Title { get; } = title;
    public string Description { get; } = description;
    public ValueKind Kind { get; } = kind;
    public Viewport Viewport { get; } = viewport;

    /// <summary>Fixed upper bound of the Y axis; null scales to the visible data.</summary>
    public double? FixedMax { get; init; }

    /// <summary>Smallest Y range when auto-scaling so idle charts do not magnify noise.</summary>
    public double MinimumScale { get; init; } = 1;

    public Func<ProcessSample, double>? ProcessSelector { get; init; }

    /// <summary>When set, <see cref="ProcessSelector"/> is drawn for top-consumer series of this reason instead of tracked processes.</summary>
    public string? HogReason { get; init; }
    public IReadOnlyList<SystemSeriesSpec> SystemSeries { get; init; } = [];

    /// <summary>Several metrics of the single process chosen in <see cref="Viewport.SelectedKey"/>.</summary>
    public IReadOnlyList<ProcessMetricSpec> ProcessMetrics { get; init; } = [];
}
