using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using LatentFolio.App.ViewModels;
using LatentFolio.App.Views;
using LatentFolio.Core.Models;

namespace LatentFolio.App;

public partial class MainWindow : Window
{
    private AppVm Vm => App.Root;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Root;
        FitToWorkArea();
        Loaded += OnLoaded;
        App.Root.StatsRequested += ShowStats;
        PreviewKeyDown += OnPreviewKey;
    }

    private void FitToWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(1680, Math.Max(960, wa.Width - 24));
        Height = Math.Min(1060, Math.Max(640, wa.Height - 24));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    /// <summary>
    /// 按真实 DPI 算窗口大小。SystemParameters.WorkArea 在窗口还没连到显示器时
    /// 读到的是未缩放的数值，125% 缩放下会算出一个比屏幕还大的窗口，右边直接被裁掉。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        FitToMonitor();
    }

    private void FitToMonitor()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        double s = dpi / 96.0;

        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        var mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        int waW = 0, waH = 0, waL = 0, waT = 0;
        if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
        {
            waL = mi.rcWork.Left; waT = mi.rcWork.Top;
            waW = mi.rcWork.Right - mi.rcWork.Left;
            waH = mi.rcWork.Bottom - mi.rcWork.Top;
        }
        if (waW <= 0) { waW = (int)(SystemParameters.PrimaryScreenWidth * s); waH = (int)(SystemParameters.PrimaryScreenHeight * s); }

        double availW = waW / s - 32;
        double availH = waH / s - 32;
        Width = Math.Min(1680, Math.Max(960, availW));
        Height = Math.Min(1060, Math.Max(640, availH));
        Left = (waL + (waW - Width * s) / 2) / s;
        Top = (waT + (waH - Height * s) / 2) / s;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Root.CurrentChangedForViewer += UpdateParams;
        App.Root.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppVm.ViewerImage) or nameof(AppVm.Current)) BigZoom.Reset();
        };
        await Vm.InitAsync();

        // 开发用：方便截图/回归，不影响正常启动
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--open-first") && Vm.Items.Count > 0)
            Vm.OpenViewerCommand.Execute(Vm.Items[0]);
        if (args.Contains("--show-stats")) ShowStats();
        if (args.Contains("--show-editor") && Vm.Columns.Count > 0)
        {
            var win = new ColumnEditorWindow(Vm.Columns[0]) { Owner = this };
            win.ShowDialog();
        }
        if (args.Contains("--show-people"))
        {
            var win = new PeopleWindow(Vm) { Owner = this };
            win.ShowDialog();
        }
        if (args.Contains("--debug-size"))
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            uint sysDpi = GetDpiForWindow(hwnd);
            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            var mon = MonitorFromWindow(hwnd, 2);
            GetMonitorInfo(mon, ref mi);
            var txt = $"actual {ActualWidth:F0}x{ActualHeight:F0}\nwin {Width:F0}x{Height:F0}\n"
                      + $"dpi {dpi.DpiScaleX:F3} sys {sysDpi}\n"
                      + $"monitor {mi.rcMonitor.Right - mi.rcMonitor.Left}x{mi.rcMonitor.Bottom - mi.rcMonitor.Top} "
                      + $"work {mi.rcWork.Right - mi.rcWork.Left}x{mi.rcWork.Bottom - mi.rcWork.Top}\n"
                      + $"left,top {Left:F0},{Top:F0}\n"
                      + $"transform {PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11:F3}\n";
            System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lf-size.txt"), txt);
        }
    }

    // ================= 窗口按钮 =================

    private void OnMinClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxBtn.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnScanClick(object sender, RoutedEventArgs e) => await Vm.ScanCommand.ExecuteAsync(null);

    // ================= 导航 =================

    private void OnOverviewClick(object sender, RoutedEventArgs e)
    {
        ColList.SelectedItem = null;
        FolderList.SelectedItem = null;
        Vm.ShowOverviewCommand.Execute(null);
    }

    private void OnDeletedClick(object sender, RoutedEventArgs e)
    {
        ColList.SelectedItem = null;
        FolderList.SelectedItem = null;
        Vm.ShowDeletedCommand.Execute(null);
    }

    private void OnColumnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ColList.SelectedItem is not ColumnVm col) return;
        FolderList.SelectedItem = null;
        Vm.ShowColumnCommand.Execute(col);
    }

    private void OnFolderSelected(object sender, SelectionChangedEventArgs e)
    {
        if (FolderList.SelectedItem is not FolderVm f) return;
        ColList.SelectedItem = null;
        Vm.ShowFolder(f);
    }

    private void OnPersonClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PersonVm p }) Vm.ShowPerson(p);
    }

    private void OnPeopleClick(object sender, RoutedEventArgs e)
    {
        if (Vm.People.Count == 0) return;
        var win = new PeopleWindow(Vm) { Owner = this };
        win.ShowDialog();
    }

    // ================= 栏目编辑 =================

    private void OnNewColumnClick(object sender, RoutedEventArgs e)
    {
        var win = new ColumnEditorWindow(null) { Owner = this };
        if (win.ShowDialog() == true) _ = Vm.AddColumnAsync(win.ColumnName, win.Tree);
    }

    private void OnEditColumnClick(object sender, RoutedEventArgs e)
    {
        var col = Vm.View == ViewKind.Column ? Vm.Columns.FirstOrDefault(c => c.Id == Vm.ViewColumnId) : null;
        if (col is null)
        {
            MessageBox.Show("先选一个「我的栏」，或者点「+ 新建」。", "LatentFolio",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var win = new ColumnEditorWindow(col) { Owner = this };
        if (win.ShowDialog() == true)
        {
            Vm.UpdateColumn(col.Id, win.ColumnName, win.Tree);
            _ = Vm.RunColumnCountsAsync();
            Vm.RefreshGrid();
        }
    }

    // ================= 排序 / 尺寸 =================

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (App.Root is null) return;
        string key = SortBox.SelectedIndex switch
        {
            0 => "Date|1",
            1 => "Date|0",
            2 => "Rating|1",
            3 => "Rating|0",
            4 => "Name|1",
            5 => "Resolution|1",
            6 => "Size|1",
            _ => "Date|1",
        };
        Vm.SetSortCommand.Execute(key);
    }

    private void OnSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (App.Root is not null) App.Root.SetTileSize(e.NewValue);
    }

    // ================= 网格 =================

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Vm.Selection.Clear();
        foreach (var o in GridList.SelectedItems) if (o is ImageVm v) Vm.Selection.Add(v);
        Vm.RefreshSelectionStatus();
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridList.SelectedItem is ImageVm vm) Vm.OpenViewerCommand.Execute(vm);
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && GridList.SelectedItem is ImageVm vm)
        {
            Vm.OpenViewerCommand.Execute(vm);
            e.Handled = true;
            return;
        }
        if (e.Key is >= Key.D0 and <= Key.D5 && Keyboard.Modifiers == ModifierKeys.None)
        {
            int r = e.Key - Key.D0;
            if (Vm.Selection.Count > 0) Vm.SetRating(Vm.Selection, r);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && Vm.Selection.Count > 0)
        {
            if (Vm.View == ViewKind.Deleted) Vm.RestoreFromDeleted(Vm.Selection);
            else Vm.MoveToDeleted(Vm.Selection);
            e.Handled = true;
        }
        if (e.Key == Key.OemComma && Vm.Selection.Count > 0) { OnAddTag(); e.Handled = true; }
    }

    /// <summary>右键先把光标下的格子选中，再弹菜单。</summary>
    private void OnGridPreviewRightDown(object sender, MouseButtonEventArgs e)
    {
        var el = e.OriginalSource as DependencyObject;
        while (el is not null and not ListBoxItem) el = VisualTreeHelper.GetParent(el);
        if (el is ListBoxItem item && !item.IsSelected)
        {
            GridList.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void OnGridRightUp(object sender, MouseButtonEventArgs e)
    {
        if (Vm.Selection.Count == 0 && GridList.SelectedItem is ImageVm only)
        {
            Vm.Selection.Clear();
            Vm.Selection.Add(only);
        }
        if (Vm.Selection.Count == 0) return;

        var menu = BuildMenu(gallery: true);
        menu.PlacementTarget = GridList;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnViewerRightUp(object sender, MouseButtonEventArgs e)
    {
        if (Vm.Current is null) return;
        var menu = BuildMenu(gallery: false);
        menu.PlacementTarget = BigImage;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildMenu(bool gallery)
    {
        var menu = new ContextMenu();
        var targets = gallery ? Vm.Selection.ToList()
            : (Vm.Current is null ? [] : new List<ImageVm> { Vm.Current });

        void Item(string header, Action action, string? gesture = null, bool enabled = true, bool? check = null)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
            if (check is not null)
            {
                mi.Icon = new TextBlock
                {
                    Text = check.Value ? "\uE73E" : " ",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("B.Accent"),
                };
            }
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        if (gallery)
        {
            Item("打开", () =>
            {
                if (targets.Count > 0) Vm.OpenViewerCommand.Execute(targets[0]);
            }, "Enter");
            menu.Items.Add(new Separator());
        }

        bool vsrOk = Vm.VsrReady;
        Item("RTX VSR 处理", () =>
        {
            if (gallery) Vm.OpenViewerCommand.Execute(targets[0]);
            _ = Vm.ApplyVsrAsync();
        }, null, vsrOk);
        Item("显示原图", () => Vm.ToggleOriginalCommand.Execute(null), null, !gallery);
        Item("始终使用 RTX VSR", () => Vm.ToggleAlwaysVsrCommand.Execute(null), null, vsrOk, Vm.AlwaysVsr);

        menu.Items.Add(new Separator());
        Item("复制正向提示词", () => { if (targets.Count > 0) Vm.CopyPrompt(targets[0]); });
        Item("复制负面提示词", () => { if (targets.Count > 0) Vm.CopyNegative(targets[0]); });
        Item("复制元数据", () => { if (targets.Count > 0) Vm.CopyParams(targets[0]); });

        menu.Items.Add(new Separator());
        Item("清除评分", () => Vm.SetRating(targets, 0), "0");
        Item("添加标签…", OnAddTag, ",");

        menu.Items.Add(new Separator());
        Item("在文件夹中显示", () => { if (targets.Count > 0) Vm.RevealInExplorerCommand.Execute(targets[0]); });
        if (gallery)
        {
            if (Vm.View == ViewKind.Deleted)
                Item("还原", () => Vm.RestoreFromDeleted(targets), "Delete");
            else
                Item("删除", () => Vm.MoveToDeleted(targets), "Delete");
        }

        return menu;
    }

    private void OnAddTag()
    {
        var targets = Vm.Selection.Count > 0 ? Vm.Selection.ToList()
            : (Vm.Current is null ? new List<ImageVm>() : [Vm.Current]);
        if (targets.Count == 0) return;

        var dlg = new TagDialog(targets.Count) { Owner = this };
        var existing = App.Services.Store.LoadTags();
        using (var c = App.Services.Db.Cmd(
            $"SELECT DISTINCT tag_id FROM image_app_tag WHERE image_id IN ({string.Join(',', targets.Select(t => t.Id))})"))
        using (var r = c.ExecuteReader())
        {
            var ids = new List<long>();
            while (r.Read()) ids.Add(r.GetInt64(0));
            dlg.PreCheck(ids);
        }

        if (dlg.ShowDialog() != true) return;

        using var tx = App.Services.Db.Connection.BeginTransaction();
        var del = App.Services.Db.Cmd("DELETE FROM image_app_tag WHERE image_id=$i");
        var ins = App.Services.Db.Cmd("INSERT OR IGNORE INTO image_app_tag(image_id,tag_id) VALUES($i,$t)");
        foreach (var vm in targets)
        {
            del.Parameters.Clear();
            del.Parameters.AddWithValue("$i", vm.Id);
            del.ExecuteNonQuery();
            foreach (var tag in dlg.Selected)
            {
                ins.Parameters.Clear();
                ins.Parameters.AddWithValue("$i", vm.Id);
                ins.Parameters.AddWithValue("$t", tag);
                ins.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    // ================= 查看器 =================

    private void OnCloseViewer(object sender, RoutedEventArgs e) => Vm.CloseViewerCommand.Execute(null);
    private void OnPrev(object sender, RoutedEventArgs e) => Vm.PrevImageCommand.Execute(null);
    private void OnNext(object sender, RoutedEventArgs e) => Vm.NextImageCommand.Execute(null);
    private void OnVsrClick(object sender, RoutedEventArgs e) => _ = Vm.ApplyVsrAsync();
    private void OnToggleOriginal(object sender, RoutedEventArgs e) => Vm.ToggleOriginalCommand.Execute(null);

    private void OnReveal(object sender, RoutedEventArgs e) => Vm.RevealInExplorerCommand.Execute(Vm.Current);
    private void OnCopyPrompt(object sender, RoutedEventArgs e) => Vm.CopyPrompt(Vm.Current);
    private void OnCopyNegative(object sender, RoutedEventArgs e) => Vm.CopyNegative(Vm.Current);

    private void OnBigRatingChanged(object sender, int value)
    {
        if (Vm.Current is null) return;
        Vm.SetRating([Vm.Current], value);
    }

    private void OnNoteChanged(object sender, TextChangedEventArgs e) { }

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        if (!Vm.ViewerOpen) return;
        if (e.Key == Key.Escape) { Vm.CloseViewerCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Left) { Vm.PrevImageCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Right) { Vm.NextImageCommand.Execute(null); e.Handled = true; }
    }

    private void UpdateParams()
    {
        if (Vm.Current is null) { ParamsList.ItemsSource = null; return; }
        var c = Vm.Current;
        ParamsList.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("模型", c.ModelText),
            new("采样器", c.SamplerText),
            new("调度器", c.SchedulerText),
            new("步数", c.StepsText),
            new("CFG", c.CfgText),
            new("种子", c.SeedText),
        };
    }

    // ================= 统计 =================

    private void ShowStats()
    {
        StatsPane.Visibility = Visibility.Visible;
        StatsBuilder.Build(StatsHost, Vm);
    }

    private void OnStatsClick(object sender, RoutedEventArgs e) => ShowStats();

    private void OnCloseStats(object sender, RoutedEventArgs e) => StatsPane.Visibility = Visibility.Collapsed;
}
