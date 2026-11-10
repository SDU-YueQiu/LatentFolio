using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        TrayBox.IsChecked = App.Services.Store.GetSetting("close_to_tray") != "0";

        GpuText.Text = _vm.VsrStarted
            ? $"显卡可用 · RTX VSR 已就绪 · {_vm.VsrQualityName}"
            : _vm.VsrReady
                ? "显卡可用 · RTX VSR 正在加载…"
                : _vm.VsrStatus.Length == 0 ? "检测中…" : "不可用：" + _vm.VsrStatus;

        BuildChips();
        BuildVsrChips();
    }

    private static readonly string[] LevelLabels = ["低", "中", "高", "极"];

    private static readonly string[] ModeLabels = ["放大", "降噪", "锐化", "高清", "直播"];

    private static readonly string[] ModeTips =
    [
        "超分放大，2× / 4×（对压缩视频源的通用模型）",
        "同分辨率去噪，去掉压缩噪点和大块伪影",
        "同分辨率去模糊，把糊的地方拉清晰",
        "干净源（无损 / 高码率）专用放大，不压细节",
        "直播画面用的放大模型",
    ];

    /// <summary>档位属于哪一组（-1 = 这组没有这个强度）。</summary>
    private static int GroupIndex(int quality)
    {
        var groups = LatentFolio.Vsr.VsrEngine.QualityGroups;
        for (int i = 0; i < groups.Length; i++)
            if (Array.IndexOf(groups[i].Values, quality) >= 0) return i;
        return 3;   // 认不出来就归到「高清放大」
    }

    /// <summary>档位在这一组里的位置；这组没有这个强度就返回 -1。</summary>
    private static int LevelIndex(int quality, int group)
    {
        int i = Array.IndexOf(LatentFolio.Vsr.VsrEngine.QualityGroups[group].Values, quality);
        return i;
    }

    private void BuildVsrChips()
    {
        ModeBar.Children.Clear();
        LevelBar.Children.Clear();
        ScaleBar.Children.Clear();

        int group = GroupIndex(_vm.VsrQuality);
        var groups = LatentFolio.Vsr.VsrEngine.QualityGroups;
        for (int i = 0; i < groups.Length; i++)
        {
            int gi = i;
            var b = new Button
            {
                Content = ModeLabels[i],
                ToolTip = ModeTips[i],
                Style = (Style)FindResource(gi == group ? "P.Set.SegOn" : "P.Set.Seg"),
            };
            b.Click += (_, _) =>
            {
                // 换组时保留当前强度，这组没有就看第一个可用的
                int level = LevelIndex(_vm.VsrQuality, gi);
                var vals = groups[gi].Values;
                if (level < 0 || vals[level] < 0)
                {
                    level = 0;
                    while (level < vals.Length && vals[level] < 0) level++;
                }
                if (level < vals.Length && vals[level] > 0)
                {
                    _vm.VsrQuality = vals[level];
                    BuildVsrChips();
                }
            };
            ModeBar.Children.Add(b);
        }

        int cur = LevelIndex(_vm.VsrQuality, group);
        for (int i = 0; i < LevelLabels.Length; i++)
        {
            int li = i;
            int value = groups[group].Values[i];
            var b = new Button
            {
                Content = LevelLabels[i],
                Style = (Style)FindResource(li == cur ? "P.Set.SegOn" : "P.Set.Seg"),
                IsEnabled = value > 0,
            };
            b.Click += (_, _) =>
            {
                int v = groups[group].Values[li];
                if (v <= 0) return;
                _vm.VsrQuality = v;
                BuildVsrChips();
            };
            LevelBar.Children.Add(b);
        }

        bool scaleApplies = _vm.VsrScaleApplies;
        foreach (var (value, label) in new[] { (2, "2×"), (4, "4×") })
        {
            int v = value;
            var b = new Button
            {
                Content = label,
                Style = (Style)FindResource(_vm.VsrScale == v ? "P.Set.SegOn" : "P.Set.Seg"),
                IsEnabled = scaleApplies,
                ToolTip = scaleApplies ? null : "降噪 / 去模糊是同分辨率处理，不放大",
            };
            b.Click += (_, _) => { _vm.VsrScale = v; BuildVsrChips(); };
            ScaleBar.Children.Add(b);
        }
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
                Style = (Style)FindResource(_vm.StatsThreshold == v ? "P.Set.SegOn" : "P.Set.Seg"),
            };
            b.Click += (_, _) => { _vm.StatsThreshold = v; BuildChips(); };
            ThresholdBar.Children.Add(b);
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

    private void OnTrayCloseChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = TrayBox.IsChecked == true;
        App.Services.Store.SetSetting("close_to_tray", on ? "1" : "0");
        if (Owner is MainWindow mw) mw.CloseToTray = on;
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

