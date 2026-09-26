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
        // Auto-scroll to the newest entry as activity streams in.
        Activity.Updated += () =>
        {
            if (AutoScrollCheck.IsChecked == true)
                LogScroll.ChangeView(null, double.MaxValue, null, true);
        };
    }

    public ActivityLog Activity => App.Activity;

    private void ClearLog_Click(object sender, RoutedEventArgs e) => App.Activity.Clear();

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(App.Activity.FilePath) { UseShellExecute = true }); }
        catch (Exception ex) { App.Activity.Note($"open log file failed: {ex.Message}", isError: true); }
    }
}
