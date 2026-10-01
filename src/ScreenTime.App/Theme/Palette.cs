using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ScreenTime.App.Theme;

/// <summary>
/// The colour tokens, per theme: the web dashboard's, one for one. Dark is the
/// true-black OLED theme this app started as; light is its own design, not
/// the dark one inverted (see <see cref="Light"/>).
/// </summary>
/// <remarks>
/// <para><b>This is the only place any accent colour exists.</b> Never write an
/// accent hex in a page or a chart: the web dashboard's trend chart once stayed
/// blue on a green page because a chart carried its own copy.</para>
/// <para><b>How a theme switch reaches the screen.</b> Every <c>...Brush</c>
/// here is ONE shared object, retinted in place by <see cref="Apply"/>, so
/// everything painted with one repaints at once. A <see cref="Color"/> read
/// while building (a chart's gradient, an app's colour) is baked into what was
/// built, which is why the window also rebuilds the page after a switch.</para>
/// <para><b>Red is reserved for genuine anomalies</b> - a failed ingest, a
/// sampler that stopped - so it reads as signal, not decoration.</para>
/// <para><b>The accent is the web dashboard's laptop violet</b>, #7c5cff, so the
/// two read as one product; the Data Usage apps are blue on purpose, which is
/// how the siblings tell apart at a glance.</para>
/// <para>Contrast is measured, not judged. Dark: <c>TextFaint</c> (#7c7c88)
/// clears 4.6:1 on every panel; solid controls use <c>AccentFill</c> (#795af9,
/// white on it 4.52:1 - #7c5cff itself fails at 4.35:1). Light: text-muted
/// 7.7:1 and text-faint 5.3:1 on white; the accent deepened to #6644e8 so it
/// reads as text.</para>
/// </remarks>
public static class Palette
{
    public static Color Hex(string hex, double alpha = 1)
    {
        var h = hex.TrimStart('#');
        return Color.FromArgb((byte)Math.Round(alpha * 255), Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t),
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    public static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Round(a * 255), c.R, c.G, c.B);

    /// <summary>One theme's values. Brushes derive from these in <see cref="Apply"/>.</summary>
    private sealed record Tokens(
        Color Bg, Color Surface, Color SurfaceHover, Color Inset, Color Border, Color BorderBright,
        Color Grid, Color Text, Color TextMuted, Color TextFaint, Color TooltipBg, Color Warn, Color Good,
        Color RowBorder, Color Accent, Color AccentBright, Color AccentFill, Color Locked, Color Unknown, Color Asleep,
        Color HeatNone, Color HeatNoneRing, Color HoverWash, Color Plate, string[] Heat,
        double AccentDim, double AccentBorder, double AccentBorderStrong,
        double InkMin, double InkMax, double InkTextMin, double InkTextMax);

    private static readonly Tokens Dark = new(
        Bg: Hex("#000000"), Surface: Hex("#0a0a0b"), SurfaceHover: Hex("#101012"), Inset: Hex("#08080a"),
        Border: Hex("#1e1e22"), BorderBright: Hex("#2c2c33"), Grid: Hex("#1a1a1e"),
        Text: Hex("#f5f5f7"), TextMuted: Hex("#a1a1aa"), TextFaint: Hex("#7c7c88"), TooltipBg: Hex("#0c0c0e"),
        Warn: Hex("#ff4d4f"), Good: Hex("#22c55e"), RowBorder: Hex("#1e1e22", 0.6),
        Accent: Hex("#7c5cff"), AccentBright: Hex("#9b82ff"), AccentFill: Hex("#795af9"),
        // Where the time went: locked is the accent at a third, so it reads as
        // "the machine was in use, but nobody at it"; unattributed and asleep
        // are neutrals, asleep the quieter - absence must read as absence.
        Locked: Mix(Hex("#0a0a0b"), Hex("#7c5cff"), 0.34), Unknown: Mix(Hex("#0a0a0b"), Hex("#a1a1aa"), 0.55),
        Asleep: Mix(Hex("#0a0a0b"), Hex("#a1a1aa"), 0.28),
        HeatNone: Hex("#0b0b0d"), HeatNoneRing: Hex("#1e1e22"), HoverWash: Hex("#ffffff", 0.04), Plate: Hex("#ececf1"),
        Heat: ["#131519", "#241a5c", "#33228a", "#4c33bd", "#7c5cff", "#a795ff"],
        AccentDim: 0.16, AccentBorder: 0.34, AccentBorderStrong: 0.46,
        // An app colour is painted within CIE L* 38..100 on black: a user's
        // black would otherwise vanish. Text in an app's colour: 55..100.
        InkMin: 38, InkMax: 100, InkTextMin: 55, InkTextMax: 100);

