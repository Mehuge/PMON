using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PMON.Data;
using PMON.ViewModels;

namespace PMON.Controls;

/// <summary>Overview strip of the whole data set with a draggable window that selects what the charts display.</summary>
public sealed class TimelineBar : Control
{
    public static readonly StyledProperty<Viewport?> ViewportProperty =
        AvaloniaProperty.Register<TimelineBar, Viewport?>(nameof(Viewport));

    private const double Inset = 12;
    private const double LabelHeight = 18;

    private static readonly Color CpuColor = Color.Parse("#2DD4BF");
    private static readonly Color MemoryColor = Color.Parse("#A78BFA");

    private Viewport? _hooked;
    private bool _dragging;
    private DateTime _grabTime;
    private DateTime _grabEnd;

    private SampleStore? _cacheStore;
    private int _cacheCount = -1;
    private int _cacheWidth = -1;
    private DateTime _cacheStart;
    private double[] _cpu = [];
    private double[] _memory = [];

    static TimelineBar()
    {
        AffectsRender<TimelineBar>(ViewportProperty);
    }

    public TimelineBar()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public Viewport? Viewport
    {
        get => GetValue(ViewportProperty);
        set => SetValue(ViewportProperty, value);
    }

    private Rect Track => new(Inset, 8, Math.Max(0, Bounds.Width - Inset * 2), Math.Max(0, Bounds.Height - 8 - LabelHeight - 6));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ViewportProperty)
            Rehook();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Rehook();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_hooked is not null)
            _hooked.PropertyChanged -= OnViewportChanged;

        _hooked = null;
    }

    private void Rehook()
    {
        var next = VisualRoot is null ? null : Viewport;
        if (ReferenceEquals(next, _hooked))
            return;

        if (_hooked is not null)
            _hooked.PropertyChanged -= OnViewportChanged;

        _hooked = next;
        if (_hooked is not null)
            _hooked.PropertyChanged += OnViewportChanged;

        InvalidateVisual();
    }

    private void OnViewportChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Viewport is not { } viewport || viewport.Store.IsEmpty || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var (start, end) = Range(viewport);
        var x = e.GetPosition(this).X;
        var pointer = TimeAt(x, start, end);

        if (pointer < viewport.ViewStart || pointer > viewport.ViewEnd)
            viewport.Seek(pointer + viewport.Span / 2);

        _dragging = true;
        _grabTime = pointer;
        _grabEnd = viewport.ViewEnd;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || Viewport is not { } viewport)
            return;

        var (start, end) = Range(viewport);
        viewport.Seek(_grabEnd + (TimeAt(e.GetPosition(this).X, start, end) - _grabTime));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging)
            return;

        _dragging = false;
        e.Pointer.Capture(null);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#0D1117")), new Pen(new SolidColorBrush(Color.Parse("#2A313C")), 1), bounds.Deflate(0.5), 8, 8);

        var track = Track;
        if (Viewport is not { } viewport || viewport.Store.IsEmpty || track.Width < 20)
        {
            var message = ChartKit.Text("Timeline appears once data is available", 12, ChartKit.TextBrush);
            context.DrawText(message, new Point(bounds.Center.X - message.Width / 2, bounds.Center.Y - message.Height / 2));
            return;
        }

        var (start, end) = Range(viewport);
        EnsureCache(viewport.Store, start, end, (int)track.Width);

        DrawSparkline(context, _memory, MemoryColor, track);
        DrawSparkline(context, _cpu, CpuColor, track);
        DrawLabels(context, track, start, end);
        DrawThumb(context, viewport, track, start, end);
    }

    private static (DateTime Start, DateTime End) Range(Viewport viewport)
    {
        var store = viewport.Store;
        var end = store.End > store.Start ? store.End : store.Start.AddSeconds(1);
        return (store.Start, end);
    }

    private double TimeToX(DateTime time, DateTime start, DateTime end) =>
        ChartKit.XOf(time, Track, start, end);

    private DateTime TimeAt(double x, DateTime start, DateTime end) =>
        ChartKit.TimeOf(x, Track, start, end);

    private void EnsureCache(SampleStore store, DateTime start, DateTime end, int width)
    {
        var count = store.System.Count;
        if (ReferenceEquals(store, _cacheStore) && count == _cacheCount && width == _cacheWidth && start == _cacheStart && store.End == end)
            return;

        _cacheStore = store;
        _cacheStart = start;
        _cacheCount = count;
        _cacheWidth = width;
        _cpu = new double[width];
        _memory = new double[width];
        Array.Fill(_cpu, double.NaN);
        Array.Fill(_memory, double.NaN);

        var span = (end - start).Ticks;
        foreach (var sample in store.System)
        {
            var column = Math.Clamp((int)((sample.Time - start).Ticks / (double)span * (width - 1)), 0, width - 1);
            _cpu[column] = double.IsNaN(_cpu[column]) ? sample.CpuPercent : Math.Max(_cpu[column], sample.CpuPercent);
            _memory[column] = double.IsNaN(_memory[column]) ? sample.MemoryPercent : Math.Max(_memory[column], sample.MemoryPercent);
        }
    }

    private static void DrawSparkline(DrawingContext context, double[] columns, Color color, Rect track)
    {
        var area = new StreamGeometry();
        var line = new StreamGeometry();
        using (var areaContext = area.Open())
        using (var lineContext = line.Open())
        {
            var started = false;
            var lastX = 0.0;
            for (var i = 0; i < columns.Length; i++)
            {
                if (double.IsNaN(columns[i]))
                    continue;

                var point = new Point(track.Left + i, track.Bottom - Math.Clamp(columns[i], 0, 100) / 100.0 * track.Height);
                lastX = point.X;
                if (!started)
                {
                    areaContext.BeginFigure(new Point(point.X, track.Bottom), true);
                    lineContext.BeginFigure(point, false);
                    started = true;
                }

                areaContext.LineTo(point);
                lineContext.LineTo(point);
            }

            if (!started)
                return;

            areaContext.LineTo(new Point(lastX, track.Bottom));
            areaContext.EndFigure(true);
            lineContext.EndFigure(false);
        }

        context.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)), null, area);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1), line);
    }

    private void DrawLabels(DrawingContext context, Rect track, DateTime start, DateTime end)
    {
        var format = start.Date == end.Date ? "HH:mm:ss" : "MMM d HH:mm";
        var left = ChartKit.Text(start.ToString(format, CultureInfo.CurrentCulture), 11, ChartKit.TextBrush);
        var right = ChartKit.Text(end.ToString(format, CultureInfo.CurrentCulture), 11, ChartKit.TextBrush);
        context.DrawText(left, new Point(track.Left, track.Bottom + 4));
        context.DrawText(right, new Point(track.Right - right.Width, track.Bottom + 4));

        var legend = ChartKit.Text("CPU", 11, new SolidColorBrush(CpuColor));
        var memory = ChartKit.Text("Memory", 11, new SolidColorBrush(MemoryColor));
        var x = track.Center.X - (legend.Width + memory.Width + 12) / 2;
        context.DrawText(legend, new Point(x, track.Bottom + 4));
        context.DrawText(memory, new Point(x + legend.Width + 12, track.Bottom + 4));
    }

    private void DrawThumb(DrawingContext context, Viewport viewport, Rect track, DateTime start, DateTime end)
    {
        var left = Math.Clamp(TimeToX(viewport.ViewStart, start, end), track.Left, track.Right);
        var right = Math.Clamp(TimeToX(viewport.ViewEnd, start, end), track.Left, track.Right);
        var thumb = new Rect(left, track.Top - 3, Math.Max(6, right - left), track.Height + 6);

        var dim = new SolidColorBrush(Color.Parse("#99080B10"));
        if (thumb.Left > track.Left)
            context.DrawRectangle(dim, null, new Rect(track.Left, track.Top, thumb.Left - track.Left, track.Height));
        if (thumb.Right < track.Right)
            context.DrawRectangle(dim, null, new Rect(thumb.Right, track.Top, track.Right - thumb.Right, track.Height));

        var accent = Color.Parse("#4C9AFF");
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x26, accent.R, accent.G, accent.B)), new Pen(new SolidColorBrush(accent), 1.5), thumb, 5, 5);

        var grip = new Pen(new SolidColorBrush(accent), 2, lineCap: PenLineCap.Round);
        foreach (var edge in new[] { thumb.Left + 4, thumb.Right - 4 })
            context.DrawLine(grip, new Point(edge, thumb.Center.Y - 7), new Point(edge, thumb.Center.Y + 7));

        if (viewport.CursorTime is { } cursor && cursor >= start && cursor <= end)
        {
            var x = TimeToX(cursor, start, end);
            context.DrawLine(new Pen(ChartKit.CursorBrush, 1), new Point(x, track.Top), new Point(x, track.Bottom));
        }
    }
}
