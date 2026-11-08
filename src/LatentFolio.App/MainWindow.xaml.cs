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

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
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

    private const int WM_GETMINMAXINFO = 0x0024;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    // 五个字段都是 POINT（8 字节），不能用 RECT 顶替 —— 那样结构体会翻倍，
    // 读到的是垃圾，写回去还会踩坏后面的内存。
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 最大化时别把窗口撑到屏幕外。WindowChrome 的无边框窗口最大化会先按整块屏幕
    /// 再往外加一圈 resize border（实测拿到 -8,-8 2576x1456，比工作区高 76 px），
    /// 结果底部的胶片条和状态栏直接被切掉。这里把最大化尺寸钉回工作区。
    ///
    /// 必须先让 DefWindowProc 把结构填好默认值（尤其 ptMaxTrackSize），
    /// 直接改写再 handled=true 会让最大跟踪尺寸留在 0，窗口会缩成 16x16。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var result = DefWindowProc(hwnd, msg, wParam, lParam);

        var hmon = MonitorFromWindow(hwnd, 2);
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (hmon == IntPtr.Zero || !GetMonitorInfo(hmon, ref mi))
        {
            handled = true;
            return result;
        }

        var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = mi.rcWork.Left;
        mmi.ptMaxPosition.Y = mi.rcWork.Top;
        mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
        mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
        System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return result;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Root.PropertyChanged += (_, pe) =>
        {
            // 换图/换源时若只是分辨率变了，缩放保持不动；换图才重置
            if (pe.PropertyName == nameof(AppVm.Current))
            {
                BigZoom.Reset();
                Dispatcher.BeginInvoke(new Action(ScrollFilmToCurrent),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        };
        BigZoom.WheelNavigate += dir =>
        {
            if (dir < 0) Vm.PrevImageCommand.Execute(null);
            else Vm.NextImageCommand.Execute(null);
        };
        await Vm.InitAsync();
        SyncSizeButtons();

        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--self-test"))
        {
            await SelfTest.RunAsync(this, Vm);
            Close();
            return;
        }
        if (args.Contains("--show-tags"))
        {
            var win = new TagsWindow(Vm) { Owner = this };
            win.ShowDialog();
        }
        if (args.Contains("--show-candidates"))
        {
            var win = new TagsWindow(Vm, "候选") { Owner = this };
            win.ShowDialog();
        }
        if (args.Contains("--show-filter"))
        {
            var win = new ColumnEditorWindow(null, filterMode: true) { Owner = this };
            win.ShowDialog();
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
        if (args.Contains("--show-people")) Vm.ShowPeopleGridCommand.Execute(null);
        if (args.Contains("--show-editor") && Vm.Columns.Count > 0)
        {
            var win = new ColumnEditorWindow(Vm.Columns[0]) { Owner = this };
            win.ShowDialog();
        }
        if (args.Contains("--show-settings"))
        {
            var win = new SettingsWindow(Vm) { Owner = this };
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
        StatsPane.Visibility = Visibility.Collapsed;
        Vm.ShowOverviewCommand.Execute(null);
    }

    private void OnDeletedClick(object sender, RoutedEventArgs e)
    {
        ColList.SelectedItem = null;
        FolderList.SelectedItem = null;
        StatsPane.Visibility = Visibility.Collapsed;
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

    private void OnPersonAvatar(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PersonVm p } el) return;
        e.Handled = true;
        var menu = new ContextMenu();

        var current = Vm.Current;
        var set = new MenuItem
        {
            Header = current is null ? "用当前查看的图作头像" : $"用「{current.FileName}」作头像",
            IsEnabled = current is not null,
        };
        set.Click += (_, _) => Vm.SetPersonAvatar(p.Tag, current!.Id);
        menu.Items.Add(set);

        var reset = new MenuItem { Header = "恢复默认头像（最高分那张）" };
        reset.Click += (_, _) => Vm.SetPersonAvatar(p.Tag, 0);
        menu.Items.Add(reset);

        menu.PlacementTarget = el;
        menu.IsOpen = true;
    }

    private void OnPeopleClick(object sender, RoutedEventArgs e)
    {
        StatsPane.Visibility = Visibility.Collapsed;
        Vm.ShowPeopleGridCommand.Execute(null);
    }

    private void OnClosePeople(object sender, RoutedEventArgs e) => Vm.ClosePeopleGridCommand.Execute(null);

    private void OnPeopleSort(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb) Vm.PeopleSort = cb.SelectedIndex;
    }

    private void OnCandidates(object sender, RoutedEventArgs e)
    {
        var win = new TagsWindow(Vm, "候选") { Owner = this };
        win.ShowDialog();
        Vm.ApplyPeopleFilter();
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

    // ================= 我的栏右键菜单 =================

    private ColumnVm? ColumnAt(Point p)
    {
        for (int i = 0; i < Vm.Columns.Count; i++)
        {
            if (ColList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem c) continue;
            var origin = c.TranslatePoint(new Point(0, 0), ColList);
            if (p.Y >= origin.Y && p.Y <= origin.Y + c.ActualHeight) return c.DataContext as ColumnVm;
        }
        return null;
    }

    private void OnColRightUp(object sender, MouseButtonEventArgs e)
    {
        var col = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as ColumnVm
                  ?? ColumnAt(e.GetPosition(ColList));
        if (col is null) return;
        ColList.SelectedItem = col;
        e.Handled = true;

        var menu = new ContextMenu();
        void Item(string header, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        Item("编辑条件…", () => EditColumn(col));
        Item("重命名…", () =>
        {
            var dlg = new PromptWindow("重命名", col.Name) { Owner = this };
            if (dlg.ShowDialog() == true) Vm.RenameColumn(col.Id, dlg.Value);
        });
        Item("复制", () => Vm.DuplicateColumn(col.Id));
        Item("另存为模板…", () =>
        {
            var dlg = new PromptWindow("模板名称", col.Name) { Owner = this };
            if (dlg.ShowDialog() == true) Vm.SaveAsTemplate(dlg.Value, col.Tree);
        });

        menu.Items.Add(new Separator());
        Item("上移", () => Vm.MoveColumnBy(col.Id, -1), Vm.Columns.IndexOf(col) > 0);
        Item("下移", () => Vm.MoveColumnBy(col.Id, 1), Vm.Columns.IndexOf(col) < Vm.Columns.Count - 1);

        menu.Items.Add(new Separator());
        var colorMenu = new MenuItem { Header = "标记色" };
        foreach (var (name, hex) in ColumnColors)
        {
            var mi = new MenuItem
            {
                Header = name,
                IsChecked = string.Equals(col.Color, hex, StringComparison.OrdinalIgnoreCase),
            };
            mi.Click += (_, _) => Vm.SetColumnColor(col.Id, hex);
            colorMenu.Items.Add(mi);
        }
        menu.Items.Add(colorMenu);

        menu.Items.Add(new Separator());
        Item("删除这一栏", () =>
        {
            if (MessageBox.Show($"删除「{col.Name}」这一栏？图片不会动。", "LatentFolio",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            Vm.DeleteColumn(col.Id);
            Vm.ShowOverviewCommand.Execute(null);
        });

        menu.PlacementTarget = ColList;
        menu.IsOpen = true;
    }

    private static readonly (string Name, string Hex)[] ColumnColors =
    [
        ("无", ""),
        ("红", "#E5484D"),
        ("橙", "#F5A524"),
        ("绿", "#30A46C"),
        ("蓝", "#3E63DD"),
        ("紫", "#8E4EC6"),
    ];

    private void EditColumn(ColumnVm col)
    {
        var win = new ColumnEditorWindow(col) { Owner = this };
        if (win.ShowDialog() != true) return;
        if (win.DeleteRequested)
        {
            Vm.DeleteColumn(col.Id);
            Vm.ShowOverviewCommand.Execute(null);
            return;
        }
        Vm.UpdateColumn(col.Id, win.ColumnName, win.Tree, win.ColumnColor);
        _ = Vm.RunColumnCountsAsync();
        Vm.RefreshGrid();
    }

    private void OnNewColumnClick(object sender, RoutedEventArgs e)
    {
        var win = new ColumnEditorWindow(null) { Owner = this };
        if (win.ShowDialog() == true) _ = Vm.AddColumnAsync(win.ColumnName, win.Tree, win.ColumnColor);
    }

    private void OnEditColumnClick(object sender, RoutedEventArgs e)
    {
        var col = Vm.View == ViewKind.Column ? Vm.Columns.FirstOrDefault(c => c.Id == Vm.ViewColumnId) : null;
        if (col is null)
        {
            OnNewColumnClick(sender, e);
            return;
        }
        EditColumn(col);
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

    // ================= 排序 / 尺寸 / 筛选 =================

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

    private void OnSizeStep(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string s || !int.TryParse(s, out var i)) return;
        Vm.TileStep = i;
        SyncSizeButtons();
    }

    private void SyncSizeButtons()
    {
        if (SizeBar is null) return;
        foreach (var child in SizeBar.Children)
        {
            if (child is not Button b || b.Content is not Border glyph) continue;
            bool on = b.Tag is string s && int.TryParse(s, out var i) && i == Vm.TileStep;
            glyph.Opacity = on ? 1.0 : 0.3;
            b.Background = on ? (Brush)FindResource("B.Sel") : Brushes.Transparent;
        }
    }

    private static FilterGroup One(FieldType f, CondOp op, string v) => new()
    {
        Op = BoolOp.And,
        Children = { new FilterCond { Field = f, Op = op, Value = v } },
    };

    private static MenuItem Mi(string header, bool check, Action act)
    {
        var mi = new MenuItem { Header = header, IsChecked = check, IsCheckable = true };
        mi.Click += (_, _) => act();
        return mi;
    }

    private void OpenMenu(UIElement anchor, ContextMenu menu)
    {
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>星级：仅 / 及以上 / 及以下 × 1-5 星。</summary>
    private void OnStarFilter(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        void Mode(string title, CondOp op, string suffix)
        {
            var sub = new MenuItem { Header = title };
            for (int n = 1; n <= 5; n++)
            {
                int v = n;
                var name = $"{v} 星 {suffix}";
                sub.Items.Add(Mi(new string('★', v), Vm.TempFilterName == name, () =>
                {
                    Vm.SetTempFilter(One(FieldType.Rating, op, v.ToString()), name);
                }));
            }
            menu.Items.Add(sub);
        }

        Mode("仅", CondOp.Eq, "仅");
        Mode("及以上", CondOp.Gte, "及以上");
        Mode("及以下", CondOp.Lte, "及以下");
        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "不限星级", IsEnabled = Vm.HasTempFilter };
        clear.Click += (_, _) => Vm.ClearFilterCommand.Execute(null);
        menu.Items.Add(clear);

        OpenMenu((UIElement)sender, menu);
    }

    /// <summary>角色：从当前结果里出现的角色中选。</summary>
    private void OnCharFilter(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var list = Vm.CharsInView();
        if (list.Count == 0)
            menu.Items.Add(new MenuItem { Header = "当前没有识别到角色", IsEnabled = false });
        foreach (var (tag, display, count) in list)
        {
            var name = "角色 · " + tag;
            menu.Items.Add(Mi($"{display}   {count}", Vm.TempFilterName == name,
                () => Vm.SetTempFilter(One(FieldType.Character, CondOp.Eq, tag), name)));
        }
        if (Vm.HasTempFilter)
        {
            menu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "清除筛选" };
            clear.Click += (_, _) => Vm.ClearFilterCommand.Execute(null);
            menu.Items.Add(clear);
        }
        OpenMenu((UIElement)sender, menu);
    }

    /// <summary>模型：从当前结果里出现的模型里选。</summary>
    private void OnModelFilter(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var list = Vm.ModelsInView();
        if (list.Count == 0)
            menu.Items.Add(new MenuItem { Header = "当前结果里没有模型信息", IsEnabled = false });
        foreach (var (model, count) in list)
        {
            var name = "模型 · " + model;
            menu.Items.Add(Mi($"{model}   {count}", Vm.TempFilterName == name,
                () => Vm.SetTempFilter(One(FieldType.Model, CondOp.Eq, model), name)));
        }
        if (Vm.HasTempFilter)
        {
            menu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "清除筛选" };
            clear.Click += (_, _) => Vm.ClearFilterCommand.Execute(null);
            menu.Items.Add(clear);
        }
        OpenMenu((UIElement)sender, menu);
    }

    private void OnFilterClear(object sender, RoutedEventArgs e) => Vm.ClearFilterCommand.Execute(null);

    /// <summary>条件树：真正的复杂组合还是走它，只是不摆在台面上。</summary>
    private void OnAdvancedFilter(object sender, RoutedEventArgs e)
    {
        var win = new ColumnEditorWindow(null, filterMode: true) { Owner = this };
        if (win.ShowDialog() == true) Vm.SetTempFilter(win.Tree, "高级筛选");
    }

    // ================= 胶片条：滚轮横滚 / 拖高度 =================

    private void OnFilmWheel(object sender, MouseWheelEventArgs e)
    {
        FilmScroll.ScrollToHorizontalOffset(FilmScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private Point _filmDragStart;
    private double _filmDragFrom;
    private bool _filmDragging;

    private void OnFilmDragStart(object sender, MouseButtonEventArgs e)
    {
        _filmDragStart = e.GetPosition(this);
        _filmDragFrom = Vm.FilmHeight;
        _filmDragging = true;
        (sender as UIElement)?.CaptureMouse();
        e.Handled = true;
    }

    private void OnFilmDragMove(object sender, MouseEventArgs e)
    {
        if (!_filmDragging || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        Vm.FilmHeight = Math.Clamp(_filmDragFrom - (p.Y - _filmDragStart.Y), 48, 300);
    }

    private void OnFilmDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_filmDragging) return;
        _filmDragging = false;
        (sender as UIElement)?.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ScrollFilmToCurrent()
    {
        int i = Vm.FilmstripIndex;
        if (i < 0) return;
        if (FilmList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement c)
            c.BringIntoView();
    }


    private void OnTogglePanel(object sender, RoutedEventArgs e) => Vm.PanelCollapsed = !Vm.PanelCollapsed;

    private void OnToggleLeftRail(object sender, RoutedEventArgs e) => Vm.ToggleLeftRailCommand.Execute(null);

    private void OnLeftSplitterDrag(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        Vm.SetLeftWidth(Vm.LeftWidth + e.HorizontalChange);
        e.Handled = true;
    }

    private void OnFolderSortMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        void Item(string header, AppVm.FolderSortKind kind)
        {
            var mi = new MenuItem { Header = header, IsChecked = Vm.FolderSort == kind };
            mi.Click += (_, _) => Vm.FolderSort = kind;
            menu.Items.Add(mi);
        }
        Item("名称", AppVm.FolderSortKind.Name);
        Item("张数", AppVm.FolderSortKind.Count);
        Item("日期", AppVm.FolderSortKind.Date);

        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    private void OnFolderChevron(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FolderVm f })
        {
            Vm.ToggleFolder(f);
            e.Handled = true;
        }
    }

    private void OnFilmClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ImageVm vm } && vm.Id != Vm.Current?.Id)
            Vm.OpenViewerCommand.Execute(vm);
    }

    // ================= 筛选 / 标签体系 =================

    private void OnTagsClick(object sender, RoutedEventArgs e)
    {
        var win = new TagsWindow(Vm) { Owner = this };
        win.ShowDialog();
        Vm.RefreshGrid();
    }

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
        Vm.ClosePeopleGridCommand.Execute(null);
        StatsPane.Visibility = Visibility.Visible;
        StatsBuilder.Build(StatsHost, Vm);
    }

    private void OnStatsClick(object sender, RoutedEventArgs e) => ShowStats();

    private void OnCloseStats(object sender, RoutedEventArgs e) => StatsPane.Visibility = Visibility.Collapsed;
}
