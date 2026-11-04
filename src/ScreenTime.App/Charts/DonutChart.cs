using ScreenTime.App.Theme;
using ScreenTime.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ScreenTime.App.Charts;

/// <summary>One slice of a <see cref="DonutChart"/>: a duration.</summary>
public sealed record DonutSlice(string Label, long Ms, Brush Fill);

/// <summary>
/// A ring of slices clockwise from twelve o'clock, in the order given, with a
/// figure in its centre: "Where the time went". Ported from Data Usage
/// Native's network ring, in durations rather than bytes. Hovering a slice
/// names it with its time and share.
/// </summary>
public sealed class DonutChart : ChartSurface
{
    private const double Thickness = 0.32, Gap = 1.5;

    private readonly IReadOnlyList<DonutSlice> _slices;
    private readonly long _centreMs;
    private readonly string _caption;
    private readonly List<(double From, double To)> _angles = [];
    private Point _centre;
    private double _outer;

    /// <param name="centreMs">The figure in the middle (active time), which need not be the slices' sum.</param>
    public DonutChart(IReadOnlyList<DonutSlice> slices, long centreMs, string caption, double size = 230, bool animate = true) : base(size, animate)
    {
        _slices = slices;
        _centreMs = centreMs;
        _caption = caption;
        Width = size;
    }

    private long Sum => Math.Max(1, _slices.Sum(s => s.Ms));

    protected override void Draw(bool animate)
    {
        _angles.Clear();
        _outer = Math.Min(W, H) / 2;
        _centre = new Point(W / 2, H / 2);
        var inner = _outer * (1 - Thickness);
        var sum = Sum;
        var gap = _slices.Count(s => s.Ms > 0) > 1 ? Gap : 0;

        var ring = new Microsoft.UI.Xaml.Controls.Canvas();
        var at = -90.0;
        foreach (var s in _slices)
        {
            var sweep = 360.0 * s.Ms / sum;
            _angles.Add((at, at + sweep));
            if (sweep > 0.05) ring.Children.Add(Slice(at + gap / 2, Math.Max(0.05, sweep - gap), _outer, inner, s.Fill));
            at += sweep;
        }
        Canvas.Children.Add(ring);
        if (animate) Ui.Rise(ring);

        var value = Ui.Text(Format.Duration(_centreMs), 23, 650, Palette.TextBrush, -0.02, numeric: true, selectable: false);
        var caption = Ui.Text(_caption, 13, 400, Palette.TextFaintBrush, selectable: false);
        var centre = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        value.HorizontalAlignment = caption.HorizontalAlignment = HorizontalAlignment.Center;
        centre.Children.Add(value);
        centre.Children.Add(caption);
        centre.Measure(new Size(W, H));
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(centre, _centre.X - centre.DesiredSize.Width / 2);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(centre, _centre.Y - centre.DesiredSize.Height / 2);
        Canvas.Children.Add(centre);
    }

    /// <summary>An annular sector from <paramref name="start"/>, clockwise, in degrees.</summary>
    private Microsoft.UI.Xaml.Shapes.Path Slice(double start, double sweep, double outer, double inner, Brush fill)
    {
        // One arc cannot close a full ring, so a lone slice stops just short.
        if (sweep >= 359.9) sweep = 359.9;
        Point On(double deg, double r) => new(_centre.X + r * Math.Cos(deg * Math.PI / 180), _centre.Y + r * Math.Sin(deg * Math.PI / 180));
        var large = sweep > 180;
        var figure = new PathFigure { StartPoint = On(start, outer), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment { Point = On(start + sweep, outer), Size = new Size(outer, outer), IsLargeArc = large, SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = On(start + sweep, inner) });
        figure.Segments.Add(new ArcSegment { Point = On(start, inner), Size = new Size(inner, inner), IsLargeArc = large, SweepDirection = SweepDirection.Counterclockwise });
        return new Microsoft.UI.Xaml.Shapes.Path { Data = new PathGeometry { Figures = { figure } }, Fill = fill };
    }

    protected override void OnHover(Point at)
    {
        var dx = at.X - _centre.X;
        var dy = at.Y - _centre.Y;
        var r = Math.Sqrt(dx * dx + dy * dy);
        if (r > _outer || r < _outer * (1 - Thickness))
        {
            Tooltip.Hide();
            return;
        }
        var deg = Math.Atan2(dy, dx) * 180 / Math.PI;
        if (deg < -90) deg += 360;
        var index = _angles.FindIndex(a => deg >= a.From && deg < a.To);
        if (index < 0) return;
        var s = _slices[index];
        var body = ChartTooltip.Stack();
        body.Children.Add(ChartTooltip.Title(s.Label));
        body.Children.Add(ChartTooltip.Big(Format.Duration(s.Ms), Palette.AccentBrightBrush));
        body.Children.Add(ChartTooltip.Muted($"{Format.Percent((double)s.Ms / Sum * 100)} of all recorded time", 4));
        Tooltip.Show(body, at);
    }
}
