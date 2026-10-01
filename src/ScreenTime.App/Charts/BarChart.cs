using ScreenTime.App.Theme;
using ScreenTime.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace ScreenTime.App.Charts;

/// <summary>One column: its axis label, its tooltip title, and its value (null = not recorded).</summary>
public sealed record Bar(string Label, string Title, long? Value);

/// <summary>
/// Columns in the accent: hour of day, and an app's days - in time or in
/// opens. With <c>dimAllButPeak</c> the busiest column keeps full strength and
/// the rest are dimmed, so the shape of the day reads at a glance.
/// </summary>
public sealed class BarChart : ChartSurface
{
    private const double Top = 8, Right = 8, Bottom = 4, Left = 4, YAxisWidth = 56, XAxisHeight = 28;

    private readonly IReadOnlyList<Bar> _bars;
    private readonly Core.View.Measure _measure;
    private readonly bool _dimAllButPeak;
    private readonly bool _labelEvery;
    private readonly double _radius;
    private readonly List<(double X, double Width)> _bands = [];
    private Rectangle? _wash;
    private double _plotBottom;

    public BarChart(IReadOnlyList<Bar> bars, Core.View.Measure measure, bool dimAllButPeak, bool labelEvery, double radius, double height, bool animate = true) : base(height, animate)
    {
        _bars = bars;
        _measure = measure;
        _dimAllButPeak = dimAllButPeak;
        _labelEvery = labelEvery;
        _radius = radius;
    }

    /// <summary>Hour of day: every hour labelled, the busiest at full strength.</summary>
    public static BarChart Hourly(IReadOnlyList<HourPoint> hours, Core.View.Measure measure, double height = 220) =>
        new(hours.Select(h => new Bar(h.Hour.ToString("00"), $"{Format.HourOfDay(h.Hour)} – {Format.HourOfDay((h.Hour + 1) % 24)}", h.Value)).ToList(), measure, true, true, 5, height);

    /// <summary>An app's days: every day it had time.</summary>
    public static BarChart Daily(IReadOnlyList<DailyPoint> days, Core.View.Measure measure, double height = 260) =>
        new(days.Select(d => new Bar(d.Date, Format.DayShort(d.Date), d.Ms)).ToList(), measure, false, false, 4, height);

    protected override void Draw(bool animate)
    {
        _bands.Clear();
        var plotLeft = Left + YAxisWidth;
        var plotRight = W - Right;
        _plotBottom = H - Bottom - XAxisHeight;
        var plotW = Math.Max(1, plotRight - plotLeft);

        var peak = _bars.Count == 0 ? 0 : _bars.Max(b => b.Value ?? 0);
        var top = Axes.YGrid(Canvas, peak, _measure, plotLeft, plotRight, Top, _plotBottom);
        double Y(double v) => _plotBottom - (top > 0 ? v / top : 0) * (_plotBottom - Top);

        _wash = new Rectangle { Fill = Palette.HoverWashBrush, Visibility = Visibility.Collapsed, Height = _plotBottom - Top };
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(_wash, Top);
        Canvas.Children.Add(_wash);

        var n = _bars.Count;
        var band = plotW / Math.Max(1, n);
        var gap = band * 0.1;
        var barW = Math.Max(1, band - 2 * gap);
        var layer = new Microsoft.UI.Xaml.Controls.Canvas();
        var centres = new List<double>();
        for (var i = 0; i < n; i++)
        {
            var x = plotLeft + i * band;
            _bands.Add((x, band));
            centres.Add(x + band / 2);
            var value = _bars[i].Value ?? 0;
            if (value <= 0) continue;
            var dim = _dimAllButPeak && value != peak;
            var y = Y(value);
            layer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = Axes.RoundedTop(new Rect(x + gap, y, barW, Math.Max(0.5, _plotBottom - y)), _radius),
                Fill = Palette.AccentBrush,
                Opacity = dim ? 0.55 : 1,
            });
        }
        Canvas.Children.Add(layer);

        if (_labelEvery)
        {
            for (var i = 0; i < n; i++) ChartKit.Label(Canvas, _bars[i].Label, centres[i], _plotBottom + 6 + 11, 0);
        }
        else
        {
            Axes.DateLabels(Canvas, _bars.Select(b => b.Label).ToList(), centres, _plotBottom + 6 + 11, W);
        }

        if (animate) Axes.GrowUp(layer, _plotBottom);
    }

    protected override void OnHover(Point at)
    {
        if (_wash is null) return;
        var index = _bands.FindIndex(b => at.X >= b.X && at.X < b.X + b.Width);
        if (index < 0 || at.Y > _plotBottom)
        {
            ClearHover();
            return;
        }
        _wash.Width = _bands[index].Width;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_wash, _bands[index].X);
        _wash.Visibility = Visibility.Visible;

        var bar = _bars[index];
        var body = ChartTooltip.Stack();
        body.Children.Add(ChartTooltip.Title(bar.Title));
        body.Children.Add(bar.Value is { } v ? Ui.Text(Axis.Value(v, _measure), 17, 650, Palette.AccentBrightBrush, numeric: true) : Axes.NoData());
        Tooltip.Show(body, at);
    }

    protected override void ClearHover()
    {
        base.ClearHover();
        if (_wash is not null) _wash.Visibility = Visibility.Collapsed;
    }
}
