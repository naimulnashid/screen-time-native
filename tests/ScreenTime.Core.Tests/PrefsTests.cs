using ScreenTime.Core.Naming;
using ScreenTime.Core.Query;

namespace ScreenTime.Core.Tests;

/// <summary>Hiding and merging apps (Naming/AppPrefs.cs), and the per-range stacks.</summary>
public sealed class PrefsTests
{
    private const string Code = @"C:\Users\x\AppData\Local\Programs\Microsoft VS Code\Code.exe";
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string Edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";

    /// <summary>Chrome five hours, 40 days back; Code an hour and Edge half an hour yesterday.</summary>
    private static (TestDb Db, UsageQueries Q, string Code, string Chrome, string Edge) Three()
    {
        var t = new TestDb();
        var old = TestDb.Noon(40);
        var day = TestDb.Noon(1);
        t.Insert(
            TestDb.Span(old, old.AddHours(5), app: Chrome),
            TestDb.Span(day, day.AddHours(1), app: Code),
            TestDb.Span(day.AddHours(1), day.AddHours(1.5), app: Edge));
        var q = new UsageQueries(t.DbPath);
        var names = q.AppNamesByKey();
        string Key(string name) => names.First(kv => kv.Value.Base == name).Key;
        return (t, q, Key("VS Code"), Key("Google Chrome"), Key("Microsoft Edge"));
    }

    [Fact]
    public void AMergedAppIsReadAsPartOfItsTarget()
    {
        var (t, q, code, chrome, edge) = Three();
        using var _t = t;
        var total = q.Overview(Scope.All).Total;
        Assert.Equal(MergeError.None, AppPrefs.SetMerge(t.Conn, chrome, edge, q.AppKeys()));

        var apps = q.ByApp(Scope.All).Apps;
        Assert.Equal(2, apps.Count);
        Assert.Equal(330 * 60_000L, apps.Single(a => a.Key == edge).Ms);
        var detail = q.AppDetailFor(edge, Scope.All)!;
        Assert.Equal("Microsoft Edge", detail.Name);
        Assert.Equal([(chrome, "Google Chrome")], detail.Members.Select(m => (m.Key, m.Name)));
        Assert.Equal(edge, q.MergedInto(chrome));
        Assert.False(q.AppNamesByKey().ContainsKey(chrome));
        Assert.Contains(chrome, q.AppKeys());
        // Nothing moves but the name: the total is the same.
        Assert.Equal(total, q.Overview(Scope.All).Total);
        Assert.Equal("Microsoft Edge", q.DisplayName(Chrome));

        Assert.Equal(MergeError.None, AppPrefs.SetMerge(t.Conn, chrome, null, q.AppKeys()));
        Assert.Equal(3, q.ByApp(Scope.All).Apps.Count);
        Assert.Empty(q.AppDetailFor(edge, Scope.All)!.Members);
        Assert.Equal("VS Code", q.AppNamesByKey()[code].Name);
    }

    [Fact]
    public void MergesStayOneLevelDeep()
    {
        var (t, q, code, chrome, edge) = Three();
        using var _t = t;
        var keys = q.AppKeys();
        AppPrefs.SetMerge(t.Conn, chrome, edge, keys);
        // Into a merged app: stored against its target.
        AppPrefs.SetMerge(t.Conn, code, chrome, keys);
        Assert.Equal(edge, AppPrefs.ReadMerges(t.Conn)[code]);
        Assert.Single(q.ByApp(Scope.All).Apps);
        // Edge into an app merged into Edge would be a loop: refused.
        Assert.Equal(MergeError.SameApp, AppPrefs.SetMerge(t.Conn, edge, chrome, keys));
        // Separating one leaves the other where it was.
        AppPrefs.SetMerge(t.Conn, chrome, null, keys);
        Assert.Equal(edge, AppPrefs.ReadMerges(t.Conn)[code]);
        // Merging the target away carries what was merged into it along.
        AppPrefs.SetMerge(t.Conn, edge, chrome, keys);
        var merges = AppPrefs.ReadMerges(t.Conn);
        Assert.Equal(chrome, merges[edge]);
        Assert.Equal(chrome, merges[code]);
        Assert.Equal(MergeError.UnknownApp, AppPrefs.SetMerge(t.Conn, "exe:nope", edge, keys));
    }

    [Fact]
    public void HidingFlagsTheAppAndChangesNoFigure()
    {
        var (t, q, _, chrome, edge) = Three();
        using var _t = t;
        var before = q.ByApp(Scope.All).Apps.Sum(a => a.Ms);
        AppPrefs.SetHidden(t.Conn, chrome, true);
        var after = q.ByApp(Scope.All).Apps;
        Assert.True(after.Single(a => a.Key == chrome).Hidden);
        Assert.Equal(before, after.Sum(a => a.Ms));
        AppPrefs.SetHidden(t.Conn, chrome, false);
        Assert.Empty(AppPrefs.ReadHidden(t.Conn));
        // A merge takes the app off the hidden list: it is no longer listed.
        AppPrefs.SetHidden(t.Conn, chrome, true);
        AppPrefs.SetMerge(t.Conn, chrome, edge, q.AppKeys());
        Assert.Empty(AppPrefs.ReadHidden(t.Conn));
    }

    [Fact]
    public void EveryRangeOfTheStacksRanksItsOwnTopApps()
    {
        var (t, q, code, chrome, _) = Three();
        using var _t = t;
        var o = q.Overview(Scope.All);
        Assert.Equal(Scope.Ranges.Order(), o.TimeByApp.Keys.Order());
        Assert.Equal(Scope.Ranges.Order(), o.OpensByApp.Keys.Order());
        // Chrome's five hours were 40 days ago: All leads with it, while the
        // seven-day range does not hold it at all and leads with Code.
        Assert.Equal(chrome, o.TimeByApp[Scope.AllDays].Series[0].Id);
        Assert.Equal(code, o.TimeByApp[7].Series[0].Id);
        Assert.DoesNotContain(o.TimeByApp[7].Series, s => s.Id == chrome);
        Assert.True(o.TimeByApp[7].Points.Count <= 7);
        Assert.Equal(3, o.AppCount);
    }
}
