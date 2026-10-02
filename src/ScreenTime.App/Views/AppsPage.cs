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
/// By App: Top apps and Most opened, then every app. The table opens on the
/// apps that clear <see cref="AppList"/> and folds the tail behind "Show all",
/// saying how many it folded and how much time they hold - so the table still
/// visibly sums to the Overview's total.
/// </summary>
/// <remarks>
/// System components are listed, not hidden, but marked: hiding them would
/// make the rows stop adding up, and File Explorer genuinely IS foreground
/// time - you were looking at a folder.
/// </remarks>
public sealed class AppsPage(PageContext ctx) : IPage
{
    private ByAppData? _data;
    private string _sort = "time";
    private bool _asc;
    private bool _expanded;

    public void Load() => _data = ctx.State.Queries!.ByApp(ctx.State.Scope);

    public void Placeholder() => _data = Views.Placeholder.ByApp();

    public UIElement Build()
    {
        var data = _data!;
        var page = new StackPanel();
        var days = $"{data.DaysWithData} day{(data.DaysWithData == 1 ? "" : "s")}";
        page.Children.Add(Parts.PageHead("By App", $"{data.Apps.Count} app{(data.Apps.Count == 1 ? "" : "s")} across {days}"));

        if (data.Apps.Count == 0)
        {
            page.Children.Add(Parts.Empty("Nothing in this range", "No foreground activity recorded in the selected range."));
            return page;
        }

        void Open(AppRow a) => ctx.Navigate(new Route(PageKind.App, a.Key));
        page.Children.Add(Ui.Panel("Top apps", "Grouped by resolved app, not by executable path. Hover a bar for time, share and opens.", null,
            new TopAppsChart(data.Apps.Take(8).ToList(), Measure.Time, ctx.State.Colors, open: Open)));
        // Its own sort, not a slice of the list above: the top eight by time
        // are not the top eight by opens, which is the reason for a second chart.
        var opened = data.Apps.Where(a => a.Opens > 0).OrderByDescending(a => a.Opens).ThenByDescending(a => a.Ms).Take(8).ToList();
        if (opened.Count > 0)
            page.Children.Add(Ui.Panel("Most opened", "Ranked by how often you switched to it. Hover a bar for time and share.", null,
                new TopAppsChart(opened, Measure.Count, ctx.State.Colors, open: Open)));

        var listed = data.Apps.Count(a => a.Listed);
        var folded = data.Apps.Count - listed;
        page.Children.Add(Ui.Panel(folded > 0 && !_expanded ? $"Apps · {listed} of {data.Apps.Count}" : "All apps",
            (folded > 0 ? AppList.Rule + " " : "") + "Every row together sums to the active total: the sampler records exactly one foreground app at a time, so the rows partition the time rather than overlapping it.",
            null, Table(data)));
        return page;
    }

    private StackPanel Table(ByAppData data)
    {
        var state = ctx.State;
        var visible = _expanded ? data.Apps.ToList() : data.Apps.Where(a => a.Listed).ToList();
        IEnumerable<AppRow> ordered = _sort switch
        {
            "name" => visible.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            "opens" => visible.OrderBy(a => a.Opens),
            "days" => visible.OrderBy(a => a.Days),
            _ => visible.OrderBy(a => a.Ms),
        };
        if (!_asc) ordered = ordered.Reverse();
        var rows = ordered.ToList();
        var max = Math.Max(1, data.Apps.Max(a => a.Ms));

        Column Sortable(string header, string key, bool left = false, double? width = null) =>
            new(header, Width: width, Left: left, OnSort: () =>
            {
                if (_sort == key) _asc = !_asc;
                else
                {
                    _sort = key;
                    _asc = key == "name";
                }
                ctx.Redraw();
            }, SortMark: _sort == key ? (_asc ? "↑" : "↓") : null);

        var table = new DataTable([
            Sortable("App", "name", left: true),
            Sortable("Time", "time"),
            Sortable("Opens", "opens"),
            Sortable("Days", "days"),
            new("Share", Width: 250, Left: true),
        ]) { RowPadding = 12 };

        foreach (var a in rows)
        {
            var share = new Grid { ColumnSpacing = 11, Tag = "stretch", HorizontalAlignment = HorizontalAlignment.Stretch };
            share.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            share.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            share.Children.Add(Ui.ShareBar((double)a.Ms / max, new SolidColorBrush(Palette.App(state.Colors, a.Name))));
            var pct = Ui.Text(Format.Percent(a.Share), 15, 400, Palette.TextMutedBrush, numeric: true);
            pct.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(pct, 1);
            share.Children.Add(pct);
            table.AddRow([
                AppActions.NameCell(ctx, a.Key, a.Name, a.BaseName, a.System, a.Detailed),
                DataTable.Cell(Format.Duration(a.Ms), weight: 600),
                DataTable.Cell(Format.Count(a.Opens), Palette.TextMutedBrush),
                DataTable.Cell(Format.Count(a.Days), Palette.TextFaintBrush),
                share,
            ]);
        }

        var body = new StackPanel();
        body.Children.Add(table.Build());
        var hidden = data.Apps.Where(a => !a.Listed).ToList();
        if (hidden.Count > 0)
        {
            var more = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 18, 0, 0) };
            var toggle = Ui.Chip(_expanded ? "Show fewer" : $"Show all {data.Apps.Count} apps", false);
            toggle.Click += (_, _) =>
            {
                _expanded = !_expanded;
                ctx.Redraw();
            };
            more.Children.Add(toggle);
            if (!_expanded)
            {
                var note = Ui.Text($"{hidden.Count} more, {Format.Duration(hidden.Sum(a => a.Ms))} between them", 15, 400, Palette.TextMutedBrush);
                note.VerticalAlignment = VerticalAlignment.Center;
                more.Children.Add(note);
            }
            body.Children.Add(more);
        }
        return body;
    }
}
