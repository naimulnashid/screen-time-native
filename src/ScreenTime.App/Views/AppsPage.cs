using ScreenTime.App.Charts;
using ScreenTime.App.Controls;
using ScreenTime.App.Theme;
using ScreenTime.Core.Query;
using ScreenTime.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace ScreenTime.App.Views;

/// <summary>
/// By App, over all history: Top apps and Most opened, then every app as a
/// table or a grid of cards. The list opens on the apps that clear
/// <see cref="AppList"/> and folds the tail behind "Show all", saying how many
/// it folded and how much time they hold - so the list still visibly sums to
/// the Overview's total.
/// </summary>
/// <remarks>
/// <para>Apps the user hid (Naming/AppPrefs.cs) leave the list and both
/// rankings and nothing else; "Show N hidden" lists them again to bring one
/// back. Neither view shows a share: the table's Share column went in 0.2.0,
/// as on the web dashboard, and the rankings show the large shares.</para>
/// <para>System components are listed, not hidden, but marked: hiding them
/// would make the rows stop adding up, and File Explorer genuinely IS
/// foreground time - you were looking at a folder.</para>
/// </remarks>
public sealed class AppsPage(PageContext ctx) : IPage
{
    private ByAppData? _data;
    private string _sort = "time";
    private bool _asc;
    private bool _expanded;
    private bool _showHidden;

    public void Load() => _data = ctx.State.Queries!.ByApp(Scope.All);

    public void Placeholder() => _data = Views.Placeholder.ByApp();

    public UIElement Build()
    {
        var data = _data!;
        var page = new StackPanel();
        var days = $"{data.DaysWithData} day{(data.DaysWithData == 1 ? "" : "s")}";
        page.Children.Add(Parts.PageHead("By App", $"{data.Apps.Count} app{(data.Apps.Count == 1 ? "" : "s")} across {days}"));

        if (data.Apps.Count == 0) return Shell.NoData(ctx);

        void Open(AppRow a) => ctx.Navigate(new Route(PageKind.App, a.Key));
        var shown = data.Apps.Where(a => !a.Hidden).ToList();
        page.Children.Add(Ui.Panel("Top apps", "Grouped by resolved app, not by executable path. Hover a bar for time, share and opens.", null,
            new TopAppsChart(shown.Take(8).ToList(), Measure.Time, ctx.State.Colors, open: Open)));
        // Its own sort, not a slice of the list above: the top eight by time
        // are not the top eight by opens, which is the reason for a second chart.
        var opened = shown.Where(a => a.Opens > 0).OrderByDescending(a => a.Opens).ThenByDescending(a => a.Ms).Take(8).ToList();
        if (opened.Count > 0)
            page.Children.Add(Ui.Panel("Most opened", "Ranked by how often you switched to it. Hover a bar for time and share.", null,
                new TopAppsChart(opened, Measure.Count, ctx.State.Colors, open: Open)));

        var folded = shown.Any(a => !a.Listed);
        page.Children.Add(Ui.Panel("Apps",
            (folded ? AppList.Rule + " " : "") + "Every app together sums to the active total: the sampler records exactly one foreground app at a time, so the apps partition the time rather than overlapping it.",
            ViewToggle(), List(data)));
        return page;
    }

