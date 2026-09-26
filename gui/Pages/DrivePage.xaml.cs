using LTOG.Gui.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LTOG.Gui;

/// <summary>One drive's tab: header with mount controls, cartridge and drive dashboard, cartridge tools.</summary>
public sealed partial class DrivePage : UserControl
{
    public DriveViewModel ViewModel { get; }

    public DrivePage(DriveViewModel vm)
    {
        ViewModel = vm;
        InitializeComponent();
    }

    private void ReadOnly_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ReadOnlyChecked = (sender as CheckBox)?.IsChecked == true;

    private async void MountButton_Click(object sender, RoutedEventArgs e)
    {
        switch (ViewModel.Phase)
        {
            case MountPhase.Idle when ViewModel.SelectedLetter == null:
                await new ContentDialog
                {
                    Title = "LTOG", Content = "No free drive letter selected.",
                    CloseButtonText = "OK", XamlRoot = XamlRoot,
                }.ShowAsync();
                break;
            case MountPhase.Idle:
                await App.DriveStore.MountAsync(ViewModel);
                break;
            case MountPhase.Mounting:   // Cancel
            case MountPhase.Mounted:    // Unmount
                await App.DriveStore.UnmountAsync(ViewModel);
                break;
        }
    }

    private async void EjectLoad_Click(object sender, RoutedEventArgs e) =>
        await App.DriveStore.EjectOrLoadAsync(ViewModel);

    private async void Format_Click(object sender, RoutedEventArgs e)
    {
        if (!App.DriveStore.CanUseUtilities(ViewModel)) return;
        string dev = ViewModel.Drive.Device;

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
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var args = new List<string> { "-i", LtfsEnv.LtfsConf, "-d", dev };
        if (!string.IsNullOrWhiteSpace(serialBox.Text)) { args.Add("-s"); args.Add(serialBox.Text.Trim()); }
        if (!string.IsNullOrWhiteSpace(nameBox.Text)) { args.Add("-n"); args.Add(nameBox.Text.Trim()); }
        if (forceBox.IsChecked == true) args.Add("-f");
        await App.DriveStore.RunToolAsync(ViewModel, LtfsEnv.MkltfsExe, args, $"Format cartridge — {dev}");
    }

    private async void Unformat_Click(object sender, RoutedEventArgs e)
    {
        if (!App.DriveStore.CanUseUtilities(ViewModel)) return;
        string dev = ViewModel.Drive.Device;

        var dialog = new ContentDialog
        {
            Title = $"Unformat cartridge in {dev}",
            Content = "This removes the LTFS format (and all data) from the cartridge, " +
                      "returning it to a single unformatted partition. Continue?",
            PrimaryButtonText = "Unformat",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await App.DriveStore.RunToolAsync(ViewModel, LtfsEnv.UnltfsExe,
            new[] { "-i", LtfsEnv.LtfsConf, "-d", dev, "-y" }, $"Unformat cartridge — {dev}");
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (!App.DriveStore.CanUseUtilities(ViewModel)) return;
        string dev = ViewModel.Drive.Device;
        await App.DriveStore.RunToolAsync(ViewModel, LtfsEnv.LtfsckExe,
            new[] { "-i", LtfsEnv.LtfsConf, dev }, $"Check filesystem — {dev}");
    }

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
}
