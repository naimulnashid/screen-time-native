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
/// Renaming an app, choosing its colour, giving it a logo, and on By App
/// hiding it or merging it into another - all from the "..." beside its name.
/// </summary>
/// <remarks>
/// <para>A rename is stored in the database (it is part of the history, so it
/// is backed up and survives a reset), covers every path that resolves to the
/// app, and keeps the original name's colour and logo. The same rules as the
/// web dashboard's route: a name another app shows is refused (they would
/// share a colour and a logo), as is Other; an empty name clears the rename.</para>
/// <para>A colour is stored beside the renames (<see cref="ColorOverrides"/>)
/// and wins over the brand and palette colours everywhere the app is drawn.
/// Picked on the spectrum or typed as a hex code; "Default colour" drops it.</para>
/// <para>Rename and colour share one dialog. Until 0.2.0 it was a flyout under
/// a pencil that also carried the logo buttons; the logo now has its own items
/// in the menu, as in Data Usage Native.</para>
/// <para>A logo is a file in the data folder's logos\, named after the app. Set
/// logo copies one in; dropping an image on the name does the same.</para>
/// <para>Hiding and merging: see Naming/AppPrefs.cs. Hiding is By App's view
/// only; a merge is read everywhere and undone from the target's Merged apps.</para>
/// </remarks>
public static class AppActions
{
    /// <summary>
    /// The rename-and-recolour dialog. Modal, so the table or grid behind it
    /// keeps its shape; a refused name or colour keeps it open with the reason.
    /// </summary>
    public static async Task EditAsync(PageContext ctx, string key, string name, string baseName)
    {
        if (ctx.Window.Content?.XamlRoot is not { } root) return;
        var stack = new StackPanel { Width = 320, Spacing = 8 };
        stack.Children.Add(Ui.Text("Name", 13.5, 600, Palette.TextMutedBrush));
        var box = new TextBox { Text = name, MaxLength = Renames.MaxLength, FontFamily = Fonts.Sans, FontSize = 15, PlaceholderText = baseName, SelectionHighlightColor = Palette.AccentBrush };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Display name");
        stack.Children.Add(box);
        if (name != baseName) stack.Children.Add(Ui.Text($"Originally \"{baseName}\". Clear the box to restore it.", 13, 400, Palette.TextFaintBrush, wrap: true));

        // ---- Colour: the stored hex (override included), on Windows' own
        // picker, whose hex box is where a brand's exact code is typed.
        var original = AppColors.Of(ctx.State.Colors, name).ToLowerInvariant();
        var custom = ctx.State.ColorOverrides.ContainsKey(key);
        var label = Ui.Text("Chart colour", 13.5, 600, Palette.TextMutedBrush);
        label.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(label);
        var picker = new ColorPicker
        {
            Color = Palette.HexOrOther(original),
            IsAlphaEnabled = false,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
            IsMoreButtonVisible = false,
            IsColorSliderVisible = true,
            // The swatch beside the name already previews it.
            IsColorPreviewVisible = false,
            ColorSpectrumShape = ColorSpectrumShape.Box,
            Width = 320,
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, $"Colour for {name}");
        stack.Children.Add(picker);
        var error = Ui.Text("", 13, 500, Palette.WarnBrush, wrap: true);
        error.Visibility = Visibility.Collapsed;
        stack.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = "Rename or recolour",
            Content = stack,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            // Only offered when there is an override to drop.
            SecondaryButtonText = custom ? "Default colour" : "",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
            RequestedTheme = Palette.IsLight ? ElementTheme.Light : ElementTheme.Dark,
        };
        Ui.Accented(dialog);
        var saved = false;
        dialog.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };

        // Saves what changed: the name, then the colour. Null when it all went
        // in, else the reason to show.
        async Task<string?> Save(bool defaultColour)
        {
            if (box.Text.Trim() != name)
            {
                var text = box.Text;
                var (err, _) = await Task.Run(() => Rename(ctx, key, text, baseName));
                if (err != RenameError.None) return Renames.Message(err);
            }
            var hex = defaultColour ? "" : ToHex(picker.Color);
            if (defaultColour || hex != original)
            {
                var (err, _) = await Task.Run(() => Recolor(ctx, key, hex));
                if (err != ColorError.None) return ColorOverrides.Message(err);
            }
            saved = true;
            return null;
        }
        async void OnButton(ContentDialogButtonClickEventArgs args, bool defaultColour)
        {
            var deferral = args.GetDeferral();
            try
            {
                if (await Save(defaultColour) is { } message)
                {
                    error.Text = message;
                    error.Visibility = Visibility.Visible;
                    args.Cancel = true;
                }
            }
            finally { deferral.Complete(); }
        }
        dialog.PrimaryButtonClick += (_, args) => OnButton(args, false);
        dialog.SecondaryButtonClick += (_, args) => OnButton(args, true);
        await dialog.ShowAsync();
        if (saved) await ctx.State.ReloadAsync();
    }

    private static (RenameError, string?) Rename(PageContext ctx, string key, string requested, string baseName)
    {
        var current = ctx.State.Queries!.AppNamesByKey();
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
    /// The "..." beside an app's name: rename or recolour it, set or remove its
    /// logo, and on By App (where <paramref name="candidates"/> is given) hide
    /// it from that page or merge it into another.
    /// </summary>
    public static Button Menu(PageContext ctx, string key, string name, string baseName, bool hidden = false,
        IReadOnlyList<(string Key, string Name)>? candidates = null, bool alwaysVisible = false, double size = 14)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = size, Foreground = Palette.TextFaintBrush },
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
        ToolTipService.SetToolTip(button, Ui.TipContent(name != baseName ? $"Rename, recolour or set a logo (originally {baseName})" : "Rename, recolour or set a logo"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"More for {name}");
        // Reachable by keyboard even while invisible.
        button.GotFocus += (_, _) => button.Opacity = 1;

        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var edit = new MenuFlyoutItem { Text = "Rename or recolour…", Icon = new FontIcon { Glyph = "" } };
        edit.Click += async (_, _) => await EditAsync(ctx, key, name, baseName);
        menu.Items.Add(edit);
        menu.Items.Add(new MenuFlyoutSeparator());

        var setLogo = new MenuFlyoutItem { Text = "Set logo…", Icon = new FontIcon { Glyph = "" } };
        setLogo.Click += async (_, _) => await PickLogo(ctx, name);
        menu.Items.Add(setLogo);
        var removeLogo = new MenuFlyoutItem { Text = "Remove logo", Icon = new FontIcon { Glyph = "" } };
        removeLogo.Click += (_, _) =>
        {
            try { ctx.State.Logos.RemoveLogo(name); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = Shell.ShowMessage(ctx.Window, "Could not remove the logo", ex.Message); }
        };
        menu.Items.Add(removeLogo);
        // Only a file in the logos folder can be removed: a built-in alias or
        // a colour swatch has nothing to delete.
        menu.Opening += (_, _) => removeLogo.IsEnabled = ctx.State.Logos.HasOwnLogo(name);

        if (candidates is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var hide = new MenuFlyoutItem
            {
                Text = hidden ? "Show on this page" : "Hide from this page",
                Icon = new FontIcon { Glyph = hidden ? "" : "" },
            };
            hide.Click += async (_, _) =>
            {
                await Task.Run(() =>
                {
                    using var db = OpenForWrite(ctx);
                    AppPrefs.SetHidden(db, key, !hidden);
                });
                await ctx.State.ReloadAsync();
            };
            menu.Items.Add(hide);
            var others = candidates.Where(c => c.Key != key).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var merge = new MenuFlyoutItem { Text = "Merge into…", Icon = new FontIcon { Glyph = "" }, IsEnabled = others.Count > 0 };
            merge.Click += (_, _) => MergeFlyout(ctx, key, name, others).ShowAt(button);
            menu.Items.Add(merge);
        }
        button.Flyout = Ui.Menu(menu);
        return button;
    }

    private static Flyout MergeFlyout(PageContext ctx, string key, string name, IReadOnlyList<(string Key, string Name)> others)
    {
        var stack = new StackPanel { Width = 300, Spacing = 10 };
        stack.Children.Add(Ui.Text($"Count {name} as part of", 14, 600, wrap: true));
        // A list in the flyout, not a ComboBox: a ComboBox's drop-down is a
        // popup opened from this flyout's popup, and scrolling it with a long
        // list hung the UI thread at full CPU (measured on 162 apps, 0.2.1).
        var box = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 280,
            FontFamily = Fonts.Sans,
            BorderBrush = Palette.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(0, 4, 0, 4),
        };
        box.Resources["ListViewItemBackgroundPointerOver"] = Palette.MenuHoverBrush;
        box.Resources["ListViewItemBackgroundPressed"] = Palette.MenuPressedBrush;
        box.Resources["ListViewItemBackgroundSelected"] = Palette.AccentDimBrush;
        box.Resources["ListViewItemBackgroundSelectedPointerOver"] = Palette.AccentDimBrush;
        box.Resources["ListViewItemBackgroundSelectedPressed"] = Palette.AccentDimBrush;
        foreach (var state in new[] { "", "PointerOver", "Pressed" })
            box.Resources[$"ListViewItemSelectionIndicator{state}Brush"] = Palette.AccentBrush;
        foreach (var o in others) box.Items.Add(o.Name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, $"App to merge {name} into");
        stack.Children.Add(box);
        stack.Children.Add(Ui.Text("Its time and opens are then shown as that app's on every page. Separate it again from that app's Merged apps.", 12.5, 400, Palette.TextFaintBrush, wrap: true));
        var error = Ui.Text("", 13, 500, Palette.WarnBrush, wrap: true);
        error.Visibility = Visibility.Collapsed;
        stack.Children.Add(error);
        var go = Ui.Button("Merge", primary: true, fontSize: 14, padding: new Thickness(16, 7, 16, 7));
        go.HorizontalAlignment = HorizontalAlignment.Right;
        go.IsEnabled = false;
        box.SelectionChanged += (_, _) => go.IsEnabled = box.SelectedIndex >= 0;
        stack.Children.Add(go);
        var flyout = new Flyout { Content = stack, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        go.Click += async (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            var into = others[box.SelectedIndex].Key;
            var err = await Task.Run(() => Merge(ctx, key, into));
            if (err != MergeError.None)
            {
                error.Text = AppPrefs.Message(err);
                error.Visibility = Visibility.Visible;
                return;
            }
            flyout.Hide();
            await ctx.State.ReloadAsync();
        };
        return flyout;
    }

    /// <summary>Merges, or separates again with <paramref name="into"/> null.</summary>
    private static MergeError Merge(PageContext ctx, string key, string? into)
    {
        var known = ctx.State.Queries!.AppKeys();
        using var db = OpenForWrite(ctx);
        return AppPrefs.SetMerge(db, key, into, known);
    }

    /// <summary>Separate: undo a merge, from the target's Merged apps card.</summary>
    public static Button Separate(PageContext ctx, string key, string name)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14, Foreground = Palette.TextMutedBrush },
            Padding = new Thickness(7),
            MinWidth = 0,
            MinHeight = 0,
            CornerRadius = new CornerRadius(15),
            Background = Palette.TransparentBrush,
            BorderBrush = Palette.BorderBrightBrush,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Resources["ButtonBackgroundPointerOver"] = Palette.SurfaceHoverBrush;
        button.Resources["ButtonBorderBrushPointerOver"] = Palette.AccentBrush;
        ToolTipService.SetToolTip(button, Ui.TipContent($"Separate: show {name} as an app of its own again"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Separate {name}");
        button.Click += async (_, _) =>
        {
            var err = await Task.Run(() => Merge(ctx, key, null));
            if (err != MergeError.None) { _ = Shell.ShowMessage(ctx.Window, "Could not separate", AppPrefs.Message(err)); return; }
            await ctx.State.ReloadAsync();
        };
        return button;
    }

    /// <summary>
    /// A name cell: the mark, the name (a link when the app earns a page), a
    /// "system" badge for a Windows component, a "hidden" badge when listed
    /// again, and the "..." on hover.
    /// </summary>
    /// <remarks>
    /// A Grid, not a horizontal StackPanel: a StackPanel offers its children
    /// infinite width, so a long name never trims and widens the table
    /// instead. Here the name's star column takes what the mark, badges and
    /// menu leave, trims to an ellipsis, and the tooltip carries it whole.
    /// Left-aligned (as the table sets it), the Grid still sizes to its
    /// content, so a short name keeps its badges and menu beside it.
    /// </remarks>
    public static Grid NameCell(PageContext ctx, string key, string name, string baseName, bool system, bool detailed, double size = 15.5,
        bool hidden = false, IReadOnlyList<(string Key, string Name)>? candidates = null)
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
        if (hidden) Add(Ui.Badge("hidden", size: 11.2, padding: new Thickness(7, 1.5, 7, 2)));
        var menu = Menu(ctx, key, name, baseName, hidden, candidates);
        Add(menu);
        cell.PointerEntered += (_, _) => menu.Opacity = 1;
        cell.PointerExited += (_, _) => { if (menu.FocusState == FocusState.Unfocused) menu.Opacity = 0; };
        AcceptLogoDrop(ctx, cell, name);
        return cell;
    }
}
