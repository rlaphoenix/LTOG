using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LTOG;

/// <summary>One visible line of the tree: a node at some depth.</summary>
public sealed class SearchRow(CatalogNode node, int depth, bool expanded, bool hasChildren) : INotifyPropertyChanged
{
    public CatalogNode Node => node;
    public int Depth => depth;
    public bool HasChildren => hasChildren;
    public bool IsExpanded
    {
        get => expanded;
        set { expanded = value; PropertyChanged?.Invoke(this, new(nameof(ChevronGlyph))); }
    }
    public Thickness Indent => new(depth * 20, 0, 0, 0);
    public double ChevronOpacity => hasChildren ? 1 : 0;
    public string ChevronGlyph => expanded ? "" : "";
    public string Icon => node.IsDirectory ? "" : "";
    public string SizeText => SchemaSnapshot.FormatSize(node.Size);
    public string SizeTip => SchemaSnapshot.FormatBytes(node.Size);
    public string ModifiedText => node.Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";
    public string? ModifiedTip => node.Modified?.ToString("dddd, d MMMM yyyy, HH:mm:ss");
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Every captured index merged into one browsable, searchable tree.</summary>
public sealed partial class SearchPage : UserControl
{
    private List<CatalogTape> _tapes = new();
    private CatalogNode _root = new() { IsDirectory = true };
    private CatalogNode _folder;
    // by path, so they survive the tree being rebuilt (reload, index filter)
    private readonly Stack<string> _back = new(), _forward = new();
    private readonly HashSet<string> _expanded = new();
    private readonly HashSet<string> _collapsedInSearch = new();   // searching opens everything
    private HashSet<CatalogNode>? _shown;                          // matches + their folders; null = all
    private ObservableCollection<SearchRow> _rows = new();
    private string _sort = "name";
    private bool _desc;
    private readonly SearchOptions _options = new();
    private readonly DispatcherQueueTimer _debounce;
    private string _signature = "";
    private string? _builtFor;   // snapshot paths the tree was built from
    private string? _tapeId;     // volume UUID the tree lists; null = every tape
    private bool _loading;
    private SearchFiltersWindow? _filters;
    private readonly List<Window> _windows = new();   // keep open property windows referenced

    public SearchPage()
    {
        InitializeComponent();
        _folder = _root;
        _debounce = DispatcherQueue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(200);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => ApplyFilter();
        _options.Changed += () =>
        {
            FiltersButton.Style = (Style)Resources[_options.IsFiltering ? "AccentIconButton" : "IconButton"];
            _debounce.Start();
        };
        UpdateSortChevrons();
        Navigate(_root, record: false);
    }

