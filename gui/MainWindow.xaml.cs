using LTOG.Gui.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LTOG.Gui;

/// <summary>
/// The app shell: title bar with page switchers, the drive tab strip, and the
/// page host. Pages are self-contained components in Pages/.
/// </summary>
public sealed partial class MainWindow : Window
{
    private string _page = "drives";   // title-bar switcher: drives, log, index, about, settings
    private readonly Dictionary<string, FrameworkElement> _drivePages = new();   // by device

    public MainWindow()
    {
        InitializeComponent();
        MainPages.SelectedItem = MainPages.Items[0];   // Drives; not in XAML, it'd fire before Tabs exists
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        Root.ActualThemeChanged += (_, _) => SyncCaptionColors();
        foreach (var page in PagesHost.Children) AddShowTransition(page);

        RestoreWindowBounds();
        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
        if (File.Exists(ico))
            AppWindow.SetIcon(ico);
        Closed += (_, _) => SaveWindowBounds();

        if (!App.EnvOk)
        {
            EnvBar.Message = "ltfs.exe / ltfs.conf not found. Place the WinLtfs engine in a " +
                             "winltfs\\ subfolder next to this app, or set \"DistPath\" in " +
                             Settings.FilePath;
            EnvBar.IsOpen = true;
            EnvBar.Visibility = Visibility.Visible;
        }

        // one tab + page per drive, following the store
        string lastTag = App.Settings.LastTab;   // before drive tabs auto-select the first drive
        App.DriveStore.Drives.CollectionChanged += (_, _) => SyncDriveTabs();
        App.DriveStore.Start();   // the first drive scan runs synchronously: tabs exist below

        // restore last selected drive tab
        var lastTab = Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(i => (string?)i.Tag == lastTag);
        if (lastTab != null)
        {
            Tabs.SelectedItem = lastTab;
            ShowPage();
        }

        Root.Loaded += async (_, _) =>
        {
            SyncCaptionColors();
            // start focus on the tab, not inside a drive page (would scroll it to the first button)
            (Tabs.SelectedItem as Control)?.Focus(FocusState.Programmatic);
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
            {
                double scale = Root.XamlRoot.RasterizationScale;
                // ponytail: TitleBar (WinAppSDK 2.2) reserves RightInset as DIPs though it's physical px,
                // leaving a gap before the caption buttons at >100% scale; drop this if the control is fixed
                TitleButtons.Margin = new Thickness(0, 0, -AppWindow.TitleBar.RightInset * (1 - 1 / scale), 0);
                op.PreferredMinimumWidth = (int)((640 + 32) * scale);
                op.PreferredMinimumHeight = (int)(600 * scale);
                op.PreferredMaximumWidth = (int)(1000 * scale);
            }
            if (App.AutoRemount)
                await App.DriveStore.RemountPersistedAsync();
        };
    }

    // ------------------------------------------------------------ window bounds

