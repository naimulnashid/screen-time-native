using ScreenTime.Core.Data;
using Microsoft.Data.Sqlite;

namespace ScreenTime.Core.Tests;

/// <summary>A throwaway data folder in TEMP - database, sampler folder - removed afterwards.</summary>
internal sealed class TestDb : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "ScreenTimeNative-test-" + Guid.NewGuid().ToString("N"));
    public string DbPath => AppPaths.DatabasePath(Dir);
    public string SamplerDir => Path.Combine(Dir, "sampler");
    public SqliteConnection Conn { get; }

    public TestDb()
    {
        Directory.CreateDirectory(SamplerDir);
        Conn = UsageDb.Open(DbPath, allowSystemDrive: true);
    }

    public void Dispose()
    {
        Conn.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }

    public static Span Span(DateTime startUtc, DateTime endUtc, string kind = "app", string app = @"C:\Program Files\Test\test.exe") => new()
    {
        Start = Time.Iso(startUtc),
        End = Time.Iso(endUtc),
        Ms = (long)(endUtc - startUtc).TotalMilliseconds,
        Kind = kind,
        App = kind == "app" ? app : "",
    };

    /// <summary>Spans straight into the table, through the real hour split.</summary>
    public long Insert(params Span[] spans) => UsageDb.InsertSegments(Conn, spans.SelectMany(Time.Segments));

    /// <summary>Local noon on a day some days back, as UTC: away from midnight, so a test's day is unambiguous.</summary>
    public static DateTime Noon(int daysAgo) => DateTime.Today.AddDays(-daysAgo).AddHours(12).ToUniversalTime();
}
