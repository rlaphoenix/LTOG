using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

namespace LTOG;

/// <summary>The activity log: every LTFS, WinFsp and tape-drive call.</summary>
public sealed partial class LogPage : UserControl
{
    public LogPage()
    {
        InitializeComponent();
    }

    public ActivityLog Activity => App.Activity;

    /// <summary>
    /// Auto-scroll: follow the bottom whenever the log grows. SizeChanged fires after
    /// layout, so the new bottom is already known (scrolling straight after adding a
    /// line would stop short of it).
    /// </summary>
    private void LogContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (AutoScrollCheck.IsChecked == true)
            LogScroll.ChangeView(null, double.MaxValue, null, true);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => App.Activity.Clear();

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(App.Activity.FilePath) { UseShellExecute = true }); }
        catch (Exception ex) { App.Activity.Note($"open log file failed: {ex.Message}", isError: true); }
    }
}
