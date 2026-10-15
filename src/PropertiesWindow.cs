using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LTOG;

/// <summary>Explorer-style properties of a search result: General tab, then its Cartridge.</summary>
internal static class PropertiesWindow
{
    public static Window Open(CatalogNode node)
    {
        string name = node.Parent == null ? "All tapes" : node.Name;
        var w = new Window { Title = $"{name} Properties", SystemBackdrop = new MicaBackdrop() };
        w.AppWindow.Resize(new Windows.Graphics.SizeInt32(480, 640));
        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
        if (File.Exists(ico)) w.AppWindow.SetIcon(ico);

        // the same path can be on several tapes; each copy has its own file and cartridge details
        var copy = new ComboBox
        {
            Header = "Copy on tape",
            ItemsSource = node.Copies.Select(c => c.Tape.Label).ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = node.Copies.Count > 1 ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 12),
        };
        var general = new StackPanel { Spacing = 8 };
        var cartridge = new StackPanel { Spacing = 8 };
        var tabs = new TabView
        {
            IsAddTabButtonVisible = false, CanReorderTabs = false, CanDragTabs = false,
            TabWidthMode = TabViewWidthMode.SizeToContent,
        };
        foreach (var (header, body) in new[] { ("General", general), ("Cartridge", cartridge) })
            tabs.TabItems.Add(new TabViewItem
            {
                Header = header, IsClosable = false,
                Content = new ScrollViewer
                {
                    Content = body, Padding = new Thickness(16),
                    Background = (Brush)Application.Current.Resources["TabViewItemHeaderBackgroundSelected"],
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1, 0, 1, 1),
                    CornerRadius = new CornerRadius(0, 0, 8, 8),
                },
            });
        tabs.SelectedIndex = 0;
        var ok = new Button
        {
            Content = "OK", MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        ok.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = Windows.System.VirtualKey.Enter });
        ok.Click += (_, _) => w.Close();

        void Render()
        {
            general.Children.Clear();
            cartridge.Children.Clear();
            if (node.Copies.Count == 0) return;
            var (tape, src) = node.Copies[Math.Max(0, copy.SelectedIndex)];
            GeneralTab(general, node, src, name);
            CartridgeTab(cartridge, tape);
        }
        copy.SelectionChanged += (_, _) => Render();
        Render();

        // no RowSpacing: it would still pad around the collapsed copy picker
        var root = new Grid { Padding = new Thickness(16) };
        var esc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (_, a) => { a.Handled = true; w.Close(); };
        root.KeyboardAccelerators.Add(esc);
        foreach (var h in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });
        foreach (var (el, row) in new (FrameworkElement, int)[] { (copy, 0), (tabs, 1), (ok, 2) })
        {
            Grid.SetRow(el, row);
            root.Children.Add(el);
        }
        w.Content = root;
        w.Activate();
        return w;
    }

    private static void GeneralTab(Panel p, CatalogNode node, SchemaNode src, string name)
    {
        Head(p, node.IsDirectory ? "" : "", name);
        Divider(p);
        string ext = Path.GetExtension(node.Name).TrimStart('.').ToUpperInvariant();
        Row(p, "Type", node.IsDirectory ? "File folder" : ext == "" ? "File" : $"{ext} File");
        Row(p, "Location", node.Parent?.Path);
        Row(p, "Size", SchemaSnapshot.FormatBytes(node.IsDirectory ? node.Size : src.Length ?? 0));
        if (node.IsDirectory)
        {
            int dirs = node.Descendants().Count(n => n.IsDirectory), files = node.Descendants().Count() - dirs;
            Row(p, "Contains", $"{files:N0} Files, {dirs:N0} Folders");
        }
        Row(p, "Stored on", string.Join(", ", node.Copies.Select(c => c.Tape.Label)));
        Divider(p);
        Row(p, "Created", src.CreationTime);
        Row(p, "Modified", src.ModifyTime);
        Row(p, "Accessed", src.AccessTime);
        Row(p, "Backup time", src.BackupTime);
        Divider(p);
        Row(p, "File ID", src.FileUid);
        if (!node.IsDirectory) Row(p, "Tape location", src.FirstLocationText);
        p.Children.Add(new CheckBox { Content = "Read-only", IsChecked = src.ReadOnly, IsEnabled = false });
    }

    private static void CartridgeTab(Panel p, CatalogTape tape)
    {
        var s = tape.Snapshot;
        var rec = tape.Record;
        Head(p, "", tape.Label);
        Divider(p);
        Row(p, "Volume name", s.Root.Name is "/" or "(empty index)" ? null : s.Root.Name);
        Row(p, "Barcode", rec?.Barcode);
        Row(p, "Serial", rec?.Serial);
        Row(p, "Capacity", rec == null ? null : SchemaSnapshot.FormatBytes(rec.Capacity));
        Row(p, "Free space", rec == null ? null : SchemaSnapshot.FormatBytes(rec.Free));
        Row(p, "Generation", rec?.Generation);
        Row(p, "Space as of", rec?.SeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        Divider(p);
        Row(p, "Volume UUID", s.VolumeUuid);
        Row(p, "Format version", s.FormatVersion);
        Row(p, "Index written by", s.Creator);
        Row(p, "Index revision", s.GenerationNumber);
        Row(p, "Updated", s.UpdatedText);
        Row(p, "Highest file id", s.HighestFileUid);
        Row(p, "Policy override", s.PolicyOverrideText);
        Row(p, "Volume lock", s.VolumeLockState);
        Divider(p);
        Row(p, "Index file", s.Path);
    }

    private static void Head(Panel p, string glyph, string title)
    {
        var g = new Grid { ColumnSpacing = 16 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new FontIcon { Glyph = glyph, FontSize = 32, HorizontalAlignment = HorizontalAlignment.Left });
        var box = new TextBox { Text = title, IsReadOnly = true, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(box, 1);
        g.Children.Add(box);
        p.Children.Add(g);
    }

    private static void Row(Panel p, string label, string? value)
    {
        var g = new Grid { ColumnSpacing = 16 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = label + ":", Opacity = 0.75 });
        var v = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? "Not specified" : value,
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
        };
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        p.Children.Add(g);
    }

    private static void Divider(Panel p) => p.Children.Add(new Border
    {
        Height = 1, Margin = new Thickness(0, 4, 0, 4),
        Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
    });
}