    /// <summary>Table or Grid, remembered with the app's preferences.</summary>
    private StackPanel ViewToggle()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (view, label, glyph) in new[] { ("table", "Table", ""), ("grid", "Grid", "") })
        {
            var active = ctx.State.AppsView == view;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13, Foreground = active ? Palette.OnAccentFillBrush : Palette.TextMutedBrush, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(Ui.Text(label, 14, 500, active ? Palette.OnAccentFillBrush : Palette.TextMutedBrush, selectable: false));
            var chip = Ui.Chip("", active, 14);
            chip.Content = content;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"Show apps as a {view}");
            chip.Click += (_, _) =>
            {
                if (ctx.State.AppsView == view) return;
                ctx.State.AppsView = view;
                ctx.Redraw();
            };
            row.Children.Add(chip);
        }
        return row;
    }

    private StackPanel List(ByAppData data)
    {
        var listed = _showHidden ? data.Apps.ToList() : data.Apps.Where(a => !a.Hidden).ToList();
        var visible = _expanded ? listed : listed.Where(a => a.Listed).ToList();
        IEnumerable<AppRow> ordered = _sort switch
        {
            "name" => visible.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            "opens" => visible.OrderBy(a => a.Opens),
            "days" => visible.OrderBy(a => a.Days),
            _ => visible.OrderBy(a => a.Ms),
        };
        if (!_asc) ordered = ordered.Reverse();
        var rows = ordered.ToList();
        // What an app may be merged into: every other app, as shown.
        var candidates = data.Apps.Select(a => (a.Key, a.Name)).ToList();

        var body = new StackPanel();
        body.Children.Add(ctx.State.AppsView == "grid" ? Grid(rows, candidates) : Table(rows, candidates));

        var hidden = data.Apps.Count(a => a.Hidden);
        var rest = listed.Where(a => !a.Listed).ToList();
        if (rest.Count == 0 && hidden == 0) return body;
        var more = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 18, 0, 0) };
        if (rest.Count > 0)
        {
            var toggle = Ui.Chip(_expanded ? "Show fewer apps" : $"Show all {listed.Count} apps", false);
            toggle.Click += (_, _) =>
            {
                _expanded = !_expanded;
                ctx.Redraw();
            };
            more.Children.Add(toggle);
        }
        if (hidden > 0)
        {
            var show = Ui.Chip(_showHidden ? $"Leave out the {hidden} hidden" : $"Show {hidden} hidden", false);
            show.Click += (_, _) =>
            {
                _showHidden = !_showHidden;
                ctx.Redraw();
            };
            more.Children.Add(show);
        }
        if (!_expanded && rest.Count > 0)
        {
            var note = Ui.Text($"{rest.Count} more, {Format.Duration(rest.Sum(a => a.Ms))} between them", 15, 400, Palette.TextMutedBrush);
            note.VerticalAlignment = VerticalAlignment.Center;
            more.Children.Add(note);
        }
        body.Children.Add(more);
        return body;
    }

    private UIElement Table(List<AppRow> rows, IReadOnlyList<(string Key, string Name)> candidates)
    {
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
        ]) { RowPadding = 12 };

        foreach (var a in rows)
        {
            var name = AppActions.NameCell(ctx, a.Key, a.Name, a.BaseName, a.System, a.Detailed, hidden: a.Hidden, candidates: candidates);
            if (a.Hidden) name.Opacity = 0.6;
            table.AddRow([
                name,
                DataTable.Cell(Format.Duration(a.Ms), weight: 600),
                DataTable.Cell(Format.Count(a.Opens), Palette.TextMutedBrush),
                DataTable.Cell(Format.Count(a.Days), Palette.TextFaintBrush),
            ]);
        }
        return table.Build();
    }

    /// <summary>
    /// Each app a card: its logo large, then the figures. The whole card opens
    /// the detail page when the app has one; its own buttons are left alone.
    /// </summary>
    private FitGrid Grid(List<AppRow> rows, IReadOnlyList<(string Key, string Name)> candidates)
    {
        var state = ctx.State;
        var cards = rows.Select((a, i) =>
        {
            var stack = new StackPanel();
            var top = new Grid { Margin = new Thickness(0, 0, 0, 13) };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var mark = AppIconView.Create(a.Name, state.Colors, state.Icons, 52);
            mark.HorizontalAlignment = HorizontalAlignment.Left;
            top.Children.Add(mark);
            var menu = AppActions.Menu(ctx, a.Key, a.Name, a.BaseName, a.Hidden, candidates);
            menu.VerticalAlignment = VerticalAlignment.Top;
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(menu, 1);
            top.Children.Add(menu);
            stack.Children.Add(top);

            var title = Ui.Text(a.Name, 16.5, 600, selectable: false);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            Ui.SetTip(title, a.Detailed ? $"{a.Name} - open detail" : $"{a.Name} - under a minute recorded, too little for a detail page");
            stack.Children.Add(title);
            if (a.System || a.Hidden)
            {
                var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
                if (a.System) badges.Children.Add(Ui.Badge("system", size: 11.2, padding: new Thickness(7, 1.5, 7, 2)));
                if (a.Hidden) badges.Children.Add(Ui.Badge("hidden", size: 11.2, padding: new Thickness(7, 1.5, 7, 2)));
                stack.Children.Add(badges);
            }
            var total = Ui.Text(Format.Duration(a.Ms), 25, 650, Palette.TextBrush, -0.02, numeric: true);
            total.Margin = new Thickness(0, 11, 0, 0);
            stack.Children.Add(total);
            var opens = Ui.Text(Format.Opens(a.Opens), 14, 400, Palette.TextMutedBrush, numeric: true);
            opens.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(opens);
            stack.Children.Add(Ui.Text($"{a.Days} active {(a.Days == 1 ? "day" : "days")}", 13, 400, Palette.TextFaintBrush));

            var card = new Border
            {
                Background = Palette.InsetBrush,
                BorderBrush = Palette.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Ui.RadiusSmall),
                Padding = new Thickness(18, 17, 18, 16),
                Child = stack,
                Opacity = a.Hidden ? 0.6 : 1,
            };
            Ui.HoverLift(card, 3);
            card.PointerEntered += (_, _) =>
            {
                card.Background = Palette.SurfaceBrush;
                if (a.Detailed) card.BorderBrush = Palette.AccentBorderBrush;
                menu.Opacity = 1;
                if (a.Hidden) card.Opacity = 1;
            };
            card.PointerExited += (_, _) =>
            {
                card.Background = Palette.InsetBrush;
                card.BorderBrush = Palette.BorderBrush;
                if (menu.FocusState == FocusState.Unfocused) menu.Opacity = 0;
                if (a.Hidden) card.Opacity = 0.6;
            };
            if (a.Detailed)
            {
                card.Tapped += (_, e) =>
                {
                    if (FromButton(e.OriginalSource as DependencyObject, card)) return;
                    ctx.Navigate(new Route(PageKind.App, a.Key));
                };
            }
            AppActions.AcceptLogoDrop(ctx, card, a.Name);
            Ui.Rise(card, Math.Min(i * 22, 360));
            return (UIElement)card;
        }).ToArray();
        return Parts.Grid(205, cards);
    }

    /// <summary>Whether a tap began on one of the card's own buttons.</summary>
    private static bool FromButton(DependencyObject? from, DependencyObject card)
    {
        for (var d = from; d is not null && d != card; d = VisualTreeHelper.GetParent(d))
            if (d is ButtonBase) return true;
        return false;
    }
}
