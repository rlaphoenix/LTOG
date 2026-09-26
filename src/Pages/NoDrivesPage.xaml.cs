using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

namespace LTOG;

/// <summary>Shown on the Drives page when no tape drive is detected: setup steps.</summary>
public sealed partial class NoDrivesPage : UserControl
{
    public NoDrivesPage()
    {
        InitializeComponent();
    }

    private void DeviceManager_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true }); }
        catch (Exception ex) { App.Activity.Note($"open Device Manager failed: {ex.Message}", isError: true); }
    }
}
