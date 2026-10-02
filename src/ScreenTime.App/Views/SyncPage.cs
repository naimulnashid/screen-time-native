using ScreenTime.App.Controls;
using ScreenTime.App.Theme;
using ScreenTime.Core;
using ScreenTime.Core.Collect;
using ScreenTime.Core.Query;
using ScreenTime.Core.Sample;
using ScreenTime.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ScreenTime.App.Views;

/// <summary>
/// Sync Status: is the sampler recording right now, and is what it records
/// reaching the database? The first question matters more here than on any
/// sibling: nothing backfills foreground time, so a minute the sampler is
/// down is lost for good.
/// </summary>
/// <remarks>
/// "Running" is read from the sampler's HEARTBEAT, never from the task state:
/// the heartbeat is what proves samples are being taken.
/// </remarks>
public sealed class SyncPage(PageContext ctx) : IPage
{
    private const int PageSize = 25;
    private SyncData? _data;
    private SamplerStatus _sampler = SamplerStatus.Dead;
    private TaskInfo? _task;
    private string? _currentApp;
    private int _page = 1;

    public void Placeholder()
    {
        _data = Views.Placeholder.Sync();
        _sampler = Views.Placeholder.Sampler();
        _task = Views.Placeholder.Task();
        _currentApp = "VS Code";
    }

    public void Load()
    {
        var q = ctx.State.Queries!;
        var first = q.Sync(PageSize, 0);
        var pages = Math.Max(1, (int)Math.Ceiling(first.TotalRuns / (double)PageSize));
        _page = Math.Clamp(_page, 1, pages);
        _data = _page == 1 ? first : q.Sync(PageSize, (_page - 1) * PageSize);
        _sampler = SamplerStatus.Read(AppPaths.SamplerDir);
        _currentApp = _sampler.InFlight is { Kind: "app" } f ? q.DisplayName(f.App) : null;
        try
        {
            _task = ScheduledTasks.Query(ScheduledTasks.SamplerTask);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            _task = null;
        }
    }

    public UIElement Build()
    {
        var data = _data!;
        var page = new StackPanel();
        page.Children.Add(Parts.PageHead("Sync Status", "Whether the sampler is recording, and whether what it records is reaching the database."));

        var missing = _task is { Exists: false };
        var broken = data.ConsecutiveFailures >= 2;
        if (missing)
        {
            page.Children.Add(Parts.Alert("The sampler's task is not registered",
                "Nothing is being recorded. The task lives in the Windows task store, which a reset wipes; installing registers it again.",
                "Run `tools\\Install.ps1` from the source folder, or reinstall Screen Time. It needs no administrator approval."));
        }
        else if (!_sampler.Alive)
        {
            var start = Ui.Button("Start the sampler", primary: true, fontSize: 14, padding: new Thickness(16, 7, 16, 7));
            start.Margin = new Thickness(0, 12, 0, 0);
            start.Click += async (_, _) =>
            {
                start.IsEnabled = false;
                try { await Task.Run(() => ScheduledTasks.Run(ScheduledTasks.SamplerTask)); }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
                {
                    await Shell.ShowMessage(ctx.Window, "Could not start the sampler", ex.Message);
                }
                await Task.Delay(3000);
                ctx.Redraw();
            };
            var alert = Parts.Alert("The sampler is not running",
                $"No heartbeat {(_sampler.Updated is { } u ? "since " + Format.DateTimeLocal(u) : "yet")}. It records only while it runs and nothing backfills, so time passing now is time lost.",
                $"It starts at every sign-in. Its log is in `{AppPaths.LogsDir}`.");
            ((StackPanel)alert.Child).Children.Add(start);
            page.Children.Add(alert);
        }
        else if (broken)
        {
            page.Children.Add(Parts.Alert("Saving is failing",
                $"The last {data.ConsecutiveFailures} ingests failed. The sampler keeps its spans on disk until one succeeds, so nothing is lost yet.",
                $"Check the log in `{AppPaths.LogsDir}`, then use Sync now to try again."));
        }

        var cards = Parts.Grid(300, SamplerCard(), SavedCard(data), StoredCard(data));
        cards.Margin = new Thickness(0, 0, 0, 18.4);
        page.Children.Add(cards);

        page.Children.Add(RunsPanel(data));
        page.Children.Add(TaskPanel());
        return page;
    }

    private Border SamplerCard()
    {
        var value = Ui.Text(_sampler.Alive ? "Running" : "Not running", 30.4, 650, _sampler.Alive ? Palette.GoodBrush : Palette.WarnBrush, -0.035);
        string sub;
        if (_sampler.Alive && _sampler.InFlight is { } f)
            sub = $"Last tick {_sampler.StaleSeconds}s ago. Currently {(_currentApp is not null ? "in " + _currentApp : f.Kind)} for {Format.Duration(f.Ms)} - written when the foreground changes, so it is not in the totals yet.";
        else sub = _sampler.Updated is { } u ? $"Last heartbeat {Format.DateTimeLocal(u)}" : "No heartbeat has ever been written.";
        return Parts.FigureCard("Sampler", value, sub);
    }

