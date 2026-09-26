using System.Collections.ObjectModel;
using System.Diagnostics;
using LTOG.Gui.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;

namespace LTOG.Gui;

public sealed partial class MainWindow : Window
{
    /// <summary>One tab per detected tape drive.</summary>
    public ObservableCollection<DriveSlot> Slots { get; } = new();

    /// <summary>Structured log of every LTFS/WinFsp/tape-drive invocation (Log page).</summary>
    public ActivityLog Activity { get; }

    /// <summary>Active mounts (logic only; the UI lives in the slots).</summary>
    private readonly List<Mapping> _mappings = new();

    private readonly Settings _settings = Settings.Load();
    private readonly MountManager _mounts = new();
    /// <summary>Sole source of drive state: poll + publish. Nothing else touches the device.</summary>
    private readonly TapeMonitor _monitor;
    private readonly List<Window> _childWindows = new();
    private string _page = "drives";   // title-bar switcher: drives, log, index, about, settings
    private bool _loadingUi;
    private bool _utilityRunning;
    private bool _envOk = true;
    private readonly Dictionary<string, FrameworkElement> _drivePages = new();   // by device

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public MainWindow()
    {
        // Created before InitializeComponent so the Log page's x:Bind sees it,
        // and on the UI thread so it can resolve its theme brushes.
        Activity = new ActivityLog(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());

        InitializeComponent();
        MainPages.SelectedItem = MainPages.Items[0];   // Drives; not in XAML, it'd fire before Tabs exists
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        Root.ActualThemeChanged += (_, _) => SyncCaptionColors();
        foreach (var page in PagesHost.Children) AddShowTransition(page);

        // Auto-scroll the log to the newest entry as activity streams in.
        Activity.Updated += () =>
        {
            if (AutoScrollCheck?.IsChecked == true)
                LogScroll?.ChangeView(null, double.MaxValue, null, true);
        };
        RestoreWindowBounds();
        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
        if (File.Exists(ico))
            AppWindow.SetIcon(ico);
        Closed += (_, _) => SaveWindowBounds();

        if (!LtfsEnv.Resolve(_settings.DistPath))
        {
            _envOk = false;
            EnvBar.Message = "ltfs.exe / ltfs.conf not found. Place the WinLtfs engine in a " +
                             "winltfs\\ subfolder next to this app, or set \"DistPath\" in " +
                             Settings.FilePath;
            EnvBar.IsOpen = true;
            EnvBar.Visibility = Visibility.Visible;
        }
        ApplySettingsToUi();
        string lastTag = _settings.LastTab;   // before drive tabs auto-select the first drive

        // ---- drive state subscriptions: the only way the UI learns about the hardware
        _monitor = new TapeMonitor(Activity);
        _monitor.DriveStateChanged += (dev, state) =>
        {
            var slot = SlotFor(dev);
            if (state != null && slot != null) { slot.State = state; return; }
            if (state == null) Slots.Remove(slot!);
            else
            {
                slot = new DriveSlot { State = state, GlobalEnabled = _envOk && !_utilityRunning };
                slot.SetLetters(_monitor.FreeLetters);
                Slots.Add(slot);
                TryAdopt(slot);   // before its first cartridge read, which is later in the same pass
            }
            SyncDriveTabs();
        };
        _monitor.FreeLettersChanged += letters =>
        {
            foreach (var s in Slots.Where(s => s.Phase == SlotPhase.Idle)) s.SetLetters(letters);
        };
        _monitor.Start();   // the first drive scan runs synchronously: tabs exist below

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
                await RemountPersistedAsync();
        };
    }

    // ------------------------------------------------------------ window bounds

    private void RestoreWindowBounds()
    {
        if (_settings is { WindowWidth: int w, WindowHeight: int h, WindowX: int x, WindowY: int y }
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
            _settings.WindowX = AppWindow.Position.X;
            _settings.WindowY = AppWindow.Position.Y;
            _settings.WindowWidth = AppWindow.Size.Width;
            _settings.WindowHeight = AppWindow.Size.Height;
            _settings.Save();
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

    private void PagesHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Deterministic page container: fill to 960px, centered.
        double page = Math.Max(320, Math.Min(960, e.NewSize.Width - 32)); // 16px gutters
        IndexContent.Width = page;
        SettingsContent.Width = page;
        AboutContent.Width = page;
        LogPanel.Width = page;
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowPage();
        var tag = (Tabs.SelectedItem as TabViewItem)?.Tag as string;
        if (tag != null && _settings.LastTab != tag)
        {
            _settings.LastTab = tag;
            _settings.Save();
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
        if (_page == "index") RefreshSchemas();
        if (_page == "about") _ = LoadAboutInfoAsync();
        ShowPage();
    }

    /// <summary>Show the current page; on Tape Drives, the selected drive's page (or the no-drives hint).</summary>
    private void ShowPage()
    {
        static Visibility Vis(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
        bool drives = _page == "drives";
        var dev = (Tabs.SelectedItem as TabViewItem)?.Tag as string;
        foreach (var (d, page) in _drivePages) page.Visibility = Vis(drives && d == dev);
        NoDrivesText.Visibility = Vis(drives && dev == null);
        TabStrip.Visibility = Vis(drives && Tabs.TabItems.Count > 1);
        LogPanel.Visibility = Vis(_page == "log");
        IndexPanel.Visibility = Vis(_page == "index");
        AboutPanel.Visibility = Vis(_page == "about");
        SettingsPanel.Visibility = Vis(_page == "settings");
    }

    /// <summary>One tab + page per slot, in TAPE0..9 order; stale ones removed.</summary>
    private void SyncDriveTabs()
    {
        foreach (var item in Tabs.TabItems.OfType<TabViewItem>().ToList())
        {
            var dev = (string)item.Tag;
            if (Slots.Any(s => s.Drive.Device == dev)) continue;
            if (ReferenceEquals(Tabs.SelectedItem, item)) Tabs.SelectedItem = null;
            Tabs.TabItems.Remove(item);
            PagesHost.Children.Remove(_drivePages[dev]);
            _drivePages.Remove(dev);
        }
        foreach (var slot in Slots)
        {
            var dev = slot.Drive.Device;
            if (_drivePages.ContainsKey(dev)) continue;
            var page = new ContentControl
            {
                Content = slot,
                ContentTemplate = (DataTemplate)Root.Resources["DrivePage"],
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                IsTabStop = false,
                Visibility = Visibility.Collapsed,
            };
            _drivePages[dev] = page;
            AddShowTransition(page);
            PagesHost.Children.Add(page);
            int at = Tabs.TabItems.OfType<TabViewItem>()
                .Count(i => string.CompareOrdinal((string)i.Tag, dev) < 0);
            Tabs.TabItems.Insert(at, new TabViewItem
            {
                Header = slot.Drive.TabTitle,
                Tag = dev,
                IsClosable = false,
                IconSource = new FontIconSource { Glyph = "" },   // generic drive (Segoe "HardDrive")
            });
        }
        Tabs.SelectedItem ??= Tabs.TabItems.FirstOrDefault();
        // a collapsed (never templated) TabView doesn't raise SelectionChanged, so don't rely on it
        ShowPage();
    }

    // ------------------------------------------------------------ about

    private bool _aboutLoaded;

    private async Task LoadAboutInfoAsync()
    {
        if (_aboutLoaded) return;
        _aboutLoaded = true;

        try
        {
            var png = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.png");
            if (File.Exists(png))
                AboutIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(png));
        }
        catch { }

        var appVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        AboutVersionText.Text = appVer == null ? "v?" : $"v{appVer.Major}.{appVer.Minor}.{appVer.Build}";
        try
        {
            if (Environment.ProcessPath is { } exe)
                AboutBuildText.Text = $"Built {File.GetLastWriteTime(exe):yyyy-MM-dd HH:mm}";
        }
        catch { }

        try
        {
            string runtime = $".NET {Environment.Version}";
            // exact package version baked in at build time (the runtime DLLs'
            // version resources don't carry the real SDK version)
            var wasdk = System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .OfType<System.Reflection.AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "WindowsAppSDKVersion")?.Value;
            if (!string.IsNullOrEmpty(wasdk))
                runtime += $", Windows App SDK {wasdk}";
            AboutRuntimeText.Text = $"Runtime: {runtime}";
        }
        catch { AboutRuntimeText.Text = $"Runtime: .NET {Environment.Version}"; }

        if (LtfsEnv.DistPath == null)
        {
            AboutLtfsText.Text = "LTFS engine: dist folder not found";
            AboutWinFspText.Text = "WinFsp: dist folder not found";
            return;
        }

        try
        {
            var winfsp = Path.Combine(LtfsEnv.DistPath, "winfsp-x64.dll");
            if (File.Exists(winfsp))
            {
                // ProductVersion is the marketing year ("2025"); compose the
                // real version from the file-version parts instead.
                var v = FileVersionInfo.GetVersionInfo(winfsp);
                AboutWinFspText.Text = $"WinFsp: {v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}";
            }
            else
            {
                AboutWinFspText.Text = "WinFsp: winfsp-x64.dll not found in dist";
            }
        }
        catch (Exception ex) { AboutWinFspText.Text = $"WinFsp: ({ex.Message})"; }

        try
        {
            var result = await ToolRunner.RunAsync(LtfsEnv.LtfsExe, new[] { "-V" },
                Activity, LogKind.Tool, "ltfs --version (component probe)");
            var lines = result.Output;
            // ltfs -V prints "HPE StoreOpen Software version X (build N)".
            string engine = lines.FirstOrDefault(l => l.Contains("Software version"))?.Trim() ?? "unknown";
            engine = engine.Replace("Software version ", "");
            string spec = lines.FirstOrDefault(l => l.Contains("Format Specification"))?.Trim() ?? "";
            AboutLtfsText.Text = $"LTFS engine: {engine}" +
                                 (spec.Length > 0 ? $", {spec.Replace("LTFS Format Specification version", "format spec")}" : "");
        }
        catch (Exception ex) { AboutLtfsText.Text = $"LTFS engine: ({ex.Message})"; }
    }

    // ------------------------------------------------------------ ui state

    private void ApplySettingsToUi()
    {
        _loadingUi = true;
        EjectAfterUnmountCheck.IsChecked = _settings.EjectAfterUnmount;
        RemountCheck.IsChecked = _settings.RemountAtStartup;
        CaptureIndexCheck.IsChecked = _settings.CaptureIndex;
        WorkFolderBox.Text = _settings.WorkFolder;
        OverridePolicyCheck.IsChecked = _settings.OverrideSyncPolicy;
        PolicyDismountRadio.IsChecked = _settings.SyncPolicyMode == 0;
        PolicyPeriodicRadio.IsChecked = _settings.SyncPolicyMode == 1;
        PeriodBox.Value = _settings.SyncPeriodMinutes;
        SchemaSortCombo.SelectedIndex = Math.Clamp(_settings.IndexSort, 0, 3);
        AppendOnlyCheck.IsChecked = _settings.AppendOnly;
        OverrideIndexCheck.IsChecked = _settings.OverrideIndexPlacement;
        IndexSizeBox.Value = _settings.IndexMaxSize;
        IndexUnitCombo.SelectedIndex = Math.Clamp(_settings.IndexSizeUnit, 0, 2);
        IndexNameBox.Text = _settings.IndexNamePatterns;
        LogDirBox.Text = _settings.LogDirectory;
        LogDirBox.PlaceholderText = Settings.DefaultLogDirectory;
        VerbosityCombo.SelectedIndex = Math.Clamp(_settings.Verbosity, 0, 2);
        UpdatePolicyEnabled();
        UpdateIndexEnabled();
        _loadingUi = false;
    }

    private void UpdatePolicyEnabled()
    {
        bool en = OverridePolicyCheck.IsChecked == true;
        PolicyDismountRadio.IsEnabled = en;
        PolicyPeriodicRadio.IsEnabled = en;
        PeriodBox.IsEnabled = en && PolicyPeriodicRadio.IsChecked == true;
    }

    private void UpdateIndexEnabled()
    {
        bool en = OverrideIndexCheck.IsChecked == true;
        IndexSizeBox.IsEnabled = en;
        IndexUnitCombo.IsEnabled = en;
        IndexNameBox.IsEnabled = en;
    }

    private void IndexSize_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loadingUi) return;
        SaveSettingsFromUi();
    }

    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        UpdatePolicyEnabled();
        UpdateIndexEnabled();
        SaveSettingsFromUi();
        UpdateRunKey();
    }

    private void Period_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loadingUi) return;
        SaveSettingsFromUi();
    }

    private void SaveSettingsFromUi()
    {
        _settings.EjectAfterUnmount = EjectAfterUnmountCheck.IsChecked == true;
        _settings.RemountAtStartup = RemountCheck.IsChecked == true;
        _settings.CaptureIndex = CaptureIndexCheck.IsChecked == true;
        _settings.WorkFolder = string.IsNullOrWhiteSpace(WorkFolderBox.Text)
            ? @"C:\tmp\ltfs" : WorkFolderBox.Text.Trim();
        _settings.OverrideSyncPolicy = OverridePolicyCheck.IsChecked == true;
        _settings.SyncPolicyMode = PolicyDismountRadio.IsChecked == true ? 0 : 1;
        _settings.SyncPeriodMinutes = double.IsNaN(PeriodBox.Value) ? 5 : Math.Max(1, (int)PeriodBox.Value);
        _settings.AppendOnly = AppendOnlyCheck.IsChecked == true;
        _settings.OverrideIndexPlacement = OverrideIndexCheck.IsChecked == true;
        _settings.IndexMaxSize = double.IsNaN(IndexSizeBox.Value) ? 1 : Math.Max(1, (int)IndexSizeBox.Value);
        _settings.IndexSizeUnit = Math.Max(0, IndexUnitCombo.SelectedIndex);
        _settings.IndexNamePatterns = IndexNameBox.Text?.Trim() ?? "";
        _settings.LogDirectory = string.IsNullOrWhiteSpace(LogDirBox.Text)
            ? Settings.DefaultLogDirectory : LogDirBox.Text.Trim();
        _settings.Verbosity = Math.Max(0, VerbosityCombo.SelectedIndex);
        _settings.Save();
    }

    private void UpdateGlobalEnabled()
    {
        bool ok = _envOk && !_utilityRunning;
        foreach (var s in Slots) s.GlobalEnabled = ok;
    }

    /// <summary>Record a simple, one-line GUI event in the activity log.</summary>
    private void Log(string line) => Activity.Note(line);

    private void ClearLog_Click(object sender, RoutedEventArgs e) => Activity.Clear();

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Activity.FilePath) { UseShellExecute = true }); }
        catch (Exception ex) { Activity.Note($"open log file failed: {ex.Message}", isError: true); }
    }

    // ------------------------------------------------------------ drives & slots

    private DriveSlot? SlotFor(string device) => Slots.FirstOrDefault(s => s.Drive.Device == device);

    // ------------------------------------------------------------ mounting

    private MountOptions OptionsFromSlot(DriveSlot slot) => new()
    {
        ReadOnly = slot.ReadOnlyChecked,
        EjectAfterUnmount = _settings.EjectAfterUnmount,
        CaptureIndex = _settings.CaptureIndex,
        WorkFolder = _settings.WorkFolder,
        OverrideSyncPolicy = _settings.OverrideSyncPolicy,
        SyncPolicyMode = _settings.SyncPolicyMode,
        SyncPeriodMinutes = _settings.SyncPeriodMinutes,
        AppendOnly = _settings.AppendOnly,
        IndexRules = _settings.ComposeIndexRules(),
        LogDirectory = _settings.LogDirectory,
        Verbosity = _settings.Verbosity,
    };

    // ---- raw MAM table: copy rows as tab-separated text, header first

    private ListView? _mamMenuTarget;   // the table a context menu was opened on

    private static void CopyMamRows(IEnumerable<MamRow> rows)
    {
        var lines = rows.Select(r => string.Join('\t', r.Id, r.Name, r.Partition, r.Size, r.Value))
            .Prepend("ID\tAttribute\tPartition\tSize\tValue");
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(string.Join("\r\n", lines));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }

    private void MamCopy_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs e)
    {
        if (e.Element is not ListView lv) return;
        CopyMamRows(lv.SelectedItems.Cast<MamRow>());
        e.Handled = true;
    }

    private void MamMenu_Opening(object sender, object e) =>
        _mamMenuTarget = (sender as MenuFlyout)?.Target as ListView;

    private void MamCopySelected_Click(object sender, RoutedEventArgs e)
    {
        if (_mamMenuTarget is { } lv) CopyMamRows(lv.SelectedItems.Cast<MamRow>());
    }

    private void MamCopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_mamMenuTarget is { } lv) CopyMamRows(lv.Items.Cast<MamRow>());
    }

    private void SlotReadOnly_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DriveSlot slot && sender is CheckBox cb)
            slot.ReadOnlyChecked = cb.IsChecked == true;
    }

    private async void SlotAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DriveSlot slot) return;
        switch (slot.Phase)
        {
            case SlotPhase.Idle:
                await MountSlotAsync(slot);
                break;
            case SlotPhase.Mounting:   // Cancel
            case SlotPhase.Mounted:    // Unmount
                await UnmountSlotAsync(slot);
                break;
        }
    }

    private async Task MountSlotAsync(DriveSlot slot)
    {
        var letter = slot.SelectedLetter;
        if (letter == null) { await Message("No free drive letter selected."); return; }

        if (_settings.CaptureIndex)
            Directory.CreateDirectory(_settings.WorkFolder);

        var mapping = new Mapping
        {
            Letter = letter,
            Device = slot.Drive.Device,
            Description = slot.Drive.Display,
            Options = OptionsFromSlot(slot),
            // Reuse the cartridge identity already read from the MAM chip.
            VolumeName = slot.LastCart?.State == CartridgeState.Ltfs ? slot.LastCart.VolumeName : null,
            FormatVersion = slot.LastCart?.State == CartridgeState.Ltfs ? slot.LastCart.FormatVersion : null,
            LtoGeneration = slot.LastCart?.LtoGeneration,
            WriteProtected = slot.LastCart?.WriteProtected ?? false,
        };
        slot.Mapping = mapping;
        _mappings.Add(mapping);
        slot.Phase = SlotPhase.Mounting;
        try
        {
            await _monitor.ExclusiveAsync(mapping.Device, async () =>
            {
                await _mounts.MountAsync(mapping, Activity, _monitor.VolumeUpAsync(letter));
                _monitor.SetMount(mapping.Device, letter);   // inside: the re-read goes through the volume
            });
            slot.Phase = SlotPhase.Mounted;
            slot.SetMountedLetter(letter);
            PersistMapping(mapping);
        }
        catch (Exception ex)
        {
            // Close the mount entry as failed (covers the volume-timeout case
            // where ltfs.exe is still alive and its Exited handler hasn't fired).
            mapping.Scope?.Complete(null, ex.Message);
            _mappings.Remove(mapping);
            slot.Mapping = null;
            slot.Phase = SlotPhase.Idle;
            slot.SetLetters(_monitor.FreeLetters);
        }
    }

    private async Task UnmountSlotAsync(DriveSlot slot)
    {
        if (slot.Mapping is not { } mapping) { slot.Phase = SlotPhase.Idle; return; }
        bool wasMounting = slot.Phase == SlotPhase.Mounting;
        slot.Phase = SlotPhase.Unmounting;
        try
        {
            await _monitor.ExclusiveAsync(mapping.Device, async () =>
            {
                try { await _mounts.UnmountAsync(mapping, Activity); }
                finally { _monitor.SetMount(mapping.Device, null); }
            });
        }
        finally
        {
            _mappings.Remove(mapping);
            _settings.Mappings.RemoveAll(p => p.Letter == mapping.Letter);
            _settings.Save();
            UpdateRunKey();
            slot.Mapping = null;
            slot.Phase = SlotPhase.Idle;
            slot.SetLetters(_monitor.FreeLetters, prefer: mapping.Letter);
            if (wasMounting) Log($"[{mapping.Letter}] mount cancelled");
        }
    }

    private void PersistMapping(Mapping m)
    {
        _settings.Mappings.RemoveAll(p => p.Letter == m.Letter);
        _settings.Mappings.Add(new PersistedMapping
        {
            Letter = m.Letter,
            Device = m.Device,
            ReadOnly = m.Options.ReadOnly,
        });
        _settings.Save();
        UpdateRunKey();
    }

    /// <summary>The autostart entry exists iff remount-at-startup is on and something is mounted.</summary>
    private void UpdateRunKey()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (_settings.RemountAtStartup && _settings.Mappings.Count > 0)
        {
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LTOG.exe");
            key.SetValue("LTOG", $"\"{exe}\" --remount");
        }
        else
        {
            key.DeleteValue("LTOG", false);
        }
    }

    /// <summary>Take over a mount of this drive still running from a previous session.</summary>
    private void TryAdopt(DriveSlot slot)
    {
        foreach (var p in _settings.Mappings.Where(p => p.Device == slot.Drive.Device))
        {
            if (!Directory.Exists($@"{p.Letter}\")) continue;
            int? pid = MountManager.FindExternalMount(p.Device);
            if (pid == null) continue;

            Process? proc = null;
            try { proc = Process.GetProcessById(pid.Value); } catch { continue; }

            var mapping = new Mapping
            {
                Letter = p.Letter,
                Device = p.Device,
                Description = slot.Drive.Display,
                Options = new MountOptions
                {
                    ReadOnly = p.ReadOnly,
                    EjectAfterUnmount = _settings.EjectAfterUnmount,
                    CaptureIndex = _settings.CaptureIndex,
                    WorkFolder = _settings.WorkFolder,
                },
                Pid = pid.Value,
                IsExternal = true,
                State = "Mounted",
                // The real volume label WinFsp set from -o volname.
                VolumeName = MountManager.GetVolumeLabel(p.Letter),
                Proc = proc,
            };
            _mappings.Add(mapping);
            slot.Mapping = mapping;
            slot.ReadOnlyChecked = p.ReadOnly;
            slot.SetMountedLetter(p.Letter);
            slot.Phase = SlotPhase.Mounted;
            _monitor.SetMount(p.Device, p.Letter);
            Activity.Note($"Adopted existing mount on {p.Letter} ({p.Device}, pid {pid}) from a previous session.");
            return;
        }
    }

    private async Task RemountPersistedAsync()
    {
        if (!_settings.RemountAtStartup) return;
        foreach (var p in _settings.Mappings.ToList())
        {
            var slot = SlotFor(p.Device);
            if (slot == null || slot.Phase != SlotPhase.Idle) continue;

            var free = _monitor.FreeLetters;
            if (!free.Contains(p.Letter))
            {
                Log($"[{p.Letter}] startup remount skipped: letter not free");
                continue;
            }
            slot.ReadOnlyChecked = p.ReadOnly;
            slot.SetLetters(free, prefer: p.Letter);
            await MountSlotAsync(slot);
        }
    }

    // ------------------------------------------------------------ cartridge utilities

    private bool GuardUtility(DriveSlot? slot)
    {
        if (slot == null) return false;
        if (slot.Phase != SlotPhase.Idle)
        {
            Log($"{slot.Drive.Device} is mounted - unmount it before using cartridge utilities.");
            return false;
        }
        return true;
    }

    private static DriveSlot? SlotOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as DriveSlot;

    /// <summary>Ejects the cartridge, or loads one when the drive is empty.</summary>
    private async void SlotEjectLoad_Click(object sender, RoutedEventArgs e)
    {
        var slot = SlotOf(sender);
        if (!GuardUtility(slot) || slot!.MediaOpRunning) return;
        slot.MediaOpRunning = true;     // spinner until the new state is published
        try { await _monitor.LoadOrEjectAsync(slot.Drive.Device, slot.MediaAbsent); }
        finally { slot.MediaOpRunning = false; }
    }

    private async void SlotFormat_Click(object sender, RoutedEventArgs e)
    {
        var slot = SlotOf(sender);
        if (!GuardUtility(slot)) return;
        string dev = slot!.Drive.Device;

        var serialBox = new TextBox { Header = "Tape serial (6 characters)", MaxLength = 6 };
        var nameBox = new TextBox { Header = "Volume name", PlaceholderText = "LTFS VOLUME" };
        var forceBox = new CheckBox { Content = "Force format (overwrite existing LTFS volume)" };
        var dialog = new ContentDialog
        {
            Title = $"Format cartridge in {dev}",
            Content = new StackPanel
            {
                Spacing = 12,
                Children = { new TextBlock
                    { Text = "This DESTROYS all data on the cartridge.", TextWrapping = TextWrapping.Wrap },
                    serialBox, nameBox, forceBox },
            },
            PrimaryButtonText = "Format",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var args = new List<string> { "-i", LtfsEnv.LtfsConf, "-d", dev };
        if (!string.IsNullOrWhiteSpace(serialBox.Text)) { args.Add("-s"); args.Add(serialBox.Text.Trim()); }
        if (!string.IsNullOrWhiteSpace(nameBox.Text)) { args.Add("-n"); args.Add(nameBox.Text.Trim()); }
        if (forceBox.IsChecked == true) args.Add("-f");
        await RunUtility(dev, LtfsEnv.MkltfsExe, args, $"Format cartridge — {dev}");
    }

    private async void SlotUnformat_Click(object sender, RoutedEventArgs e)
    {
        var slot = SlotOf(sender);
        if (!GuardUtility(slot)) return;
        string dev = slot!.Drive.Device;

        var dialog = new ContentDialog
        {
            Title = $"Unformat cartridge in {dev}",
            Content = "This removes the LTFS format (and all data) from the cartridge, " +
                      "returning it to a single unformatted partition. Continue?",
            PrimaryButtonText = "Unformat",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await RunUtility(dev, LtfsEnv.UnltfsExe,
            new[] { "-i", LtfsEnv.LtfsConf, "-d", dev, "-y" }, $"Unformat cartridge — {dev}");
    }

    private async void SlotCheck_Click(object sender, RoutedEventArgs e)
    {
        var slot = SlotOf(sender);
        if (!GuardUtility(slot)) return;
        string dev = slot!.Drive.Device;
        await RunUtility(dev, LtfsEnv.LtfsckExe,
            new[] { "-i", LtfsEnv.LtfsConf, dev }, $"Check filesystem — {dev}");
    }

    private async Task RunUtility(string dev, string exe, IReadOnlyList<string> args, string what)
    {
        _utilityRunning = true;
        UpdateGlobalEnabled();
        try
        {
            // The command line, streamed output and exit code are logged by ToolRunner.
            await _monitor.ExclusiveAsync(dev, () => ToolRunner.RunAsync(exe, args, Activity, LogKind.Tool, what));
        }
        catch (Exception ex) { Activity.Note($"{what} failed: {ex.Message}", isError: true); }
        finally
        {
            _utilityRunning = false;
            UpdateGlobalEnabled();
        }
    }

    // ------------------------------------------------------------ indexes

    private bool _schemasLoading;
    private List<SchemaItem> _schemaItems = new();

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);   // Explorer's natural sort

    private async void RefreshSchemas()
    {
        if (_schemasLoading) return;
        _schemasLoading = true;
        try
        {
            string folder = _settings.WorkFolder;
            _schemaItems = await Task.Run(() =>
                new DirectoryInfo(folder)
                    .GetFiles("*.schema")
                    .Select(ParseSchema)
                    .ToList());
            ApplySchemaSort();
            NoSchemasText.Visibility = _schemaItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            SchemaList.ItemsSource = null;
            NoSchemasText.Visibility = Visibility.Visible;
        }
        finally { _schemasLoading = false; }
    }

    private void ApplySchemaSort()
    {
        IEnumerable<SchemaItem> sorted = _settings.IndexSort switch
        {
            1 => _schemaItems.OrderByDescending(i => i.Title, Comparer<string>.Create(StrCmpLogicalW)),
            2 => _schemaItems.OrderByDescending(i => i.Captured),
            3 => _schemaItems.OrderBy(i => i.Captured),
            _ => _schemaItems.OrderBy(i => i.Title, Comparer<string>.Create(StrCmpLogicalW)),
        };
        SchemaList.ItemsSource = sorted.ToList();
    }

    private void SchemaSort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        _settings.IndexSort = Math.Max(0, SchemaSortCombo.SelectedIndex);
        _settings.Save();
        ApplySchemaSort();
    }

    /// <summary>
    /// Extract the interesting bits of an LTFS index snapshot: volume name,
    /// generation, on-tape update time, file count, volume UUID.
    /// </summary>
    private static SchemaItem ParseSchema(FileInfo f)
    {
        string? name = null, uuid = null, gen = null, updated = null;
        int fileCount = 0;
        try
        {
            using var reader = System.Xml.XmlReader.Create(f.FullName,
                new System.Xml.XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true });
            while (reader.Read())
            {
                if (reader.NodeType != System.Xml.XmlNodeType.Element) continue;
                switch (reader.LocalName)
                {
                    case "name" when name == null:           // first <name> = volume name
                        name = reader.ReadElementContentAsString();
                        break;
                    case "volumeuuid" when uuid == null:
                        uuid = reader.ReadElementContentAsString();
                        break;
                    case "generationnumber" when gen == null:
                        gen = reader.ReadElementContentAsString();
                        break;
                    case "updatetime" when updated == null:
                        updated = reader.ReadElementContentAsString();
                        break;
                    case "file":
                        fileCount++;
                        break;
                }
            }
        }
        catch
        {
            return new SchemaItem
            {
                Title = f.Name,
                GenLine = "Unreadable index snapshot",
                CaptureLine = $"Captured {f.LastWriteTime:yyyy-MM-dd HH:mm}, {f.Length / 1024.0:0.#} KB",
                Path = f.FullName,
                Captured = f.LastWriteTime,
            };
        }

        string updatedText = "";
        if (updated != null && DateTime.TryParse(updated, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
            updatedText = $", tape index written {dt.ToLocalTime():yyyy-MM-dd HH:mm}";

        return new SchemaItem
        {
            Title = string.IsNullOrEmpty(name) ? "(unlabelled volume)" : name,
            GenLine = $"Index generation {gen ?? "?"}{updatedText}",
            CaptureLine = $"Captured {f.LastWriteTime:yyyy-MM-dd HH:mm}, " +
                          $"{fileCount:N0} file{(fileCount == 1 ? "" : "s")}, {f.Length / 1024.0:0.#} KB",
            Uuid = uuid ?? "",
            Path = f.FullName,
            Captured = f.LastWriteTime,
        };
    }

    private void SchemaOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SchemaItem item) return;
        try
        {
            if (!File.Exists(item.Path))
            {
                Activity.Note($"open snapshot failed: file no longer exists: {item.Path}", isError: true);
                RefreshSchemas();
                return;
            }

            var viewer = new SchemaViewerWindow(item.Path);
            _childWindows.Add(viewer);
            viewer.Closed += (_, _) => _childWindows.Remove(viewer);
            viewer.Activate();
        }
        catch (Exception ex) { Activity.Note($"open snapshot failed: {ex.Message}", isError: true); }
    }

    private void RefreshSchemas_Click(object sender, RoutedEventArgs e) => RefreshSchemas();

    private void OpenWorkFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settings.WorkFolder);
            Process.Start(new ProcessStartInfo(_settings.WorkFolder) { UseShellExecute = true });
        }
        catch (Exception ex) { Activity.Note($"open folder failed: {ex.Message}", isError: true); }
    }

    // ------------------------------------------------------------ settings

    private void DeviceManager_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true }); }
        catch (Exception ex) { Activity.Note($"open Device Manager failed: {ex.Message}", isError: true); }
    }

    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/rlaphoenix/LTOG")
            { UseShellExecute = true });
        }
        catch (Exception ex) { Activity.Note($"open GitHub failed: {ex.Message}", isError: true); }
    }

    private async Task Message(string text)
    {
        var dialog = new ContentDialog
        {
            Title = "LTOG",
            Content = text,
            CloseButtonText = "OK",
            XamlRoot = Root.XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
