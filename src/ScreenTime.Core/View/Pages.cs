namespace ScreenTime.Core.View;

/// <summary>
/// Which page numbers a pager shows: the first and the last, the current page
/// with two either side, and a gap (null) wherever pages are skipped -
/// <c>1 … 4 5 6 7 8 … 12</c>. A gap never stands in for a single page: that
/// page is shown instead, since the marker would be as wide as the number.
/// The web dashboard's <c>lib/pager.ts</c>, one for one.
/// </summary>
public static class Pages
{
    public static List<int?> Items(int page, int count, int around = 2)
    {
        if (count <= 1) return [1];
        var current = Math.Clamp(page, 1, count);
        var shown = new SortedSet<int> { 1, count };
        for (var p = current - around; p <= current + around; p++)
            if (p >= 1 && p <= count) shown.Add(p);
        var items = new List<int?>();
        int? previous = null;
        foreach (var p in shown)
        {
            if (previous is { } prev)
            {
                if (p - prev == 2) items.Add(prev + 1);
                else if (p - prev > 2) items.Add(null);
            }
            items.Add(p);
            previous = p;
        }
        return items;
    }
}
