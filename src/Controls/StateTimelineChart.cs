using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using PMON.Data;
using PMON.Models;
using PMON.ViewModels;

namespace PMON.Controls;

/// <summary>One row per process showing when it was foreground, in the background, minimised, windowless or hung.</summary>
public sealed class StateTimelineChart : ViewportControl
{
    public static readonly StyledProperty<Viewport?> ViewportProperty =
        AvaloniaProperty.Register<StateTimelineChart, Viewport?>(nameof(Viewport));

    public static readonly StyledProperty<bool> SelectedOnlyProperty =
        AvaloniaProperty.Register<StateTimelineChart, bool>(nameof(SelectedOnly));

    private const double LabelWidth = 170;
    private const double LegendHeight = 30;
    private const double RowHeight = 30;

    private enum ActivityState
    {
        NoWindow,
        Background,
        Foreground,
        Minimized,
        NotResponding,
    }

    private static readonly (ActivityState State, string Label, Color Color)[] States =
    [
        (ActivityState.Foreground, "Foreground", Color.Parse("#2DD4BF")),
        (ActivityState.Background, "Background", Color.Parse("#4C79B8")),
        (ActivityState.Minimized, "Minimised", Color.Parse("#FBBF24")),
        (ActivityState.NoWindow, "No window", Color.Parse("#323B49")),
        (ActivityState.NotResponding, "Not responding", Color.Parse("#F43F5E")),
    ];

    private int _hoverRow = -1;

    static StateTimelineChart()
    {
        AffectsRender<StateTimelineChart>(ViewportProperty);
        AffectsMeasure<StateTimelineChart>(ViewportProperty);
    }

    /// <summary>Show only the process chosen in <see cref="Viewport.SelectedKey"/>.</summary>
    public bool SelectedOnly
    {
        get => GetValue(SelectedOnlyProperty);
        set => SetValue(SelectedOnlyProperty, value);
    }

    public Viewport? Viewport
    {
        get => GetValue(ViewportProperty);
        set => SetValue(ViewportProperty, value);
    }

    protected override Viewport? ActiveViewport => Viewport;

