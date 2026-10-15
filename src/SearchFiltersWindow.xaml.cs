using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LTOG;

/// <summary>Edits the Search page's <see cref="SearchOptions"/>; every change applies live.</summary>
public sealed partial class SearchFiltersWindow : Window
{
    private readonly SearchOptions _options;
    private readonly List<string> _types;
    private bool _loading;

    // client area, DIPs: fits the tallest state (every criterion on "Between") with no spare
    private const int ClientWidth = 740, ClientHeight = 470;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public SearchFiltersWindow(SearchOptions options, List<string> extensions)
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)(ClientWidth * scale), (int)(ClientHeight * scale)));
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
        {
            op.IsResizable = op.IsMaximizable = op.IsMinimizable = false;
            op.PreferredMinimumWidth = op.PreferredMaximumWidth = AppWindow.Size.Width;
            op.PreferredMinimumHeight = op.PreferredMaximumHeight = AppWindow.Size.Height;
        }
        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
        if (File.Exists(ico)) AppWindow.SetIcon(ico);

        _options = options;
        _types = ["", "files", "folders", .. extensions];
        TypeBox.ItemsSource = new[] { "All files and folders", "Files", "Folders" }
            .Concat(extensions.Select(x => $"{x} files")).ToList();
        Load(options);

        foreach (var c in new[] { PartialBox, CaseBox, AnyWordBox, DiacriticsBox, WildcardsBox, RegexBox, FuzzyBox })
            c.Click += (_, _) => Save();
        foreach (var c in new[] { TypeBox, DateOp, TimeOp, SizeOp, SizeUnit })
            c.SelectionChanged += (_, _) => Save();
        foreach (var c in new[] { DateA, DateB }) c.DateChanged += (_, _) => Save();
        foreach (var c in new[] { TimeA, TimeB }) c.TimeChanged += (_, _) => Save();
        foreach (var c in new[] { DateDays, TimeHours, SizeA, SizeB }) c.ValueChanged += (_, _) => Save();
    }

    private void Load(SearchOptions o)
    {
        _loading = true;
        PartialBox.IsChecked = o.Partial;
        CaseBox.IsChecked = o.CaseSensitive;
        AnyWordBox.IsChecked = o.AnyWord;
        DiacriticsBox.IsChecked = o.IgnoreDiacritics;
        WildcardsBox.IsChecked = o.Wildcards;
        RegexBox.IsChecked = o.UseRegex;
        FuzzyBox.IsChecked = o.Fuzzy;
        TypeBox.SelectedIndex = Math.Max(0, _types.IndexOf(o.Type));
        DateOp.SelectedIndex = o.DateOp;
        DateA.Date = o.DateA;
        DateB.Date = o.DateB;
        DateDays.Value = o.WithinDays;
        TimeOp.SelectedIndex = o.TimeOp;
        TimeA.Time = o.TimeA;
        TimeB.Time = o.TimeB;
        TimeHours.Value = o.WithinHours;
        SizeOp.SelectedIndex = o.SizeOp;
        SizeA.Value = o.SizeA;
        SizeB.Value = o.SizeB;
        SizeUnit.SelectedIndex = o.SizeUnit;
        _loading = false;
        Save();
    }

    private void Save()
    {
        if (_loading) return;
        static double Num(NumberBox b, double fallback) => double.IsNaN(b.Value) ? fallback : b.Value;
        var o = _options;
        o.Partial = PartialBox.IsChecked == true;
        o.CaseSensitive = CaseBox.IsChecked == true;
        o.AnyWord = AnyWordBox.IsChecked == true;
        o.IgnoreDiacritics = DiacriticsBox.IsChecked == true;
        o.Wildcards = WildcardsBox.IsChecked == true;
        o.UseRegex = RegexBox.IsChecked == true;
        o.Fuzzy = FuzzyBox.IsChecked == true;
        o.Type = _types[Math.Max(0, TypeBox.SelectedIndex)];
        o.DateOp = Math.Max(0, DateOp.SelectedIndex);
        o.DateA = DateA.Date?.Date ?? DateTime.Today;
        o.DateB = DateB.Date?.Date ?? DateTime.Today;
        o.WithinDays = Num(DateDays, 7);
        o.TimeOp = Math.Max(0, TimeOp.SelectedIndex);
        o.TimeA = TimeA.Time;
        o.TimeB = TimeB.Time;
        o.WithinHours = Num(TimeHours, 1);
        o.SizeOp = Math.Max(0, SizeOp.SelectedIndex);
        o.SizeA = Num(SizeA, 0);
        o.SizeB = Num(SizeB, 0);
        o.SizeUnit = Math.Max(0, SizeUnit.SelectedIndex);

        // only the inputs the chosen operator uses
        static Visibility Vis(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
        bool dWithin = o.DateOp == 6, dTwo = o.DateOp is 4 or 5, tWithin = o.TimeOp == 8, tTwo = o.TimeOp is 4 or 5, sTwo = o.SizeOp is 4 or 5;
        DateA.Visibility = Vis(o.DateOp > 0 && !dWithin);
        DateAnd.Visibility = DateB.Visibility = Vis(dTwo);
        DateDays.Visibility = DateDaysLabel.Visibility = Vis(dWithin);
        TimeA.Visibility = Vis(o.TimeOp > 0 && !tWithin);
        TimeAnd.Visibility = TimeB.Visibility = Vis(tTwo);
        TimeHours.Visibility = TimeHoursLabel.Visibility = Vis(tWithin);
        SizeA.Visibility = SizeUnit.Visibility = Vis(o.SizeOp > 0);
        SizeAnd.Visibility = SizeB.Visibility = Vis(sTwo);

        o.RaiseChanged();
    }

    private void Close_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Reset_Click(object sender, RoutedEventArgs e) => Load(new SearchOptions());
}
