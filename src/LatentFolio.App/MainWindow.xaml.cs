using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LatentFolio.App.ViewModels;
using LatentFolio.App.Views;
using LatentFolio.Core.Models;

namespace LatentFolio.App;

public partial class MainWindow : Window
{
    private AppVm Vm => App.Root;

    /// <summary>自检要用它检查缩放状态。</summary>
    internal Controls.ZoomBox BigZoomControl => BigZoom;
    internal ListBox GridListForTest => GridList;

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
        public Rect2 rcWork;
        public int dwFlags;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect2 { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// 按真实 DPI 算窗口大小。SystemParameters.WorkArea 在窗口还没连到显示器时
    /// 读到的是未缩放的数值，125% 缩放下会算出比屏幕还大的窗口。
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
        var mon = MonitorFromWindow(hwnd, 2);
        int waW = 0, waH = 0, waL = 0, waT = 0;
        if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
        {
            waL = mi.rcWork.Left; waT = mi.rcWork.Top;
            waW = mi.rcWork.Right - mi.rcWork.Left;
            waH = mi.rcWork.Bottom - mi.rcWork.Top;
        }
        if (waW <= 0)
        {
            waW = (int)(SystemParameters.PrimaryScreenWidth * s);
            waH = (int)(SystemParameters.PrimaryScreenHeight * s);
        }

        double availW = waW / s - 32;
        double availH = waH / s - 32;
        Width = Math.Min(1680, Math.Max(960, availW));
        Height = Math.Min(1060, Math.Max(640, availH));
        Left = (waL + (waW - Width * s) / 2) / s;
        Top = (waT + (waH - Height * s) / 2) / s;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Root.PropertyChanged += (_, pe) =>
        {
            // 换图/换源时若只是分辨率变了，缩放保持不动；换图才重置
            if (pe.PropertyName == nameof(AppVm.Current)) BigZoom.Reset();
        };
        await Vm.InitAsync();

        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--self-test"))
        {
            await SelfTest.RunAsync(this, Vm);
            Close();
            return;
        }
        if (args.Contains("--select-first") && Vm.Items.Count > 0)
        {
            GridList.SelectedItem = Vm.Items[0];
            Vm.SelectSingle(Vm.Items[0]);
        }
        if (args.Contains("--expand-workflow")) Vm.EnsureWorkflow(Vm.Items[0]);
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
    }

    // ================= 窗口按钮 =================