    protected override Rect PlotArea => new(
        LabelWidth,
        LegendHeight,
        Math.Max(0, Bounds.Width - LabelWidth - ChartKit.RightMargin),
        Math.Max(0, Bounds.Height - LegendHeight - ChartKit.BottomMargin));

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = Viewport is null ? 0 : VisibleSeries(Viewport).Count;
        return new Size(0, LegendHeight + Math.Max(rows, 1) * RowHeight + ChartKit.BottomMargin);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ViewportProperty)
            Rehook();
    }

    protected override void OnViewportChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnViewportChanged(sender, e);
        if (e.PropertyName is nameof(Viewport.Version) or nameof(Viewport.Store) or nameof(Viewport.ViewEnd) or nameof(Viewport.Span) or nameof(Viewport.SelectedKey))
            InvalidateMeasure();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hoverRow = (int)Math.Floor((e.GetPosition(this).Y - LegendHeight) / RowHeight);
    }

    public override void Render(DrawingContext context)
    {
        DrawHitSurface(context);
        if (Viewport is not { } viewport)
            return;

        var plot = PlotArea;
        if (plot.Width < 40)
            return;

        var start = viewport.ViewStart;
        var end = viewport.ViewEnd;
        var rows = VisibleSeries(viewport);

        DrawLegend(context);
        var area = new Rect(plot.Left, plot.Top, plot.Width, Math.Max(rows.Count, 1) * RowHeight);
        ChartKit.DrawTimeAxis(context, area, start, end);

        if (rows.Count == 0)
        {
            var message = ChartKit.Text(SelectedOnly ? "Select a process in the sidebar" : "No process activity in this time range", 12, ChartKit.TextBrush);
            context.DrawText(message, new Point(area.Center.X - message.Width / 2, area.Center.Y - message.Height / 2));
            return;
        }

        var gap = viewport.GapSeconds;
        var nominal = viewport.Store.NominalIntervalSeconds();
        for (var row = 0; row < rows.Count; row++)
        {
            var band = new Rect(area.Left, area.Top + row * RowHeight + 5, area.Width, RowHeight - 10);
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#12FFFFFF")), null, band, 4, 4);

            var series = rows[row];
            DrawRowLabel(context, series, area.Left - 12, band);
            using (context.PushClip(band))
                DrawSegments(context, series, band, area, start, end, gap, nominal);
        }

        DrawCursor(context, viewport, rows, area, start, end);
    }

    private List<ProcessSeries> VisibleSeries(Viewport viewport)
    {
        var store = viewport.Store;
        IEnumerable<ProcessSeries> candidates = SelectedOnly
            ? store.FindSubject(viewport.SelectedKey) is { } subject ? [subject] : []
            : store.Series.Concat(store.Groups).Where(s => s.Visible && !s.Collapsed);

        return candidates
            .Where(s => s.Samples.Count > 0 && s.Samples[0].Time <= viewport.ViewEnd && s.Samples[^1].Time >= viewport.ViewStart)
            .ToList();
    }

    private static void DrawLegend(DrawingContext context)
    {
        var x = LabelWidth;
        foreach (var (_, label, color) in States)
        {
            context.DrawRectangle(new SolidColorBrush(color), null, new Rect(x, 9, 12, 12), 3, 3);
            var text = ChartKit.Text(label, 11, ChartKit.TextBrush);
            context.DrawText(text, new Point(x + 18, 15 - text.Height / 2));
            x += 18 + text.Width + 18;
        }
    }

    private static void DrawRowLabel(DrawingContext context, ProcessSeries series, double right, Rect band)
    {
        var text = ChartKit.Text(series.Label, 12, ChartKit.StrongTextBrush);
        text.MaxTextWidth = right - 30;
        text.MaxLineCount = 1;
        text.Trimming = TextTrimming.CharacterEllipsis;
        context.DrawEllipse(new SolidColorBrush(series.Color), null, new Point(14, band.Center.Y), 4.5, 4.5);
        context.DrawText(text, new Point(26, band.Center.Y - text.Height / 2));
    }

    private static void DrawSegments(DrawingContext context, ProcessSeries series, Rect band, Rect area, DateTime start, DateTime end, double gapSeconds, double nominalSeconds)
    {
        var samples = series.Samples;
        var first = Math.Max(0, TimeSearch.LastAtOrBefore(samples, start));
        var last = Math.Min(samples.Count - 1, TimeSearch.FirstAtOrAfter(samples, end));

        ActivityState? runState = null;
        double runStart = 0, runEnd = 0;

        void Flush()
        {
            if (runState is { } state && runEnd > runStart)
                context.DrawRectangle(new SolidColorBrush(ColorOf(state)), null, new Rect(runStart, band.Top, Math.Max(1, runEnd - runStart), band.Height));

            runState = null;
        }

        for (var i = first; i <= last; i++)
        {
            var from = samples[i].Time;
            var to = i + 1 < samples.Count && (samples[i + 1].Time - from).TotalSeconds <= gapSeconds
                ? samples[i + 1].Time
                : from.AddSeconds(nominalSeconds);

            var x0 = ChartKit.XOf(from, area, start, end);
            var x1 = ChartKit.XOf(to, area, start, end);
            var state = StateOf(samples[i]);

            if (runState == state && Math.Abs(x0 - runEnd) < 0.5)
            {
                runEnd = x1;
                continue;
            }

            Flush();
            runState = state;
            runStart = x0;
            runEnd = x1;
        }

        Flush();
    }

    private void DrawCursor(DrawingContext context, Viewport viewport, List<ProcessSeries> rows, Rect area, DateTime start, DateTime end)
    {
        if (viewport.CursorTime is not { } cursor || cursor < start || cursor > end)
            return;

        var x = ChartKit.XOf(cursor, area, start, end);
        context.DrawLine(new Pen(ChartKit.CursorBrush, 1, DashStyle.Dash), new Point(x, area.Top), new Point(x, area.Bottom));

        if (!IsHovering || _hoverRow < 0 || _hoverRow >= rows.Count)
            return;

        var series = rows[_hoverRow];
        var index = TimeSearch.LastAtOrBefore(series.Samples, cursor);
        if (index < 0 || (cursor - series.Samples[index].Time).TotalSeconds > viewport.GapSeconds)
            return;

        var sample = series.Samples[index];
        var lines = new List<(Color, string)>
        {
            (series.Color, series.Label),
            (ColorOf(StateOf(sample)), States.First(s => s.State == StateOf(sample)).Label),
        };

        if (sample.Window != WindowState.None)
        {
            lines.Add((Color.Parse("#8B949E"), $"{sample.Window} at {sample.WindowX}, {sample.WindowY}  {sample.WindowWidth}x{sample.WindowHeight}"));
            if (sample.WindowTitle.Length > 0)
                lines.Add((Color.Parse("#8B949E"), sample.WindowTitle.Length > 60 ? sample.WindowTitle[..60] + "…" : sample.WindowTitle));
        }

        ChartKit.DrawTooltip(context, new Rect(Bounds.Size), new Point(x, area.Top + (_hoverRow + 0.5) * RowHeight),
            cursor.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture), lines);
    }

    private static ActivityState StateOf(ProcessSample sample)
    {
        if (!sample.Responding)
            return ActivityState.NotResponding;
        if (sample.Foreground)
            return ActivityState.Foreground;

        return sample.Window switch
        {
            WindowState.None => ActivityState.NoWindow,
            WindowState.Minimized => ActivityState.Minimized,
            _ => ActivityState.Background,
        };
    }

    private static Color ColorOf(ActivityState state) => States.First(s => s.State == state).Color;
}
