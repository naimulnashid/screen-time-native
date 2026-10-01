using ScreenTime.App.Theme;
using ScreenTime.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace ScreenTime.App.Charts;

/// <summary>
/// Active time per day: ONE continuous line over a fading area, every day of
/// the range a point.
/// </summary>
/// <remarks>
/// A day the sampler did not record is drawn at ZERO, the line dipping to the
/// floor - the owner's call on 2026-09-23, after a line broken at the gap read
/// as a broken chart and a shaded "Not recorded" band read as clutter. The day
/// is still null in the data, so the tooltip says "Not recorded" rather than
/// "0s", and nothing downstream averages it in.
/// </remarks>
public sealed class TrendChart : ChartSurface
{
    private const double Top = 8, Right = 8, Bottom = 4, Left = 4, YAxisWidth = 56, XAxisHeight = 28;

    private readonly IReadOnlyList<DailyPoint> _days;
    private readonly List<Point> _points = [];
    private Line? _cursor;
    private Ellipse? _activeDot;

    public TrendChart(IReadOnlyList<DailyPoint> daily, double height = 300, bool animate = true) : base(height, animate) => _days = daily;

    protected override void Draw(bool animate)
    {
        _points.Clear();
        var plotLeft = Left + YAxisWidth;
        var plotRight = W - Right;
        var plotBottom = H - Bottom - XAxisHeight;
        var plotW = Math.Max(1, plotRight - plotLeft);

        var max = _days.Count == 0 ? 0 : _days.Max(d => d.Ms ?? 0);
        var top = Axes.YGrid(Canvas, max, Core.View.Measure.Time, plotLeft, plotRight, Top, plotBottom);
        double Y(double v) => plotBottom - (top > 0 ? v / top : 0) * (plotBottom - Top);

        var n = _days.Count;
        var xs = Enumerable.Range(0, n).Select(i => n == 1 ? plotLeft + plotW / 2 : plotLeft + i * plotW / (n - 1)).ToList();
        for (var i = 0; i < n; i++) _points.Add(new Point(xs[i], Y(_days[i].Ms ?? 0)));
        Axes.DateLabels(Canvas, _days.Select(d => d.Date).ToList(), xs, plotBottom + 6 + 11, W);

        var layer = new Microsoft.UI.Xaml.Controls.Canvas();
        if (_points.Count == 1)
        {
            layer.Children.Add(Dot(_points[0], 3, Palette.AccentBrush));
        }
        else if (_points.Count > 1)
        {
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Palette.WithAlpha(Palette.Accent, 0.55), Offset = 0 },
                    new GradientStop { Color = Palette.WithAlpha(Palette.Accent, 0.02), Offset = 1 },
                },
            };
            var fill = ChartKit.MonotoneFigure(_points);
            fill.Segments.Add(new LineSegment { Point = new Point(_points[^1].X, plotBottom) });
            fill.Segments.Add(new LineSegment { Point = new Point(_points[0].X, plotBottom) });
            fill.IsClosed = true;
            layer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = new PathGeometry { Figures = { fill } }, Fill = gradient });
            layer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = new PathGeometry { Figures = { ChartKit.MonotoneFigure(_points) } },
                Stroke = Palette.AccentBrush,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
            });
        }
        Canvas.Children.Add(layer);
        if (animate) Axes.RevealLeft(layer, W);

        _cursor = new Line { Stroke = Palette.AccentBrush, StrokeThickness = 1, StrokeDashArray = [4, 4], Visibility = Visibility.Collapsed, Y1 = Top, Y2 = plotBottom };
        _activeDot = Dot(new Point(0, 0), 5, Palette.AccentBrightBrush);
        _activeDot.Visibility = Visibility.Collapsed;
        Canvas.Children.Add(_cursor);
        Canvas.Children.Add(_activeDot);
    }

    private static Ellipse Dot(Point at, double r, Brush fill)
    {
        var dot = new Ellipse { Width = r * 2, Height = r * 2, Fill = fill, Stroke = Palette.BgBrush, StrokeThickness = 2 };
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(dot, at.X - r);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(dot, at.Y - r);
        return dot;
    }

    protected override void OnHover(Point at)
    {
        if (_days.Count == 0 || _cursor is null || _activeDot is null) return;
        var n = _days.Count;
        var plotLeft = Left + YAxisWidth;
        var plotW = Math.Max(1, W - Right - plotLeft);
        var index = n == 1 ? 0 : (int)Math.Clamp(Math.Round((at.X - plotLeft) / plotW * (n - 1)), 0, n - 1);
        var p = _points[index];
        _cursor.X1 = _cursor.X2 = p.X;
        _cursor.Visibility = Visibility.Visible;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_activeDot, p.X - 5);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(_activeDot, p.Y - 5);
        _activeDot.Visibility = Visibility.Visible;

        var day = _days[index];
        var body = ChartTooltip.Stack();
        body.Children.Add(ChartTooltip.Title(Format.DayShort(day.Date)));
        body.Children.Add(day.Ms is { } ms ? Ui.Text(Format.Duration(ms), 17, 650, Palette.AccentBrightBrush, numeric: true) : Axes.NoData());
        Tooltip.Show(body, at);
    }

    protected override void ClearHover()
    {
        base.ClearHover();
        if (_cursor is not null) _cursor.Visibility = Visibility.Collapsed;
        if (_activeDot is not null) _activeDot.Visibility = Visibility.Collapsed;
    }
}