    private void OnMinClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnScanClick(object sender, RoutedEventArgs e) => await Vm.ScanCommand.ExecuteAsync(null);

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(Vm) { Owner = this };
        win.ShowDialog();
    }

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

    // ================= 我的栏拖拽排序 =================

    private Point _colDragStart;
    private ColumnVm? _colDragItem;

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private void OnColMouseDown(object sender, MouseButtonEventArgs e)
    {
        _colDragStart = e.GetPosition(ColList);
        _colDragItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as ColumnVm;
    }

    private void OnColMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _colDragItem is null) return;
        var p = e.GetPosition(ColList);
        if (Math.Abs(p.Y - _colDragStart.Y) < 8) return;

        var dragged = _colDragItem;
        DragDrop.DoDragDrop(ColList, dragged, DragDropEffects.Move);
        ColDragLine.Visibility = Visibility.Collapsed;
        _colDragItem = null;
    }

    private void OnColDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ColumnVm))) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        int idx = ColumnInsertIndex(e.GetPosition(ColList));
        double y = ColumnInsertY(idx);
        ColDragLine.Margin = new Thickness(6, Math.Max(0, y - 1), 6, 0);
        ColDragLine.Visibility = Visibility.Visible;
    }

    private void OnColDragLeave(object sender, DragEventArgs e) => ColDragLine.Visibility = Visibility.Collapsed;

    private void OnColDrop(object sender, DragEventArgs e)
    {
        ColDragLine.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(typeof(ColumnVm)) is not ColumnVm dragged) return;

        int to = ColumnInsertIndex(e.GetPosition(ColList));
        int from = Vm.Columns.IndexOf(dragged);
        if (from < 0) return;
        Vm.MoveColumn(from, to);
        e.Handled = true;
    }

    private int ColumnInsertIndex(Point p)
    {
        for (int i = 0; i < Vm.Columns.Count; i++)
        {
            if (ColList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem c) continue;
            var origin = c.TranslatePoint(new Point(0, 0), ColList);
            if (p.Y < origin.Y + c.ActualHeight / 2) return i;
        }
        return Vm.Columns.Count;
    }

    private double ColumnInsertY(int index)
    {
        if (ColList.ItemContainerGenerator.ContainerFromIndex(Math.Min(index, Vm.Columns.Count - 1)) is ListBoxItem c)
        {
            var origin = c.TranslatePoint(new Point(0, 0), ColList);
            return index >= Vm.Columns.Count ? origin.Y + c.ActualHeight : origin.Y;
        }
        return 0;
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
        if (win.ShowDialog() != true) return;

        if (win.DeleteRequested)
        {
            Vm.DeleteColumn(col.Id);
            Vm.ShowOverviewCommand.Execute(null);
            return;
        }
        Vm.UpdateColumn(col.Id, win.ColumnName, win.Tree);
        _ = Vm.RunColumnCountsAsync();
        Vm.RefreshGrid();
    }

    // ================= 图库目录 =================

    private void OnLibraryMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        void Item(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        Item("打开图库目录", () =>
        {
            try
            {
                if (Directory.Exists(Vm.LibraryRoot))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Vm.LibraryRoot)
                    { UseShellExecute = true });
            }
            catch { }
        });
        Item("更换图库目录…", async () =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择 ComfyUI 的 output 目录",
                InitialDirectory = Directory.Exists(Vm.LibraryRoot) ? Vm.LibraryRoot : "",
            };
            if (dlg.ShowDialog() != true) return;
            App.Services.LibraryRoot = dlg.FolderName;
            App.Services.ThumbService.ClearMemory();
            var r = MessageBox.Show("换了目录要重新扫一遍。现在扫？", "LatentFolio",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;
            Vm.LibraryRoot = dlg.FolderName;
            await Vm.ScanCommand.ExecuteAsync(null);
            Vm.StartWatcher();
        });
        Item("重新扫描", async () => await Vm.ScanCommand.ExecuteAsync(null));

        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
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

    private void OnTogglePanel(object sender, RoutedEventArgs e) => Vm.PanelCollapsed = !Vm.PanelCollapsed;

    // ================= 网格 =================

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Vm.Selection.Clear();
        foreach (var o in GridList.SelectedItems) if (o is ImageVm v) Vm.Selection.Add(v);
        Vm.RefreshSelectionStatus();

        // 单选就把右栏详情换成这张；多选取最后点的那张
        if (GridList.SelectedItems.Count > 0 && GridList.SelectedItems[^1] is ImageVm last)
            Vm.SelectSingle(last);
        else
            Vm.SelectSingle(null);
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
    }

    /// <summary>右键先把光标下的格子选中，再弹菜单。</summary>
    private void OnGridPreviewRightDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is not null && !item.IsSelected)
        {
            GridList.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void OnGridRightUp(object sender, MouseButtonEventArgs e)
    {
        // 点在星星上：交给评分条自己处理（右键清空），不要弹菜单
        if (FindAncestor<Controls.RatingBar>(e.OriginalSource as DependencyObject) is not null) return;

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
            Item("打开", () => { if (targets.Count > 0) Vm.OpenViewerCommand.Execute(targets[0]); }, "Enter");
            menu.Items.Add(new Separator());
        }

        bool vsrOk = Vm.VsrReady;
        Item("RTX VSR 处理", () =>
        {
            if (gallery && targets.Count > 0 && !Vm.ViewerOpen) Vm.OpenViewerCommand.Execute(targets[0]);
            _ = Vm.ApplyVsrAsync();
        }, null, vsrOk);
        Item("显示原图", () => Vm.ToggleOriginalCommand.Execute(null), null, !gallery && !Vm.ShowOriginal);
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
        var idList = string.Join(',', targets.Select(t => t.Id));
        using (var c = App.Services.Db.Cmd(
            $"SELECT DISTINCT tag_id FROM image_app_tag WHERE image_id IN ({idList})"))
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

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        if (!Vm.ViewerOpen) return;
        if (e.Key == Key.Escape)
        {
            // 正在看 VSR 结果时 Esc 先切回原图（设计稿 §12.1「显示原图 Esc 同效」），
            // 已经在看原图再按才退出放大。
            if (!Vm.ShowOriginal) Vm.ToggleOriginalCommand.Execute(null);
            else Vm.CloseViewerCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Left) { Vm.PrevImageCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Right) { Vm.NextImageCommand.Execute(null); e.Handled = true; }
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

