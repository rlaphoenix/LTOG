using LTOG.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

namespace LTOG;

/// <summary>Version, components, credits and license.</summary>
public sealed partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
    }

    private bool _aboutLoaded;

    public async Task LoadAsync()
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
                App.Activity, LogKind.Tool, "ltfs --version (component probe)");
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

    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/rlaphoenix/LTOG")
            { UseShellExecute = true });
        }
        catch (Exception ex) { App.Activity.Note($"open GitHub failed: {ex.Message}", isError: true); }
    }
}