    /// <summary>
    /// The light theme. What makes a light dashboard read, and what this
    /// follows: a pale grey canvas under white cards, not white on white; a
    /// border plus a soft shadow for separation, which unlike on black is
    /// visible here; hover as elevation; near-black text and measured greys;
    /// the accent deepened until it reads as text; a heat map that runs light
    /// to dark; and app colours clamped darker, so near-white brand colours
    /// (7-Zip, Armoury Crate) do not vanish into the card.
    /// </summary>
    private static readonly Tokens Light = new(
        Bg: Hex("#f4f5f7"), Surface: Hex("#ffffff"), SurfaceHover: Hex("#eceff4"), Inset: Hex("#eef0f3"),
        Border: Hex("#e3e5ea"), BorderBright: Hex("#cfd2d9"), Grid: Hex("#eceef2"),
        Text: Hex("#16171a"), TextMuted: Hex("#52525b"), TextFaint: Hex("#6b6b76"), TooltipBg: Hex("#ffffff"),
        Warn: Hex("#b42318"), Good: Hex("#15803d"), RowBorder: Hex("#e3e5ea"),
        Accent: Hex("#6644e8"), AccentBright: Hex("#5a38d6"), AccentFill: Hex("#6644e8"),
        Locked: Mix(Hex("#ffffff"), Hex("#6644e8"), 0.38), Unknown: Mix(Hex("#ffffff"), Hex("#52525b"), 0.45),
        Asleep: Mix(Hex("#ffffff"), Hex("#52525b"), 0.22),
        // Until 2026-10-01 the hover fill was #f5f7fa, the no-data cell #fbfbfc
        // with a #e3e5ea ring, and the chart wash 5% ink: all within ~1.1:1 of
        // the white card, so none of them showed. The hover fill is the deepest
        // step that keeps TextFaint at 4.6:1 on a hovered row.
        HeatNone: Hex("#f6f7f9"), HeatNoneRing: Hex("#cdd1d8"), HoverWash: Hex("#101828", 0.09), Plate: Hex("#000000", 0),
        Heat: ["#e1e6ed", "#ddd5fd", "#bcaaf9", "#9479f1", "#6c4ce6", "#4a2bb8"],
        AccentDim: 0.10, AccentBorder: 0.30, AccentBorderStrong: 0.42,
        InkMin: 0, InkMax: 74, InkTextMin: 0, InkTextMax: 50);

    private static Tokens _t = Dark;

    /// <summary>Whether the light theme is applied.</summary>
    public static bool IsLight { get; private set; }

    public static Color Bg => _t.Bg;
    public static Color Surface => _t.Surface;
    public static Color SurfaceHover => _t.SurfaceHover;
    public static Color Inset => _t.Inset;
    public static Color Border => _t.Border;
    public static Color BorderBright => _t.BorderBright;
    public static Color Grid => _t.Grid;
    public static Color Text => _t.Text;
    public static Color TextMuted => _t.TextMuted;
    public static Color TextFaint => _t.TextFaint;
    public static Color TooltipBg => _t.TooltipBg;
    public static Color Warn => _t.Warn;
    public static Color Good => _t.Good;
    public static Color RowBorder => _t.RowBorder;
    public static Color Accent => _t.Accent;
    public static Color AccentBright => _t.AccentBright;
    public static Color Locked => _t.Locked;
    public static Color Unknown => _t.Unknown;

