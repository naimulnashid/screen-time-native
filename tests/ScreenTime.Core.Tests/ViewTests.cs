using ScreenTime.Core.Naming;
using ScreenTime.Core.View;

namespace ScreenTime.Core.Tests;

public class ViewTests
{
    private const long S = 1000, M = 60 * S, H = 60 * M;

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(999, "0s")]
    [InlineData(45 * S, "45s")]
    [InlineData(12 * M + 30 * S, "12m 30s")]
    [InlineData(12 * M, "12m")]
    [InlineData(6 * H + 42 * M + 59 * S, "6h 42m")]
    [InlineData(4 * H, "4h")]
    // No day rung: a multi-day total stays in hours.
    [InlineData(51 * H + 12 * M, "51h 12m")]
    public void DurationsHaveTwoRungsAndStopAtHours(long ms, string expected) => Assert.Equal(expected, Format.Duration(ms));

    [Fact]
    public void CountUpKeepsTheFinalShape()
    {
        // Half-way to 6h 42m is still hours and minutes, never "3h 21m" turning into "201m".
        Assert.Equal("3h 21m", Format.DurationLike(3 * H + 21 * M, 6 * H + 42 * M));
        Assert.Equal("0h 0m", Format.DurationLike(0, 6 * H + 42 * M));
        Assert.Equal("0h", Format.DurationLike(40 * M, 4 * H));
        Assert.Equal("0m 5s", Format.DurationLike(5 * S, 12 * M + 30 * S));
    }

    [Fact]
    public void RelativeTimeRoundsBeforeComparing()
    {
        Assert.Equal("just now", Format.Relative(0.001));
        Assert.Equal("12 min ago", Format.Relative(0.2));
        // 59.7 minutes rounds to an hour, not to "60 min ago".
        Assert.Equal("1h ago", Format.Relative(59.7 / 60));
        Assert.Equal("yesterday", Format.Relative(30));
        Assert.Equal("3 days ago", Format.Relative(72));
    }

    [Fact]
    public void HourOfDayAndElapsed()
    {
        Assert.Equal("12 AM", Format.HourOfDay(0));
        Assert.Equal("12 PM", Format.HourOfDay(12));
        Assert.Equal("2 PM", Format.HourOfDay(14));
        Assert.Equal("340 ms", Format.Elapsed(340));
        Assert.Equal("1.4 s", Format.Elapsed(1400));
        Assert.Equal("—", Format.Elapsed(null));
    }

    [Fact]
    public void TimeAxisEndsOneStepAboveThePeak()
    {
        // The measured web case: an 11h 5m peak drew an axis to 17h.
        Assert.Equal([0, 2 * H, 4 * H, 6 * H, 8 * H, 10 * H, 12 * H], Axis.Ticks(11 * H + 5 * M, Measure.Time));
        Assert.Equal([0, 15 * M, 30 * M, 45 * M, 60 * M, 75 * M, 90 * M], Axis.Ticks(80 * M, Measure.Time));
        Assert.Equal([0, M], Axis.Ticks(0, Measure.Time));
        // All-history totals: whole days of hours, never 47h steps.
        var big = Axis.Ticks(279 * H, Measure.Time);
        Assert.Equal(48 * H, big[1]);
        Assert.Equal(288 * H, big[^1]);
    }

    [Fact]
    public void CountAxisIsWholeNumbers()
    {
        Assert.Equal([0, 25, 50, 75, 100, 125, 150], Axis.Ticks(130, Measure.Count));
        Assert.Equal([0, 1], Axis.Ticks(0, Measure.Count));
        Assert.All(Axis.Ticks(7, Measure.Count), t => Assert.Equal(0, t % 1));
    }

    [Fact]
    public void TimeTicksAreExact()
    {
        Assert.Equal("0", Axis.TimeTick(0));
        Assert.Equal("30m", Axis.TimeTick(30 * M));
        Assert.Equal("1h30", Axis.TimeTick(90 * M));
        Assert.Equal("2h", Axis.TimeTick(2 * H));
    }

    [Fact]
    public void VisitsStitchOnlyWhenNothingElseCameBetween()
    {
        var sessions = new[]
        {
            new RawSession("code", 0, 10 * S),
            new RawSession("code", 20 * S, 40 * S),      // 10s later: same visit
            new RawSession("chrome", 41 * S, 50 * S),
            new RawSession("code", 55 * S, 60 * S),      // 5s later, but Chrome came between
            new RawSession("code", 2 * M, 3 * M),        // a minute later: a new visit
        };
        var visits = Visits.Stitch(sessions);
        Assert.Equal(["code", "chrome", "code", "code"], visits.Select(v => v.App));
        // Summed, never spanned: the 10s gap is not usage.
        Assert.Equal(30 * S, visits[0].Ms);
        Assert.Equal(2, visits[0].Parts);
        Assert.Equal(3, Visits.Counts(visits)["code"]);

        var (count, median, longest) = Visits.Stats(visits, "code");
        Assert.Equal(3, count);
        Assert.Equal(30 * S, median);
        Assert.Equal(60 * S, longest);
    }

    [Fact]
    public void AnOpenIsFiledWhereItsVisitBegan()
    {
        var visits = Visits.Stitch([new RawSession("a", 1000, 2000), new RawSession("a", 900_000, 950_000)]);
        var starts = new Dictionary<long, SessionBucket> { [1000] = new("2026-09-30", 23) };
        var (byDate, byHour) = Visits.OpenBuckets(visits, "a", starts);
        // The second began before the range (no bucket): dropped, not guessed at.
        Assert.Equal(1, byDate["2026-09-30"]);
        Assert.Single(byDate);
        Assert.Equal(1, byHour[23]);
    }

    [Fact]
    public void StacksKeepTheTopEightAndOtherOnlyWhenThereIsAnyOther()
    {
        var rows = Enumerable.Range(0, 10).Select(i => new StackInput("2026-09-02", "app" + i, 100 - i)).ToList();
        var (series, points) = Stack.ByApp(rows, ["2026-09-01", "2026-09-02"]);
        Assert.Equal(9, series.Count);
        Assert.Null(series[^1].Id);
        Assert.Equal(92 + 91, series[^1].Total);
        Assert.Equal(2, points.Count);
        Assert.True(points[0].Recorded);
        Assert.Equal(0, points[0].Values.Sum());

        var (few, _) = Stack.ByApp(rows.Take(3), ["2026-09-02"]);
        Assert.DoesNotContain(few, s => s.Id is null);

        // A day in neither set is not recorded, and is still a point.
        var (_, gap) = Stack.ByApp([new StackInput("2026-09-01", "a", 5), new StackInput("2026-09-03", "a", 5)], ["2026-09-01", "2026-09-03"]);
        Assert.Equal(3, gap.Count);
        Assert.False(gap[1].Recorded);
    }

    [Fact]
    public void PeakNeedsAPositiveWinner()
    {
        Assert.Null(Stack.PeakOf(new[] { new HourPoint(0, 0), new HourPoint(1, 0) }, h => h.Value));
        Assert.Equal(1, Stack.PeakOf(new[] { new HourPoint(0, 5), new HourPoint(1, 9), new HourPoint(2, 9) }, h => h.Value)!.Hour);
    }

    [Fact]
    public void AnAppIsListedByTimeOrByHabit()
    {
        Assert.True(AppList.IsListed(10 * M, 0, 1));          // one long sitting is enough
        Assert.False(AppList.IsListed(9 * M, 29, 9));
        Assert.True(AppList.IsListed(M, 30, 5));              // a daily habit of seconds
        Assert.False(AppList.IsListed(M, 60, 4));             // one afternoon of alt-tabbing is not
    }

    [Fact]
    public void FillLeavesUnrecordedDaysNull()
    {
        var filled = Days.Fill([("2026-09-01", 5L), ("2026-09-03", 0L)]);
        Assert.Equal(3, filled.Count);
        Assert.Null(filled[1].Ms);
        Assert.Equal(0, filled[2].Ms);       // recorded, and quiet
        Assert.Equal(24, Days.FillHours(new Dictionary<int, long> { [3] = 7 }).Count);
    }

    [Fact]
    public void HeatStepsAreEven()
    {
        Assert.Equal(0, AppColors.HeatStep(0, 100));
        Assert.Equal(1, AppColors.HeatStep(1, 100));
        Assert.Equal(1, AppColors.HeatStep(20, 100));
        Assert.Equal(3, AppColors.HeatStep(55, 100));
        Assert.Equal(5, AppColors.HeatStep(100, 100));
    }

    [Fact]
    public void ExpandedHeatmapStartsAtTheEarliestDay()
    {
        var today = new DateOnly(2026, 10, 1);
        var blocks = Heatmap.Expanded([("2026-06-27", 5L)], "2026-06-27", today);
        Assert.Equal("2026-06-27", blocks[0].First);
        Assert.All(blocks[0].Cells.Where(c => string.CompareOrdinal(c.Date, "2026-06-27") < 0), c => Assert.True(c.Hidden));
        Assert.Equal(1, blocks.Sum(b => b.ActiveDays));
    }

    [Fact]
    public void PagerShowsTheEndsAndTheNeighbourhood()
    {
        Assert.Equal([1, null, 4, 5, 6, 7, 8, null, 12], Pages.Items(6, 12));
        // A gap never stands in for a single page.
        Assert.Equal([1, 2, 3, 4, 5, 6, null, 12], Pages.Items(4, 12));
        Assert.Equal([1], Pages.Items(1, 1));
    }
}
