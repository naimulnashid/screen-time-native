using ScreenTime.App.Theme;
using ScreenTime.Core.Query;
using ScreenTime.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace ScreenTime.App.Charts;

/// <summary>
/// The top apps as horizontal bars in their own colours, ranked by time or by
/// opens. Two charts on By App, not one: the app you spend longest in and the
/// app you reach for most are routinely not the same, and either ranking
/// alone reads as "what I use most" while meaning something narrower.
/// </summary>
public sealed class TopAppsChart : ChartSurface
{
    private const double Top = 4, Right = 20, Bottom = 4, Left = 4, CategoryWidth = 150, XAxisHeight = 28, BarSize = 26;

    private readonly IReadOnlyList<AppRow> _apps;
    private readonly Core.View.Measure _measure;
    private readonly IReadOnlyDictionary<string, string> _colors;
    private readonly Action<AppRow>? _open;
    private int _hovered = -1;
    private readonly List<(double Y, double H)> _bands = [];
    private Rectangle? _wash;
    private double _plotLeft;

    /// <param name="open">
    /// Opens an app's detail page. A bar is a shortcut to the page the table's
    /// name links to, and only for apps that earn one, exactly as the table
    /// links only those.
    /// </param>
    public TopAppsChart(IReadOnlyList<AppRow> apps, Core.View.Measure measure, IReadOnlyDictionary<string, string> colors, bool animate = true, Action<AppRow>? open = null)
        : base(Math.Max(220, apps.Count * 40) + 32, animate)
    {
        _apps = apps;
        _measure = measure;
        _colors = colors;
        _open = open;
        Canvas.Tapped += (_, e) =>
        {
            var index = BandAt(e.GetPosition(Canvas));
            if (index >= 0 && _apps[index].Detailed) _open?.Invoke(_apps[index]);
        };
    }

    private long ValueOf(AppRow a) => _measure == Core.View.Measure.Time ? a.Ms : a.Opens;

    private int BandAt(Point at) => _bands.FindIndex(b => at.Y >= b.Y && at.Y < b.Y + b.H);

    protected override void Draw(bool animate)
    {
        _bands.Clear();
        _plotLeft = Left + CategoryWidth;
        var plotRight = W - Right;
        var plotBottom = H - Bottom - XAxisHeight;
        var plotW = Math.Max(1, plotRight - _plotLeft);

        var ticks = Axis.Ticks(_apps.Count == 0 ? 0 : _apps.Max(ValueOf), _measure);
        double top = ticks[^1];
        double X(double v) => _plotLeft + (top > 0 ? v / top : 0) * plotW;
        foreach (var tick in ticks)
        {
            Canvas.Children.Add(ChartKit.VLine(X(tick), Top, plotBottom, Palette.GridBrush));
            ChartKit.Label(Canvas, Axis.Tick(tick, _measure), X(tick), plotBottom + 6 + 11, 0);
        }

        _wash = new Rectangle { Fill = Palette.HoverWashBrush, Visibility = Visibility.Collapsed, Width = plotW };
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_wash, _plotLeft);
        Canvas.Children.Add(_wash);

        var band = (plotBottom - Top) / Math.Max(1, _apps.Count);
        var layer = new Microsoft.UI.Xaml.Controls.Canvas();
        for (var i = 0; i < _apps.Count; i++)
        {
            var app = _apps[i];
            var y = Top + i * band;
            _bands.Add((y, band));
            var centre = y + band / 2;
            CategoryLabel(app.Name, centre);
            var width = Math.Max(0, X(ValueOf(app)) - _plotLeft);
            var barH = Math.Min(BarSize, band * 0.8);
            if (width > 0)
                layer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = Axes.RoundedRight(new Rect(_plotLeft, centre - barH / 2, width, barH), 6),
                    Fill = new SolidColorBrush(Palette.App(_colors, app.Name)),
                });
        }
        Canvas.Children.Add(layer);

        if (animate)
        {
            var scale = new ScaleTransform { ScaleX = 0, CenterX = _plotLeft };
            layer.RenderTransform = scale;
            var story = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var grow = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(800)), EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut } };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(grow, scale);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(grow, "ScaleX");
            story.Children.Add(grow);
            story.Begin();
        }
    }

    /// <summary>Right-aligned against the bars, wrapping to two lines.</summary>
    private void CategoryLabel(string text, double centre)
    {
        var block = Ui.Text(text, 13, 400, Palette.TextFaintBrush, wrap: true, selectable: false);
        block.TextAlignment = TextAlignment.Right;
        block.MaxLines = 2;
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        block.LineHeight = 14;
        block.Width = CategoryWidth - 12;
        block.Measure(new Size(CategoryWidth - 12, double.PositiveInfinity));
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(block, Left);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(block, centre - block.DesiredSize.Height / 2);
        Canvas.Children.Add(block);
    }

    protected override void OnHover(Point at)
    {
        if (_wash is null) return;
        var index = BandAt(at);
        if (index < 0)
        {
            ClearHover();
            return;
        }
        if (index != _hovered)
        {
            _hovered = index;
            ProtectedCursor = _open is not null && _apps[index].Detailed
                ? Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand)
                : null;
        }
        _wash.Height = _bands[index].H;
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(_wash, _bands[index].Y);
        _wash.Visibility = Visibility.Visible;

        var app = _apps[index];
        var body = ChartTooltip.Stack();
        var head = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(Ui.Swatch(Palette.App(_colors, app.Name)));
        head.Children.Add(Ui.Text(app.Name, 14, 600));
        body.Children.Add(head);
        var big = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 7 };
        big.Children.Add(Ui.Text(Axis.Value(ValueOf(app), _measure), 17, 650, new SolidColorBrush(Palette.AppText(_colors, app.Name)), numeric: true));
        if (_measure == Core.View.Measure.Time)
        {
            var share = Ui.Text($"{Format.Percent(app.Share)} of active time", 13, 500, Palette.TextMutedBrush);
            share.VerticalAlignment = VerticalAlignment.Bottom;
            share.Margin = new Thickness(0, 0, 0, 2);
            big.Children.Add(share);
        }
        body.Children.Add(big);
        body.Children.Add(ChartTooltip.Muted(_measure == Core.View.Measure.Time ? Format.Opens(app.Opens) : $"{Format.Duration(app.Ms)} · {Format.Percent(app.Share)} of active time"));
        if (_open is not null && app.Detailed) body.Children.Add(ChartTooltip.Muted("Click the bar for details", 8));
        Tooltip.Show(body, at);
    }

    protected override void ClearHover()
    {
        base.ClearHover();
        _hovered = -1;
        ProtectedCursor = null;
        if (_wash is not null) _wash.Visibility = Visibility.Collapsed;
    }
}