    /// <summary>Reload when the snapshots on disk changed; keeps the current folder if it still exists.</summary>
    public async void Refresh()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var dir = new DirectoryInfo(App.Settings.WorkFolder);
            var files = dir.Exists ? dir.GetFiles("*.schema") : [];
            string sig = string.Join("|", files.Select(f => f.Name + f.LastWriteTimeUtc.Ticks))
                         + string.Join("|", Core.TapeDb.All.Select(t => t.Key + t.Value.Barcode + t.Value.Serial));
            if (sig != _signature)
            {
                var records = new Dictionary<string, Core.TapeRecord>(Core.TapeDb.All, StringComparer.OrdinalIgnoreCase);
                _tapes = await Task.Run(() => CatalogNode.Load(files, records));
                _signature = sig;
                _builtFor = null;
                if (!_tapes.Any(t => t.Snapshot.VolumeUuid == _tapeId)) _tapeId = null;
                BuildTree();
                ApplyFilter();
                UpdateAddress();
            }
            UpdateFree();
        }
        catch (Exception ex) { App.Activity.Note($"search: loading indexes failed: {ex.Message}", isError: true); }
        finally { _loading = false; }
    }

    /// <summary>(Re)merge the chosen tape, or all of them; false when nothing changed.</summary>
    private bool BuildTree()
    {
        var use = _tapes.Where(t => _tapeId == null || t.Snapshot.VolumeUuid == _tapeId).ToList();
        string key = string.Join("|", use.Select(t => t.Snapshot.Path));
        if (key == _builtFor) return false;
        _builtFor = key;
        _root = CatalogNode.Build(use);
        _folder = _root.Resolve(_folder.Path) ?? _root;
        return true;
    }

    // ------------------------------------------------------------ navigation

    private void Navigate(CatalogNode folder, bool record = true)
    {
        if (record && folder != _folder)
        {
            _back.Push(_folder.Path);
            _forward.Clear();
        }
        var from = _folder;
        bool focus = ListHasFocus();
        _folder = folder;
        ApplyFilter();
        UpdateAddress();
        // like Explorer: going up selects the folder you came from
        var row = _rows.FirstOrDefault(r => r.Depth == 0 && (r.Node == from || IsAncestor(r.Node, from)));
        if (row != null) SelectRow(row, focus);
        else if (focus && _rows.Count > 0) SelectRow(_rows[0], true);
    }

    private static bool IsAncestor(CatalogNode a, CatalogNode n)
    {
        for (var p = n.Parent; p != null; p = p.Parent) if (p == a) return true;
        return false;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (!_back.TryPop(out var path)) return;
        _forward.Push(_folder.Path);
        Navigate(_root.Resolve(path) ?? _root, record: false);
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (!_forward.TryPop(out var path)) return;
        _back.Push(_folder.Path);
        Navigate(_root.Resolve(path) ?? _root, record: false);
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_folder.Parent is { } p) Navigate(p);
    }

    private void UpdateAddress()
    {
        BackButton.IsEnabled = _back.Count > 0;
        ForwardButton.IsEnabled = _forward.Count > 0;
        UpButton.IsEnabled = _folder.Parent != null;
        Crumbs.Children.Clear();
        var chain = new List<CatalogNode>();
        for (var n = _folder; n != null; n = n.Parent) chain.Insert(0, n);
        foreach (var n in chain)
        {
            if (n.Parent != null)
                Crumbs.Children.Add(new TextBlock { Text = "/", Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
            if (n.Parent == null)
            {
                Crumbs.Children.Add(TapePicker());
                continue;
            }
            var b = new HyperlinkButton
            {
                Content = n.Name,
                Padding = new Thickness(6, 2, 6, 2),
                Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            };
            b.Click += (_, _) => Navigate(n);
            Crumbs.Children.Add(b);
        }
    }

    /// <summary>The root crumb: which tape the tree lists, or all of them.</summary>
    private DropDownButton TapePicker()
    {
        var choices = _tapes.OrderBy(t => t.Label, SchemaParser.Natural)
            .Select(t => (Id: (string?)t.Snapshot.VolumeUuid, t.Label)).Prepend((null, "All tapes")).ToList();
        var list = new ListView
        {
            IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.Single, MaxHeight = 480,
            ItemsSource = choices.Select(c =>
            {
                var p = new StackPanel { Padding = new Thickness(0, 4, 0, 4) };
                p.Children.Add(new TextBlock { Text = c.Label });
                if (c.Id != null)
                    p.Children.Add(new TextBlock
                    {
                        Text = c.Id, FontSize = 12,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                    });
                return p;
            }).ToList(),
            SelectedIndex = Math.Max(0, choices.FindIndex(c => c.Id == _tapeId)),
        };
        var flyout = new Flyout { Content = list };
        list.ItemClick += (_, e) =>
        {
            flyout.Hide();
            _tapeId = choices[list.Items.IndexOf(e.ClickedItem)].Id;
            if (BuildTree()) ApplyFilter();
            UpdateAddress();
        };
        flyout.Opened += (_, _) => (list.ContainerFromIndex(list.SelectedIndex) as Control)?.Focus(FocusState.Keyboard);
        return new DropDownButton
        {
            Content = choices.First(c => c.Id == _tapeId).Label,
            Flyout = flyout, Padding = new Thickness(8, 2, 8, 2),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
        };
    }

    private void AddressBar_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, AddressBar)) EditAddress();   // not a crumb
    }

    private void EditAddress_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        EditAddress();
    }

    private void EditAddress()
    {
        Crumbs.Visibility = Visibility.Collapsed;
        AddressBox.Visibility = Visibility.Visible;
        AddressBox.Text = _folder.Path;
        AddressBox.Focus(FocusState.Programmatic);
        AddressBox.SelectAll();
    }

    private void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (_root.Resolve(AddressBox.Text.Replace('\\', '/')) is not { } f)
            {
                AddressBox.SelectAll();   // unknown path: leave it for correcting
                return;
            }
            List.Focus(FocusState.Keyboard);   // LostFocus closes the box
            Navigate(f);
        }
        else if (e.Key == Windows.System.VirtualKey.Escape) List.Focus(FocusState.Keyboard);
        else return;
        e.Handled = true;
    }

    private void AddressBox_LostFocus(object sender, RoutedEventArgs e)
    {
        AddressBox.Visibility = Visibility.Collapsed;
        Crumbs.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------ search + tree

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _debounce.Start();

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && SearchBox.Text.Length > 0) SearchBox.Text = "";
        else if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Down)
        {
            if (_debounce.IsRunning) ApplyFilter();   // don't wait out the debounce
            if (_rows.Count > 0) SelectRow(_rows[0], true);
        }
        else return;
        e.Handled = true;
    }

    private void FocusSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
    }

    private void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Refresh();
    }

    private void ApplyFilter()
    {
        _debounce.Stop();
        var match = _options.Matcher(SearchBox.Text);
        _collapsedInSearch.Clear();
        _shown = null;
        if (match != null)
        {
            var shown = _shown = new HashSet<CatalogNode>();
            bool Collect(CatalogNode n)
            {
                bool any = false;
                foreach (var c in n.Children.Values)
                    if (Collect(c) | match(c)) { shown.Add(c); any = true; }
                return any;
            }
            Collect(_folder);
        }
        Rebuild();
    }

    private bool IsOpen(CatalogNode n) =>
        _shown != null ? !_collapsedInSearch.Contains(n.Path) : _expanded.Contains(n.Path);

    private IEnumerable<CatalogNode> Visible(CatalogNode n)
    {
        var xs = n.Children.Values.Where(c => _shown?.Contains(c) ?? true).OrderByDescending(c => c.IsDirectory);
        IOrderedEnumerable<CatalogNode> By<T>(Func<CatalogNode, T> key, IComparer<T>? cmp = null) =>
            _desc ? xs.ThenByDescending(key, cmp) : xs.ThenBy(key, cmp);
        return (_sort switch
        {
            "size" => By(c => c.Size),
            "modified" => By(c => c.Modified),
            "location" => By(c => c.Location, SchemaParser.Natural),
            _ => By(c => c.Name, SchemaParser.Natural),
        }).ThenBy(c => c.Name, SchemaParser.Natural);
    }

    private void AddRows(CatalogNode n, int depth, IList<SearchRow> into)
    {
        foreach (var c in Visible(n))
        {
            bool open = c.IsDirectory && IsOpen(c);
            into.Add(new SearchRow(c, depth, open, c.Children.Values.Any(x => _shown?.Contains(x) ?? true)));
            if (open) AddRows(c, depth + 1, into);
        }
    }

    private void Rebuild()
    {
        var rows = new List<SearchRow>();
        AddRows(_folder, 0, rows);
        _rows = new ObservableCollection<SearchRow>(rows);
        List.ItemsSource = _rows;
        EmptyText.Text = _tapes.Count == 0 ? "No captured indexes yet." : "Nothing found.";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatus();
    }

    private void Chevron_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SearchRow row) Toggle(row);
    }

    private void Toggle(SearchRow row)
    {
        bool open = !row.IsExpanded;
        var set = _shown != null ? _collapsedInSearch : _expanded;
        if (open == (_shown == null)) set.Add(row.Node.Path); else set.Remove(row.Node.Path);
        row.IsExpanded = open;
        int i = _rows.IndexOf(row);
        if (open)
        {
            var sub = new List<SearchRow>();
            AddRows(row.Node, row.Depth + 1, sub);
            // ponytail: one insert per row; swap in a range-capable collection if huge folders feel slow
            for (int k = 0; k < sub.Count; k++) _rows.Insert(i + 1 + k, sub[k]);
        }
        else
            while (i + 1 < _rows.Count && _rows[i + 1].Depth > row.Depth) _rows.RemoveAt(i + 1);
        UpdateStatus();
    }

    private void SelectRow(SearchRow row, bool focus)
    {
        List.SelectedItem = row;
        List.ScrollIntoView(row);
        if (focus)   // the container exists only after layout
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
                () => (List.ContainerFromItem(row) as Control)?.Focus(FocusState.Keyboard));
    }

    private bool ListHasFocus()
    {
        if (XamlRoot == null) return false;
        for (var d = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d == List) return true;
        return false;
    }

    /// <summary>Explorer keys: Enter opens, Backspace goes back, Right/Left expand/collapse or step in/out.</summary>
    private void List_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var row = e.OriginalSource is ListViewItem li ? List.ItemFromContainer(li) as SearchRow : null;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Back when _back.Count > 0:
                Back_Click(this, e);
                break;
            case Windows.System.VirtualKey.Enter when row != null:
                if (row.Node.IsDirectory) Navigate(row.Node);
                else OpenProperties(row.Node);
                break;
            case Windows.System.VirtualKey.Right when row is { HasChildren: true }:
                if (!row.IsExpanded) Toggle(row);
                else SelectRow(_rows[_rows.IndexOf(row) + 1], true);
                break;
            case Windows.System.VirtualKey.Left when row != null && (row.IsExpanded || row.Depth > 0):
                if (row.IsExpanded) Toggle(row);
                else SelectRow(_rows.Take(_rows.IndexOf(row)).Last(r => r.Depth == row.Depth - 1), true);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var col = (string)((Button)sender).Tag;
        _desc = col == _sort && !_desc;
        _sort = col;
        UpdateSortChevrons();
        Rebuild();
    }

    private void UpdateSortChevrons()
    {
        foreach (var (col, icon) in new[] { ("name", NameChevron), ("size", SizeChevron), ("modified", ModifiedChevron), ("location", LocationChevron) })
            icon.Glyph = col != _sort ? "" : _desc ? "" : "";
    }

    private void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SearchRow row) return;
        if (row.Node.IsDirectory) Navigate(row.Node);
        else OpenProperties(row.Node);
    }

    private void Properties_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SearchRow row) OpenProperties(row.Node);
    }

    private void Properties_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenProperties((List.SelectedItem as SearchRow)?.Node ?? _folder);
    }

    private void OpenProperties(CatalogNode node)
    {
        var w = PropertiesWindow.Open(node);
        _windows.Add(w);
        w.Closed += (_, _) => _windows.Remove(w);
    }

    private void Filters_Click(object sender, RoutedEventArgs e)
    {
        if (_filters == null)
        {
            var types = CatalogNode.Build(_tapes).Descendants().Where(n => !n.IsDirectory)
                .Select(n => Path.GetExtension(n.Name).ToLowerInvariant()).Where(x => x != "")
                .Distinct().Order().ToList();
            _filters = new SearchFiltersWindow(_options, types);
            _filters.Closed += (_, _) => _filters = null;
        }
        _filters.Activate();
    }

    // ------------------------------------------------------------ status bar

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateStatus();

    private void UpdateStatus()
    {
        var sel = List.SelectedItems.Cast<SearchRow>().Select(r => r.Node).ToHashSet();
        int dirs = _rows.Count(r => r.Node.IsDirectory);
        FolderStatus.Text = $"{sel.Count(n => n.IsDirectory):N0} / {dirs:N0}";
        FileStatus.Text = $"{sel.Count(n => !n.IsDirectory):N0} / {_rows.Count - dirs:N0}";
        // count a selected folder's contents once, not again for selected rows inside it
        static IEnumerable<CatalogNode> Ancestors(CatalogNode n) { for (var p = n.Parent; p != null; p = p.Parent) yield return p; }
        long selSize = sel.Where(n => !Ancestors(n).Any(sel.Contains)).Sum(n => n.Size);
        long total = _rows.Where(r => r.Depth == 0).Sum(r => r.Node.Size);
        SizeStatus.Text = $"{SchemaSnapshot.FormatSize(selSize)} / {SchemaSnapshot.FormatSize(total)}";
    }

    private void UpdateFree()
    {
        var known = _tapes.Select(t => t.Record).OfType<Core.TapeRecord>().ToList();
        long cap = known.Sum(r => r.Capacity), free = known.Sum(r => r.Free);
        double used = cap > 0 ? 100.0 * (cap - free) / cap : 0;
        FreeText.Text = $"{SchemaSnapshot.FormatSize(free)} free";
        FreeBar.Value = used;
        if (used > 90) FreeBar.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        else FreeBar.ClearValue(ProgressBar.ForegroundProperty);
        var tip = $"{SchemaSnapshot.FormatSize(free)} free of {SchemaSnapshot.FormatSize(cap)} ({used:0.0}% used), " +
                  $"{known.Count} of {_tapes.Count} indexed tapes. A tape's space is recorded each time it's loaded in a drive.";
        ToolTipService.SetToolTip(FreeText, tip);
        ToolTipService.SetToolTip(FreeBar, tip);
    }

    // ------------------------------------------------------------ export

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedFileName = "tape listing",
            };
            picker.FileTypeChoices.Add("CSV", [".csv"]);
            if (await picker.PickSaveFileAsync() is not { } result) return;

            // everything shown under the current folder, expanded or not
            static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
            var lines = new List<string> { "Path,Size,Modified,Tapes" };
            void Walk(CatalogNode n)
            {
                foreach (var c in Visible(n))
                {
                    lines.Add(string.Join(",", Q(c.Path + (c.IsDirectory ? "/" : "")), c.Size,
                        Q(c.Modified?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""),
                        Q(string.Join("; ", c.Copies.Select(x => x.Tape.Label)))));
                    Walk(c);
                }
            }
            Walk(_folder);
            await File.WriteAllLinesAsync(result.Path, lines);
        }
        catch (Exception ex) { App.Activity.Note($"search: export failed: {ex.Message}", isError: true); }
    }
}
