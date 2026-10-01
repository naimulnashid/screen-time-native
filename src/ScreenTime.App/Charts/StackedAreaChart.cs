using ScreenTime.App.Theme;
using ScreenTime.Core.Query;
using ScreenTime.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace ScreenTime.App.Charts;

/// <summary>
/// Top apps by day, or most opened by day: the top eight stacked, everything
/// else as Other. On Windows every band together IS the day's active time -
/// the sampler partitions it, Other included.
/// </summary>
/// <remarks>
/// An unrecorded day is plotted at zero like the trend line, so the bands stay
/// continuous, and its tooltip says "Not recorded" rather than listing a row
/// of zeros.
/// </remarks>
public sealed class StackedAreaChart : ChartSurface
{
    private const double Top = 8, Right = 8, Bottom = 4, Left = 4, YAxisWidth = 56, XAxisHeight = 28;

    private readonly StackData _data;
    private readonly Core.View.Measure _measure;
    private readonly IReadOnlyDictionary<string, string> _colors;
    private Line? _cursor;

    public StackedAreaChart(StackData data, Core.View.Measure measure, IReadOnlyDictionary<string, string> colors, double height = 340, bool animate = true)
        : base(height, animate)
    {
        _data = data;
        _measure = measure;
        _colors = colors;
    }

    protected override void Draw(bool animate)
    {
        var plotLeft = Left + YAxisWidth;
        var plotRight = W - Right;
        var plotBottom = H - Bottom - XAxisHeight;
        var plotW = Math.Max(1, plotRight - plotLeft);
        var points = _data.Points;

        var max = points.Count == 0 ? 0 : points.Max(p => p.Values.Sum());
        var top = Axes.YGrid(Canvas, max, _measure, plotLeft, plotRight, Top, plotBottom);
        double Y(double v) => plotBottom - (top > 0 ? v / top : 0) * (plotBottom - Top);

        var n = points.Count;
        var xs = Enumerable.Range(0, n).Select(i => n == 1 ? plotLeft + plotW / 2 : plotLeft + i * plotW / (n - 1)).ToList();
        Axes.DateLabels(Canvas, points.Select(p => p.Date).ToList(), xs, plotBottom + 6 + 11, W);

        var layer = new Microsoft.UI.Xaml.Controls.Canvas();
        if (n >= 2)
        {
            var below = new double[n];
            for (var s = 0; s < _data.Series.Count; s++)
            {
                var lower = Enumerable.Range(0, n).Select(i => new Point(xs[i], Y(below[i]))).ToList();
                for (var i = 0; i < n; i++) below[i] += points[i].Values[s];
                var upper = Enumerable.Range(0, n).Select(i => new Point(xs[i], Y(below[i]))).ToList();
                var color = Palette.App(_colors, _data.Names[s]);

                // The band: along the top edge, then back along the bottom.
                var figure = ChartKit.MonotoneFigure(upper);
                var back = ChartKit.MonotoneFigure(lower.AsEnumerable().Reverse().ToList());
                figure.Segments.Add(new LineSegment { Point = lower[^1] });
                foreach (var seg in back.Segments.ToList())
                {
                    back.Segments.Remove(seg);
                    figure.Segments.Add(seg);
                }
                figure.IsClosed = true;
                layer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = new PathGeometry { Figures = { figure } },
                    Fill = new SolidColorBrush(Palette.WithAlpha(color, 0.72)),
                });
                layer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = new PathGeometry { Figures = { ChartKit.MonotoneFigure(upper) } },
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = 1,
                });
            }
        }
        Canvas.Children.Add(layer);
        if (animate) Axes.RevealLeft(layer, W, 700);

        _cursor = new Line { Stroke = Palette.AccentBrush, StrokeThickness = 1, StrokeDashArray = [4, 4], Visibility = Visibility.Collapsed, Y1 = Top, Y2 = plotBottom };
        Canvas.Children.Add(_cursor);
    }

    protected override void OnHover(Point at)
    {
        var points = _data.Points;
        if (points.Count == 0 || _cursor is null) return;
        var n = points.Count;
        var plotLeft = Left + YAxisWidth;
        var plotW = Math.Max(1, W - Right - plotLeft);
        var index = n == 1 ? 0 : (int)Math.Clamp(Math.Round((at.X - plotLeft) / plotW * (n - 1)), 0, n - 1);
        _cursor.X1 = _cursor.X2 = n == 1 ? plotLeft + plotW / 2 : plotLeft + index * plotW / (n - 1);
        _cursor.Visibility = Visibility.Visible;

        var point = points[index];
        var body = ChartTooltip.Stack();
        body.MinWidth = 220;
        body.Children.Add(ChartTooltip.Title(Format.DayShort(point.Date)));
        if (!point.Recorded)
        {
            body.Children.Add(Axes.NoData());
        }
        else
        {
            // Largest first, and no zero rows: eight apps at "0s" bury the two used.
            var rows = _data.Names.Select((name, i) => (Name: name, Value: point.Values[i])).Where(r => r.Value > 0).OrderByDescending(r => r.Value).ToList();
            var total = ChartTooltip.Big(Axis.Value(rows.Sum(r => r.Value), _measure), Palette.AccentBrightBrush);
            total.FontSize = 16;
            total.Margin = new Thickness(0, 0, 0, 6);
            body.Children.Add(total);
            foreach (var (name, value) in rows.Take(9))
                body.Children.Add(ChartTooltip.Row(Palette.App(_colors, name), name, _measure == Core.View.Measure.Time ? Format.Duration(value) : Format.Count(value)));
        }
        Tooltip.Show(body, at);
    }

    protected override void ClearHover()
    {
        base.ClearHover();
        if (_cursor is not null) _cursor.Visibility = Visibility.Collapsed;
    }
}