    /// <summary>Asleep, on "Where the time went": the quietest of the four, but visible on the panel.</summary>
    public static Color Asleep => _t.Asleep;

    /// <summary>A day nothing was recorded. Neutral: it must never read as a quiet day.</summary>
    public static Color HeatNone => _t.HeatNone;

    public static readonly SolidColorBrush BgBrush = new();
    public static readonly SolidColorBrush SurfaceBrush = new();
    public static readonly SolidColorBrush SurfaceHoverBrush = new();
    public static readonly SolidColorBrush InsetBrush = new();
    public static readonly SolidColorBrush BorderBrush = new();
    public static readonly SolidColorBrush BorderBrightBrush = new();
    public static readonly SolidColorBrush GridBrush = new();
    public static readonly SolidColorBrush TextBrush = new();
    public static readonly SolidColorBrush TextMutedBrush = new();
    public static readonly SolidColorBrush TextFaintBrush = new();
    public static readonly SolidColorBrush TooltipBgBrush = new();
    public static readonly SolidColorBrush WarnBrush = new();
    public static readonly SolidColorBrush WarnDimBrush = new();
    public static readonly SolidColorBrush WarnBorderBrush = new();
    public static readonly SolidColorBrush GoodBrush = new();
    public static readonly SolidColorBrush GoodDimBrush = new();
    public static readonly SolidColorBrush RowBorderBrush = new();
    public static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);
    public static readonly SolidColorBrush HoverWashBrush = new();

    public static readonly SolidColorBrush AccentBrush = new();
    public static readonly SolidColorBrush AccentBrightBrush = new();
    public static readonly SolidColorBrush AccentDimBrush = new();
    public static readonly SolidColorBrush AccentBorderBrush = new();
    public static readonly SolidColorBrush AccentBorderStrongBrush = new();
    public static readonly SolidColorBrush AccentFillBrush = new();
    public static readonly SolidColorBrush OnAccentFillBrush = new(Colors.White);
    public static readonly SolidColorBrush LockedBrush = new();
    public static readonly SolidColorBrush UnknownBrush = new();
    public static readonly SolidColorBrush AsleepBrush = new();
    public static readonly SolidColorBrush HeatNoneBrush = new();
    public static readonly SolidColorBrush HeatNoneRingBrush = new();

    /// <summary>
    /// The plate behind near-black logos (see AppIcons.PlateStems) on black.
    /// None on the light theme: every logo was rendered on white 2026-09-30
    /// and all of them read.
    /// </summary>
    public static readonly SolidColorBrush PlateBrush = new();

    /// <summary>The heat ramp in the accent's own hue family. Step 0 is a real but quiet day.</summary>
    public static readonly SolidColorBrush[] Heat = Enumerable.Range(0, 6).Select(_ => new SolidColorBrush()).ToArray();

    /// <summary>Raised after <see cref="Apply"/> changed the theme.</summary>
    public static event Action? Changed;

    static Palette() => Paint();

    /// <summary>
    /// Switches theme: retints every shared brush in place, then raises
    /// <see cref="Changed"/> so the window can rebuild what baked a colour in.
    /// UI thread only.
    /// </summary>
    public static void Apply(bool light)
    {
        if (light == IsLight) return;
        IsLight = light;
        _t = light ? Light : Dark;
        Paint();
        Changed?.Invoke();
    }

    private static void Paint()
    {
        BgBrush.Color = _t.Bg;
        SurfaceBrush.Color = _t.Surface;
        SurfaceHoverBrush.Color = _t.SurfaceHover;
        InsetBrush.Color = _t.Inset;
        BorderBrush.Color = _t.Border;
        BorderBrightBrush.Color = _t.BorderBright;
        GridBrush.Color = _t.Grid;
        TextBrush.Color = _t.Text;
        TextMutedBrush.Color = _t.TextMuted;
        TextFaintBrush.Color = _t.TextFaint;
        TooltipBgBrush.Color = _t.TooltipBg;
        WarnBrush.Color = _t.Warn;
        WarnDimBrush.Color = WithAlpha(_t.Warn, IsLight ? 0.10 : 0.14);
        WarnBorderBrush.Color = WithAlpha(_t.Warn, 0.34);
        GoodBrush.Color = _t.Good;
        GoodDimBrush.Color = WithAlpha(_t.Good, IsLight ? 0.10 : 0.14);
        RowBorderBrush.Color = _t.RowBorder;
        HoverWashBrush.Color = _t.HoverWash;
        AccentBrush.Color = _t.Accent;
        AccentBrightBrush.Color = _t.AccentBright;
        AccentDimBrush.Color = WithAlpha(_t.Accent, _t.AccentDim);
        AccentBorderBrush.Color = WithAlpha(_t.Accent, _t.AccentBorder);
        AccentBorderStrongBrush.Color = WithAlpha(_t.Accent, _t.AccentBorderStrong);
        AccentFillBrush.Color = _t.AccentFill;
        LockedBrush.Color = _t.Locked;
        UnknownBrush.Color = _t.Unknown;
        AsleepBrush.Color = _t.Asleep;
        HeatNoneBrush.Color = _t.HeatNone;
        HeatNoneRingBrush.Color = _t.HeatNoneRing;
        PlateBrush.Color = _t.Plate;
        for (var i = 0; i < Heat.Length; i++) Heat[i].Color = Hex(_t.Heat[i]);
    }

    /// <summary>An app's colour as PAINTED on the current theme: see <see cref="Ink(string)"/>.</summary>
    public static Color App(IReadOnlyDictionary<string, string> map, string name) =>
        Ink(Core.Naming.AppColors.Of(map, name));

    /// <summary>An app's colour for TEXT, which needs a darker band on white than a fill does.</summary>
    public static Color AppText(IReadOnlyDictionary<string, string> map, string name) =>
        InkText(Core.Naming.AppColors.Of(map, name));

    /// <summary>
    /// A stored colour as painted: its CIE L* held inside the theme's band,
    /// its hue kept. Brand colours were chosen against black, and some are
    /// near-white (7-Zip, Armoury Crate, a few more in colours.json) - white
    /// bars on a white card. A user's colour can fail the other way, black on
    /// black. The web dashboard does the same with CSS relative colours.
    /// </summary>
    public static Color Ink(string hex) => Clamp(hex, _t.InkMin, _t.InkMax);

    /// <summary>The same band, for text in a stored colour.</summary>
    public static Color InkText(string hex) => Clamp(hex, _t.InkTextMin, _t.InkTextMax);

    private static Color Clamp(string hex, double min, double max)
    {
        if (!Core.Naming.AppColors.TryParse(hex, out var rgb)) rgb = (0x4b, 0x4b, 0x55);
        var l = Core.Naming.AppColors.Lightness(rgb);
        if (l >= min && l <= max) return Color.FromArgb(255, (byte)rgb.R, (byte)rgb.G, (byte)rgb.B);
        // Blend toward black (too light) or white (too dark) by the least that
        // lands inside the band: a bisection on the blend fraction.
        var toward = l > max ? 0 : 255;
        var target = l > max ? max : min;
        double lo = 0, hi = 1;
        (int R, int G, int B) Blend(double t) => (
            (int)Math.Round(rgb.R + (toward - rgb.R) * t),
            (int)Math.Round(rgb.G + (toward - rgb.G) * t),
            (int)Math.Round(rgb.B + (toward - rgb.B) * t));
        for (var i = 0; i < 18; i++)
        {
            var mid = (lo + hi) / 2;
            var lm = Core.Naming.AppColors.Lightness(Blend(mid));
            if (toward == 0 ? lm > target : lm < target) lo = mid; else hi = mid;
        }
        var c = Blend(hi);
        return Color.FromArgb(255, (byte)c.R, (byte)c.G, (byte)c.B);
    }

    public static Color HexOrOther(string hex) => Core.Naming.AppColors.TryParse(hex, out _) ? Hex(hex) : Hex(Core.Naming.AppColors.Other);
}
