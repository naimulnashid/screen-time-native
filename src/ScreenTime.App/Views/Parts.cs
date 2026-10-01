using System.Globalization;
using ScreenTime.App.Controls;
using ScreenTime.App.Theme;
using ScreenTime.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ScreenTime.App.Views;

/// <summary>Pieces more than one page draws.</summary>
public static class Parts
{
    /// <summary>The page's h1 and the line under it.</summary>
    public static StackPanel PageHead(string title, string? sub, UIElement? before = null, UIElement? after = null, UIElement? titleExtra = null)
    {
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 35) };
        if (before is not null) head.Children.Add(before);
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        if (titleExtra is not null) titleRow.Children.Add(titleExtra);
        var h1 = Ui.Text(title, 35, 600, spacing: -0.02);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(h1, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        titleRow.Children.Add(h1);
        head.Children.Add(titleRow);
        if (sub is not null)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 11, Margin = new Thickness(0, 6, 0, 0) };
            line.Children.Add(Ui.Text(sub, 15, 400, Palette.TextMutedBrush, wrap: true));
            if (after is not null) line.Children.Add(after);
            head.Children.Add(line);
        }
        return head;
    }

    /// <summary>The small uppercase label over a figure.</summary>
    public static TextBlock StatLabel(string text)
    {
        var label = Ui.Caps(text, 13, 0.09);
        label.IsTextSelectionEnabled = false;
        return label;
    }

    /// <summary>
    /// A duration as two rungs with their units set smaller - "6h 42m" -
    /// counting up in the FINAL value's shape, so a figure ending in hours
    /// never races through "3s" and "58m 4s" on the way.
    /// </summary>
    public static StackPanel DurationValue(long ms, Brush? brush = null, double size = 44)
    {
        var parts = Format.SplitDuration(ms);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = size * 0.06 };
        string Rung(double v, int index)
        {
            var shaped = Format.DurationLike((long)v, ms).Split(' ');
            return index < shaped.Length ? shaped[index].TrimEnd('h', 'm', 's') : "0";
        }
        void Add(int index, string unit, bool gapBefore)
        {
            var value = Ui.Text("", size, 650, brush ?? Palette.TextBrush, -0.035, numeric: true);
            if (gapBefore) value.Margin = new Thickness(size * 0.18, 0, 0, 0);
            CountUp.Apply(value, ms, v => Rung(v, index));
            row.Children.Add(value);
            var unitText = Ui.Text(unit, size * 0.42, 600, Palette.TextMutedBrush);
            unitText.VerticalAlignment = VerticalAlignment.Bottom;
            unitText.Margin = new Thickness(0, 0, 0, size * 0.14);
            row.Children.Add(unitText);
        }
        Add(0, parts.Unit, false);
        if (parts.Sub is { } sub) Add(1, sub.Unit, true);
        return row;
    }

    /// <summary>A plain count, counting up: opens, days.</summary>
    public static TextBlock CountValue(long n, Brush? brush = null, double size = 44)
    {
        var value = Ui.Text("", size, 650, brush ?? Palette.TextBrush, -0.035, numeric: true);
        CountUp.Apply(value, n, v => Math.Round(v).ToString("N0", CultureInfo.GetCultureInfo("en-US")));
        return value;
    }

    /// <summary>One score card: a label, a duration, a line under it.</summary>
    public static Border StatCard(string label, long ms, string? sub, bool accent = false, int delay = 0)
    {
        var stack = new StackPanel();
        stack.Children.Add(StatLabel(label));
        var value = DurationValue(ms, accent ? Palette.AccentBrightBrush : null);
        value.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(value);
        if (sub is not null)
        {
            var s = Ui.Text(sub, 15, 400, Palette.TextMutedBrush, wrap: true);
            s.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(s);
        }
        var card = Ui.Card(stack, new Thickness(25.6), hover: true);
        Ui.Rise(card, delay);
        return card;
    }

    /// <summary>A figure card: label, a value, a line under it (Sync Status, app detail).</summary>
    public static Border FigureCard(string label, UIElement value, string? sub, int delay = 0)
    {
        var stack = new StackPanel();
        stack.Children.Add(StatLabel(label));
        if (value is FrameworkElement fe) fe.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(value);
        if (sub is not null)
        {
            var s = Ui.Text(sub, 15, 400, Palette.TextMutedBrush, wrap: true);
            s.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(s);
        }
        var card = Ui.Card(stack, new Thickness(25.6), hover: true);
        Ui.Rise(card, delay);
        return card;
    }

    /// <summary>
    /// The right-aligned callout in a card head: "HEAVIEST DAY  Jul 6" over the
    /// figure in the accent. Every chart card names its headline figure.
    /// </summary>
    public static StackPanel Callout(string label, string detail, string value)
    {
        var box = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, HorizontalAlignment = HorizontalAlignment.Right };
        var l = Ui.Caps(label, 12, 0.08);
        l.VerticalAlignment = VerticalAlignment.Bottom;
        head.Children.Add(l);
        head.Children.Add(Ui.Text(detail, 15, 500, Palette.TextMutedBrush));
        box.Children.Add(head);
        var v = Ui.Text(value, 21.6, 650, Palette.AccentBrightBrush, -0.02, numeric: true);
        v.HorizontalAlignment = HorizontalAlignment.Right;
        box.Children.Add(v);
        return box;
    }

    public static FitGrid Grid(double min, params UIElement[] children)
    {
        var grid = FitGrid.AutoFit(min, 18.4);
        foreach (var c in children) grid.Children.Add(c);
        return grid;
    }

    /// <summary>A red-bordered alert, for collection that is genuinely failing.</summary>
    public static Border Alert(string title, string body, string? detail = null)
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(title, 20.8, 600, Palette.WarnBrush));
        var b = Ui.Text(body, 15.5, 400, Palette.TextBrush, wrap: true);
        b.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(b);
        if (detail is not null)
        {
            var d = Ui.Paragraph(detail, 14.5, Palette.TextMutedBrush);
            d.Margin = new Thickness(0, 9, 0, 0);
            stack.Children.Add(d);
        }
        var alert = new Border
        {
            Padding = new Thickness(21, 18, 21, 18),
            CornerRadius = new CornerRadius(Ui.Radius),
            BorderBrush = Palette.WarnBrush,
            BorderThickness = new Thickness(1),
            Background = Palette.WarnDimBrush,
            Margin = new Thickness(0, 0, 0, 18.4),
            Child = stack,
        };
        Ui.Rise(alert);
        return alert;
    }

    /// <summary>The centred "nothing here" message, with an optional action.</summary>
    public static StackPanel Empty(string title, string body, UIElement? action = null, string? code = null)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(16, 64, 16, 64), MaxWidth = 620 };
        var h = Ui.Text(title, 35, 600, spacing: -0.02);
        h.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(h);
        var p = Ui.Text(body, 16, 400, Palette.TextMutedBrush, wrap: true);
        p.TextAlignment = TextAlignment.Center;
        p.Margin = new Thickness(0, 13, 0, 22);
        stack.Children.Add(p);
        if (code is not null)
        {
            var pre = new Border
            {
                Background = Palette.SurfaceBrush,
                BorderBrush = Palette.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Ui.RadiusSmall),
                Padding = new Thickness(18, 14, 18, 14),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 22),
                Child = Ui.Mono(code, 14),
            };
            stack.Children.Add(pre);
        }
        if (action is FrameworkElement a)
        {
            a.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(a);
        }
        return stack;
    }

    /// <summary>A short note under a chart, where a chart needs a second day to exist.</summary>
    public static TextBlock Note(string text)
    {
        var note = Ui.Text(text, 15, 400, Palette.TextMutedBrush, wrap: true);
        note.LineHeight = 23;
        return note;
    }

    /// <summary>A mono cell (dates in the run table, recorded paths).</summary>
    public static TextBlock MonoCell(string text, Brush? brush = null, double size = 14.5) => Ui.Mono(text, size, brush ?? Palette.TextBrush);
}
