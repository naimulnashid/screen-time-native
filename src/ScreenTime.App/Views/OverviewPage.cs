using ScreenTime.App.Charts;
using ScreenTime.App.Controls;
using ScreenTime.App.Theme;
using ScreenTime.Core.Query;
using ScreenTime.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ScreenTime.App.Views;

/// <summary>
/// The laptop's overview, section for section the web dashboard's: three
/// score cards, Daily trend, Activity, Top apps by day, Most opened by day,
/// Shape of the day, and "Where the time went" LAST - it answers whether the
/// numbers can be believed, not how the day was spent.
/// </summary>
public sealed class OverviewPage(PageContext ctx) : IPage
{
    private OverviewData? _data;
    private List<(string Date, long Total)> _heat = [];
    private long _rows;

    public void Load()
    {
        var q = ctx.State.Queries!;
        _data = q.Overview(ctx.State.Scope);
        _heat = q.HeatmapDays();
        _rows = _data.LatestDate is null ? q.RowCount() : 1;
    }

    public void Placeholder()
    {
        _data = Views.Placeholder.Overview();
        _heat = Views.Placeholder.Heat();
        _rows = 1;
    }

    public UIElement Build()
    {
        var data = _data!;
        var state = ctx.State;
        var page = new StackPanel();

        if (data.LatestDate is null)
        {
            if (_rows == 0) return Shell.NoData(ctx);
            var all = Ui.Chip("Show everything", false);
            all.Click += (_, _) => state.SetScope(Scope.All);
            page.Children.Add(Parts.Empty("Nothing in this range", "There is history stored, but none in the selected range. Try a longer one.", all));
            return page;
        }

        // ---- Head: which day the numbers describe, and how long ago they
        // last moved - so "quiet day" and "the sampler died" never look alike.
        var sub = $"Latest data {Format.DayLong(data.LatestDate)}";
        if (state.Sync?.HoursSinceSuccess is { } h) sub += $" · collected {Format.Relative(h)}";
        var stopped = !state.Sampler.Alive && state.Sampler.Updated is not null;
        page.Children.Add(Parts.PageHead(state.Device.Label, sub, after: stopped ? Ui.Badge("not recording", Ui.BadgeKind.Bad) : null));

        // ---- Score cards. Averaged over days WITH DATA, never the range
        // length: dividing by 30 when the sampler has run for two of them
        // reports a fifteenth of the truth.
        var isToday = data.LatestDate == Core.Data.Time.LocalToday();
        var days = $"{data.DaysWithData} day{(data.DaysWithData == 1 ? "" : "s")}";
        var cards = Parts.Grid(300,
            Parts.StatCard(isToday ? "Today" : "Latest day", data.Latest, isToday ? "so far" : Format.DayShort(data.LatestDate), accent: true),
            Parts.StatCard("Daily average", data.DailyAverage, $"over {days} with data", delay: 60),
            Parts.StatCard("Range total", data.RangeTotal, $"{data.AppCount} app{(data.AppCount == 1 ? "" : "s")}", delay: 120));
        cards.Margin = new Thickness(0, 0, 0, 18.4);
        page.Children.Add(cards);

        // ---- Daily trend
        var recorded = data.Daily.Where(d => d.Ms is not null).ToList();
        var heaviest = Stack.PeakOf(recorded, d => d.Ms ?? 0);
        page.Children.Add(Ui.Panel("Daily trend", $"Active time per day across {days} with data. Asleep time is deliberately not drawn.",
            heaviest is null ? null : Parts.Callout("Heaviest day", Format.DayShort(heaviest.Date), Format.Duration(heaviest.Ms ?? 0)),
            data.Daily.Count > 1 ? new TrendChart(data.Daily) : Parts.Note("One day of data so far. The trend appears once the sampler has run across more than one day.")));

        // ---- Activity
        page.Children.Add(Ui.Panel("Activity",
            "Active time per day, whatever the range above. Outlined days were never recorded - before the sampler existed, or while it was not running - which is not the same as a quiet day.",
            null, ActivityBody()));

        // ---- The two stacks, each ranked by its own measure.
        page.Children.Add(StackCard(data.TimeByApp, Measure.Time, "Top apps by day", "Active time per day, top 8 apps stacked; everything else grouped as Other.", "Top app"));
        page.Children.Add(StackCard(data.OpensByApp, Measure.Count, "Most opened by day", "Opens per day, top 8 apps stacked; everything else grouped as Other.", "Most opened"));

        // ---- Shape of the day
        page.Children.Add(HourPanel(data.Hourly, Measure.Time, "Shape of the day", "Active time by hour of day, summed across the range."));

        // ---- Where the time went
        page.Children.Add(Ui.Panel("Where the time went", "Every millisecond the sampler accounted for, and how.", null, KindsBody(data.Kinds)));
        return page;
    }

