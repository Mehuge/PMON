using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PMON.ViewModels;

namespace PMON.Controls;

/// <summary>Shared plumbing for controls drawn against a <see cref="Viewport"/>: redraw on change, hover cursor, drag-to-pan, wheel zoom.</summary>
public abstract class ViewportControl : Control
{
    private Viewport? _hooked;
    private bool _panning;
    private double _panOriginX;
    private DateTime _panOriginEnd;

    protected abstract Viewport? ActiveViewport { get; }
    protected abstract Rect PlotArea { get; }
    protected bool IsHovering { get; private set; }

    protected void Rehook()
    {
        var next = VisualRoot is null ? null : ActiveViewport;
        if (ReferenceEquals(next, _hooked))
            return;

        if (_hooked is not null)
            _hooked.PropertyChanged -= OnViewportChanged;

        _hooked = next;
        if (_hooked is not null)
            _hooked.PropertyChanged += OnViewportChanged;

        InvalidateMeasure();
        InvalidateVisual();
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

    protected virtual void OnViewportChanged(object? sender, PropertyChangedEventArgs e)
    {
        InvalidateVisual();
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        IsHovering = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        IsHovering = false;
        if (!_panning && ActiveViewport is { } viewport)
            viewport.CursorTime = null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (ActiveViewport is not { } viewport || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _panning = true;
        _panOriginX = e.GetPosition(this).X;
        _panOriginEnd = viewport.ViewEnd;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_panning)
            return;

        _panning = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (ActiveViewport is not { } viewport)
            return;

        var position = e.GetPosition(this);
        var plot = PlotArea;
        if (plot.Width <= 0)
            return;

        if (_panning)
        {
            var shift = TimeSpan.FromTicks((long)(-(position.X - _panOriginX) / plot.Width * viewport.Span.Ticks));
            viewport.Seek(_panOriginEnd + shift);
        }

        viewport.CursorTime = position.X >= plot.Left && position.X <= plot.Right
            ? ChartKit.TimeOf(position.X, plot, viewport.ViewStart, viewport.ViewEnd)
            : null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (ActiveViewport is not { } viewport || e.Delta.Y == 0 || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;

        viewport.Zoom(e.Delta.Y > 0 ? -1 : 1);
        e.Handled = true;
    }

    protected void DrawHitSurface(DrawingContext context) =>
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
}
