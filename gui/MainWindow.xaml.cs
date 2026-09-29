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
    /// <summary>One card per detected tape drive (Mounting page).</summary>
    public ObservableCollection<DriveSlot> Slots { get; } = new();

    /// <summary>Structured log of every LTFS/WinFsp/tape-drive invocation (Log page).</summary>
    public ActivityLog Activity { get; }

    /// <summary>Active mounts (logic only; the UI lives in the slots).</summary>
    private readonly List<Mapping> _mappings = new();

    private readonly Settings _settings = Settings.Load();
    private readonly MountManager _mounts = new();
    private List<TapeDrive> _drives = new();
    private readonly List<Window> _childWindows = new();
    private bool _loadingUi;
    private bool _polling;
    private bool _utilityRunning;
    private bool _envOk = true;
    private DispatcherTimer? _pollTimer;

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public MainWindow()
    {
        // Created before InitializeComponent so the Log page's x:Bind sees it,
        // and on the UI thread so it can resolve its theme brushes.
        Activity = new ActivityLog(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());

        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();

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
        Closed += (_, _) => { _pollTimer?.Stop(); SaveWindowBounds(); };

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
        RefreshDrives();
        UpdateGlobalEnabled();

        // restore last selected tab
        var lastTab = Nav.MenuItems.Concat(Nav.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string?)i.Tag == _settings.LastTab);
        if (lastTab != null)
            Nav.SelectedItem = lastTab;

        Root.Loaded += async (_, _) =>
        {
            FitPaneToItems();
            // min width = fitted nav pane + page container (640) + gutters (32)
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
            {
                double scale = Root.XamlRoot.RasterizationScale;
                op.PreferredMinimumWidth = (int)((Nav.OpenPaneLength + 640 + 32) * scale);
                op.PreferredMinimumHeight = (int)(480 * scale);
            }
            AdoptExternalMounts();
            await PollSlotsAsync();
            if (App.AutoRemount)
                await RemountPersistedAsync();
        };

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += async (_, _) =>
        {
            RefreshDrivesIfChanged();
            await PollSlotsAsync();
        };
        _pollTimer.Start();
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

    /// <summary>
    /// NavigationView has no pane auto-fit (through WinAppSDK 2.2), so size
    /// the pane from the measured width of the widest item instead of a
    /// hardcoded value — robust against font, DPI and label changes.
    /// </summary>
    private void FitPaneToItems()
    {
        double max = 0;
        foreach (var obj in Nav.MenuItems.Concat(Nav.FooterMenuItems))
        {
            if (obj is not NavigationViewItem item) continue;
            item.Measure(new Windows.Foundation.Size(
                double.PositiveInfinity, double.PositiveInfinity));
            max = Math.Max(max, item.DesiredSize.Width);
        }
        if (max > 0)
            Nav.OpenPaneLength = Math.Ceiling(max);   // DesiredSize already includes item margins
    }

    private void PagesHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Deterministic page container: fill to 640px, centered.
        double page = Math.Max(320, Math.Min(640, e.NewSize.Width - 32)); // 16px gutters
        MountContent.Width = page;
        IndexContent.Width = page;
        SettingsContent.Width = page;
        AboutContent.Width = page;
        LogPanel.Width = page;
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        MountPanel.Visibility = tag == "mount" ? Visibility.Visible : Visibility.Collapsed;
        IndexPanel.Visibility = tag == "index" ? Visibility.Visible : Visibility.Collapsed;
        LogPanel.Visibility = tag == "log" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "index")
            RefreshSchemas();
        if (tag == "about")
            _ = LoadAboutInfoAsync();
        if (tag != null && _settings.LastTab != tag)
        {
            _settings.LastTab = tag;
            _settings.Save();
        }
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
    }

    private void Period_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loadingUi) return;
        SaveSettingsFromUi();
    }

    private void SaveSettingsFromUi()
    {
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

    private void RefreshDrives()
    {
        if (!CanProbeDevices())
        {
            // Reopening the GUI must not probe a drive used by an external mount.
            // Persisted identities are sufficient to adopt it below.
            _drives = _settings.Mappings.Select(p => p.Device)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(device => new TapeDrive(device, "", "", "")).ToList();
            RebuildSlots();
            return;
        }
        _drives = NativeTape.Enumerate(Activity);
        RebuildSlots();
    }

    private bool CanProbeDevices() => DeviceProbePolicy.CanProbe(
        _utilityRunning || Slots.Any(s => s.MediaOpRunning || s.Phase != SlotPhase.Idle),
        () =>
        {
            // Process metadata only: no device handles, volume reads or SCSI calls.
            // Block even when an external mount cannot be adopted (unknown mapping).
            var processes = Process.GetProcessesByName("ltfs");
            try { return processes.Length > 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        });

    /// <summary>Auto-refresh (5s): re-enumerate and only touch the UI on change.</summary>
    private void RefreshDrivesIfChanged()
    {
        if (_polling || !CanProbeDevices()) return;
        List<TapeDrive> found;
        try { found = NativeTape.Enumerate(Activity); }
        catch { return; }
        bool same = found.Count == _drives.Count &&
                    found.Zip(_drives).All(p => p.First == p.Second);
        if (same) return;

        _drives = found;
        RebuildSlots();
    }

    /// <summary>Sync the slot cards with the detected drives, preserving state.</summary>
    private void RebuildSlots()
    {
        // remove slots whose drive disappeared (keep mounted ones: the device
        // node can be temporarily unopenable, and the mount still exists)
        foreach (var gone in Slots.Where(s =>
                     s.Phase == SlotPhase.Idle &&
                     _drives.All(d => d.Device != s.Drive.Device)).ToList())
            Slots.Remove(gone);

        foreach (var d in _drives)
        {
            if (Slots.Any(s => s.Drive.Device == d.Device)) continue;
            var slot = new DriveSlot { Drive = d };
            slot.SetLetters(NativeTape.UnusedLetters());
            Slots.Add(slot);
        }

        NoDrivesText.Visibility = Slots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateGlobalEnabled();
    }

    private void RefreshIdleSlotLetters()
    {
        var free = NativeTape.UnusedLetters();
        foreach (var s in Slots.Where(s => s.Phase == SlotPhase.Idle))
            s.SetLetters(free);
    }

    /// <summary>
    /// Identify the cartridge in every idle drive straight from the drive
    /// (GetTapeStatus + MAM attributes) — independent of any mount.
    /// </summary>
    private async Task PollSlotsAsync()
    {
        if (_polling || !CanProbeDevices()) return;
        _polling = true;
        try
        {
            foreach (var slot in Slots.ToList())
            {
                if (slot.MediaOpRunning) continue;   // leave the drive alone mid eject/load
                await PollSlotCoreAsync(slot);
            }
        }
        finally { _polling = false; }
    }

    /// <summary>
    /// Build a cartridge card line: "label, LTO-N, LTFS X.X[, Write-Protected]" for a
    /// formatted cartridge, or "Cartridge Unformatted, LTO-N[, Write-Protected]" otherwise.
    /// Any unknown segment (generation, format version) is simply omitted.
    /// </summary>
    private static string FormatCartridgeLine(
        string? label, string? ltoGen, string? formatVersion, bool writeProtected, bool ltfs)
    {
        var parts = new List<string>
        {
            ltfs ? (string.IsNullOrEmpty(label) ? "(unlabelled LTFS cartridge)" : label!)
                 : "Cartridge Unformatted",
        };
        if (!string.IsNullOrEmpty(ltoGen)) parts.Add(ltoGen!);
        if (ltfs && !string.IsNullOrEmpty(formatVersion)) parts.Add($"LTFS {formatVersion}");
        if (writeProtected) parts.Add("Write-Protected");
        return string.Join(", ", parts);
    }

    private async Task PollSlotCoreAsync(DriveSlot slot)
    {
        if (slot.Phase is SlotPhase.Mounting or SlotPhase.Unmounting)
            return;

        if (slot.Phase == SlotPhase.Mounted && slot.Mapping != null)
        {
            var m = slot.Mapping;
            slot.CartridgeText = FormatCartridgeLine(
                m.VolumeName, m.LtoGeneration, m.FormatVersion, m.WriteProtected, ltfs: true);
            slot.CanMount = true;
            return;
        }

        if (!CanProbeDevices()) return;

        CartridgeInfo? cart = null;
        try { cart = await Task.Run(() => NativeTape.ReadCartridgeInfo(slot.Drive.Device, Activity)); }
        catch { }
        slot.LastCart = cart;
        switch (cart?.State)
        {
            case CartridgeState.NoMedia:
                slot.CartridgeText = "No Cartridge Inserted";
                slot.CanMount = false;
                break;
            case CartridgeState.NotLtfs:
                slot.CartridgeText = FormatCartridgeLine(
                    null, cart.LtoGeneration, null, cart.WriteProtected, ltfs: false);
                slot.CanMount = true;   // detection is best-effort
                break;
            case CartridgeState.Ltfs:
                slot.CartridgeText = FormatCartridgeLine(
                    cart.VolumeName, cart.LtoGeneration, cart.FormatVersion, cart.WriteProtected, ltfs: true);
                slot.CanMount = true;
                break;
            case CartridgeState.NotReady:
                slot.CartridgeText = "Drive not ready...";
                slot.CanMount = false;
                break;
            case CartridgeState.DriveInUse:
                slot.CartridgeText = "Drive in use by another program";
                slot.CanMount = false;
                break;
            default:
                slot.CartridgeText = "Checking cartridge...";
                slot.CanMount = true;
                break;
        }
    }

    // ------------------------------------------------------------ mounting

    private MountOptions OptionsFromSlot(DriveSlot slot) => new()
    {
        ReadOnly = slot.ReadOnlyChecked,
        EjectAfterUnmount = slot.EjectChecked,
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

    /// <summary>Refresh the whole card: free letters, cartridge identity, status.</summary>
    private async void SlotRefreshCard_Click(object sender, RoutedEventArgs e)
    {
        if (SlotOf(sender) is not DriveSlot slot || slot.IsBusy || slot.RefreshRunning) return;
        slot.RefreshRunning = true;
        try
        {
            if (slot.Phase == SlotPhase.Idle)
            {
                slot.CartridgeText = "Checking cartridge...";
                slot.SetLetters(NativeTape.UnusedLetters());
            }
            await PollSlotCoreAsync(slot);
        }
        finally { slot.RefreshRunning = false; }
    }

    private void SlotReadOnly_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DriveSlot slot && sender is CheckBox cb)
            slot.ReadOnlyChecked = cb.IsChecked == true;
    }

    private void SlotEject_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DriveSlot slot && sender is CheckBox cb)
            slot.EjectChecked = cb.IsChecked == true;
    }

    private void SlotRemount_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DriveSlot slot || sender is not CheckBox cb)
            return;
        slot.RemountChecked = cb.IsChecked == true;
        // For an active mount, update the persisted entry immediately;
        // for an idle drive the flag is stored when the mount is created.
        if (slot.Mapping is { } m)
        {
            var p = _settings.Mappings.FirstOrDefault(x => x.Letter == m.Letter);
            if (p != null)
            {
                p.RemountAtStartup = slot.RemountChecked;
                _settings.Save();
            }
        }
        UpdateRunKey();
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
            await _mounts.MountAsync(mapping, Activity);
            slot.Phase = SlotPhase.Mounted;
            slot.SetMountedLetter(letter);
            PersistMapping(mapping, slot.RemountChecked);
        }
        catch (Exception ex)
        {
            // Close the mount entry as failed (covers the volume-timeout case
            // where ltfs.exe is still alive and its Exited handler hasn't fired).
            mapping.Scope?.Complete(null, ex.Message);
            _mappings.Remove(mapping);
            slot.Mapping = null;
            slot.Phase = SlotPhase.Idle;
        }
        finally
        {
            RefreshIdleSlotLetters();
            await PollSlotsAsync();
        }
    }

    private async Task UnmountSlotAsync(DriveSlot slot)
    {
        if (slot.Mapping is not { } mapping) { slot.Phase = SlotPhase.Idle; return; }
        bool wasMounting = slot.Phase == SlotPhase.Mounting;
        slot.Phase = SlotPhase.Unmounting;
        try
        {
            await _mounts.UnmountAsync(mapping, Activity);
        }
        finally
        {
            _mappings.Remove(mapping);
            _settings.Mappings.RemoveAll(p => p.Letter == mapping.Letter);
            _settings.Save();
            UpdateRunKey();
            slot.Mapping = null;
            slot.Phase = SlotPhase.Idle;
            slot.SetLetters(NativeTape.UnusedLetters(), prefer: mapping.Letter);
            RefreshIdleSlotLetters();
            if (wasMounting) Log($"[{mapping.Letter}] mount cancelled");
            await PollSlotsAsync();
        }
    }

    private void PersistMapping(Mapping m, bool remountAtStartup)
    {
        _settings.Mappings.RemoveAll(p => p.Letter == m.Letter);
        _settings.Mappings.Add(new PersistedMapping
        {
            Letter = m.Letter,
            Device = m.Device,
            ReadOnly = m.Options.ReadOnly,
            EjectAfterUnmount = m.Options.EjectAfterUnmount,
            RemountAtStartup = remountAtStartup,
        });
        _settings.Save();
        UpdateRunKey();
    }

    /// <summary>The autostart entry exists iff any mount wants remount-at-startup.</summary>
    private void UpdateRunKey()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (_settings.Mappings.Any(p => p.RemountAtStartup))
        {
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LTOG.exe");
            key.SetValue("LTOG", $"\"{exe}\" --remount");
        }
        else
        {
            key.DeleteValue("LTOG", false);
        }
    }

    private void AdoptExternalMounts()
    {
        foreach (var p in _settings.Mappings)
        {
            if (!Directory.Exists($@"{p.Letter}\")) continue;
            int? pid = MountManager.FindExternalMount(p.Device);
            if (pid == null) continue;
            var slot = Slots.FirstOrDefault(s => s.Drive.Device == p.Device);
            if (slot == null || slot.Phase != SlotPhase.Idle) continue;

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
                    EjectAfterUnmount = p.EjectAfterUnmount,
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
            slot.EjectChecked = p.EjectAfterUnmount;
            slot.RemountChecked = p.RemountAtStartup;
            slot.SetMountedLetter(p.Letter);
            slot.Phase = SlotPhase.Mounted;
            Activity.Note($"Adopted existing mount on {p.Letter} ({p.Device}, pid {pid}) from a previous session.");
        }
    }

    private async Task RemountPersistedAsync()
    {
        foreach (var p in _settings.Mappings.Where(p => p.RemountAtStartup).ToList())
        {
            var slot = Slots.FirstOrDefault(s => s.Drive.Device == p.Device);
            if (slot == null || slot.Phase != SlotPhase.Idle) continue;

            var free = NativeTape.UnusedLetters();
            if (!free.Contains(p.Letter))
            {
                Log($"[{p.Letter}] startup remount skipped: letter not free");
                continue;
            }
            slot.ReadOnlyChecked = p.ReadOnly;
            slot.EjectChecked = p.EjectAfterUnmount;
            slot.RemountChecked = true;
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

    /// <summary>Header button: ejects the cartridge, or loads one when the drive is empty.</summary>
    private async void SlotEjectLoad_Click(object sender, RoutedEventArgs e)
    {
        var slot = SlotOf(sender);
        if (!GuardUtility(slot) || slot!.MediaOpRunning) return;
        string dev = slot.Drive.Device;
        bool load = slot.MediaAbsent;
        slot.MediaOpRunning = true;     // spinner until the state is re-read
        var scope = Activity.Begin(LogKind.Native,
            $"{dev}: {(load ? "Load" : "Eject")} cartridge",
            $@"PrepareTape(\\.\{dev}, {(load ? "TAPE_LOAD" : "TAPE_UNLOAD")})");
        try
        {
            if (load) { await Task.Run(() => NativeTape.Load(dev)); scope.Line("Cartridge loaded."); }
            else { await Task.Run(() => NativeTape.Eject(dev)); scope.Line("Cartridge ejected."); }
            scope.Complete();
        }
        catch (Exception ex) { scope.Complete(null, ex.Message); }
        finally
        {
            slot.MediaOpRunning = false;
            await PollSlotCoreAsync(slot);
        }
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
        await RunUtility(LtfsEnv.MkltfsExe, args, $"Format cartridge — {dev}");
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

        await RunUtility(LtfsEnv.UnltfsExe,
            new[] { "-i", LtfsEnv.LtfsConf, "-d", dev, "-y" }, $"Unformat cartridge — {dev}");
    }

    private async void SlotCheck_Click(object sender, RoutedEventArgs e)
    {
        var slot = SlotOf(sender);
        if (!GuardUtility(slot)) return;
        string dev = slot!.Drive.Device;
        await RunUtility(LtfsEnv.LtfsckExe,
            new[] { "-i", LtfsEnv.LtfsConf, dev }, $"Check filesystem — {dev}");
    }

    private async Task RunUtility(string exe, IReadOnlyList<string> args, string what)
    {
        _utilityRunning = true;
        UpdateGlobalEnabled();
        try
        {
            // The command line, streamed output and exit code are logged by ToolRunner.
            await ToolRunner.RunAsync(exe, args, Activity, LogKind.Tool, what);
        }
        catch (Exception ex) { Activity.Note($"{what} failed: {ex.Message}", isError: true); }
        finally
        {
            _utilityRunning = false;
            UpdateGlobalEnabled();
            await PollSlotsAsync();
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
