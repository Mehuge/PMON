using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PMON.Data;
using PMON.Models;
using PMON.ViewModels;

namespace PMON.Controls;

public sealed class TimeSeriesChart : ViewportControl
{
    public static readonly StyledProperty<ChartViewModel?> ModelProperty =
        AvaloniaProperty.Register<TimeSeriesChart, ChartViewModel?>(nameof(Model));

    private const int YDivisions = 4;
    private const int MaxTooltipRows = 8;

    private sealed record DrawSeries(string Name, Color Color, IPointSource Points, int First, int Last);

    static TimeSeriesChart()
    {
        AffectsRender<TimeSeriesChart>(ModelProperty);
    }

    public ChartViewModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    protected override Viewport? ActiveViewport => Model?.Viewport;
    protected override Rect PlotArea => ChartKit.PlotArea(Bounds.Size);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty)
            Rehook();
    }

    public override void Render(DrawingContext context)
    {
        DrawHitSurface(context);
        if (Model is not { } model)
            return;

        var plot = PlotArea;
        if (plot.Width < 40 || plot.Height < 40)
            return;

        var viewport = model.Viewport;
        var start = viewport.ViewStart;
        var end = viewport.ViewEnd;
        var series = CollectSeries(model, viewport.Store, viewport.SelectedKey, start, end);

        var yMax = ComputeYMax(model, series, start, end);
        DrawYAxis(context, model, plot, yMax);
        ChartKit.DrawTimeAxis(context, plot, start, end);

        if (series.Count == 0)
        {
            var text = model.ProcessMetrics.Count > 0 && viewport.SelectedKey is null
                ? "Select a process in the sidebar"
                : "No data in this time range";
            var message = ChartKit.Text(text, 12, ChartKit.TextBrush);
            context.DrawText(message, new Point(plot.Center.X - message.Width / 2, plot.Center.Y - message.Height / 2));
            return;
        }

        var fill = model.ProcessSelector is null && model.SystemSeries.Count + model.ProcessMetrics.Count == 1;
        var gap = viewport.GapSeconds;
        using (context.PushClip(plot.Inflate(2)))
        {
            foreach (var item in series)
                DrawSeriesLine(context, item, plot, start, end, yMax, gap, fill);
        }

        DrawLegend(context, model, plot);
        DrawCursor(context, model, series, plot, start, end, yMax, gap);
    }

    private static List<DrawSeries> CollectSeries(ChartViewModel model, SampleStore store, string? selectedKey, DateTime start, DateTime end)
    {
        var result = new List<DrawSeries>();

        foreach (var spec in model.SystemSeries)
            Add(result, spec.Name, spec.Color, new PointSource<SystemSample>(store.System, spec.Selector), start, end);

        if (model.ProcessMetrics.Count > 0 && store.FindSubject(selectedKey) is { } subject)
        {
            foreach (var metric in model.ProcessMetrics)
                Add(result, metric.Name, metric.Color, new PointSource<ProcessSample>(subject.Samples, metric.Selector), start, end);
        }

        if (model.ProcessSelector is { } selector)
        {
            var source = model.HogReason is null
                ? store.Series.Concat(store.Groups).Where(p => p.Visible && !p.Collapsed)
                : store.HogSeries.Where(p => p.Reason == model.HogReason);

            foreach (var process in source)
                Add(result, process.Label, process.Color, new PointSource<ProcessSample>(process.Samples, selector), start, end);
        }

        return result;
    }

    private static void Add(List<DrawSeries> target, string name, Color color, IPointSource points, DateTime start, DateTime end)
    {
        if (points.Count == 0)
            return;

        var first = Math.Max(0, points.LastAtOrBefore(start));
        var last = Math.Min(points.Count - 1, points.FirstAtOrAfter(end));
        if (points.TimeAt(first) > end || points.TimeAt(last) < start)
            return;

        target.Add(new DrawSeries(name, color, points, first, last));
    }

    private static double ComputeYMax(ChartViewModel model, List<DrawSeries> series, DateTime start, DateTime end)
    {
        if (model.FixedMax is { } fixedMax)
            return fixedMax;

        var max = 0.0;
        foreach (var item in series)
        {
            for (var i = item.First; i <= item.Last; i++)
            {
                if (item.Points.TimeAt(i) < start || item.Points.TimeAt(i) > end)
                    continue;

                max = Math.Max(max, item.Points.ValueAt(i));
            }
        }

        return ChartKit.NiceCeiling(Math.Max(model.MinimumScale, max * 1.08));
    }

    private static void DrawYAxis(DrawingContext context, ChartViewModel model, Rect plot, double yMax)
    {
        for (var i = 0; i <= YDivisions; i++)
        {
            var y = plot.Bottom - plot.Height * i / YDivisions;
            context.DrawLine(ChartKit.GridPen, new Point(plot.Left, y), new Point(plot.Right, y));

            var label = ChartKit.Text(ValueFormatter.Format(model.Kind, yMax * i / YDivisions), 11, ChartKit.TextBrush);
            context.DrawText(label, new Point(plot.Left - label.Width - 8, y - label.Height / 2));
        }
    }

    private static void DrawSeriesLine(DrawingContext context, DrawSeries series, Rect plot, DateTime start, DateTime end, double yMax, double gapSeconds, bool fill)
    {
        var runs = BuildRuns(series, plot, start, end, yMax, gapSeconds);
        var brush = new SolidColorBrush(series.Color);
        var pen = new Pen(brush, 1.6, lineJoin: PenLineJoin.Round);

        var line = new StreamGeometry();
        var area = fill ? new StreamGeometry() : null;
        using (var lineContext = line.Open())
        using (var areaContext = area?.Open())
        {
            foreach (var run in runs)
            {
                if (run.Count == 1)
                {
                    context.DrawEllipse(brush, null, run[0], 2.5, 2.5);
                    continue;
                }

                lineContext.BeginFigure(run[0], false);
                for (var i = 1; i < run.Count; i++)
                    lineContext.LineTo(run[i]);
                lineContext.EndFigure(false);

                if (areaContext is null)
                    continue;

                areaContext.BeginFigure(new Point(run[0].X, plot.Bottom), true);
                foreach (var point in run)
                    areaContext.LineTo(point);
                areaContext.LineTo(new Point(run[^1].X, plot.Bottom));
                areaContext.EndFigure(true);
            }
        }

        if (area is not null)
        {
            var gradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x55, series.Color.R, series.Color.G, series.Color.B), 0),
                    new GradientStop(Color.FromArgb(0x04, series.Color.R, series.Color.G, series.Color.B), 1),
                },
            };
            context.DrawGeometry(gradient, null, area);
        }

        context.DrawGeometry(null, pen, line);
    }

    /// <summary>Converts samples to screen points, collapsing samples that share a pixel column to their min/max and splitting runs at data gaps.</summary>
    private static List<List<Point>> BuildRuns(DrawSeries series, Rect plot, DateTime start, DateTime end, double yMax, double gapSeconds)
    {
        var points = series.Points;
        var runs = new List<List<Point>>();
        List<Point>? run = null;
        var previousTime = DateTime.MinValue;

        double YOf(double value) => plot.Bottom - Math.Clamp(value / yMax, 0, 1) * plot.Height;
        double XOf(int index) => ChartKit.XOf(points.TimeAt(index), plot, start, end);

        var i = series.First;
        while (i <= series.Last)
        {
            var column = Math.Floor(XOf(i));
            int minIndex = i, maxIndex = i;
            double minValue = points.ValueAt(i), maxValue = minValue;

            var j = i + 1;
            while (j <= series.Last && Math.Floor(XOf(j)) == column)
            {
                var value = points.ValueAt(j);
                if (value < minValue)
                {
                    minValue = value;
                    minIndex = j;
                }

                if (value > maxValue)
                {
                    maxValue = value;
                    maxIndex = j;
                }

                j++;
            }

            if (run is null || (points.TimeAt(i) - previousTime).TotalSeconds > gapSeconds)
            {
                run = [];
                runs.Add(run);
            }

            var (firstIndex, firstValue, lastIndex, lastValue) = minIndex <= maxIndex
                ? (minIndex, minValue, maxIndex, maxValue)
                : (maxIndex, maxValue, minIndex, minValue);

            run.Add(new Point(XOf(firstIndex), YOf(firstValue)));
            if (lastIndex != firstIndex)
                run.Add(new Point(XOf(lastIndex), YOf(lastValue)));

            previousTime = points.TimeAt(j - 1);
            i = j;
        }

        return runs;
    }

    private static void DrawLegend(DrawingContext context, ChartViewModel model, Rect plot)
    {
        var entries = model.SystemSeries.Select(s => (s.Name, s.Color))
            .Concat(model.ProcessMetrics.Select(m => (m.Name, m.Color)))
            .ToList();

        if (entries.Count == 0 || (entries.Count == 1 && model.ProcessSelector is null))
            return;

        var x = plot.Right - 6;
        foreach (var (name, color) in Enumerable.Reverse(entries))
        {
            var label = ChartKit.Text(name, 11, ChartKit.TextBrush);
            x -= label.Width;
            context.DrawText(label, new Point(x, plot.Top + 4));
            x -= 12;
            context.DrawEllipse(new SolidColorBrush(color), null, new Point(x + 4, plot.Top + 4 + label.Height / 2), 4, 4);
            x -= 12;
        }
    }

    private void DrawCursor(DrawingContext context, ChartViewModel model, List<DrawSeries> series, Rect plot, DateTime start, DateTime end, double yMax, double gapSeconds)
    {
        if (model.Viewport.CursorTime is not { } cursor || cursor < start || cursor > end)
            return;

        var x = ChartKit.XOf(cursor, plot, start, end);
        context.DrawLine(new Pen(ChartKit.CursorBrush, 1, DashStyle.Dash), new Point(x, plot.Top), new Point(x, plot.Bottom));

        var readings = new List<(Color Color, string Name, double Value)>();
        foreach (var item in series)
        {
            var index = item.Points.LastAtOrBefore(cursor);
            if (index < 0 || (cursor - item.Points.TimeAt(index)).TotalSeconds > gapSeconds)
                continue;

            var value = item.Points.ValueAt(index);
            readings.Add((item.Color, item.Name, value));

            var y = plot.Bottom - Math.Clamp(value / yMax, 0, 1) * plot.Height;
            context.DrawEllipse(new SolidColorBrush(item.Color), new Pen(Brushes.White, 1), new Point(x, y), 3.5, 3.5);
        }

        if (!IsHovering || readings.Count == 0)
            return;

        var rows = readings
            .OrderByDescending(r => r.Value)
            .Take(MaxTooltipRows)
            .Select(r => (r.Color, $"{r.Name}  {ValueFormatter.Format(model.Kind, r.Value)}"))
            .ToList();

        if (readings.Count > MaxTooltipRows)
            rows.Add((Color.Parse("#8B949E"), $"+{readings.Count - MaxTooltipRows} more"));

        ChartKit.DrawTooltip(context, new Rect(Bounds.Size), new Point(x, plot.Center.Y), cursor.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture), rows);
    }
}
