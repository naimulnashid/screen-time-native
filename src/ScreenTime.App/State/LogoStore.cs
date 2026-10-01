using ScreenTime.Core.Naming;
using Microsoft.UI.Dispatching;

namespace ScreenTime.App.State;

/// <summary>
/// App logos: one folder in the data folder, claimed by file name, watched.
/// </summary>
/// <remarks>
/// <c>Telegram.svg</c> in the folder is the mark for the app shown as
/// "Telegram" (see <see cref="AppIcons"/>), ignoring case and spaces. The folder is watched, so a file
/// dropped in shows within a second, no restart - the web dashboard needed its
/// own route to get there, because <c>next start</c> snapshots <c>public/</c>
/// at boot. It lives with the history, so logos survive a reset too.
/// </remarks>
public sealed class LogoStore(DispatcherQueue ui) : IDisposable
{
    private FileSystemWatcher? _watcher;
    private DispatcherQueueTimer? _debounce;

    public string? Folder { get; private set; }

    /// <summary>Raised on the UI thread when the folder's contents change.</summary>
    public event Action? Changed;

    public void SetFolder(string folder)
    {
        if (string.Equals(folder, Folder, StringComparison.OrdinalIgnoreCase)) return;
        Folder = folder;
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            Directory.CreateDirectory(folder);
            _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, EnableRaisingEvents = true };
            _watcher.Created += (_, _) => Invalidate();
            _watcher.Deleted += (_, _) => Invalidate();
            _watcher.Renamed += (_, _) => Invalidate();
            _watcher.Changed += (_, _) => Invalidate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // No folder, no logos: every app draws its colour swatch.
        }
    }

    /// <summary>
    /// Copies an image in as an app's logo, named after it, replacing any file
    /// already named for it. The watcher then updates every view.
    /// </summary>
    public void SetLogo(string displayName, string sourceFile)
    {
        if (Folder is null) return;
        Directory.CreateDirectory(Folder);
        RemoveLogo(displayName);
        var name = AppIcons.FileStemFor(displayName) + Path.GetExtension(sourceFile).ToLowerInvariant();
        File.Copy(sourceFile, Path.Combine(Folder, name), overwrite: true);
    }

    /// <summary>Removes the file named for this app. A shared mark (an alias) is left alone.</summary>
    public void RemoveLogo(string displayName)
    {
        if (Folder is null || !Directory.Exists(Folder)) return;
        var stem = AppIcons.FileStemFor(displayName);
        foreach (var file in Directory.EnumerateFiles(Folder))
        {
            if (AppIcons.Key(Path.GetFileNameWithoutExtension(file)) == AppIcons.Key(stem)
                && AppIcons.Renderable.Contains(Path.GetExtension(file)))
                File.Delete(file);
        }
    }

    /// <summary>Whether a file named for this app exists (as opposed to an alias's shared mark).</summary>
    public bool HasOwnLogo(string displayName) =>
        Folder is not null && Directory.Exists(Folder) && Directory.EnumerateFiles(Folder).Any(f =>
            AppIcons.Key(Path.GetFileNameWithoutExtension(f)) == AppIcons.Key(AppIcons.FileStemFor(displayName))
            && AppIcons.Renderable.Contains(Path.GetExtension(f)));

    /// <summary>Copies every supported image from a folder. Existing files are replaced.</summary>
    public int Import(string folder)
    {
        if (Folder is null) return 0;
        Directory.CreateDirectory(Folder);
        var copied = 0;
        foreach (var file in Directory.GetFiles(folder))
        {
            if (!AppIcons.Renderable.Contains(Path.GetExtension(file))) continue;
            File.Copy(file, Path.Combine(Folder, Path.GetFileName(file)), overwrite: true);
            copied++;
        }
        return copied;
    }

    private void Invalidate()
    {
        ui.TryEnqueue(() =>
        {
            if (_debounce is null)
            {
                _debounce = ui.CreateTimer();
                _debounce.Interval = TimeSpan.FromMilliseconds(400);
                _debounce.IsRepeating = false;
                _debounce.Tick += (_, _) => Changed?.Invoke();
            }
            _debounce.Stop();
            _debounce.Start();
        });
    }

    public void Dispose() => _watcher?.Dispose();
}
