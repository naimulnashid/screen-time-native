using ScreenTime.App.Charts;
using ScreenTime.App.Controls;
using ScreenTime.App.Theme;
using ScreenTime.Core.Query;
using ScreenTime.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ScreenTime.App.Views;

/// <summary>
/// One app over time: Total, Opens, Typical session, Longest; then time and
/// opens per day, time and opens by hour, and Merged from.
/// </summary>
/// <remarks>
/// <para>Time and opens are separate cards, read together: a tall time bar
/// over a short opens bar is one long sitting, a short one over a tall one is
/// checking. One chart with two scales would invite comparing heights that
/// mean different things.</para>
/// <para>"No such app" is a different answer from "nothing in this range": an
/// app with no time in the current range still exists, and saying otherwise
/// sends the reader hunting for a page that is right there.</para>
/// </remarks>
public sealed class AppDetailPage(PageContext ctx, string key) : IPage
{
    private AppDetail? _app;
    private bool _exists;

    public void Load()
    {
        var q = ctx.State.Queries!;
        _app = q.AppDetailFor(key, ctx.State.Scope);
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
        var back = Ui.Link("← All apps", () => ctx.Navigate(new Route(PageKind.Apps)));
        back.Margin = new Thickness(0, 0, 0, 10);

        if (!_exists)
        {
            page.Children.Add(back);
            page.Children.Add(Parts.Empty("No such app", "Nothing in the history resolves to this app."));
            return page;
        }
        if (_app is null)
        {
            page.Children.Add(back);
            var all = Ui.Chip("Show everything", false);
            all.Click += (_, _) => ctx.State.SetScope(Scope.All);
            page.Children.Add(Parts.Empty("Nothing in this range", "This app has recorded time, but none inside the selected range.", all));
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
        var pencil = AppActions.Pencil(ctx, app.Key, app.Name, app.BaseName, 17, alwaysVisible: true);
        pencil.VerticalAlignment = VerticalAlignment.Center;
        var head = Parts.PageHead(app.Name, $"{Format.Percent(app.Share)} of active time in this range{(app.System ? " · Windows component" : "")}", before: back, titleExtra: mark);
        ((StackPanel)head.Children[1]).Children.Add(pencil);
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

        var heaviest = app.Daily.Count > 1 ? Stack.PeakOf(app.Daily, d => d.Ms ?? 0) : null;
        page.Children.Add(Ui.Panel("Daily trend", $"Time in {app.Name} per day.",
            heaviest is null ? null : Parts.Callout("Heaviest day", Format.DayShort(heaviest.Date), Format.Duration(heaviest.Ms ?? 0)),
            app.Daily.Count > 1 ? BarChart.Daily(app.Daily, Measure.Time) : Parts.Note("Only one day of data for this app so far.")));

        var mostOpens = app.DailyOpens.Count > 1 ? Stack.PeakOf(app.DailyOpens, d => d.Ms ?? 0) : null;
        page.Children.Add(Ui.Panel("Opens per day", $"How many times {app.Name} was opened each day.",
            mostOpens is null ? null : Parts.Callout("Most opens", Format.DayShort(mostOpens.Date), Format.Opens(mostOpens.Ms ?? 0)),
            app.DailyOpens.Count > 1 ? BarChart.Daily(app.DailyOpens, Measure.Count) : Parts.Note("Only one day of data for this app so far.")));

        page.Children.Add(OverviewPage.HourPanel(app.Hourly, Measure.Time, "Shape of the day", "When this app is usually open."));
        page.Children.Add(OverviewPage.HourPanel(app.HourlyOpens, Measure.Count, "When it gets opened", "The hour an open began, not the hours it went on for."));

        // So a merge is never silent: a versioned WindowsApps folder folds
        // into one app, and the day a merge is wrong, this is where it shows.
        var ids = new DataTable([new("Recorded identity"), new("Time")]) { RowPadding = 10 };
        foreach (var id in app.Identities) ids.AddRow([Parts.MonoCell(id.Path, Palette.TextMutedBrush, 13), DataTable.Cell(Format.Duration(id.Ms))]);
        var merged = new StackPanel();
        merged.Children.Add(ids.Build());
        if (app.Identities.Count == 1)
        {
            var one = Parts.Note("One identity, so nothing was merged here.");
            one.Margin = new Thickness(0, 14, 0, 0);
            merged.Children.Add(one);
        }
        page.Children.Add(Ui.Panel("Merged from", "Everything that resolved into this entry, so a merge is never silent.", null, merged));
        return page;
    }
}
