using ScreenTime.Core.Data;
using ScreenTime.Core.Naming;

namespace ScreenTime.Core.Tests;

public class NameTests
{
    private const string Apps = @"C:\Program Files\WindowsApps\";

    [Fact]
    public void AnUpdateDoesNotMakeASecondApp()
    {
        var a = AppNames.Resolve(Apps + @"Claude_1.40609.0.0_x64__pzs8sxrjxfjjc\app\Claude.exe");
        var b = AppNames.Resolve(Apps + @"Claude_2.16120.0.0_x64__pzs8sxrjxfjjc\app\claude.exe");
        Assert.Equal("appx:claude_pzs8sxrjxfjjc", a.Key);
        Assert.Equal(a.Key, b.Key);
        Assert.Equal("Claude", a.Name);
    }

    [Fact]
    public void APackageFamilyIsNotADisplayName()
    {
        Assert.Equal("ChatGPT", AppNames.Resolve(Apps + @"OpenAI.Codex_1.0.0.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe").Name);
        var unknown = AppNames.Resolve(Apps + @"B9ECED6F.SomeThingNew_1.0.0.0_x64__qmba6cd70vzyy\x.exe");
        Assert.Equal("Some Thing New", unknown.Name);
        Assert.StartsWith("appx:b9eced6f.somethingnew_", unknown.Key);
    }

    [Fact]
    public void GenericBasenamesAreKeyedByTheirFolder()
    {
        var vs = AppNames.Resolve(@"C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe");
        var nv = AppNames.Resolve(@"C:\Temp\NVIDIA\setup.exe");
        Assert.NotEqual(vs.Key, nv.Key);
        Assert.Equal("Installer (setup.exe)", vs.Name);
        Assert.Equal("exe:nvidia/setup", nv.Key);
    }

    [Fact]
    public void TheFallbackStaysRecognisable()
    {
        Assert.Equal("Internet Speed Meter", AppNames.Resolve(@"C:\Tools\InternetSpeedMeter.exe").Name);
        Assert.Equal("HWiNFO64", AppNames.Resolve(@"C:\Tools\HWiNFO64.exe").Name);
        Assert.Equal("exe:internetspeedmeter", AppNames.Resolve(@"C:\Tools\InternetSpeedMeter.exe").Key);
        Assert.Equal("Installer (hwi64_852.tmp)", AppNames.Resolve(@"C:\Users\x\AppData\Local\Temp\is-1\hwi64_852.tmp").Name);
    }

    [Fact]
    public void ABareProcessNameResolvesLikeItsPath()
    {
        // Windows refuses some paths unelevated; the name alone must land on the same app.
        Assert.Equal(AppNames.Resolve(@"C:\Windows\explorer.exe").Key, AppNames.Resolve("explorer").Key);
        Assert.Equal("NVIDIA Control Panel", AppNames.Resolve("NVDisplay.Container").Name);
        Assert.True(AppNames.Resolve(@"C:\Windows\System32\cmd.exe").System);
        Assert.Equal("Unknown", AppNames.Resolve("").Name);
    }

    [Fact]
    public void TwoExesCanBeOneApp()
    {
        Assert.Equal(AppNames.Resolve(@"C:\Program Files\7-Zip\7zFM.exe").Key, AppNames.Resolve(@"C:\Program Files\7-Zip\7zG.exe").Key);
        Assert.Equal(AppNames.Resolve(@"C:\Windows\sihost.exe").Key, AppNames.Resolve(@"C:\Windows\ShellHost.exe").Key);
    }

    [Fact]
    public void ADisplayNameBelongsToOneKey()
    {
        // The colour and logo are looked up by name: two keys sharing one would share both.
        var byName = AppNames.KnownEntries()
            .Select(e => (e.Name, Key: e.Key ?? "exe:" + e.Base))
            .GroupBy(e => e.Name)
            .Where(g => g.Select(e => e.Key).Distinct().Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(byName);
    }

    [Fact]
    public void LogosMatchIgnoringCaseAndSpaces()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ScreenTimeNative-logos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "VSCode.svg"), "<svg/>");
            File.WriteAllText(Path.Combine(dir, "Chrome.png"), "");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "");
            var map = AppIcons.Map(dir, [("My Editor", "VS Code")]);
            Assert.NotNull(AppIcons.Find(map, "VS Code"));
            Assert.NotNull(AppIcons.Find(map, "vs code"));
            Assert.NotNull(AppIcons.Find(map, "Google Chrome"));    // through an alias
            Assert.NotNull(AppIcons.Find(map, "My Editor"));         // a rename keeps its original's logo
            Assert.Null(AppIcons.Find(map, "notes"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RenamesAreCheckedAgainstEveryApp()
    {
        using var db = new TestDb();
        var current = new Dictionary<string, string> { ["exe:code"] = "VS Code", ["exe:chrome"] = "Google Chrome" };
        Assert.Equal(RenameError.Taken, Renames.Save(db.Conn, "exe:code", "google chrome", current, "VS Code").Error);
        Assert.Equal(RenameError.Reserved, Renames.Save(db.Conn, "exe:code", "Other", current, "VS Code").Error);
        Assert.Equal(RenameError.UnknownApp, Renames.Save(db.Conn, "exe:nope", "X", current, "X").Error);
        Assert.Equal(RenameError.None, Renames.Save(db.Conn, "exe:code", "  My   Editor ", current, "VS Code").Error);
        Assert.Equal("My Editor", Renames.Read(db.Conn)["exe:code"]);
        // An empty name restores the original.
        Renames.Save(db.Conn, "exe:code", "", current, "VS Code");
        Assert.Empty(Renames.Read(db.Conn));
    }

    [Fact]
    public void ColoursAreCleanedOnTheWayInAndOut()
    {
        Assert.Equal("#aabbcc", ColorOverrides.Clean("ABC"));
        Assert.Equal("#7c5cff", ColorOverrides.Clean(" #7C5CFF "));
        Assert.Null(ColorOverrides.Clean("red"));

        using var db = new TestDb();
        var known = new HashSet<string> { "exe:code" };
        Assert.Equal(ColorError.BadColor, ColorOverrides.Save(db.Conn, "exe:code", "#12", known).Error);
        Assert.Equal(ColorError.None, ColorOverrides.Save(db.Conn, "exe:code", "#f80", known).Error);
        UsageDb.Exec(db.Conn, "INSERT INTO app_colours VALUES ('exe:bad', 'javascript:', 'x')");
        var read = ColorOverrides.Read(db.Conn);
        Assert.Equal("#ff8800", read["exe:code"]);
        Assert.False(read.ContainsKey("exe:bad"));
    }
}
