namespace ScreenTime.Core.View;

public sealed record HeatmapCell(string Date, long Total, bool Known, bool Hidden, int Column, int Row);

public sealed record HeatmapBlock(
    IReadOnlyList<HeatmapCell> Cells,
    IReadOnlyList<(string Label, int Column)> Months,
    string First,
    string Last,
    long Peak,
    long Total,
    int ActiveDays);

/// <summary>
/// The calendar arithmetic behind the activity heat map, shared by the
/// overview's six-month card and the full-history page it expands into.
/// </summary>
/// <remarks>
/// Blocks are contiguous 26-week runs, not calendar half-years: half-years
/// would need 27 or 28 columns and split a week at every seam. Weeks run
/// Saturday to Friday, as the original's did.
/// </remarks>
public static class Heatmap
{
    public const int Weeks = 26;

    public static readonly string[] DayLabels = ["Sat", "Sun", "Mon", "Tue", "Wed", "Thu", "Fri"];

    private static readonly string[] MonthNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    public static DateOnly LocalToday() => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Saturday-first: Saturday is row 0.</summary>
    private static int RowOf(DateOnly d) => ((int)d.DayOfWeek + 1) % 7;

    private static DateOnly WeekStartOf(DateOnly d) => d.AddDays(-RowOf(d));

    /// <summary>
    /// One block of <see cref="Weeks"/> columns from <paramref name="firstWeek"/>.
    /// Days before <paramref name="from"/> are HIDDEN, not "no data": they are
    /// outside the page, not a gap in collection.
    /// </summary>
    private static HeatmapBlock Build(IReadOnlyDictionary<string, long> byDate, DateOnly firstWeek, DateOnly today, DateOnly? from)
    {
        var cells = new List<HeatmapCell>(Weeks * 7);
        var months = new List<(string, int)>();
        long peak = 0, total = 0;
        int active = 0, lastMonth = -1;

        for (var column = 0; column < Weeks; column++)
        {
            var weekStart = firstWeek.AddDays(column * 7);
            // Label a column by the first day it actually draws.
            var labelDay = from is { } f && weekStart < f ? f : weekStart;
            if (labelDay.Month != lastMonth)
            {
                months.Add((MonthNames[labelDay.Month - 1], column));
                lastMonth = labelDay.Month;
            }

            for (var row = 0; row < 7; row++)
            {
                var day = weekStart.AddDays(row);
                var date = Days.Iso(day);
                var hidden = day > today || (from is { } f2 && day < f2);
                var known = !hidden && byDate.ContainsKey(date);
                var value = byDate.GetValueOrDefault(date);
                if (known)
                {
                    peak = Math.Max(peak, value);
                    total += value;
                    if (value > 0) active++;
                }
                cells.Add(new HeatmapCell(date, value, known, hidden, column, row));
            }
        }

        // A block opening on a month's last week labels that month and the next
        // side by side, and "JunJul" overlap. Drop the stub.
        if (months.Count > 1 && months[1].Item2 - months[0].Item2 < 3) months.RemoveAt(0);

        var start = from is { } f3 && firstWeek < f3 ? f3 : firstWeek;
        return new HeatmapBlock(cells, months, Days.Iso(start), Days.Iso(firstWeek.AddDays(Weeks * 7 - 1)), peak, total, active);
    }

    private static Dictionary<string, long> ToMap(IEnumerable<(string Date, long Total)> daily) =>
        daily.ToDictionary(d => d.Date, d => d.Total);

    /// <summary>The overview's block: the weeks ending with the current one.</summary>
    public static HeatmapBlock Recent(IEnumerable<(string Date, long Total)> daily, DateOnly? today = null)
    {
        var t = today ?? LocalToday();
        return Build(ToMap(daily), WeekStartOf(t).AddDays(-(Weeks - 1) * 7), t, null);
    }

    /// <summary>
    /// Every block from the earliest day with data to today, oldest first.
    /// Consecutive blocks are contiguous weeks. No data at all starts today.
    /// </summary>
    /// <remarks>
    /// It opens on the first day the history holds, not on a fixed date or the
    /// 1st of that month: either drew months of empty weeks above the first real
    /// day. The days before it in that week are hidden, not outlined.
    /// </remarks>
    public static List<HeatmapBlock> Expanded(IEnumerable<(string Date, long Total)> daily, string? earliest, DateOnly? today = null)
    {
        var t = today ?? LocalToday();
        var byDate = ToMap(daily);
        var from = earliest is null ? t : Days.Parse(earliest);
        var blocks = new List<HeatmapBlock>();
        for (var week = WeekStartOf(from); week <= t; week = week.AddDays(Weeks * 7))
            blocks.Add(Build(byDate, week, t, from));
        return blocks;
    }

    /// <summary>"Jan 1 – Jun 26, 2026", or with both years across New Year.</summary>
    public static string BlockLabel(HeatmapBlock block)
    {
        static string Short(string iso)
        {
            var d = Days.Parse(iso);
            return $"{MonthNames[d.Month - 1]} {d.Day}";
        }
        var y1 = block.First[..4];
        var y2 = block.Last[..4];
        return y1 == y2 ? $"{Short(block.First)} – {Short(block.Last)}, {y2}" : $"{Short(block.First)}, {y1} – {Short(block.Last)}, {y2}";
    }
}
