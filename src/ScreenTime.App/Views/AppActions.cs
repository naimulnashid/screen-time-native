using ScreenTime.App.Controls;
using ScreenTime.App.Theme;
using ScreenTime.Core;
using ScreenTime.Core.Data;
using ScreenTime.Core.Naming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace ScreenTime.App.Views;

/// <summary>
/// Renaming an app, choosing its colour and giving it a logo, from wherever its
/// name is shown.
/// </summary>
/// <remarks>
/// <para>A rename is stored in the database (it is part of the history, so it
/// is backed up and survives a reset), covers every path that resolves to the app, and keeps the
/// original name's colour and logo. The same rules as the web dashboard's
/// route: a name another app shows is refused (they would share a colour and a
/// logo), as is Other; an empty name clears the rename.</para>
/// <para>A colour is stored beside the renames (<see cref="ColorOverrides"/>)
/// and wins over the brand and palette colours everywhere the app is drawn.
/// Picked on the spectrum or typed as a hex code; "Default colour" drops it.</para>
/// <para>A logo is a file in the data folder's logos\, named after the app. Set
/// logo copies one in; dropping an image on the name does the same.</para>
/// </remarks>
public static class AppActions
{
    /// <summary>The pencil beside a name, faint until the pointer is near.</summary>
    public static Button Pencil(PageContext ctx, string key, string name, string baseName, double size = 14, bool alwaysVisible = false)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = size, Foreground = Palette.TextFaintBrush },
            Padding = new Thickness(5),
            MinWidth = 0,
            MinHeight = 0,
            Background = Palette.TransparentBrush,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = alwaysVisible ? 1 : 0,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) },
        };
        button.Resources["ButtonBackgroundPointerOver"] = Palette.SurfaceHoverBrush;
        button.Resources["ButtonBackgroundPressed"] = Palette.SurfaceHoverBrush;
        ToolTipService.SetToolTip(button, Ui.TipContent($"Rename {name}, or set its colour or logo"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Rename {name}");
        // Built on click, not with the row: a colour picker per table row is
        // a lot of controls nobody opened, and a fresh one reads the current
        // name and colour.
        button.Click += (_, _) => Editor(ctx, key, name, baseName).ShowAt(button);
        // Reachable by keyboard even while invisible.
        button.GotFocus += (_, _) => button.Opacity = 1;
        return button;
    }

    private static Flyout Editor(PageContext ctx, string key, string name, string baseName)
    {
        var stack = new StackPanel { Width = 300, Spacing = 10 };
        stack.Children.Add(Ui.Text("Display name", 14, 600));
        var box = new TextBox { Text = name, MaxLength = Renames.MaxLength, FontFamily = Fonts.Sans, FontSize = 15 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Display name");
        stack.Children.Add(box);
        var error = Ui.Text("", 13, 500, Palette.WarnBrush, wrap: true);
        error.Visibility = Visibility.Collapsed;
        stack.Children.Add(error);
        if (name != baseName) stack.Children.Add(Ui.Text($"Originally \"{baseName}\". Clear the box to restore it.", 13, 400, Palette.TextFaintBrush, wrap: true));

        // ---- Colour: the stored hex (override included), on Windows' own
        // picker, whose hex box is where a brand's exact code is typed.
        var original = AppColors.Of(ctx.State.Colors, name).ToLowerInvariant();
        var custom = ctx.State.ColorOverrides.ContainsKey(key);
        var colors = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 0), BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0) };
        colors.Children.Add(Ui.Text("Colour", 14, 600));
        var picker = new ColorPicker
        {
            Color = Palette.HexOrOther(original),
            IsAlphaEnabled = false,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
            IsMoreButtonVisible = false,
            IsColorSliderVisible = true,
            // The swatch beside the name already previews it, and the flyout is
            // 300 wide: the default picker is half as wide again and pushes Save
            // below a short window.
            IsColorPreviewVisible = false,
            ColorSpectrumShape = ColorSpectrumShape.Box,
            Width = 300,
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, $"Colour for {name}");
        colors.Children.Add(picker);
        var reset = Ui.Button("Default colour", fontSize: 14, padding: new Thickness(14, 7, 14, 7));
        reset.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        Ui.SetTip(reset, "Back to the brand or palette colour");
        colors.Children.Add(reset);
        stack.Children.Add(colors);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var save = Ui.Button("Save", primary: true, fontSize: 14, padding: new Thickness(16, 7, 16, 7));
        buttons.Children.Add(save);
        stack.Children.Add(buttons);

        var logos = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 0), BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0) };
        logos.Children.Add(Ui.Text("Logo", 14, 600));
        var logoRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var set = Ui.Button("Set logo…", fontSize: 14, padding: new Thickness(14, 7, 14, 7));
        var remove = Ui.Button("Remove logo", fontSize: 14, padding: new Thickness(14, 7, 14, 7));
        logoRow.Children.Add(set);
        logoRow.Children.Add(remove);
        logos.Children.Add(logoRow);
        logos.Children.Add(Ui.Text("Or drop an image onto the app's name. SVG, PNG, JPG, WebP, GIF, BMP or ICO.", 12.5, 400, Palette.TextFaintBrush, wrap: true));
        stack.Children.Add(logos);

        var flyout = new Flyout { Content = stack, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        flyout.Opening += (_, _) =>
        {
            remove.IsEnabled = ctx.State.Logos.HasOwnLogo(name);
            error.Visibility = Visibility.Collapsed;
        };
        flyout.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };

        void Fail(string message)
        {
            error.Text = message;
            error.Visibility = Visibility.Visible;
        }

        // Saves what changed: the name, then the colour.
        async void Save()
        {
            if (box.Text.Trim() != name)
            {
                var text = box.Text;
                var (err, _) = await Task.Run(() => Rename(ctx, key, text, baseName));
                if (err != RenameError.None) { Fail(Renames.Message(err)); return; }
            }
            var hex = ToHex(picker.Color);
            if (hex != original)
            {
                var (err, _) = await Task.Run(() => Recolor(ctx, key, hex));
                if (err != ColorError.None) { Fail(ColorOverrides.Message(err)); return; }
            }
            flyout.Hide();
            await ctx.State.ReloadAsync();
        }
        reset.Click += async (_, _) =>
        {
            var (err, _) = await Task.Run(() => Recolor(ctx, key, ""));
            if (err != ColorError.None) { Fail(ColorOverrides.Message(err)); return; }
            flyout.Hide();
            await ctx.State.ReloadAsync();
        };
        save.Click += (_, _) => Save();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            Save();
        };
        set.Click += async (_, _) =>
        {
            flyout.Hide();
            await PickLogo(ctx, name);
        };
        remove.Click += (_, _) =>
        {
            flyout.Hide();
            try { ctx.State.Logos.RemoveLogo(name); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = Shell.ShowMessage(ctx.Window, "Could not remove the logo", ex.Message); }
        };
        return flyout;
    }

    private static (RenameError, string?) Rename(PageContext ctx, string key, string requested, string baseName)
    {
        var current = ctx.State.Queries!.AppNamesByKey().ToDictionary(kv => kv.Key, kv => kv.Value.Name);
        using var db = OpenForWrite(ctx);
        return Renames.Save(db, key, requested, current, baseName);
    }

    /// <summary>Stores a colour, or clears it with an empty string.</summary>
    private static (ColorError, string?) Recolor(PageContext ctx, string key, string requested)
    {
        var known = ctx.State.Queries!.AppNamesByKey().Keys.ToHashSet();
        using var db = OpenForWrite(ctx);
        return ColorOverrides.Save(db, key, requested, known);
    }

    private static Microsoft.Data.Sqlite.SqliteConnection OpenForWrite(PageContext ctx)
    {
        var dir = ctx.State.DataDir ?? throw new InvalidOperationException("no data folder");
        return UsageDb.Open(AppPaths.DatabasePath(dir), AppPaths.ReadLocation().AllowSystemDrive || Environment.GetEnvironmentVariable(AppPaths.DataDirVariable) is { Length: > 0 });
    }

    private static string ToHex(Windows.UI.Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    public static async Task PickLogo(PageContext ctx, string name)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        foreach (var ext in AppIcons.Renderable) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(ctx.Window));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        SetLogo(ctx, name, file.Path);
    }

    private static void SetLogo(PageContext ctx, string name, string path)
    {
        try { ctx.State.Logos.SetLogo(name, path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = Shell.ShowMessage(ctx.Window, "Could not set the logo", ex.Message); }
    }

    /// <summary>Lets an image be dropped onto an element to become the app's logo.</summary>
    public static void AcceptLogoDrop(PageContext ctx, UIElement target, string name)
    {
        target.AllowDrop = true;
        target.DragOver += (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = $"Set as the logo for {name}";
        };
        target.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.OfType<StorageFile>().FirstOrDefault(f => AppIcons.Renderable.Contains(Path.GetExtension(f.Path))) is { } file)
                SetLogo(ctx, name, file.Path);
        };
    }

    /// <summary>
    /// A name cell: the mark, the name (a link when the app earns a page), a
    /// "system" badge for a Windows component, and the pencil on hover.
    /// </summary>
    /// <remarks>
    /// A Grid, not a horizontal StackPanel: a StackPanel offers its children
    /// infinite width, so a long name never trims and widens the table
    /// instead. Here the name's star column takes what the mark, badge and
    /// pencil leave, trims to an ellipsis, and the tooltip carries it whole.
    /// Left-aligned (as the table sets it), the Grid still sizes to its
    /// content, so a short name keeps its badge and pencil beside it.
    /// </remarks>
    public static Grid NameCell(PageContext ctx, string key, string name, string baseName, bool system, bool detailed, double size = 15.5)
    {
        var state = ctx.State;
        var cell = new Grid { ColumnSpacing = 9.6, Background = Palette.TransparentBrush };
        void Add(FrameworkElement part, GridUnitType unit = GridUnitType.Auto)
        {
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, unit) });
            part.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(part, cell.Children.Count);
            cell.Children.Add(part);
        }
        Add(AppIconView.Create(name, state.Colors, state.Icons, 18));
        if (detailed)
        {
            var link = Ui.Link(name, () => ctx.Navigate(new Route(PageKind.App, key)), size, Palette.TextBrush);
            Ui.SetTip(link, $"{name} - open detail");
            Add(link, GridUnitType.Star);
        }
        else
        {
            var text = Ui.Text(name, size);
            Ui.SetTip(text, $"{name} - under a minute recorded, too little for a detail page");
            Add(text, GridUnitType.Star);
        }
        if (system) Add(Ui.Badge("system", size: 11.2, padding: new Thickness(7, 1.5, 7, 2)));
        var pencil = Pencil(ctx, key, name, baseName);
        Add(pencil);
        cell.PointerEntered += (_, _) => pencil.Opacity = 1;
        cell.PointerExited += (_, _) => { if (pencil.FocusState == FocusState.Unfocused) pencil.Opacity = 0; };
        AcceptLogoDrop(ctx, cell, name);
        return cell;
    }
}
