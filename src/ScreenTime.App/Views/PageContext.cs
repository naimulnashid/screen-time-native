using ScreenTime.App.State;
using Microsoft.UI.Xaml;

namespace ScreenTime.App.Views;

public enum PageKind
{
    Overview,
    Apps,
    App,
    Activity,
    Sync,
}

public sealed record Route(PageKind Kind, string? AppKey = null);

/// <summary>What every page needs from the shell.</summary>
public sealed class PageContext
{
    public required AppState State { get; init; }
    public required Action<Route> Navigate { get; init; }

    /// <summary>Reloads and rebuilds the current page in place, keeping the scroll position.</summary>
    public required Action Redraw { get; init; }

    public required Window Window { get; init; }
}

/// <summary>
/// A page reads its data off the UI thread, then builds its tree from it.
/// Keeping the two apart is what lets a full-history query run without the
/// window freezing.
/// </summary>
public interface IPage
{
    /// <summary>Runs on a worker thread: queries only, no UI.</summary>
    void Load();

    /// <summary>Runs on the UI thread, after <see cref="Load"/>.</summary>
    UIElement Build();

    /// <summary>
    /// Fills the page with stand-in data (<see cref="Views.Placeholder"/>) in
    /// place of <see cref="Load"/>, so <see cref="Build"/> can draw its loading
    /// skeleton. No database access.
    /// </summary>
    void Placeholder();
}
