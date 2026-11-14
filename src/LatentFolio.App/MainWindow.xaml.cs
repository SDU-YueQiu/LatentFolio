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

    private Services.TrayIcon? _tray;
    private bool _exitForReal;

    /// <summary>点关闭时收到托盘而不是退出（设置里可关）。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>自检要用它检查缩放状态。</summary>
    internal Controls.ZoomBox BigZoomControl => BigZoom;
    internal ListBox GridListForTest => GridList;
    internal Button LeftTabForTest => LeftTab;
    internal Button DetailTabForTest => DetailTab;
    // 托盘相关（自检要能真的走一遍「关窗口只是收起来」）
    internal Services.TrayIcon? TrayForTest => _tray;
    internal bool CloseToTrayForTest => CloseToTray;
    internal bool ExitForRealForTest => _exitForReal;
    internal System.ComponentModel.CancelEventArgs ClosingForTest()
    {
        var e = new System.ComponentModel.CancelEventArgs();
        OnClosing(e);
        return e;
    }
    // 自检要能验证「点我的栏会切过去」和「鼠标侧键前进后退」这两条 UI 接线，
    // 光测 VM 测不出来（上一轮就是 VM 对了、XAML 里事件没接）
    internal System.Windows.Controls.ListBox ColListForTest => ColList;
    internal void NavBackForTest() => GoBack();
    internal void NavForwardForTest() => GoForward();
    internal void NavFlushForTest() => FlushNav();
    internal void ShowStatsForTest() => ShowStats();
    internal bool StatsVisibleForTest => StatsPane.Visibility == Visibility.Visible;
    internal Panel StatsHostForTest => StatsHost;
    internal Border DetailHostForTest => LibraryDetailHost;
    internal Border DetailEdgeBandForTest => DetailEdgeBand;
    internal System.Windows.Controls.Primitives.ScrollBar? GridScrollBarForTest =>
        FindScrollBar(GridList, Orientation.Vertical);

    internal Controls.SlimThumb? GridScrollThumbForTest =>
        GridScrollBarForTest is { } sb ? FindChild<Controls.SlimThumb>(sb) : null;

    private static System.Windows.Controls.Primitives.ScrollBar? FindScrollBar(DependencyObject root, Orientation o)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is System.Windows.Controls.Primitives.ScrollBar sb && sb.Orientation == o) return sb;
            if (FindScrollBar(c, o) is { } found) return found;
        }
        return null;
    }

    internal static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } found) return found;
        }
        return null;
    }

    /// <summary>自检：走一遍真实的拖宽算法（和鼠标事件里用的是同一套）。</summary>
    internal void WidthDragForTest(bool isDetail, double x0, double x1)
    {
        BeginWidthDrag(isDetail, x0);
        MoveWidthDrag(x1);
        EndWidthDrag();
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Root;
        FitToWorkArea();
        Loaded += OnLoaded;
        App.Root.StatsRequested += ShowStats;
        // 后台导入会整表重建：重建前先把编辑框里没提交的字刷回 VM，重建后再把选中项点回去
        App.Root.BeforeReload += FlushEditingText;
        App.Root.GridReloading += CaptureGridSelection;
        App.Root.GridReloaded += RestoreGridSelection;
        PreviewKeyDown += OnPreviewKey;
        // 鼠标侧键：后退 = 关查看器 / 清搜索 / 清筛选 / 回总览，前进 = 重新打开当前那张
        // 监听装在 Services.SideButtons（FrameworkElement 类处理），菜单弹层和独立窗口也覆盖
        BigZoom.ZoomChanged += UpdateZoomLabel;
        // VSR 出图后源换了，百分比也要跟着重算
        App.Root.CurrentChangedForViewer += UpdateZoomLabel;
        // VSR 出图 / 切原图 只换 ViewerImage，不换 Current —— 百分比要跟着这两个属性走
        App.Root.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppVm.ViewerImage) or nameof(AppVm.ShowOriginal)) UpdateZoomLabel();
        };
    }

    /// <summary>侧键：back = 返回，否则前进。除主窗口客户端外，非客户区（标题栏/边框）也走这里。</summary>
    internal void SideButton(bool back)
    {
        // 同一次按下可能从两条路进来（客户区路由 + 非客户区钩子），90ms 内的重复按键丢掉
        long now = Environment.TickCount64;
        if (now - _lastSideTick < 90) return;
        _lastSideTick = now;

        if (back) GoBack();
        else GoForward();
    }

    private long _lastSideTick;

    // ================= 浏览历史（侧键 back / forward） =================

    /// <summary>
    /// 一屏状态：页面 + 网格条件 + 查看器里那张。
    /// 侧键要的是「浏览器那样」的来去，不是写死的「关查看器 → 清搜索 → 清筛选 → 回总览」——
    /// 那条链子在「总览 → 图片 → 总览 → 统计 → 总览」这种来回里根本回不到上一页。
    /// </summary>
    private sealed record NavStep(
        bool Stats, bool People, bool Viewer,
        ViewKind View, long ColumnId, string Folder, string Person,
        string Search, string FilterName, FilterNode? FilterTree, long ViewerId);

    private readonly List<NavStep> _nav = [];
    private int _navAt = -1;
    private bool _navSuppress;
    private System.Windows.Threading.DispatcherTimer? _navTimer;

    private NavStep CaptureNav() => new(
        Stats: StatsPane.Visibility == Visibility.Visible,
        People: Vm.ShowPeoplePane,
        Viewer: Vm.ViewerOpen,
        View: Vm.View,
        ColumnId: Vm.ViewColumnId,
        Folder: Vm.ViewFolder,
        Person: Vm.ViewPerson,
        Search: Vm.SearchText,
        FilterName: Vm.TempFilterName,
        FilterTree: Vm.TempFilter,
        ViewerId: Vm.ViewerOpen ? Vm.Current?.Id ?? 0 : 0);

    /// <summary>状态变了：等它稳一下再记一笔（连续打字只算一次）。</summary>
    private void NavTouched()
    {
        if (_navSuppress) return;
        if (_navTimer is null)
        {
            _navTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _navTimer.Tick += (_, _) => FlushNav();
        }
        _navTimer.Stop();
        _navTimer.Start();
    }

    /// <summary>把还没记下的当前状态记进历史（测试里也用它把时序钉住）。</summary>
    internal void FlushNav()
    {
        _navTimer?.Stop();
        if (_navSuppress) return;
        PushNav();
    }

    private void PushNav()
    {
        var step = CaptureNav();
        if (_navAt >= 0 && _navAt < _nav.Count && _nav[_navAt].Equals(step)) return;

        if (_navAt >= 0 && _navAt + 1 < _nav.Count)
            _nav.RemoveRange(_navAt + 1, _nav.Count - _navAt - 1);

        _nav.Add(step);
        if (_nav.Count > 150) _nav.RemoveAt(0);
        _navAt = _nav.Count - 1;
    }

    private void OnNavPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppVm.View) or nameof(AppVm.ViewColumnId) or nameof(AppVm.ViewFolder)
            or nameof(AppVm.ViewPerson) or nameof(AppVm.SearchText) or nameof(AppVm.TempFilter)
            or nameof(AppVm.TempFilterName) or nameof(AppVm.ViewerOpen) or nameof(AppVm.Current)
            or nameof(AppVm.ShowPeoplePane))
            NavTouched();
    }

    private void GoBack()
    {
        FlushNav();
        if (_navAt <= 0) return;
        var target = _nav[_navAt - 1];
        ApplyNav(target);
        _navAt--;
        _navTimer?.Stop();
    }

    private void GoForward()
    {
        FlushNav();
        if (_navAt + 1 >= _nav.Count) return;
        var target = _nav[_navAt + 1];
        ApplyNav(target);
        _navAt++;
        _navTimer?.Stop();
    }

    private void ApplyNav(NavStep s)
    {
        _navSuppress = true;
        try
        {
            if (s.People)
            {
                Vm.ShowPeopleGridCommand.Execute(null);
            }
            else
            {
                switch (s.View)
                {
                    case ViewKind.Column when Vm.Columns.FirstOrDefault(c => c.Id == s.ColumnId) is { } col:
                        Vm.ShowColumnCommand.Execute(col);
                        break;
                    case ViewKind.Folder:
                        Vm.ShowFolderPath(s.Folder);
                        break;
                    case ViewKind.Person:
                        Vm.ShowPersonByTag(s.Person);
                        break;
                    case ViewKind.Deleted:
                        Vm.ShowDeletedCommand.Execute(null);
                        break;
                    default:
                        Vm.ShowOverviewCommand.Execute(null);
                        break;
                }
            }

            if (Vm.SearchText != s.Search) Vm.SearchText = s.Search;
            if (!ReferenceEquals(Vm.TempFilter, s.FilterTree) || Vm.TempFilterName != s.FilterName)
                Vm.SetTempFilter(s.FilterTree, s.FilterName);

            StatsPane.Visibility = s.Stats ? Visibility.Visible : Visibility.Collapsed;
            if (s.Stats) StatsBuilder.Build(StatsHost, Vm);

            if (s.Viewer && Vm.VmById.TryGetValue(s.ViewerId, out var img))
                Vm.OpenViewerCommand.Execute(img);
            else if (Vm.ViewerOpen)
                Vm.CloseViewerCommand.Execute(null);
        }
        finally
        {
            _navSuppress = false;
        }
    }

    private void FitToWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(1680, Math.Max(960, wa.Width - 24));
        Height = Math.Min(1060, Math.Max(640, wa.Height - 24));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    // ================= 托盘 =================

    private void MakeTray()
    {
        if (_tray is not null) return;
        try
        {
            _tray = new Services.TrayIcon("LatentFolio", ShowFromTray, OpenLibraryFolder, ExitFromTray);
        }
        catch
        {
            _tray = null;   // 托盘挂了也不该拖住整个程序
        }
    }

    /// <summary>真正退出（托盘菜单 / 自检收尾 / 诊断开关）——绕过「收到托盘」。</summary>
    public void RequestExit() => _exitForReal = true;

    private void ExitFromTray()
    {
        RequestExit();
        Close();
    }

    internal void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // 从托盘点回来时窗口在别的程序后面，先顶到最前再放开 Topmost
        Topmost = true;
        Topmost = false;
    }

    /// <summary>再点一次桌面上的图标：第二个实例会 PostMessage 过来，走和托盘一样的显示路径。</summary>
    private IntPtr OnTrayMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == App.WmShowFromTray)
        {
            Dispatcher.BeginInvoke(new Action(ShowFromTray));
            handled = true;
        }
        return IntPtr.Zero;
    }

    internal void OpenLibraryFolder()
    {
        try
        {
            if (Directory.Exists(Vm.LibraryRoot))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Vm.LibraryRoot)
                { UseShellExecute = true });
        }
        catch { }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exitForReal && CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _tray?.Dispose();
        _tray = null;
        base.OnClosing(e);
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
        // 桌面图标再点一次 = 第二个实例 PostMessage 过来，走和托盘一样的显示路径
        System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(OnTrayMessage);
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

    /// <summary>非客户区（标题栏/边框）上的鼠标侧键。</summary>
    private const int WM_NCXBUTTONDOWN = 0x00AB;

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
        // 标题栏/边框这类非客户区：WPF 不会把它变成 PreviewMouseDown，侧键得在这里接
        if (msg == WM_NCXBUTTONDOWN)
        {
            int xb = (int)((long)wParam >> 16) & 0xFFFF;
            SideButton(xb == 1);
            handled = true;
            return IntPtr.Zero;
        }
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
        // 浏览历史的起点；从这里开始，页面/搜索/筛选/查看器每变一次就记一笔
        _nav.Clear();
        _navAt = -1;
        Vm.PropertyChanged += OnNavPropertyChanged;
        PushNav();
        SyncSizeButtons();
        // 关窗口收到托盘（诊断/自检里不这么干，否则收尾的 Close() 会把进程留在托盘里）
        CloseToTray = !App.SuppressTray && App.Services.Store.GetSetting("close_to_tray") != "0";
        MakeTray();
        // 排序、尺寸这些"上次留下的状态"在 InitAsync 里已经读回来了，这里把控件摆成一致的样子
        if (SortBox.SelectedIndex != Vm.SortKeyIndex) SortBox.SelectedIndex = Vm.SortKeyIndex;
        if (Vm.LastScanLost > 0) AskAboutLostFiles(Vm.LastScanLost);

        var args = Environment.GetCommandLineArgs();
        // 截图/演示用：按评分倒序。README 里的图都这么拍——高分图一般没有太敏感的
        if (args.Contains("--sort-rating"))
        {
            Vm.SortKeyIndex = 1;    // 评分
            Vm.SortDesc = true;     // 高在前
            SortBox.SelectedIndex = 1;
        }
        if (args.Contains("--self-test"))
        {
            await SelfTest.RunAsync(this, Vm);
            Close();
            return;
        }
        if (args.Contains("--mem-report"))
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lf-mem.txt");
            System.IO.File.WriteAllText(outPath, Vm.BuildMemReport());
            // 诊断挂起：设了 LF_HOLD_MS 就多留一会儿，好让外面的 VMMap 类工具挂上来
            if (int.TryParse(Environment.GetEnvironmentVariable("LF_HOLD_MS"), out var hold) && hold > 0)
                await Task.Delay(hold);
            Close();
            return;
        }
        if (args.Contains("--retag"))
        {
            // 词表/别名改过之后把全库重新识别一遍（开发诊断用：不弹窗、跑完就退）
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Vm.RetagAll();
            var outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lf-retag.txt");
            System.IO.File.WriteAllText(outPath, Vm.StatusLeft + "\n" + Vm.LibrarySummary);
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
        // 开发诊断：--open-name <文件名片段> 直接选中某一张，方便截图核对
        int nameArg = Array.IndexOf(args, "--open-name");
        if (nameArg >= 0 && nameArg + 1 < args.Length)
        {
            var q = args[nameArg + 1];
            // 先按路径找（能区分同名文件），找不到再按文件名
            var hit = Vm.Items.FirstOrDefault(x => x.Item.Path.Replace('\\', '/').Contains(q.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                      ?? Vm.Items.FirstOrDefault(x => x.FileName.Contains(q, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                Vm.SelectSingle(hit);
                GridList.SelectedItem = hit;
                GridList.ScrollIntoView(hit);
                Vm.EnsureWorkflow(hit);
            }
        }
        if (args.Contains("--open-first") && Vm.Items.Count > 0)
            Vm.OpenViewerCommand.Execute(Vm.Items[0]);
        // 开发诊断：--filter-model <模型名> 直接按模型筛，方便截图核对
        int modelArg = Array.IndexOf(args, "--filter-model");
        if (modelArg >= 0 && modelArg + 1 < args.Length)
            Vm.SetTempFilter(One(FieldType.Model, CondOp.Eq, args[modelArg + 1]), "模型");
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

    // ================= 栏宽拖拽 =================

    // 两条边共用一套：按下记起点和当时的宽度，之后按横向位移改宽度。
    // 详情栏在右边，往左拖变宽；左栏在左边，往右拖变宽。
    private bool _widthDrag;
    private bool _dragIsDetail;
    private double _dragX0;
    private double _dragW0;

    private void BeginWidthDrag(bool isDetail, double x)
    {
        _widthDrag = true;
        _dragIsDetail = isDetail;
        _dragX0 = x;
        _dragW0 = isDetail ? Vm.DetailWidth : Vm.LeftWidth;
    }

    private void MoveWidthDrag(double x)
    {
        if (!_widthDrag) return;
        double dx = x - _dragX0;
        if (_dragIsDetail) Vm.SetDetailWidth(_dragW0 - dx);
        else Vm.SetLeftWidth(_dragW0 + dx);
    }

    private void EndWidthDrag() => _widthDrag = false;

    // 折叠手柄：按下不动 = 折叠/展开，按住横拖 = 调栏宽（超过 4px 才算拖，免得手抖）。
    private bool _handleDrag;
    private double _handleX0;

    private void OnHandleDown(object sender, MouseButtonEventArgs e)
    {
        _handleDrag = false;
        _handleX0 = e.GetPosition(this).X;
        BeginWidthDrag(ReferenceEquals(sender, DetailTab), _handleX0);
    }

    private void OnHandleMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        double x = e.GetPosition(this).X;
        if (!_handleDrag)
        {
            if (Math.Abs(x - _handleX0) < 4) return;
            _handleDrag = true;
        }
        MoveWidthDrag(x);
    }

    private void OnHandleUp(object sender, MouseButtonEventArgs e)
    {
        EndWidthDrag();
        if (!_handleDrag) return;
        _handleDrag = false;
        e.Handled = true;   // 拖过了就别再当成一次点击去折叠
    }

    // 详情栏整条边框都能按住横拖：带子贴边框内侧，手柄以外的位置拖就是调栏宽。
    // 以前这条带子铺在栏里面，把手柄和按钮的点击吃了；现在只压内边距，手柄又压在它上面。
    private void OnEdgeDown(object sender, MouseButtonEventArgs e)
    {
        BeginWidthDrag(ReferenceEquals(sender, DetailEdgeBand), e.GetPosition(this).X);
        if (sender is UIElement el) el.CaptureMouse();
        e.Handled = true;
    }

    private void OnEdgeMove(object sender, MouseEventArgs e)
    {
        if (!_widthDrag || e.LeftButton != MouseButtonState.Pressed) return;
        MoveWidthDrag(e.GetPosition(this).X);
    }

    private void OnEdgeUp(object sender, MouseButtonEventArgs e)
    {
        EndWidthDrag();
        if (sender is UIElement el && el.IsMouseCaptured) el.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnFolderSelected(object sender, SelectionChangedEventArgs e)
    {
        if (FolderList.SelectedItem is not FolderVm f) return;
        ColList.SelectedItem = null;
        Vm.ExpandFolder(f);       // 点目录顺手展开它的子目录
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
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        _colDragItem = item?.DataContext as ColumnVm;
        // 点「我的栏」必须立刻切过去。窗口没激活时第一次点击常常只把焦点给过去、
        // 选中事件不触发，表现就是"点了没反应"，这里直接把选中补上。
        if (item?.DataContext is ColumnVm col && !ReferenceEquals(ColList.SelectedItem, col))
            ColList.SelectedItem = col;
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

        Item("打开图库目录", OpenLibraryFolder);
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
        menu.Items.Add(new Separator());
        Item("展开所有文件夹", Vm.ExpandAllFolders);
        Item("折叠所有文件夹", Vm.CollapseAllFolders);

        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    // ================= 排序 / 尺寸 / 筛选 =================

    private void OnSortKeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (App.Root is null) return;
        Vm.SortKeyIndex = SortBox.SelectedIndex;
    }

    private void OnToggleSortDir(object sender, RoutedEventArgs e) => Vm.SortDesc = !Vm.SortDesc;

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

    /// <summary>筛选菜单里选一项：已经是它的话再点一次就取消。</summary>
    private void PickFilter(string name, FieldType field, CondOp op, string value, string label)
    {
        if (Vm.TempFilterName == name)
        {
            Vm.ClearFilterCommand.Execute(null);
            return;
        }
        Vm.SetTempFilter(One(field, op, value), name);
        _ = label;
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
                sub.Items.Add(Mi(new string('★', v), Vm.TempFilterName == name,
                    () => PickFilter(name, FieldType.Rating, op, v.ToString(), name)));
            }
            menu.Items.Add(sub);
        }

        Mode("仅", CondOp.Eq, "仅");
        Mode("及以上", CondOp.Gte, "及以上");
        Mode("及以下", CondOp.Lte, "及以下");

        OpenMenu((UIElement)sender, menu);
    }

    /// <summary>角色：从当前视图（不含临时筛选）里出现的角色中选。</summary>
    private void OnCharFilter(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var list = Vm.CharsInView();
        if (list.Count == 0)
            menu.Items.Add(new MenuItem { Header = "当前视图里没有识别到角色", IsEnabled = false });
        foreach (var (tag, display, count) in list)
        {
            var name = "角色 · " + tag;
            menu.Items.Add(Mi($"{display}   {count}", Vm.TempFilterName == name,
                () => PickFilter(name, FieldType.Character, CondOp.Eq, tag, display)));
        }
        OpenMenu((UIElement)sender, menu);
    }

    /// <summary>模型：从当前视图（不含临时筛选）里出现的模型里选。</summary>
    private void OnModelFilter(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var list = Vm.ModelsInView();
        if (list.Count == 0)
            menu.Items.Add(new MenuItem { Header = "当前视图里没有模型信息", IsEnabled = false });
        foreach (var (model, count) in list)
        {
            var name = "模型 · " + model;
            menu.Items.Add(Mi($"{model}   {count}", Vm.TempFilterName == name,
                () => PickFilter(name, FieldType.Model, CondOp.Eq, model, model)));
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

    // ================= 左栏分区高度：上下拖，位置记进设置 =================

    private bool _leftRowDragging;
    private string _leftRowWhat = "cols";
    private Point _leftRowStart;
    private double _leftRowFrom;

    private void OnLeftRowDragStart(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        _leftRowWhat = fe.Tag as string ?? "cols";
        _leftRowStart = e.GetPosition(this);
        _leftRowFrom = _leftRowWhat == "cols" ? Vm.LeftColumnsHeight : Vm.LeftPeopleHeight;
        _leftRowDragging = true;
        fe.CaptureMouse();
        e.Handled = true;
    }

    private void OnLeftRowDragMove(object sender, MouseEventArgs e)
    {
        if (!_leftRowDragging || e.LeftButton != MouseButtonState.Pressed) return;
        double d = e.GetPosition(this).Y - _leftRowStart.Y;
        double total = LeftPane.ActualHeight > 0 ? LeftPane.ActualHeight : 800;
        if (_leftRowWhat == "cols")
            Vm.SetLeftColumnsHeight(Math.Clamp(_leftRowFrom + d, 88, Math.Max(120, total - 260)));
        else
            Vm.SetLeftPeopleHeight(Math.Clamp(_leftRowFrom + d, 64, Math.Max(90, total - 300)));
    }

    private void OnLeftRowDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_leftRowDragging) return;
        _leftRowDragging = false;
        (sender as UIElement)?.ReleaseMouseCapture();
        e.Handled = true;
    }

    // ================= 源文件丢失 =================

    /// <summary>
    /// 启动扫描发现源文件不见了。问一句：当成「已删除」，还是先记成「源文件丢失」留着。
    /// </summary>
    private void AskAboutLostFiles(int count)
    {
        var r = MessageBox.Show(
            $"有 {count} 张图的源文件找不到了（可能是在资源管理器里删掉或改名了）。\n\n" +
            "「是」= 标记为已删除，跟在这里删的一样\n" +
            "「否」= 标记为源文件丢失（缩略图还在，文件回来还能恢复）",
            "LatentFolio", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) Vm.MarkMissingAsDeleted();
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
        Item("最近更新", AppVm.FolderSortKind.Date);

        menu.Items.Add(new Separator());
        var asc = new MenuItem { Header = "正序", IsChecked = !Vm.FolderDesc };
        asc.Click += (_, _) => Vm.FolderDesc = false;
        var desc = new MenuItem { Header = "倒序", IsChecked = Vm.FolderDesc };
        desc.Click += (_, _) => Vm.FolderDesc = true;
        menu.Items.Add(asc);
        menu.Items.Add(desc);

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

        // 重建后回填选中项：只同步集合和状态栏，不动 Current，也不顺手把右栏展开
        if (_restoringGrid) return;

        // 单选就把右栏详情换成这张；多选取最后点的那张
        if (GridList.SelectedItems.Count > 0 && GridList.SelectedItems[^1] is ImageVm last)
            Vm.SelectSingle(last);
        else if (!(Vm.IsReloading && Vm.Current is { } cur && Vm.Items.Contains(cur)))
            Vm.SelectSingle(null);
    }

    private HashSet<long> _gridKeepIds = [];
    private bool _restoringGrid;

    /// <summary>网格重建前记下选中了哪几张（按 id：ReloadFromDb 会换掉 VM 实例）。</summary>
    private void CaptureGridSelection() =>
        _gridKeepIds = GridList.SelectedItems.OfType<ImageVm>().Select(v => v.Id).ToHashSet();

    /// <summary>
    /// 重建后把选中项点回去。不做这一步的话，ListBox 的 Reset 会把选中项清空，
    /// 界面就会当成「用户取消了选择」，后台导入一张新图就把右栏关掉。
    /// </summary>
    private void RestoreGridSelection()
    {
        if (_gridKeepIds.Count == 0) return;
        var keep = _gridKeepIds;
        _gridKeepIds = [];
        // 当前图已经不在网格里（切了视图/筛选）：跟以前一样清空，别硬留一个
        if (Vm.Current is not { } cur || !Vm.Items.Contains(cur)) return;

        _restoringGrid = true;
        try
        {
            GridList.SelectedItems.Clear();
            foreach (var vm in Vm.Items)
                if (keep.Contains(vm.Id)) GridList.SelectedItems.Add(vm);
        }
        finally
        {
            _restoringGrid = false;
        }
    }

    /// <summary>备注是 Delay=400 的绑定；整库重建前先把它刷进 VM，否则最后几个字会被旧值覆盖。</summary>
    private void FlushEditingText()
    {
        if (Keyboard.FocusedElement is TextBox tb)
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
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

    // ================= 查看器缩放 =================

    private void OnZoomOut(object sender, RoutedEventArgs e) => BigZoom.SetScale(BigZoom.Zoom / 1.25);

    private void OnZoomIn(object sender, RoutedEventArgs e) => BigZoom.SetScale(BigZoom.Zoom * 1.25);

    private void OnZoomFit(object sender, RoutedEventArgs e) => BigZoom.Reset();

    private void OnZoomActual(object sender, RoutedEventArgs e) => BigZoom.ToggleActualSize();

    private void UpdateZoomLabel()
    {
        if (ZoomText is null) return;
        ZoomText.Text = $"{BigZoom.ActualScale * 100:F0}%";
    }
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
        NavTouched();
    }

    private void OnStatsClick(object sender, RoutedEventArgs e) => ShowStats();

    private void OnCloseStats(object sender, RoutedEventArgs e)
    {
        StatsPane.Visibility = Visibility.Collapsed;
        NavTouched();
    }
}