    private static Border SavedCard(SyncData data)
    {
        var hours = data.HoursSinceSuccess;
        var stale = hours > 1;
        var backup = data.Runs.FirstOrDefault(r => r.BackupStatus is not null)?.BackupStatus;
        var value = Ui.Text(hours is { } h ? Format.Relative(h) : "never", 30.4, 650, stale ? Palette.WarnBrush : Palette.AccentBrightBrush, -0.035);
        var sub = (data.LastSuccess is { } ls ? $"Every 15 minutes while the sampler runs. Last at {Format.DateTimeLocal(ls.StartedAt)}." : "No successful save recorded.")
            + (backup is null ? "" : $" Backup: {(backup == "ok" ? "OK" : backup)}.");
        return Parts.FigureCard("Last saved", value, sub, 60);
    }

    private static Border StoredCard(SyncData data)
    {
        var stack = new StackPanel { Spacing = 7 };
        stack.Children.Add(Parts.StatLabel("Stored"));
        void Row(string label, string value)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Ui.Text(label, 15, 400, Palette.TextMutedBrush));
            var v = Ui.Text(value, 15, 500, numeric: true);
            Grid.SetColumn(v, 1);
            row.Children.Add(v);
            stack.Children.Add(row);
        }
        Row("Days with data", Format.Count(data.DaysWithData));
        Row("Active", Format.Duration(data.Stored.Active));
        Row("Locked", Format.Duration(data.Stored.Locked));
        Row("Unattributed", Format.Duration(data.Stored.Unknown));
        Row("Asleep / not recording", Format.Duration(data.Stored.Gap));
        Row("Recorded", data.FirstDate is null ? "—" : $"{Format.DayShort(data.FirstDate)} – {Format.DayShort(data.LatestDate!)}");
        var card = Ui.Card(stack, new Thickness(25.6), hover: true);
        Ui.Rise(card, 120);
        return card;
    }

    /// <summary>
    /// Newest and Oldest at the ends, Newer and Older one step each, the page
    /// numbers around this one (Core/View/Pages), and a box that goes straight
    /// to any page. Page 1 is the newest. The web dashboard's pager.
    /// </summary>
    private FrameworkElement Pager(int pages)
    {
        void Go(int page)
        {
            _page = Math.Clamp(page, 1, pages);
            ctx.Redraw();
        }

        var pager = new WrapPanel { HorizontalSpacing = 7, VerticalSpacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
        Button Step(string label, int to, bool enabled, string tip)
        {
            var chip = Ui.Chip(label, false);
            chip.IsEnabled = enabled;
            chip.Click += (_, _) => Go(to);
            Ui.SetTip(chip, tip);
            return chip;
        }
        pager.Children.Add(Step("Newest", 1, _page > 1, "First page: the newest runs"));
        pager.Children.Add(Step("← Newer", _page - 1, _page > 1, "Previous page"));

        var numbers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(6, 0, 6, 0) };
        foreach (var item in Pages.Items(_page, pages))
        {
            if (item is not { } n)
            {
                var gap = Ui.Text("…", 15, 400, Palette.TextFaintBrush, selectable: false);
                gap.VerticalAlignment = VerticalAlignment.Center;
                numbers.Children.Add(gap);
                continue;
            }
            var chip = Ui.Chip(n.ToString(System.Globalization.CultureInfo.InvariantCulture), n == _page);
            chip.MinWidth = 40;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"Page {n}");
            chip.Click += (_, _) => Go(n);
            numbers.Children.Add(chip);
        }
        pager.Children.Add(numbers);

        pager.Children.Add(Step("Older →", _page + 1, _page < pages, "Next page"));
        pager.Children.Add(Step("Oldest", pages, _page < pages, "Last page: the oldest runs"));

        var jump = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(10, 0, 0, 0) };
        var label = Ui.Text("Go to page", 15, 400, Palette.TextMutedBrush, selectable: false);
        label.VerticalAlignment = VerticalAlignment.Center;
        jump.Children.Add(label);
        var box = new NumberBox
        {
            Value = _page,
            Minimum = 1,
            Maximum = pages,
            Width = 76,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, $"Page number, 1 to {pages}");
        void Submit()
        {
            if (!double.IsNaN(box.Value)) Go((int)box.Value);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            Submit();
        };
        jump.Children.Add(box);
        var of = Ui.Text($"of {pages}", 15, 400, Palette.TextMutedBrush, selectable: false);
        of.VerticalAlignment = VerticalAlignment.Center;
        jump.Children.Add(of);
        var go = Ui.Chip("Go", false, 14);
        go.Click += (_, _) => Submit();
        jump.Children.Add(go);
        pager.Children.Add(jump);
        return pager;
    }

    private Border RunsPanel(SyncData data)
    {
        var first = (_page - 1) * PageSize + 1;
        var last = Math.Min(_page * PageSize, (int)data.TotalRuns);
        var aside = data.TotalRuns > 0 ? Ui.Text($"{first}–{last} of {data.TotalRuns}", 15, 500, Palette.TextMutedBrush, numeric: true) : null;

        var table = new DataTable([
            new("Started"), new("Status", Left: true), new("Spans"), new("New"), new("Already saved"), new("Took"), new("Backup", Left: true),
        ]) { RowPadding = 11 };
        foreach (var r in data.Runs)
        {
            var status = Ui.Badge(r.Status, r.Status == "success" ? Ui.BadgeKind.Ok : r.Status == "failed" ? Ui.BadgeKind.Bad : Ui.BadgeKind.Plain);
            table.AddRow([
                Parts.MonoCell(Format.DateTimeLocal(r.StartedAt), Palette.TextBrush, 14),
                status,
                DataTable.Cell(Format.Count(r.RowsRead)),
                r.RowsInserted > 0 ? DataTable.Cell($"+{Format.Count(r.RowsInserted)}", Palette.AccentBrightBrush, 600) : DataTable.Cell("0", Palette.TextFaintBrush),
                DataTable.Cell(Format.Count(r.RowsSkipped), Palette.TextFaintBrush),
                DataTable.Cell(Format.Elapsed(r.DurationMs), Palette.TextMutedBrush),
                DataTable.Cell(r.BackupStatus ?? "—", r.BackupStatus == "ok" ? Palette.GoodBrush : Palette.TextMutedBrush, size: 15),
            ]);
        }

        var body = new StackPanel();
        body.Children.Add(table.Build());

        var pages = Math.Max(1, (int)Math.Ceiling(data.TotalRuns / (double)PageSize));
        if (pages > 1) body.Children.Add(Pager(pages));

        var errors = data.Runs.Where(r => r.Error is not null).Take(5).ToList();
        if (errors.Count > 0)
        {
            var box = new StackPanel { Margin = new Thickness(0, 22, 0, 0), Spacing = 6 };
            var head = Parts.StatLabel("Recent errors");
            head.Margin = new Thickness(0, 0, 0, 4);
            box.Children.Add(head);
            foreach (var r in errors)
            {
                var line = Ui.Text("", 14, 400, Palette.WarnBrush, wrap: true);
                line.FontFamily = Fonts.Mono;
                line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = Format.DateTimeLocal(r.StartedAt), Foreground = Palette.TextMutedBrush });
                line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " — " + r.Error });
                box.Children.Add(new Border { Background = Palette.WarnDimBrush, CornerRadius = new CornerRadius(Ui.RadiusSmall), Padding = new Thickness(13, 10, 13, 10), Child = line });
            }
            body.Children.Add(box);
        }

        return Ui.Panel("Run history",
            "Newest first. Each run folds the sampler's spans into the database; one that adds nothing is normal - the spans were already saved, which is the dedup working.",
            aside, body);
    }

    /// <summary>The one scheduled task, as Windows reports it.</summary>
    private Border TaskPanel()
    {
        var table = new DataTable([new("Task"), new("Runs as", Left: true), new("Started"), new("State"), new("Last result")]) { RowPadding = 11 };
        if (_task is not { Exists: true })
        {
            table.AddRow([DataTable.Cell(ScheduledTasks.SamplerTask, numeric: false), DataTable.Cell("you, not elevated", Palette.TextMutedBrush, numeric: false), DataTable.Cell("not registered", Palette.WarnBrush), null, null]);
        }
        else
        {
            var t = _task;
            table.AddRow([
                DataTable.Cell(ScheduledTasks.SamplerTask, numeric: false),
                DataTable.Cell("you, not elevated", Palette.TextMutedBrush, numeric: false),
                DataTable.Cell(t.LastRunUtc is { } at ? Format.DateTimeLocal(Core.Data.Time.Iso(at)) : "—", Palette.TextMutedBrush),
                DataTable.Cell(t.Running ? "running" : "not running", t.Running ? Palette.GoodBrush : Palette.WarnBrush),
                // 0x41301 is "currently running", the normal answer for a task that never finishes.
                DataTable.Cell(t.LastResult is 0 or 0x41301 ? "OK" : $"0x{t.LastResult:X}", t.LastResult is 0 or 0x41301 ? Palette.GoodBrush : Palette.WarnBrush),
            ]);
        }
        return Ui.Panel("Scheduled task",
            "Starts the sampler at every sign-in and keeps it running, with no time limit. It needs no elevation: reading which window is in front is something any program may do.",
            null, table.Build());
    }
}
