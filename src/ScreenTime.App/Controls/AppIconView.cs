using ScreenTime.App.Imaging;
using ScreenTime.App.Theme;
using ScreenTime.Core.Naming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ScreenTime.App.Controls;

/// <summary>
/// The mark beside an app's name: its logo where there is one, its chart
/// colour where there is not - the swatch is what ties a row to its band, so it
/// is a fallback, not a gap. The box is its final size from the first frame,
/// so nothing moves when the image arrives, and a file that cannot be drawn
/// leaves the swatch.
/// </summary>
public static class AppIconView
{
    public static FrameworkElement Create(string name, IReadOnlyDictionary<string, string> colors, IReadOnlyDictionary<string, AppIcon> icons, double size = 18)
    {
        var color = Palette.App(colors, name);
        // Slightly smaller and rounder than a logo box, so it reads as a marker
        // rather than a missing image.
        var swatch = new Border
        {
            Width = size * 0.62,
            Height = size * 0.62,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = swatch,
        };
        if (AppIcons.Find(icons, name) is not { } icon) return box;

        box.Loaded += async (_, _) =>
        {
            // A skeleton keeps the swatch box: its logos would be stand-ins' logos.
            if (Views.Skeleton.Contains(box)) return;
            var source = await ImageLoader.LoadAsync(icon.Path, size, box.XamlRoot?.RasterizationScale ?? 1);
            if (source is null) return;
            // Near-black marks vanish on a true-black card; only those that
            // measured dark get the light plate (see AppIcons.PlateStems).
            if (icon.Plate)
            {
                box.Background = Palette.PlateBrush;
                box.Padding = new Thickness(1.5);
            }
            box.Child = new Image { Source = source, Stretch = Stretch.Uniform };
        };
        return box;
    }

    /// <summary>A centred legend of names with their marks, as under the web's charts.</summary>
    public static FrameworkElement Legend(IEnumerable<string> names, IReadOnlyDictionary<string, string> colors, IReadOnlyDictionary<string, AppIcon> icons)
    {
        var wrap = new WrapPanel { HorizontalSpacing = 18, VerticalSpacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var name in names)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            item.Children.Add(name == "Other" ? SwatchOnly(Palette.App(colors, name), 15) : Create(name, colors, icons, 15));
            item.Children.Add(Ui.Text(name, 15, 400, Palette.TextMutedBrush));
            wrap.Children.Add(item);
        }
        return wrap;
    }

    /// <summary>A legend entry's swatch alone (Other).</summary>
    public static Border SwatchOnly(Windows.UI.Color color, double size = 11) => Ui.Swatch(color, size * (size > 12 ? 0.73 : 1), 3);
}

/// <summary>
/// Flow layout, centred per line: WinUI has no wrap panel, and a legend that
/// runs off the card is worse than one that wraps.
/// </summary>
public sealed class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 12;
    public double VerticalSpacing { get; set; } = 8;

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        double lineW = 0, lineH = 0, width = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Windows.Foundation.Size(available.Width, double.PositiveInfinity));
            var d = child.DesiredSize;
            if (lineW > 0 && lineW + HorizontalSpacing + d.Width > available.Width)
            {
                width = Math.Max(width, lineW);
                height += lineH + VerticalSpacing;
                lineW = lineH = 0;
            }
            lineW += (lineW > 0 ? HorizontalSpacing : 0) + d.Width;
            lineH = Math.Max(lineH, d.Height);
        }
        width = Math.Max(width, lineW);
        height += lineH;
        return new Windows.Foundation.Size(double.IsFinite(available.Width) ? available.Width : width, height);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        var lines = new List<List<UIElement>>();
        var current = new List<UIElement>();
        double lineW = 0;
        foreach (var child in Children)
        {
            var w = child.DesiredSize.Width;
            if (current.Count > 0 && lineW + HorizontalSpacing + w > final.Width)
            {
                lines.Add(current);
                current = [];
                lineW = 0;
            }
            lineW += (current.Count > 0 ? HorizontalSpacing : 0) + w;
            current.Add(child);
        }
        if (current.Count > 0) lines.Add(current);

        double y = 0;
        foreach (var line in lines)
        {
            var total = line.Sum(c => c.DesiredSize.Width) + HorizontalSpacing * (line.Count - 1);
            var x = HorizontalAlignment == HorizontalAlignment.Left ? 0 : Math.Max(0, (final.Width - total) / 2);
            var h = line.Max(c => c.DesiredSize.Height);
            foreach (var child in line)
            {
                child.Arrange(new Windows.Foundation.Rect(x, y + (h - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height));
                x += child.DesiredSize.Width + HorizontalSpacing;
            }
            y += h + VerticalSpacing;
        }
        return final;
    }
}