    private void RestoreWindowBounds()
    {
        if (App.Settings is { WindowWidth: int w, WindowHeight: int h, WindowX: int x, WindowY: int y }
            && w >= 600 && h >= 400)
        {
            var rect = new Windows.Graphics.RectInt32(x, y, w, h);
            // only restore a position that still intersects a display
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromRect(
                rect, Microsoft.UI.Windowing.DisplayAreaFallback.None);
            if (area != null)
            {
                AppWindow.MoveAndResize(rect);
                return;
            }
            AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
            return;
        }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 720));   // 16:9 default
    }

    private void SaveWindowBounds()
    {
        try
        {
            // don't persist a maximized/minimized rect
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op &&
                op.State != Microsoft.UI.Windowing.OverlappedPresenterState.Restored)
                return;
            App.Settings.WindowX = AppWindow.Position.X;
            App.Settings.WindowY = AppWindow.Position.Y;
            App.Settings.WindowWidth = AppWindow.Size.Width;
            App.Settings.WindowHeight = AppWindow.Size.Height;
            App.Settings.Save();
        }
        catch { /* best effort */ }
    }

    // ------------------------------------------------------------ navigation

    /// <summary>Caption buttons are system-drawn: give them the same colour active or not.</summary>
    private void SyncCaptionColors()
    {
        var fg = Root.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        AppWindow.TitleBar.ButtonForegroundColor = fg;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = fg;
    }

    /// <summary>Fade + slide in from the right whenever a page becomes visible (pages switch by Visibility).</summary>
    private static void AddShowTransition(UIElement page)
    {
        var c = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(page).Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new(0.1f, 0.9f), new(0.2f, 1f));   // decelerate
        var duration = TimeSpan.FromMilliseconds(250);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = duration;
        var slide = c.CreateVector3KeyFrameAnimation();
        slide.Target = "Translation";
        slide.InsertKeyFrame(0, new System.Numerics.Vector3(32, 0, 0));
        slide.InsertKeyFrame(1, System.Numerics.Vector3.Zero, ease);
        slide.Duration = duration;
        var show = c.CreateAnimationGroup();
        show.Add(fade);
        show.Add(slide);
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(page, true);
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetImplicitShowAnimation(page, show);
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowPage();
        var tag = (Tabs.SelectedItem as TabViewItem)?.Tag as string;
        if (tag != null && App.Settings.LastTab != tag)
        {
            App.Settings.LastTab = tag;
            App.Settings.Save();
        }
    }

    /// <summary>Title-bar switchers: the left and right SelectorBars behave as one group.</summary>
    private void Page_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not { } item) return;   // cleared by the other bar
        (sender == MainPages ? TitleButtons : MainPages).SelectedItem = null;
        // SelectorBarItem's selected+hover state falls back to the item's own Background, so
        // put the selected fill there too or it vanishes under the pointer
        foreach (var i in MainPages.Items.Concat(TitleButtons.Items))
        {
            if (i == item) i.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
            else i.ClearValue(Control.BackgroundProperty);
        }
        _page = (string)item.Tag;
        if (_page == "index") IndexesView.RefreshSchemas();
        if (_page == "about") _ = AboutView.LoadAsync();
        ShowPage();
    }

    /// <summary>Show the current page; on Tape Drives, the selected drive's page (or the no-drives hint).</summary>
    private void ShowPage()
    {
        static Visibility Vis(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
        bool drives = _page == "drives";
        var dev = (Tabs.SelectedItem as TabViewItem)?.Tag as string;
        foreach (var (d, page) in _drivePages) page.Visibility = Vis(drives && d == dev);
        NoDrivesView.Visibility = Vis(drives && dev == null);
        TabStrip.Visibility = Vis(drives && Tabs.TabItems.Count > 1);
        LogView.Visibility = Vis(_page == "log");
        IndexesView.Visibility = Vis(_page == "index");
        AboutView.Visibility = Vis(_page == "about");
        SettingsView.Visibility = Vis(_page == "settings");
    }

    /// <summary>One tab + page per vm, in TAPE0..9 order; stale ones removed.</summary>
    private void SyncDriveTabs()
    {
        foreach (var item in Tabs.TabItems.OfType<TabViewItem>().ToList())
        {
            var dev = (string)item.Tag;
            if (App.DriveStore.Drives.Any(s => s.Drive.Device == dev)) continue;
            if (ReferenceEquals(Tabs.SelectedItem, item)) Tabs.SelectedItem = null;
            Tabs.TabItems.Remove(item);
            PagesHost.Children.Remove(_drivePages[dev]);
            _drivePages.Remove(dev);
        }
        foreach (var vm in App.DriveStore.Drives)
        {
            var dev = vm.Drive.Device;
            if (_drivePages.ContainsKey(dev)) continue;
            var page = new DrivePage(vm) { Visibility = Visibility.Collapsed };
            _drivePages[dev] = page;
            AddShowTransition(page);
            PagesHost.Children.Add(page);
            int at = Tabs.TabItems.OfType<TabViewItem>()
                .Count(i => string.CompareOrdinal((string)i.Tag, dev) < 0);
            Tabs.TabItems.Insert(at, new TabViewItem
            {
                Header = vm.Drive.TabTitle,
                Tag = dev,
                IsClosable = false,
                IconSource = new FontIconSource { Glyph = "" },   // generic drive (Segoe "HardDrive")
            });
        }
        Tabs.SelectedItem ??= Tabs.TabItems.FirstOrDefault();
        // a collapsed (never templated) TabView doesn't raise SelectionChanged, so don't rely on it
        ShowPage();
    }
}
