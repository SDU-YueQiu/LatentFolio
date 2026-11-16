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

    /// <summary>自检/截图用：滚到最底下（RTX VSR 那张卡片在最后）。</summary>
    internal void ScrollToBottom()
    {
        Body.ScrollToEnd();
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

    private static readonly string[] ModeTips =
    [
        "LOW / MEDIUM / HIGH / ULTRA：超分放大，2× / 4×（对压缩视频源的通用模型）",
        "DENOISE_*：同分辨率去噪，去掉压缩噪点和大块伪影",
        "DEBLUR_*：同分辨率去模糊，把糊的地方拉清晰",
        "HIGHBITRATE_*：干净源（无损 / 高码率）专用放大，不压细节",
        "STREAMING_*：直播画面用的放大模型（只有 MEDIUM / ULTRA）",
    ];

    /// <summary>
    /// 界面上的名字直接用 NVIDIA Video Effects SDK 的 QualityLevel 原文，
    /// 不翻译：VSR / DENOISE / DEBLUR / HIGHBITRATE / STREAMING。
    /// </summary>
    internal static string ModeLabelFor(int group)
    {
        foreach (var v in LatentFolio.Vsr.VsrEngine.QualityGroups[group].Values)
        {
            if (v <= 0) continue;
            var name = LatentFolio.Vsr.VsrEngine.QualityName(v);
            int i = name.IndexOf('_');
            return i > 0 ? name[..i] : "VSR";
        }
        return "VSR";
    }

    /// <summary>档位名同样用原文：LOW / MEDIUM / HIGH / ULTRA；这组没有的档位不摆出来。</summary>
    internal static List<string> LevelLabelsFor(int group)
    {
        var list = new List<string>();
        foreach (var v in LatentFolio.Vsr.VsrEngine.QualityGroups[group].Values)
            if (v > 0) list.Add(LevelOf(v));
        return list;
    }

    private static string LevelOf(int quality)
    {
        var name = LatentFolio.Vsr.VsrEngine.QualityName(quality);
        int i = name.IndexOf('_');
        return i >= 0 ? name[(i + 1)..] : name;
    }

    /// <summary>档位属于哪一组（-1 = 这组没有这个强度）。</summary>
    private static int GroupIndex(int quality)
    {
        var groups = LatentFolio.Vsr.VsrEngine.QualityGroups;
        for (int i = 0; i < groups.Length; i++)
            if (Array.IndexOf(groups[i].Values, quality) >= 0) return i;
        return 3;   // 认不出来就归到 HIGHBITRATE
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
                Content = ModeLabelFor(gi),
                ToolTip = ModeTips[gi],
                Style = (Style)FindResource(gi == group ? "P.Set.SegOn" : "P.Set.Seg"),
            };
            b.Click += (_, _) =>
            {
                // 换组时保同名档位（ULTRA → ULTRA），这组没有就取最高的一档
                string want = LevelOf(_vm.VsrQuality);
                var vals = groups[gi].Values.Where(v => v > 0).ToArray();
                if (vals.Length == 0) return;
                int pick = Array.FindIndex(vals, v => LevelOf(v) == want);
                if (pick < 0) pick = vals.Length - 1;
                _vm.VsrQuality = vals[pick];
                BuildVsrChips();
            };
            ModeBar.Children.Add(b);
        }

        // 只摆这一组真有的档位：STREAMING 只有 MEDIUM / ULTRA，就别留两个空格子
        var levels = groups[group].Values.Where(v => v > 0).ToArray();
        foreach (var value in levels)
        {
            int v = value;
            var b = new Button
            {
                Content = LevelOf(v),
                Style = (Style)FindResource(v == _vm.VsrQuality ? "P.Set.SegOn" : "P.Set.Seg"),
            };
            b.Click += (_, _) => { _vm.VsrQuality = v; BuildVsrChips(); };
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

