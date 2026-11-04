using ScreenTime.App.Charts;
using ScreenTime.App.Controls;
using ScreenTime.App.Theme;
using ScreenTime.Core.Query;
using ScreenTime.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ScreenTime.App.Views;

/// <summary>
/// One app over all history: Total, Opens, Typical session, Longest; then time
/// and opens per day under one range, time and opens by hour, Merged from, and
/// Merged apps when the user merged any into this one.
/// </summary>
/// <remarks>
/// <para>Time and opens are separate cards, read together, so they share ONE
/// range: changing either menu moves both. A tall time bar over a short opens
/// bar is one long sitting, a short one over a tall one is checking. One chart
/// with two scales would invite comparing heights that mean different things.</para>
/// <para>A merged app's page goes to its target's: a bookmark made before the
/// merge still lands on the app it is now part of.</para>
/// </remarks>
public sealed class AppDetailPage(PageContext ctx, string key) : IPage
{
    private AppDetail? _app;
    private bool _exists;
    private string? _mergedInto;
    private int _range = Scope.DefaultChartDays;

    public Route Back { get; } = new(PageKind.Apps);

    public void Load()
    {
        var q = ctx.State.Queries!;
        _mergedInto = q.MergedInto(key);
        if (_mergedInto is not null) return;
        _app = q.AppDetailFor(key, Scope.All);
        _exists = _app is not null || q.AppExists(key);
    }

    public void Placeholder()
    {
        _app = Views.Placeholder.App(key);
        _exists = true;
    }

    public UIElement Build()
    {
        var page = new StackPanel();
        if (_mergedInto is { } into)
        {
            // After the build that is running now, not inside it.
            ctx.Window.DispatcherQueue.TryEnqueue(() => ctx.Navigate(new Route(PageKind.App, into)));
            return page;
        }
        var back = Ui.Link("← All apps", () => ctx.Navigate(Back));
        back.Margin = new Thickness(0, 0, 0, 10);

        // All history: no rows means no such app; there is no range to be outside of.
        if (!_exists || _app is null)
        {
            page.Children.Add(back);
            page.Children.Add(Parts.Empty("No such app", "Nothing in the history resolves to this app."));
            return page;
        }
        // Re-checked here, not just on the table's link: a page of one bar says nothing.
        if (!UsageQueries.EarnsDetailPage(_app.Ms))
        {
            page.Children.Add(back);
            page.Children.Add(Parts.Empty(_app.Name, $"Only {Format.Duration(_app.Ms)} recorded. A page here would be a single bar, so there is nothing to show over time yet."));
            return page;
        }

        var app = _app;
        var state = ctx.State;
        var mark = AppIconView.Create(app.Name, state.Colors, state.Icons, 30);
        // Rename, recolour and logo; no hide or merge, which belong to By App.
        var menu = AppActions.Menu(ctx, app.Key, app.Name, app.BaseName, alwaysVisible: true, size: 17);
        var head = Parts.PageHead(app.Name, $"{Format.Percent(app.Share)} of all active time{(app.System ? " · Windows component" : "")}", before: back, titleExtra: mark, titleAfter: menu);
        AppActions.AcceptLogoDrop(ctx, head, app.Name);
        page.Children.Add(head);

        var days = $"{app.Days} day{(app.Days == 1 ? "" : "s")}";
        var cards = Parts.Grid(215,
            Parts.FigureCard("Total", Parts.DurationValue(app.Ms, Palette.AccentBrightBrush, 34), $"across {days}"),
            Parts.FigureCard("Opens", Parts.CountValue(app.Opens, null, 34), "times brought to the foreground", 60),
            // Median, not mean: one four-hour sitting drags a mean somewhere no
            // actual visit ever was.
            Parts.FigureCard("Typical session", Parts.DurationValue(app.MedianMs, null, 34), "median, not mean", 120),
            Parts.FigureCard("Longest", Parts.DurationValue(app.LongestMs, null, 34), "single unbroken session", 180));
        cards.Margin = new Thickness(0, 0, 0, 18.4);
        page.Children.Add(cards);

        // ---- Time and opens per day, under one range counted back from the
        // machine's newest day. The range's peak sits in the subtitle, beside
        // the menu.
        var from = _range >= Scope.AllDays ? "" : Days.Add(app.LatestDate, -(_range - 1));
        bool In(DailyPoint d) => string.CompareOrdinal(d.Date, from) >= 0;
        var daily = app.Daily.Where(In).ToList();
        var opens = app.DailyOpens.Where(In).ToList();
        UIElement Menu(string label) => Parts.RangeBox(_range, d =>
        {
            _range = d;
            ctx.Redraw();
        }, label);
        UIElement None(int n) => Parts.Note(n == 1 ? "One day of data for this app in this range." : "Nothing recorded in this range.");

        var heaviest = Stack.PeakOf(daily, d => d.Ms ?? 0);
        page.Children.Add(Ui.Panel("Daily trend",
            $"Time in {app.Name} per day.{(heaviest is null ? "" : $" Heaviest day {Format.DayShort(heaviest.Date)}, {Format.Duration(heaviest.Ms ?? 0)}.")}",
            Menu("Daily trend range"),
            daily.Count > 1 ? BarChart.Daily(daily, Measure.Time) : None(daily.Count)));

        var most = Stack.PeakOf(opens, d => d.Ms ?? 0);
        page.Children.Add(Ui.Panel("Opens per day",
            $"How many times {app.Name} was opened each day.{(most is null ? "" : $" Most {Format.DayShort(most.Date)}, {Format.Opens(most.Ms ?? 0)}.")}",
            Menu("Opens per day range"),
            opens.Count > 1 ? BarChart.Daily(opens, Measure.Count) : None(opens.Count)));

        page.Children.Add(OverviewPage.HourPanel(app.Hourly, Measure.Time, "Shape of the day", "When this app is usually open."));
        page.Children.Add(OverviewPage.HourPanel(app.HourlyOpens, Measure.Count, "When it gets opened", "The hour an open began, not the hours it went on for."));

        // So a merge is never silent: a versioned WindowsApps folder folds
        // into one app, and the day a merge is wrong, this is where it shows.
        var ids = new DataTable([new("Recorded identity"), new("Time")]) { RowPadding = 10 };
        foreach (var id in app.Identities)
        {
            // Trimmed when the panel is narrower than the path; the tip has it whole.
            var path = Parts.MonoCell(id.Path, Palette.TextMutedBrush, 13);
            Ui.SetTip(path, id.Path);
            ids.AddRow([path, DataTable.Cell(Format.Duration(id.Ms))]);
        }
        var merged = new StackPanel();
        merged.Children.Add(ids.Build());
        if (app.Identities.Count == 1)
        {
            var one = Parts.Note("One identity, so nothing was merged here.");
            one.Margin = new Thickness(0, 14, 0, 0);
            merged.Children.Add(one);
        }
        page.Children.Add(Ui.Panel("Merged from", "Everything that resolved into this entry, so a merge is never silent.", null, merged));

        if (app.Members.Count > 0)
        {
            var members = new DataTable([new("App", Left: true), new("Time"), new("", Width: 52)]) { RowPadding = 10 };
            foreach (var m in app.Members)
                members.AddRow([
                    DataTable.Cell(m.Name, numeric: false),
                    DataTable.Cell(Format.Duration(m.Ms)),
                    AppActions.Separate(ctx, m.Key, m.Name),
                ]);
            page.Children.Add(Ui.Panel("Merged apps", "Apps you merged into this one. Their time and opens are counted here, on every page.", null, members.Build()));
        }
        return page;
    }
}
