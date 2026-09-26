using LTOG.Gui.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LTOG.Gui;

/// <summary>App-wide settings: mounting, index capture, index updates, advanced mount options.</summary>
public sealed partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        ApplySettingsToUi();
    }

    private bool _loadingUi;

    private void ApplySettingsToUi()
    {
        _loadingUi = true;
        EjectAfterUnmountCheck.IsChecked = App.Settings.EjectAfterUnmount;
        RemountCheck.IsChecked = App.Settings.RemountAtStartup;
        CaptureIndexCheck.IsChecked = App.Settings.CaptureIndex;
        WorkFolderBox.Text = App.Settings.WorkFolder;
        OverridePolicyCheck.IsChecked = App.Settings.OverrideSyncPolicy;
        PolicyDismountRadio.IsChecked = App.Settings.SyncPolicyMode == 0;
        PolicyPeriodicRadio.IsChecked = App.Settings.SyncPolicyMode == 1;
        PeriodBox.Value = App.Settings.SyncPeriodMinutes;
        AppendOnlyCheck.IsChecked = App.Settings.AppendOnly;
        OverrideIndexCheck.IsChecked = App.Settings.OverrideIndexPlacement;
        IndexSizeBox.Value = App.Settings.IndexMaxSize;
        IndexUnitCombo.SelectedIndex = Math.Clamp(App.Settings.IndexSizeUnit, 0, 2);
        IndexNameBox.Text = App.Settings.IndexNamePatterns;
        LogDirBox.Text = App.Settings.LogDirectory;
        LogDirBox.PlaceholderText = Settings.DefaultLogDirectory;
        VerbosityCombo.SelectedIndex = Math.Clamp(App.Settings.Verbosity, 0, 2);
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
        App.Settings.ApplyRunKey();
    }

    private void Period_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loadingUi) return;
        SaveSettingsFromUi();
    }

    private void SaveSettingsFromUi()
    {
        App.Settings.EjectAfterUnmount = EjectAfterUnmountCheck.IsChecked == true;
        App.Settings.RemountAtStartup = RemountCheck.IsChecked == true;
        App.Settings.CaptureIndex = CaptureIndexCheck.IsChecked == true;
        App.Settings.WorkFolder = string.IsNullOrWhiteSpace(WorkFolderBox.Text)
            ? @"C:\tmp\ltfs" : WorkFolderBox.Text.Trim();
        App.Settings.OverrideSyncPolicy = OverridePolicyCheck.IsChecked == true;
        App.Settings.SyncPolicyMode = PolicyDismountRadio.IsChecked == true ? 0 : 1;
        App.Settings.SyncPeriodMinutes = double.IsNaN(PeriodBox.Value) ? 5 : Math.Max(1, (int)PeriodBox.Value);
        App.Settings.AppendOnly = AppendOnlyCheck.IsChecked == true;
        App.Settings.OverrideIndexPlacement = OverrideIndexCheck.IsChecked == true;
        App.Settings.IndexMaxSize = double.IsNaN(IndexSizeBox.Value) ? 1 : Math.Max(1, (int)IndexSizeBox.Value);
        App.Settings.IndexSizeUnit = Math.Max(0, IndexUnitCombo.SelectedIndex);
        App.Settings.IndexNamePatterns = IndexNameBox.Text?.Trim() ?? "";
        App.Settings.LogDirectory = string.IsNullOrWhiteSpace(LogDirBox.Text)
            ? Settings.DefaultLogDirectory : LogDirBox.Text.Trim();
        App.Settings.Verbosity = Math.Max(0, VerbosityCombo.SelectedIndex);
        App.Settings.Save();
    }
}
