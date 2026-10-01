using ScreenTime.App.Charts;
using ScreenTime.App.Theme;
using ScreenTime.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ScreenTime.App.Views;

/// <summary>
/// The full history as 26-week blocks stacked oldest first, from the first
/// day with data. Growing downward keeps the cells the overview's size however
/// many years accumulate; one colour scale across every block, so a shade
/// means the same time in every row.
/// </summary>
public sealed class ActivityPage(PageContext ctx) : IPage
{
    private List<(string Date, long Total)> _days = [];

    public void Load() => _days = ctx.State.Queries!.HeatmapDays();

    public void Placeholder() => _days = Views.Placeholder.Heat();

    public UIElement Build()
    {
        var page = new StackPanel();
        var back = Ui.Link("← Overview", () => ctx.Navigate(new Route(PageKind.Overview)));
        back.Margin = new Thickness(0, 0, 0, 10);
        page.Children.Add(Parts.PageHead("Activity", "Every day recorded, six months to a row, on one colour scale.", before: back));

        var earliest = _days.Count > 0 ? _days.Min(d => d.Date) : null;
        var blocks = Heatmap.Expanded(_days, earliest);
        var max = blocks.Count == 0 ? 0 : blocks.Max(b => b.Peak);
        var body = new StackPanel();
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var head = new Grid { Margin = new Thickness(7, i == 0 ? 0 : 22, 7, 4) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(Ui.Text(Heatmap.BlockLabel(block), 15, 600));
            var total = Ui.Text(Format.Duration(block.Total), 15, 500, Palette.TextMutedBrush, numeric: true);
            Grid.SetColumn(total, 1);
            head.Children.Add(total);
            body.Children.Add(head);
            body.Children.Add(new HeatmapView(block, max));
        }
        var span = blocks.Count > 0 ? $"since {Format.DayShort(blocks[0].First)}, {blocks[0].First[..4]}" : "";
        body.Children.Add(HeatmapView.Legend(blocks.Sum(b => b.Total), blocks.Sum(b => b.ActiveDays), span, null));
        page.Children.Add(Ui.Panel(ctx.State.Device.Label,
            "Active time per day. Outlined days were never recorded - before the sampler existed, or while it was not running - which is not the same as a quiet day.",
            null, body));
        return page;
    }
}
