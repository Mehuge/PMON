using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace PMON.Controls;

internal static class ChartKit
{
    public const double LeftMargin = 60;
    public const double RightMargin = 14;
    public const double TopMargin = 12;
    public const double BottomMargin = 26;

    private static readonly double[] TimeSteps =
        [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 21600, 43200, 86400];

    public static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#8B949E"));
    public static readonly IBrush StrongTextBrush = new SolidColorBrush(Color.Parse("#E6EDF3"));
    public static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#1FFFFFFF")), 1);
    public static readonly IBrush CursorBrush = new SolidColorBrush(Color.Parse("#99E6EDF3"));
    public static readonly IBrush TooltipBackground = new SolidColorBrush(Color.Parse("#F2121820"));
    public static readonly IPen TooltipBorder = new Pen(new SolidColorBrush(Color.Parse("#3D4A5C")), 1);

    private static readonly Typeface Face = new(FontFamily.Default);

    public static Rect PlotArea(Size size) => new(
        LeftMargin,
        TopMargin,
        Math.Max(0, size.Width - LeftMargin - RightMargin),
        Math.Max(0, size.Height - TopMargin - BottomMargin));

    public static FormattedText Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(Face.FontFamily, FontStyle.Normal, weight), size, brush);

    public static double XOf(DateTime time, Rect plot, DateTime start, DateTime end)
    {
        var span = (end - start).Ticks;
        return span <= 0 ? plot.Left : plot.Left + (time - start).Ticks / (double)span * plot.Width;
    }

    public static DateTime TimeOf(double x, Rect plot, DateTime start, DateTime end)
    {
        var fraction = plot.Width <= 0 ? 0 : Math.Clamp((x - plot.Left) / plot.Width, 0, 1);
        return start + TimeSpan.FromTicks((long)((end - start).Ticks * fraction));
    }

    public static double NiceCeiling(double value)
    {
        if (value <= 0)
            return 1;

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var fraction = value / magnitude;
        var nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 2.5 ? 2.5 : fraction <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    public static void DrawTimeAxis(DrawingContext context, Rect plot, DateTime start, DateTime end)
    {
        var seconds = (end - start).TotalSeconds;
        if (seconds <= 0 || plot.Width <= 0)
            return;

        var minimumStep = seconds / plot.Width * 90;
        var step = TimeSteps.FirstOrDefault(s => s >= minimumStep);
        if (step == 0)
            step = TimeSteps[^1];

        var stepTicks = (long)(step * TimeSpan.TicksPerSecond);
        var format = start.Date != end.Date ? "MMM d HH:mm" : step < 60 ? "HH:mm:ss" : "HH:mm";
        var first = (start.Ticks + stepTicks - 1) / stepTicks * stepTicks;

        for (var ticks = first; ticks <= end.Ticks; ticks += stepTicks)
        {
            var time = new DateTime(ticks);
            var x = XOf(time, plot, start, end);
            context.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));

            var label = Text(time.ToString(format, CultureInfo.CurrentCulture), 11, TextBrush);
            var labelX = Math.Clamp(x - label.Width / 2, 0, Math.Max(0, plot.Right + RightMargin - label.Width));
            context.DrawText(label, new Point(labelX, plot.Bottom + 6));
        }
    }

    public static void DrawTooltip(DrawingContext context, Rect bounds, Point anchor, string header, IReadOnlyList<(Color Color, string Text)> lines)
    {
        const double padding = 8;
        const double lineHeight = 17;
        const double swatch = 8;

        var headerText = Text(header, 11, TextBrush);
        var texts = lines.Select(line => Text(line.Text, 12, StrongTextBrush)).ToList();
        var width = Math.Max(headerText.Width, texts.Count == 0 ? 0 : texts.Max(t => t.Width + swatch + 6)) + padding * 2;
        var height = padding * 2 + lineHeight * (texts.Count + 1);

        var x = anchor.X + 14 + width > bounds.Right ? anchor.X - 14 - width : anchor.X + 14;
        var y = Math.Clamp(anchor.Y - height / 2, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - height));
        context.DrawRectangle(TooltipBackground, TooltipBorder, new Rect(x, y, width, height), 6, 6);

        context.DrawText(headerText, new Point(x + padding, y + padding));
        for (var i = 0; i < texts.Count; i++)
        {
            var top = y + padding + lineHeight * (i + 1);
            context.DrawEllipse(new SolidColorBrush(lines[i].Color), null, new Point(x + padding + swatch / 2, top + lineHeight / 2), swatch / 2, swatch / 2);
            context.DrawText(texts[i], new Point(x + padding + swatch + 6, top));
        }
    }
}
