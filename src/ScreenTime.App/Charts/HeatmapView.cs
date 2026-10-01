using System.Numerics;
using ScreenTime.App.Theme;
using ScreenTime.Core.Naming;
using ScreenTime.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace ScreenTime.App.Charts;

/// <summary>
/// One block of the activity heat map: 26 week columns, a weekday column and
/// a month row on one set of tracks, so the three stay registered. Cells are
/// fluid - the block fills its panel and stays square.
/// </summary>
/// <remarks>
/// Three kinds of cell, and conflating any two invents or hides history:
/// hidden (after today, or before the page starts) is not drawn; never
/// recorded is the neutral <c>HeatNone</c> with an outline; a known day,
/// quiet or not, takes the ramp. <c>max</c> is passed in so that stacked
/// blocks share one scale.
/// </remarks>
public sealed class HeatmapView : Canvas
{
    private const double DayColumn = 34, Gap = 3, MonthRow = 15, MinCell = 11, Gutter = 7;

    private readonly HeatmapBlock _block;
    private readonly long _max;
    private double _lastWidth = -1;

    public HeatmapView(HeatmapBlock block, long max)
    {
        _block = block;
        _max = max;
        SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - _lastWidth) < 0.5) return;
            _lastWidth = e.NewSize.Width;
            Draw();
        };
        Height = CellSize(1000) * 7 + Gap * 7 + MonthRow + Gutter * 2;
    }

    private static double CellSize(double width) =>
        Math.Max(MinCell, (width - Gutter * 2 - DayColumn - Gap * Heatmap.Weeks) / Heatmap.Weeks);

    private void Draw()
    {
        Children.Clear();
        var width = ActualWidth;
        if (width <= 0) return;
        var cell = CellSize(width);
        Height = Gutter + MonthRow + Gap + 7 * cell + 6 * Gap + Gutter + 3;
        double X(int column) => Gutter + DayColumn + Gap + column * (cell + Gap);
        double Y(int row) => Gutter + MonthRow + Gap + row * (cell + Gap);

        foreach (var (label, column) in _block.Months)
        {
            var text = Ui.Text(label, 12, 500, Palette.TextFaintBrush, selectable: false);
            SetLeft(text, X(column));
            SetTop(text, Gutter - 2);
            Children.Add(text);
        }
        for (var row = 0; row < 7; row++)
        {
            var text = Ui.Text(Heatmap.DayLabels[row], 11, 400, Palette.TextFaintBrush, selectable: false);
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            SetLeft(text, Gutter);
            SetTop(text, Y(row) + (cell - text.DesiredSize.Height) / 2);
            Children.Add(text);
        }

        foreach (var c in _block.Cells)
        {
            if (c.Hidden) continue;
            var rect = new Rectangle
            {
                Width = cell,
                Height = cell,
                RadiusX = 3,
                RadiusY = 3,
                Fill = Views.Skeleton.Contains(this) ? Palette.InsetBrush
                    : c.Known ? Palette.Heat[AppColors.HeatStep(c.Total, _max)] : Palette.HeatNoneBrush,
                // Never-recorded days carry an outline, so they cannot read as quiet ones.
                Stroke = c.Known ? Palette.TransparentBrush : Palette.HeatNoneRingBrush,
                StrokeThickness = 1,
                CenterPoint = new Vector3((float)cell / 2, (float)cell / 2, 0),
                ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(140) },
            };
            SetLeft(rect, X(c.Column));
            SetTop(rect, Y(c.Row));
            var rest = rect.Stroke;
            rect.PointerEntered += (_, _) =>
            {
                rect.Scale = new Vector3(1.14f, 1.14f, 1);
                rect.Stroke = Palette.TextFaintBrush;
                SetZIndex(rect, 1);
            };
            rect.PointerExited += (_, _) =>
            {
                rect.Scale = Vector3.One;
                rect.Stroke = rest;
                SetZIndex(rect, 0);
            };
            ToolTipService.SetToolTip(rect, Ui.TipContent(c.Known ? $"{Format.DayLong(c.Date)} - {Format.Duration(c.Total)}" : $"{Format.DayLong(c.Date)} - not recorded"));
            Children.Add(rect);
        }
    }

    /// <summary>
    /// The legend row: the summary on the left, an optional action (Expand) in
    /// the middle, the Less-More scale on the right.
    /// </summary>
    public static Grid Legend(long total, int activeDays, string span, UIElement? action)
    {
        var legend = new Grid { Margin = new Thickness(0, 16, 0, 0), ColumnSpacing = 16 };
        legend.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        legend.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        legend.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var summary = Ui.Text($"{Format.Duration(total)} across {activeDays} active {(activeDays == 1 ? "day" : "days")} {span}", 13.5, 400, Palette.TextFaintBrush);
        summary.VerticalAlignment = VerticalAlignment.Center;
        legend.Children.Add(summary);
        if (action is FrameworkElement a)
        {
            Grid.SetColumn(a, 1);
            legend.Children.Add(a);
        }
        var scale = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        scale.Children.Add(Ui.Text("Less", 13.5, 400, Palette.TextFaintBrush, selectable: false));
        foreach (var brush in Palette.Heat)
            scale.Children.Add(new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(3), Background = brush, VerticalAlignment = VerticalAlignment.Center });
        scale.Children.Add(Ui.Text("More", 13.5, 400, Palette.TextFaintBrush, selectable: false));
        Grid.SetColumn(scale, 2);
        legend.Children.Add(scale);
        return legend;
    }
}