    private Border StackCard(StackData stack, Measure measure, string title, string sub, string calloutLabel)
    {
        var top = stack.Series.Select((s, i) => (s, i)).FirstOrDefault(x => x.s.Id is not null);
        var callout = top.s is null ? null : Parts.Callout(calloutLabel, stack.Names[top.i], Axis.Value(top.s.Total, measure));
        if (stack.Points.Count < 2) return Ui.Panel(title, sub, callout, Parts.Note("One day of data so far."));
        var body = new Microsoft.UI.Xaml.Controls.StackPanel();
        body.Children.Add(new StackedAreaChart(stack, measure, ctx.State.Colors));
        body.Children.Add(AppIconView.Legend(stack.Names, ctx.State.Colors, ctx.State.Icons));
        return Ui.Panel(title, sub, callout, body);
    }

    private Microsoft.UI.Xaml.Controls.StackPanel ActivityBody()
    {
        var block = Heatmap.Recent(_heat);
        var body = new Microsoft.UI.Xaml.Controls.StackPanel();
        body.Children.Add(new HeatmapView(block, block.Peak));
        // Always offered: a control that appears only once the history is long
        // enough is one nobody finds.
        var expand = Ui.Chip("Expand", false, 14);
        expand.Click += (_, _) => ctx.Navigate(new Route(PageKind.Activity));
        body.Children.Add(HeatmapView.Legend(block.Total, block.ActiveDays, "in the last 6 months", expand));
        return body;
    }

    /// <summary>Hour of day, with the busiest hour as the head's callout. Shared with the app page.</summary>
    public static Border HourPanel(IReadOnlyList<HourPoint> hours, Measure measure, string title, string sub)
    {
        var busiest = Stack.PeakOf(hours, x => x.Value);
        var callout = busiest is null ? null : Parts.Callout("Busiest hour", Format.HourOfDay(busiest.Hour), Axis.Value(busiest.Value, measure));
        return Ui.Panel(title, sub, callout, BarChart.Hourly(hours, measure));
    }

    private static readonly (string Label, string Hint)[] KindText =
    [
        ("Active", "An application was in the foreground."),
        ("Locked", "The lock screen was in front. Positively detected, not inferred."),
        ("Unattributed", "No foreground window, and no positive evidence of a lock. Not guessed at."),
        ("Asleep", "The machine slept, or the sampler was not running. The absence of a measurement, not a quantity."),
    ];

    /// <summary>
    /// The four kinds as one bar and a legend. Unattributed is shown, never
    /// hidden: it is time the sampler saw no foreground window and declined
    /// to guess, and a growing share means it is not seeing the desktop.
    /// </summary>
    private static Microsoft.UI.Xaml.Controls.StackPanel KindsBody(KindTotals k)
    {
        long[] values = [k.Active, k.Locked, k.Unknown, k.Gap];
        Brush[] fills = [Palette.AccentBrush, Palette.LockedBrush, Palette.UnknownBrush, Palette.AsleepBrush];
        var total = Math.Max(1, values.Sum());

        var body = new Microsoft.UI.Xaml.Controls.StackPanel();
        body.Children.Add(TwoPartBar(values.Select((v, i) => ((double)v / total, fills[i])).ToList()));

        var legend = new WrapPanel { HorizontalSpacing = 26, VerticalSpacing = 10, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        for (var i = 0; i < 4; i++)
        {
            var item = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            item.Children.Add(new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(3), Background = fills[i], BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(Ui.Text(KindText[i].Label, 15, 400, Palette.TextMutedBrush));
            item.Children.Add(Ui.Text(Format.Duration(values[i]), 15, 500, numeric: true));
            Ui.SetTip(item, KindText[i].Hint);
            legend.Children.Add(item);
        }
        body.Children.Add(legend);

        if (k.UnknownShare >= 10)
        {
            var warn = Ui.Text($"{Format.Percent(k.UnknownShare)} of tracked time is unattributed - the sampler saw no foreground window and declined to guess. A large or growing share means it is not seeing the desktop properly, not that the machine was idle.", 15, 400, Palette.WarnBrush, wrap: true);
            warn.Margin = new Thickness(0, 16, 0, 0);
            body.Children.Add(warn);
        }
        return body;
    }

    /// <summary>A 16px rounded bar split into parts, each growing in from the left.</summary>
    public static Grid TwoPartBar(IReadOnlyList<(double Fraction, Brush Fill)> parts)
    {
        var track = new Grid { Height = 16, CornerRadius = new CornerRadius(8), Background = Palette.InsetBrush };
        var row = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Orientation.Horizontal };
        track.Children.Add(row);
        var fills = parts.Select(p => new Border { Background = p.Fill }).ToList();
        foreach (var f in fills) row.Children.Add(f);
        track.SizeChanged += (_, e) =>
        {
            for (var i = 0; i < parts.Count; i++) fills[i].Width = Math.Max(0, parts[i].Fraction) * e.NewSize.Width;
        };
        for (var i = 0; i < fills.Count; i++) Ui.GrowX(fills[i], 800, i * 120);
        return track;
    }
}
