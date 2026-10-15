using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

namespace LTOG;

/// <summary>Captured LTFS index snapshots in the working folder; opens them in the viewer.</summary>
public sealed partial class TapesPage : UserControl
{
    public TapesPage()
    {
        InitializeComponent();
        _loadingUi = true;
        SchemaSortCombo.SelectedIndex = Math.Clamp(App.Settings.IndexSort, 0, 3);
        _loadingUi = false;
    }

    private bool _loadingUi;
    private readonly List<Window> _childWindows = new();   // keep open viewers referenced

    private bool _schemasLoading;
    private List<SchemaItem> _schemaItems = new();

    public async void RefreshSchemas()
    {
        if (_schemasLoading) return;
        _schemasLoading = true;
        try
        {
            string folder = App.Settings.WorkFolder;
            _schemaItems = await Task.Run(() =>
                new DirectoryInfo(folder)
                    .GetFiles("*.schema")
                    .Select(SchemaParser.Summarize)
                    .ToList());
            foreach (var i in _schemaItems) i.Tape = Core.TapeDb.Get(i.Uuid);
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
        IEnumerable<SchemaItem> sorted = App.Settings.IndexSort switch
        {
            1 => _schemaItems.OrderByDescending(i => i.Title, SchemaParser.Natural),
            2 => _schemaItems.OrderByDescending(i => i.Written),
            3 => _schemaItems.OrderBy(i => i.Written),
            _ => _schemaItems.OrderBy(i => i.Title, SchemaParser.Natural),
        };
        SchemaList.ItemsSource = sorted.ToList();
    }

    private void SchemaSort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        App.Settings.IndexSort = Math.Max(0, SchemaSortCombo.SelectedIndex);
        App.Settings.Save();
        ApplySchemaSort();
    }

    private void SchemaOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SchemaItem item) return;
        try
        {
            if (!File.Exists(item.Path))
            {
                App.Activity.Note($"open snapshot failed: file no longer exists: {item.Path}", isError: true);
                RefreshSchemas();
                return;
            }

            var viewer = new SchemaViewerWindow(item.Path);
            _childWindows.Add(viewer);
            viewer.Closed += (_, _) => _childWindows.Remove(viewer);
            viewer.Activate();
        }
        catch (Exception ex) { App.Activity.Note($"open snapshot failed: {ex.Message}", isError: true); }
    }

    private void RefreshSchemas_Click(object sender, RoutedEventArgs e) => RefreshSchemas();

    private void OpenWorkFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(App.Settings.WorkFolder);
            Process.Start(new ProcessStartInfo(App.Settings.WorkFolder) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Activity.Note($"open folder failed: {ex.Message}", isError: true); }
    }
}
