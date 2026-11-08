using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LatentFolio.App.ViewModels;

namespace LatentFolio.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppVm _vm;
    private bool _loading = true;

    public SettingsWindow(AppVm vm)
    {
        InitializeComponent();
        _vm = vm;
        Refresh();
        _loading = false;
    }

    private static Brush R(string key) => (Brush)Application.Current.FindResource(key);

    private void Refresh()
    {
        RootText.Text = _vm.LibraryRoot;

        var (bytes, files) = App.Services.Thumbs.Stat();
        GridCacheText.Text = $"{bytes / 1024.0 / 1024.0:F0} MB · {files:N0} 张";

        int archiveCount = 0;
        try
        {
            var dir = App.Services.Thumbs.ArchiveDir;
            if (Directory.Exists(dir))
                archiveCount = Directory.EnumerateFiles(dir, "*.webp", SearchOption.AllDirectories).Count();
        }
        catch { }
        ArchiveText.Text = $"{archiveCount:N0} 张";
        ArchiveBox.IsChecked = App.Services.Store.GetSetting("archive_thumbs") != "0";
        DedupeBox.IsChecked = App.Services.Store.GetSetting("dedupe") != "0";

        GpuText.Text = _vm.VsrStarted
            ? "显卡可用 · RTX Video Super Resolution 已就绪"
            : _vm.VsrReady
                ? "显卡可用 · RTX VSR 待命（第一次点右键 VSR 时才加载，省 3 秒和几百 MB）"
                : _vm.VsrStatus.Length == 0 ? "检测中…" : "不可用：" + _vm.VsrStatus;

        BuildChips();
    }

    private void BuildChips()
    {
        ThresholdBar.Children.Clear();
        foreach (var (value, label) in new[] { (3, "≥3 张"), (5, "≥5 张"), (10, "≥10 张"), (0, "全部") })
        {
            int v = value;
            var b = new Button
            {
                Content = label,
                Style = (Style)FindResource("Btn.Outline"),
                FontSize = 11.5,
                Height = 27,
                Padding = new Thickness(11, 0, 11, 0),
                Margin = new Thickness(0, 0, 6, 0),
                Background = _vm.StatsThreshold == v ? R("B.AccentSoft") : Brushes.White,
                BorderBrush = _vm.StatsThreshold == v ? R("B.Accent") : R("B.BorderStrong"),
                Foreground = _vm.StatsThreshold == v ? R("B.Accent") : R("B.Text2"),
            };
            b.Click += (_, _) => { _vm.StatsThreshold = v; BuildChips(); };
            ThresholdBar.Children.Add(b);
        }

        ScaleBar.Children.Clear();
        foreach (var (value, label) in new[] { (2, "2×"), (4, "4×") })
        {
            int v = value;
            var b = new Button
            {
                Content = label,
                Style = (Style)FindResource("Btn.Outline"),
                FontSize = 11.5,
                Height = 27,
                Padding = new Thickness(14, 0, 14, 0),
                Margin = new Thickness(0, 0, 6, 0),
                Background = _vm.VsrScale == v ? R("B.AccentSoft") : Brushes.White,
                BorderBrush = _vm.VsrScale == v ? R("B.Accent") : R("B.BorderStrong"),
                Foreground = _vm.VsrScale == v ? R("B.Accent") : R("B.Text2"),
            };
            b.Click += (_, _) => { _vm.VsrScale = v; BuildChips(); };
            ScaleBar.Children.Add(b);
        }
    }

    private async void OnChangeRoot(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 ComfyUI 的 output 目录",
            InitialDirectory = Directory.Exists(_vm.LibraryRoot) ? _vm.LibraryRoot : "",
        };
        if (dlg.ShowDialog() != true) return;

        App.Services.LibraryRoot = dlg.FolderName;
        App.Services.ThumbService.ClearMemory();
        _vm.LibraryRoot = dlg.FolderName;
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.StartWatcher();
        Refresh();
    }

    private async void OnRescan(object sender, RoutedEventArgs e)
    {
        await _vm.ScanCommand.ExecuteAsync(null);
        Refresh();
    }

    private void OnArchiveChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = ArchiveBox.IsChecked == true;
        App.Services.Store.SetSetting("archive_thumbs", on ? "1" : "0");
        App.Services.Scanner.ArchiveThumbsEnabled = on;
    }

    private void OnDedupeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = DedupeBox.IsChecked == true;
        App.Services.Store.SetSetting("dedupe", on ? "1" : "0");
        App.Services.Scanner.DedupeByHash = on;
        if (!on) App.Services.Store.ClearSkipped();
    }

    private void OnClearSkip(object sender, RoutedEventArgs e)
    {
        App.Services.Store.ClearSkipped();
        _vm.StatusLeft = "判重记录已清空，下次扫描会重新收一遍重复图";
    }

    private void OnClearCache(object sender, RoutedEventArgs e)    {
        try
        {
            App.Services.ThumbService.ClearMemory();
            var dir = App.Services.Thumbs.GridDir;
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.webp", SearchOption.AllDirectories))
                    File.Delete(f);
            }
        }
        catch { }
        Refresh();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

