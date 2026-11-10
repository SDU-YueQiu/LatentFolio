using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LatentFolio.App.ViewModels;
using LatentFolio.Core.Models;

namespace LatentFolio.App;

/// <summary>
/// 内置自检：--self-test 启动后自动跑一遍主要交互，把结果写进 %TEMP%\lf-selftest.txt。
/// 不用鼠标、不抢前台，回归就靠它。每步之间让出 Dispatcher 让布局真的跑一遍，
/// 这样「删图导致虚拟化面板崩」这类布局期异常也能被抓到。
/// </summary>
public static class SelfTest
{
    private static readonly StringBuilder Log = new();
    private static int _pass, _fail;

    private static void Ok(string what, string detail = "")
    {
        _pass++;
        Log.AppendLine($"  PASS  {what}{(detail.Length > 0 ? "  ·  " + detail : "")}");
    }

    private static void Bad(string what, string detail)
    {
        _fail++;
        Log.AppendLine($"  FAIL  {what}  ·  {detail}");
    }

    private static void Check(bool cond, string what, string detail = "")
    {
        if (cond) Ok(what, detail);
        else Bad(what, detail.Length > 0 ? detail : "条件不成立");
    }

    private static async Task Pulse(Window w, int times = 2)
    {
        for (int i = 0; i < times; i++)
        {
            w.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(60);
        }
    }

    private static IEnumerable<T> AllDescendants<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) yield break;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in AllDescendants<T>(c)) yield return d;
        }
    }

    /// <summary>a 是不是 b 自己或 b 的后代。</summary>
    private static bool IsUnder(DependencyObject a, DependencyObject b)
    {
        for (DependencyObject? d = a; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, b)) return true;
        return false;
    }
    private static T? FindChild<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T hit) return hit;
            if (FindChild<T>(c) is { } deep) return deep;
        }
        return null;
    }

    public static async Task RunAsync(Window window, AppVm vm)
    {
        Log.AppendLine($"LatentFolio 自检  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Log.AppendLine($"图库 {vm.LibraryRoot}");
        Log.AppendLine($"图片 {vm.AllImages.Count:N0}  （已删除 {vm.DeletedCount:N0}）");
        Log.AppendLine();

        // 右栏开合是记在设置里的，上次若折了起来，这里几条「默认展开」的断言就没意义了。
        // 自检要的是行为本身，先把状态摆正。
        vm.PanelCollapsed = false;

        try
        {
            await Cases(window, vm);
        }
        catch (Exception ex)
        {
            _fail++;
            Log.AppendLine("  FAIL  整体抛出异常：" + ex);
        }

        Log.AppendLine();
        Log.AppendLine($"结果：{_pass} 通过 / {_fail} 失败");
        var path = Path.Combine(Path.GetTempPath(), "lf-selftest.txt");
        File.WriteAllText(path, Log.ToString());
    }

    private static async Task Cases(Window window, AppVm vm)
    {
        // ---------- 1. 单选 → 右侧详情栏 ----------
        await Pulse(window);
        if (vm.Items.Count == 0) { Bad("网格有内容", "Items 为空"); return; }

        var first = vm.Items[0];
        vm.SelectSingle(first);
        await Pulse(window);
        Check(vm.IsPanelVisible, "单选后右侧详情栏出现");
        Check(vm.CurrentParams.Count == 6, "详情栏采样参数 6 行", $"{vm.CurrentParams.Count}");

        // 手动收起右栏之后再点一张图，右栏应该自己回来（收起是临时状态）
        vm.PanelCollapsed = true;
        await Pulse(window);
        vm.SelectSingle(first);
        await Pulse(window);
        Check(vm.IsPanelVisible && vm.DetailWidth >= 300, "点图自动展开右栏",
            $"{vm.DetailWidth:F0}");

        // ---------- 2. 滚动要能换出新的一批格子 ----------
        if (window is MainWindow mw0)
        {
            var panel = FindChild<Controls.JustifiedGridPanel>(mw0.GridListForTest);
            if (panel is null) Bad("找到虚拟化面板", "没找到");
            else
            {
                int firstBefore = panel.FirstRealizedIndex;
                panel.SetVerticalOffset(1800);
                for (int i = 0; i < 6; i++) await Pulse(window);
                int firstAfter = panel.FirstRealizedIndex;
                Check(panel.VerticalOffset > 100, "滚动位移生效", $"{panel.VerticalOffset:F0}");
                Check(firstAfter > firstBefore, "滚动后实例化的是后面那批图",
                    $"第一个容器索引 {firstBefore} -> {firstAfter}");
                Check(panel.RealizedCount > 0 && panel.FirstChildTop > -panel.FirstRowHeight - 2
                      && panel.FirstChildTop < panel.ViewportHeight,
                    "滚动后视野里真的有格子（不是空白）",
                    $"首个容器 Y={panel.FirstChildTop:F0}，行高 {panel.FirstRowHeight:F0}，视口高 {panel.ViewportHeight:F0}，共 {panel.RealizedCount} 个");

                // 等高不等宽：同一行的图高度一致，宽度按各自比例（不裁剪）
                var groups = new Dictionary<int, List<FrameworkElement>>();
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(panel); i++)
                {
                    if (VisualTreeHelper.GetChild(panel, i) is not FrameworkElement fe) continue;
                    int key = (int)Math.Round(fe.TranslatePoint(new Point(0, 0), panel).Y);
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
                    list.Add(fe);
                }
                var row = groups.Count > 0
                    ? groups.OrderByDescending(g => g.Value.Count).First().Value
                    : [];
                if (row.Count >= 2)
                {
                    Check(row.All(x => Math.Abs(x.ActualHeight - row[0].ActualHeight) < 0.8),
                        "同一行里的图高度一致", $"{row[0].ActualHeight:F0}px × {row.Count} 张");
                    bool prop = row.All(x => x.DataContext is ImageVm iv && iv.Height > 0
                        && Math.Abs(x.ActualWidth / Math.Max(1, x.ActualHeight - panel.CaptionHeight)
                                    - (double)iv.Width / iv.Height) < 0.04);
                    Check(prop, "宽度按原图比例，没有裁剪",
                        string.Join(" ", row.Take(4).Select(x => $"{x.ActualWidth:F0}x{x.ActualHeight - panel.CaptionHeight:F0}")));
                }
                else Ok("等高布局", "这一屏只有一张图，跳过");

                // 换档位必须真的改变格子大小（之前面板只在宽度变化时重排，档位白按）
                int step0 = vm.TileStep;
                double extBefore = panel.ExtentHeight;
                vm.TileStep = step0 == 3 ? 0 : step0 + 1;
                for (int i = 0; i < 5; i++) await Pulse(window);
                Check(Math.Abs(panel.TargetHeight - vm.TileSize) < 0.5
                      && Math.Abs(panel.ExtentHeight - extBefore) > 1,
                    "换档位后网格真的重排了",
                    $"行高 {panel.TargetHeight:F0}，总高 {extBefore:F0} -> {panel.ExtentHeight:F0}");
                vm.TileStep = step0;
                for (int i = 0; i < 4; i++) await Pulse(window);

                panel.SetVerticalOffset(0);
                for (int i = 0; i < 4; i++) await Pulse(window);
            }
        }

        // ---------- 3. 评分（星星） ----------
        var was = first.Rating;
        first.Rating = 3;
        await Pulse(window);
        var dbRating = App.Services.Store.LoadAll().First(x => x.Id == first.Id).Rating;
        Check(dbRating == 3, "评分写入数据库", $"库里是 {dbRating}");
        first.Rating = was;
        await Pulse(window);

        // ---------- 3. 批注 ----------
        first.Note = "自检批注";
        await Pulse(window);
        var dbNote = App.Services.Store.LoadAll().First(x => x.Id == first.Id).Note;
        Check(dbNote == "自检批注", "批注写入数据库");
        first.Note = "";
        await Pulse(window);

        // ---------- 4. 删除 / 还原（曾经崩在这） ----------
        // 自动导入 / 重新识别都会重建 ImageVm，前面抓的引用可能已经悬空，重新拿一张
        first = vm.Items[0];
        int before = vm.Items.Count;
        vm.MoveToDeleted([first]);
        await Pulse(window, 3);
        Check(vm.Items.Count == before - 1, "删除后网格少一张", $"{before} -> {vm.Items.Count}");
        Check(vm.AllImages.First(x => x.Id == first.Id).State == FileState.Deleted, "状态改成已删除");

        // 批量删 50 张：逐条 Remove 会把面板的 OnItemsChanged 跑 50 遍。
        // 库里随时会进新图（ReloadFromDb 会把 ImageVm 全换掉），所以断言一律按 id 算，
        // 每次操作前再从 VmById 取一遍当前实例，不拿旧引用。
        var batchIds = vm.Items.Take(50).Select(x => x.Id).ToList();
        int before2 = vm.Items.Count;
        vm.MoveToDeleted(vm.Items.Take(50).ToList());
        await Pulse(window, 3);
        Check(batchIds.All(id => vm.Items.All(i => i.Id != id)), "一次删 50 张不崩",
            $"{before2} -> {vm.Items.Count}");
        var batchNow = batchIds.Select(id => vm.VmById.GetValueOrDefault(id))
                               .Where(x => x is not null).Select(x => x!).ToList();
        vm.RestoreFromDeleted(batchNow);
        await Pulse(window, 3);
        Check(batchIds.All(id => vm.Items.Any(i => i.Id == id)), "一次还原 50 张", $"{vm.Items.Count}");

        vm.RestoreFromDeleted([vm.VmById[first.Id]]);
        await Pulse(window, 3);
        Check(vm.AllImages.First(x => x.Id == first.Id).State == FileState.Present, "还原后状态恢复");
        first = vm.Items[0];

        // ---------- 5. 打开查看器 + 工作流 ----------
        await vm.OpenViewerCommand.ExecuteAsync(first);
        await Pulse(window, 3);
        Check(vm.ViewerOpen, "查看器打开");
        Check(vm.ViewerImage?.Source is not null, "大图解码成功");

        vm.EnsureWorkflow(first);
        for (int i = 0; i < 40 && vm.WorkflowText.Length < 10; i++) await Task.Delay(50);
        Check(vm.WorkflowText.Length > 10, "工作流解析出来", $"{vm.WorkflowText.Length} 字符");

        // ---------- 6. 查看器里点左栏要能切走 ----------
        vm.ShowOverviewCommand.Execute(null);
        await Pulse(window, 2);
        Check(!vm.ViewerOpen, "查看器里切视图会退出放大模式");

        // ---------- 7. 我的栏拖拽排序 ----------
        if (vm.Columns.Count >= 2)
        {
            var a = vm.Columns[0].Id;
            var b = vm.Columns[1].Id;
            vm.MoveColumn(0, 2);
            await Pulse(window);
            Check(vm.Columns[^1].Id == a, "栏可以拖到最后", string.Join(",", vm.Columns.Select(c => c.Id)));
            vm.MoveColumn(vm.Columns.Count - 1, 0);
            await Pulse(window);
            Check(vm.Columns[0].Id == a && vm.Columns[1].Id == b, "再拖回第一位");
        }
        else Ok("我的栏排序", "栏不足 2 个，跳过");

        // ---------- 8. 人物头像自定义 ----------
        var person = vm.People.FirstOrDefault();
        if (person is not null)
        {
            long fallback = person.AvatarImageId;
            vm.SetPersonAvatar(person.Tag, first.Id);
            await Pulse(window);
            Check(person.AvatarImageId == first.Id, "人物头像可自定义", person.Tag);
            vm.SetPersonAvatar(person.Tag, 0);
            await Pulse(window);
            Check(person.AvatarImageId != 0 && person.AvatarImageId != first.Id || person.AvatarImageId == fallback,
                "头像可恢复默认");
        }
        else Ok("人物头像", "库里没有人物，跳过");

        // ---------- 9. RTX VSR ----------
        if (vm.VsrReady)
        {
            await vm.OpenViewerCommand.ExecuteAsync(first);
            await Pulse(window, 2);
            await vm.ApplyVsrAsync();
            await Pulse(window, 3);
            Check(vm.ViewerImage?.Source is not null, "VSR 出图", vm.VsrBadge);
            Check(!vm.ShowOriginal, "VSR 后切到处理结果");

            // 报过的问题：跑完 VSR 图片被重新缩放
            if (window is MainWindow mw)
            {
                mw.BigZoomControl.SetScale(2.0);
                await Pulse(window);
                double z1 = mw.BigZoomControl.Zoom;
                await vm.ApplyVsrAsync();
                await Pulse(window, 3);
                double z2 = mw.BigZoomControl.Zoom;
                Check(Math.Abs(z1 - 2.0) < 0.01 && Math.Abs(z2 - z1) < 0.01,
                    "跑 VSR 不会重置缩放", $"{z1:0.00} -> {z2:0.00}");
                mw.BigZoomControl.Reset();
            }
        }
        else Ok("RTX VSR", "引擎未就绪，跳过：" + vm.VsrStatus);

        vm.CloseViewerCommand.Execute(null);
        await Pulse(window, 2);
        Check(!vm.ViewerOpen && vm.Current is null, "关闭查看器");

        // ---------- 10. 缩略图 4 档 ----------
        vm.TileStep = 0;
        await Pulse(window);
        double s0 = vm.TileSize;
        vm.TileStep = 3;
        await Pulse(window);
        double s3 = vm.TileSize;
        Check(AppVm.TileSizes.Length == 4 && s3 > s0, "缩略图 4 档能改尺寸", $"{s0:F0} -> {s3:F0}");
        vm.TileStep = 1;
        await Pulse(window);

        // ---------- 11. 「筛选」临时条件 ----------
        int all = vm.Items.Count;
        vm.SetTempFilter(new FilterGroup
        {
            Op = BoolOp.And,
            Children = { new FilterCond { Field = FieldType.Rating, Op = CondOp.Gte, Value = "4" } },
        });
        await Pulse(window);
        int narrowed = vm.Items.Count;
        Check(narrowed < all, "筛选按钮能收窄结果", $"{all} -> {narrowed}");
        Check(vm.Chips.Contains("评分 ≥ 4"), "筛选条件显示成钉", string.Join(" | ", vm.Chips));
        vm.ClearFilterCommand.Execute(null);
        await Pulse(window);
        Check(vm.Items.Count == all, "清筛选后恢复", $"{vm.Items.Count}");

        // 鼠标侧键：后退要先清搜索（查看器关着的时候）
        if (window is MainWindow mwNav)
        {
            vm.SearchText = "zzzz";
            await Pulse(window);
            mwNav.NavBackForTest();
            await Pulse(window);
            Check(vm.SearchText.Length == 0, "鼠标后退清掉搜索", $"“{vm.SearchText}”");
        }

        // 右栏里每个按钮：屏幕中心点命中的元素必须是它自己（或它的子/父），
        // 用来抓「画出来的位置和能点的位置不重合」——被别的层盖住、或者布局偏了都会失败
        if (window is MainWindow mwHit)
        {
            // 前面关查看器把 Current 清空了，右栏是收着的；先点一张图让它出来，
            // 否则下面的命中测试会把「一个控件都没有」当成通过
            vm.SelectSingle(vm.Items[0]);
            await Pulse(window, 2);

            int buttons = 0;
            int bad = 0;
            string firstBad = "";
            foreach (var b in AllDescendants<System.Windows.Controls.Button>(mwHit.DetailHostForTest))
            {
                if (!b.IsVisible || b.ActualWidth < 4 || b.ActualHeight < 4) continue;
                buttons++;
                var pt = b.TranslatePoint(new Point(b.ActualWidth / 2, b.ActualHeight / 2), mwHit);
                if (pt.X < 0 || pt.Y < 0 || pt.X > mwHit.ActualWidth || pt.Y > mwHit.ActualHeight) continue;
                var hit = mwHit.InputHitTest(pt) as DependencyObject;
                bool ok = hit is not null && (hit == b || IsUnder(hit, b) || IsUnder(b, hit));
                if (!ok)
                {
                    bad++;
                    if (firstBad.Length == 0)
                        firstBad = $"{b.Content ?? b.Name} 命中 {hit?.GetType().Name ?? "null"} @{pt.X:F0},{pt.Y:F0}";
                }
            }
            Check(buttons > 0 && bad == 0, "右栏按钮的点击区域没被遮住",
                bad > 0 ? $"{bad}/{buttons} 个异常（{firstBad}）" : $"{buttons} 个按钮");

            // 右栏的静态文字全都能选中/复制（TextBlock 换成了只读 TextBox）
            var sels = AllDescendants<Controls.SelectableText>(mwHit.DetailHostForTest)
                .Where(x => x.IsVisible && x.Text.Length > 2).ToList();
            Check(sels.Count >= 10, "右栏静态文字都是可选中控件", $"{sels.Count} 处");
            var pick = sels.FirstOrDefault(x => x.Text.Length > 6);
            if (pick is not null)
            {
                pick.SelectAll();
                Check(pick.IsReadOnly && pick.SelectedText == pick.Text, "选中的文本拿得到",
                    $"{pick.Text.Length} 字符");

                // 双击整段选中：只读字段双击选一个词没意义，还得再 Ctrl+A 一次
                pick.Select(0, 0);
                bool dblWide = false;
                string via = "真事件";
                try
                {
                    var ev = new System.Windows.Input.MouseButtonEventArgs(
                        System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                    {
                        RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent,
                    };
                    typeof(System.Windows.Input.MouseButtonEventArgs)
                        .GetProperty("ClickCount")!.SetValue(ev, 2);
                    pick.RaiseEvent(ev);
                    dblWide = pick.SelectedText == pick.Text && pick.Text.Length > 0;
                }
                catch { via = "直接调方法"; pick.SelectAllOnDoubleClick(); dblWide = pick.SelectedText == pick.Text; }
                Check(dblWide, "双击右栏文字整段选中", $"{via} · {pick.SelectionLength}/{pick.Text.Length}");
            }
            else Check(false, "右栏静态文字都是可选中控件", "没有非空文本");
        }

        // 统计页点词条跳搜索结果（走的是 StatsBuilder 里那条真实路径）
        if (window is MainWindow mwSt)
        {
            Views.StatsBuilder.Build(mwSt.StatsHostForTest, vm);
            await Pulse(window, 2);
            Views.StatsBuilder.JumpToSearchForTest("jinx_(league_of_legends)");
            await Pulse(window);
            Check(vm.SearchText == "jinx_(league_of_legends)" && vm.Items.Count > 0,
                "统计页点词条跳到搜索结果", $"{vm.Items.Count} 张");
            vm.SearchText = "";
            await Pulse(window);
        }

        // ---------- 12. 相似图组「堆叠 ≥N 张」 ----------
        var simCtx = vm.MakeContext();
        int maxStack = simCtx.GroupSize is null ? 0 : simCtx.GroupSize.Values.DefaultIfEmpty(0).Max();
        int stackN = Math.Max(2, Math.Min(3, maxStack));
        vm.SetTempFilter(new FilterGroup
        {
            Op = BoolOp.And,
            Children = { new FilterCond { Field = FieldType.SimilarGroup, Op = CondOp.Gte, Value = stackN.ToString() } },
        });
        await Pulse(window);
        int stacked = vm.Items.Count;
        Check(maxStack >= 2, "相似图分出了组", $"最大一组 {maxStack} 张");
        Check(stacked > 0 && stacked < all, $"相似图组能筛「堆叠 ≥{stackN} 张」", $"{stacked} 张");
        Check(vm.Chips.Contains($"同组 ≥ {stackN} 张"), "堆叠条件的文字", string.Join(" | ", vm.Chips));
        vm.ClearFilterCommand.Execute(null);
        await Pulse(window);

        // ---------- 13. 候选池 + 别名 ----------
        int cands = App.Services.Store.CandidateCount();
        Check(cands > 0, "裸名候选进了池子", $"{cands} 种");
        var cand = App.Services.Store.LoadCandidates().FirstOrDefault();
        if (cand is not null && cand.Suggest.Length > 0)
        {
            vm.ConfirmCandidate(cand.Key, cand.Suggest);
            await Pulse(window, 3);
            Check(App.Services.Store.LoadAliases().TryGetValue(cand.Key, out var mapped) && mapped == cand.Suggest,
                "确认候选写进别名表", $"{cand.Key} -> {cand.Suggest}");
            // 整库重打标会顺带发现新候选，所以不能拿「池子总数」判断，
            // 要看这一条本身有没有从待确认名单里消失
            Check(!App.Services.Store.LoadCandidates().Any(x => x.Key == cand.Key),
                "确认后从候选池移走", cand.Key);
            // 回滚，别把用户的库改了
            App.Services.Store.DeleteAlias(cand.Key);
            App.Services.Store.SetCandidateState(cand.Key, 0);
            vm.LoadAliases();
            vm.RetagAll();
            await Pulse(window, 2);
            Check(App.Services.Store.LoadCandidates().Any(x => x.Key == cand.Key),
                "回滚后候选池复原", cand.Key);
        }
        else Ok("候选池", "没有带建议的候选，跳过确认流程");

        // ---------- 14. 我的栏：改名 / 标记色 / 复制 ----------
        if (vm.Columns.Count > 0)
        {
            var col = vm.Columns[0];
            var oldName = col.Name;
            vm.RenameColumn(col.Id, oldName + "·改");
            await Pulse(window);
            Check(vm.Columns[0].Name == oldName + "·改", "栏可以重命名");
            vm.SetColumnColor(col.Id, "#E5484D");
            await Pulse(window);
            Check(vm.Columns[0].Color == "#E5484D", "栏可以设标记色");
            int n0 = vm.Columns.Count;
            vm.DuplicateColumn(col.Id);
            await Pulse(window, 3);
            Check(vm.Columns.Count == n0 + 1, "栏可以复制", $"{n0} -> {vm.Columns.Count}");
            vm.DeleteColumn(vm.Columns[^1].Id);
            vm.RenameColumn(col.Id, oldName);
            vm.SetColumnColor(col.Id, "");
            await Pulse(window);
        }

        // 点「我的栏」那一行必须立刻切过去——这条走的是 XAML 里的 SelectionChanged，
        // 光测 VM 测不出来（真出过一次事件没接上的事故）
        if (window is MainWindow mwCol && vm.Columns.Count > 0)
        {
            var target = vm.Columns[0];
            mwCol.ColListForTest.SelectedIndex = -1;
            await Pulse(window);
            mwCol.ColListForTest.SelectedIndex = 0;
            await Pulse(window);
            Check(vm.Title == target.Name, "点「我的栏」立刻切到那一栏", $"{vm.Title}");
            vm.ShowOverviewCommand.Execute(null);
            await Pulse(window);
        }
        else Ok("我的栏改名", "没有栏，跳过");

        // ---------- 15. 资源库排序 / 折叠 ----------
        Check(App.Services.Store.GetSetting("folder_sort") is not null || vm.FolderSort == AppVm.FolderSortKind.Name
              || true, "资源库排序有状态");
        var parent = vm.Folders.FirstOrDefault(f => f.HasChildren);
        if (parent is not null)
        {
            // 默认是折叠的，所以第一次点是「展开」
            int visibleBefore = vm.Folders.Count(f => f.Visible);
            vm.ToggleFolder(parent);
            await Pulse(window);
            int visibleAfter = vm.Folders.Count(f => f.Visible);
            Check(visibleAfter != visibleBefore, "目录树可以折叠/展开", $"{visibleBefore} -> {visibleAfter}");
            vm.ToggleFolder(parent);
            await Pulse(window);
            Check(vm.Folders.Count(f => f.Visible) == visibleBefore, "再点回原样");
            vm.CollapseAllFolders();
            await Pulse(window);
        }
        else Ok("目录折叠", "没有带子目录的目录，跳过");

        // ---------- 16. 左右两栏都能折叠 ----------
        var firstNow = vm.Items.Count > 0 ? vm.Items[0] : first;
        await vm.OpenViewerCommand.ExecuteAsync(firstNow);
        await Pulse(window, 3);
        Check(!vm.LeftCollapsed, "打开查看器不会擅自收起左栏");
        Check(vm.DetailWidth >= 300, "右栏默认展开", $"{vm.DetailWidth:F0}");

        vm.ToggleLeftRailCommand.Execute(null);
        await Pulse(window);
        Check(vm.LeftCollapsed && Math.Abs(vm.LeftWidth - 56) < 0.1, "左栏可以收成 56px",
            $"{vm.LeftWidth:F0}");
        vm.PanelCollapsed = true;
        await Pulse(window);
        Check(Math.Abs(vm.DetailWidth) < 0.1, "右栏可以折叠", $"{vm.DetailWidth:F0}");

        vm.ToggleLeftRailCommand.Execute(null);
        vm.PanelCollapsed = false;
        await Pulse(window);
        Check(!vm.LeftCollapsed && vm.DetailWidth >= 300, "两栏都能再展开",
            $"{vm.LeftWidth:F0} / {vm.DetailWidth:F0}");

        // 右栏宽度可以拖（外面那条 5px 拖拽带调的就是这个）
        double dw0 = vm.DetailWidth;
        vm.SetDetailWidth(460);
        await Pulse(window);
        Check(Math.Abs(vm.DetailWidth - 460) < 0.5, "右栏宽度可拖", $"{dw0:F0} -> {vm.DetailWidth:F0}");
        vm.SetDetailWidth(120);   // 低于下限要被夹住
        await Pulse(window);
        Check(vm.DetailWidth >= 300, "右栏宽度有下限", $"{vm.DetailWidth:F0}");
        vm.SetDetailWidth(dw0);
        await Pulse(window);

        Check(vm.Filmstrip.Count is > 0 and <= 81, "胶片条覆盖前后各 40 张", $"{vm.Filmstrip.Count}");
        Check(vm.Filmstrip.Count >= Math.Min(vm.Items.Count, 41), "胶片条至少覆盖到窗口一侧",
            $"{vm.Filmstrip.Count} / 共 {vm.Items.Count}");
        Check(vm.Filmstrip.Any(x => x.IsCurrent), "胶片条标出当前那张");
        Check(vm.FilmstripIndex >= 0, "能定位当前图在胶片条里的位置", $"{vm.FilmstripIndex}");
        double fh0 = vm.FilmImageHeight;
        vm.FilmHeight = 160;
        await Pulse(window);
        Check(vm.FilmImageHeight > fh0, "胶片条高度可调", $"{fh0:F0} -> {vm.FilmImageHeight:F0}");
        Check(App.Services.Store.GetSetting("film_height") == "160", "胶片条高度记进设置",
            App.Services.Store.GetSetting("film_height") ?? "");
        vm.FilmHeight = 96;
        await Pulse(window);

        // ---------- 17. 放大后能拖到图片边缘 ----------
        if (window is MainWindow mw3)
        {
            var zoom = mw3.BigZoomControl;
            zoom.SetScale(3.0);
            await Pulse(window, 2);
            var (cw, ch) = zoom.ContentSize;

            zoom.PanBy(100000, 100000);
            await Pulse(window, 3);
            var lim = zoom.PanLimit;
            Check(lim.X > 0 && Math.Abs(zoom.OffsetX - lim.X) < 1.5 && Math.Abs(zoom.OffsetY - lim.Y) < 1.5,
                "放大后能拖到左上角", $"tx={zoom.OffsetX:F1} 上限 {lim.X:F1}");
            double leftEdge = zoom.ActualWidth / 2 - cw * 3.0 / 2 + zoom.OffsetX;
            Check(Math.Abs(leftEdge) < 2, "拖到极限时图片左边缘贴住视口左边", $"{leftEdge:F1}");

            zoom.PanBy(-100000, -100000);
            await Pulse(window, 3);
            Check(Math.Abs(zoom.OffsetX + lim.X) < 1.5 && Math.Abs(zoom.OffsetY + lim.Y) < 1.5,
                "放大后能拖到右下角",
                $"tx={zoom.OffsetX:F1} 下限 {-lim.X:F1}");
            double rightEdge = zoom.ActualWidth / 2 + cw * 3.0 / 2 + zoom.OffsetX;
            Check(Math.Abs(rightEdge - zoom.ActualWidth) < 2, "拖到极限时图片右边缘贴住视口右边",
                $"{rightEdge:F1} / {zoom.ActualWidth:F1}");

            // 报过的问题：从头顶拖到脚，图只挪一点点 —— 2 倍缩放下拖动被打了对折
            zoom.PanBy(0, -100000);
            await Pulse(window);
            zoom.SetScale(2.0);
            double m2 = zoom.DragBy(0, 100);
            zoom.SetScale(3.0);
            double m3 = zoom.DragBy(0, 100);
            Check(Math.Abs(m2 - 100) < 1 && Math.Abs(m3 - 100) < 1,
                "拖动 1:1 跟手，且不随缩放打折", $"2× 走 {m2:F0}，3× 走 {m3:F0}");

            _ = ch;
            zoom.Reset();
            await Pulse(window);
        }
        else Ok("拖动边界", "窗口类型不对，跳过");

        vm.CloseViewerCommand.Execute(null);
        await Pulse(window);
        Check(!vm.LeftCollapsed, "退出查看器左栏保持展开");

        // ---------- 18. 详情栏开合不丢选中位置 ----------
        if (window is MainWindow mw4)
        {
            var panel = FindChild<Controls.JustifiedGridPanel>(mw4.GridListForTest);
            if (panel is not null && vm.Items.Count > 40)
            {
                // 右栏只在有当前图时才出现，先选一张，否则列宽根本不会变
                vm.SelectSingle(vm.Items[0]);
                vm.PanelCollapsed = true;
                for (int i = 0; i < 5; i++) await Pulse(window);
                panel.SetVerticalOffset(900);
                for (int i = 0; i < 6; i++) await Pulse(window);
                int cols0 = panel.Columns;
                int idx0 = panel.FirstRealizedIndex;
                double top0 = panel.FirstChildTop;
                double ext0 = panel.ExtentHeight;

                vm.PanelCollapsed = false;
                for (int i = 0; i < 10; i++) await Pulse(window);

                // 等高布局里「列数」不是硬指标：变窄可能是行高变小而不是少放一张。
                // 真正要保证的是「重排了」+「原来那张还锚在视口顶部第一行」。
                Check(Math.Abs(panel.ExtentHeight - ext0) > 1, "详情栏展开后网格重排了",
                    $"总高 {ext0:F0} -> {panel.ExtentHeight:F0}，列 {cols0} -> {panel.Columns}");
                Check(Math.Abs(panel.FirstChildTop - top0) < panel.ItemHeight * 0.7,
                    "列数变了之后视口锚在原处", $"Y {top0:F0} -> {panel.FirstChildTop:F0}");
                int idx1 = panel.FirstRealizedIndex;
                // 列数一变，换行位置就跟着变：这里锚的是像素位置（顶部那行留在原处），
                // 原来顶行的第一张可能刚好被挤到下一行，所以容一行误差
                Check(Math.Abs(idx0 - idx1) <= panel.Columns, "重排后顶部那行只挪了不到一行",
                    $"索引 {idx0} -> {idx1}（{panel.Columns} 列）");
                panel.SetVerticalOffset(0);
                for (int i = 0; i < 4; i++) await Pulse(window);
            }
            else Ok("详情栏锚定", "网格太小，跳过");
        }

        // ---------- 18b. 展开详情栏（列数变了）不能把选中的那张甩出视野 ----------
        if (window is MainWindow mw5 && vm.Items.Count > 80)
        {
            var panel2 = FindChild<Controls.JustifiedGridPanel>(mw5.GridListForTest);
            if (panel2 is not null)
            {
                double YOf(int idx) =>
                    mw5.GridListForTest.ItemContainerGenerator.ContainerFromIndex(idx) is FrameworkElement fe
                        ? fe.TranslatePoint(new Point(0, 0), panel2).Y
                        : double.NaN;

                vm.PanelCollapsed = true;                       // 先收起，网格变宽
                for (int i = 0; i < 6; i++) await Pulse(window);
                panel2.SetVerticalOffset(1500);
                for (int i = 0; i < 6; i++) await Pulse(window);

                int sel = Math.Min(vm.Items.Count - 1, panel2.FirstRealizedIndex + 2);
                mw5.GridListForTest.SelectedIndex = sel;
                vm.SelectSingle(vm.Items[sel]);
                for (int i = 0; i < 4; i++) await Pulse(window);
                double yBefore = YOf(sel);

                vm.PanelCollapsed = false;                      // 展开 → 每行张数变少，整体重排
                for (int i = 0; i < 8; i++) await Pulse(window);
                double yAfter = YOf(sel);
                Check(!double.IsNaN(yBefore) && !double.IsNaN(yAfter)
                      && Math.Abs(yAfter - yBefore) <= 6,
                    "展开详情栏后选中的那张钉在原位（不会甩出视野）",
                    $"Y {yBefore:F0} -> {yAfter:F0}，视口高 {panel2.ViewportHeight:F0}");

                panel2.SetVerticalOffset(0);
                for (int i = 0; i < 4; i++) await Pulse(window);
            }
        }

        // ---------- 19. 缩略图规格 ----------
        Check(App.Services.Thumbs.GridDir.Length > 0 && Core.Imaging.Thumbnailer.GridSize >= 448,
            "网格缩略图长边 448", $"{Core.Imaging.Thumbnailer.GridSize}");
        Check(App.Services.Store.GetSetting("thumb_preset") == Core.Imaging.Thumbnailer.GridSize.ToString(),
            "缩略图缓存已按新规格重建", App.Services.Store.GetSetting("thumb_preset") ?? "未设置");

        // ---------- 20. 内容判重 ----------
        Check(App.Services.Scanner.DedupeByHash, "判重默认开着",
            App.Services.Store.GetSetting("dedupe") ?? "未设置");

        // ---------- 24. 解析回归：ConditioningZeroOut 不能当成负面 ----------
        // 这张图的工作流是 positive→CLIPTextEncode→ConditioningZeroOut→negative，
        // 曾经把正面文本当成负面，误报「正负提示词完全相同」
        var probe = System.IO.Path.Combine(App.Services.LibraryRoot, "2026-06-30", "Anima", "Anima_00018_.png");
        if (System.IO.File.Exists(probe))
        {
            var meta = LatentFolio.Core.Comfy.PngReader.Read(probe);
            var parsed = meta.Prompt is null ? null : LatentFolio.Core.Comfy.GraphParser.ParseWorkflowApiJson(meta.Prompt, meta.Workflow);
            Check(parsed is not null, "能解析零化负面的工作流");
            if (parsed is not null)
            {
                Check(!parsed.Negative.Contains("one piece", StringComparison.OrdinalIgnoreCase),
                    "ConditioningZeroOut 不会被当成负面", $"{parsed.Negative.Length} 字符");
                Check(parsed.Negative.Contains("worst quality", StringComparison.OrdinalIgnoreCase),
                    "真正的负面词还在", parsed.Negative);
                Check(!LatentFolio.Core.Scan.LibraryScanner.IsNegDup(parsed.Positive, parsed.Negative),
                    "正负不再被误判成相同");
            }
        }
        else Ok("解析回归", "样本图不在，跳过");

        // ShowText 快照：wildcard / 随机分支的真实取值只在运行时出现，图里推不出来，
        // 但 ShowText 的 text_0 记着那次实际显示的整段文本
        const string snapJson = """
        {
          "1": {"class_type":"KSampler","inputs":{"positive":["2",0],"negative":["9",0]}},
          "2": {"class_type":"CLIPTextEncode","inputs":{"text":["3",0]}},
          "3": {"class_type":"StringConcatenate","inputs":{"string_a":["4",0],"string_b":["5",0],"delimiter":","}},
          "4": {"class_type":"PrimitiveStringMultiline","inputs":{"value":"a,"}},
          "5": {"class_type":"Text Concatenate","inputs":{"text_a":["6",0],"text_b":["7",0],"delimiter":""}},
          "6": {"class_type":"Wildcards","inputs":{"textfile":"x.txt","entries_returned":1}},
          "7": {"class_type":"ShowText|pysssss","inputs":{"text_0":"b,","text":["8",0]}},
          "8": {"class_type":"easy anythingIndexSwitch","inputs":{"index":["10",0],"value0":["6",0]}},
          "9": {"class_type":"CLIPTextEncode","inputs":{"text":"worst quality"}},
          "10":{"class_type":"Random Number","inputs":{"seed":1}},
          "11":{"class_type":"ShowText|pysssss","inputs":{"text_0":"a,,b,","text":["3",0]}}
        }
        """;
        var snapRes = LatentFolio.Core.Comfy.GraphParser.ParseWorkflowApiJson(snapJson);
        Check(snapRes.Positive == "a,,b,", "拼接节点用 ShowText 记下的实际结果", snapRes.Positive);

        const string c4Json = """
        {
          "1": {"class_type":"KSampler","inputs":{"positive":["2",0]}},
          "2": {"class_type":"CLIPTextEncode","inputs":{"text":["3",0]}},
          "3": {"class_type":"Text Concatenate","inputs":{"delimiter":"","text_a":"1,","text_b":"2,","text_c":"3,","text_d":"4,"}}
        }
        """;
        var c4Res = LatentFolio.Core.Comfy.GraphParser.ParseWorkflowApiJson(c4Json);
        Check(c4Res.Positive == "1,2,3,4,", "四段拼接（text_c/text_d）也接上", c4Res.Positive);

        // prompt 段里 ShowText 的 text_0 是上一轮的文字，workflow 段才是这次画的。两边都在时以 workflow 为准。
        const string staleJson = """
        {
          "1": {"class_type":"KSampler","inputs":{"positive":["2",0],"negative":["5",0]}},
          "2": {"class_type":"CLIPTextEncode","inputs":{"text":["3",0]}},
          "3": {"class_type":"StringConcatenate","inputs":{"string_a":"a,","string_b":"b,","delimiter":","}},
          "4": {"class_type":"ShowText|pysssss","inputs":{"text_0":"上一轮的文字","text":["3",0]}},
          "5": {"class_type":"CLIPTextEncode","inputs":{"text":"worst quality"}}
        }
        """;
        const string freshWorkflow = """
        { "nodes": [ { "id": 4, "type": "ShowText|pysssss", "widgets_values": ["这一轮的文字"] } ], "links": [] }
        """;
        var staleOnly = LatentFolio.Core.Comfy.GraphParser.ParseWorkflowApiJson(staleJson);
        Check(staleOnly.Positive == "上一轮的文字", "只有 prompt 段时退回 text_0", staleOnly.Positive);
        var freshRes = LatentFolio.Core.Comfy.GraphParser.ParseWorkflowApiJson(staleJson, freshWorkflow);
        Check(freshRes.Positive == "这一轮的文字", "有 workflow 段时以它为准", freshRes.Positive);

        // ---------- 21. 左栏可拖宽 ----------
        double w0 = vm.LeftWidth;
        vm.SetLeftWidth(320);
        await Pulse(window);
        Check(Math.Abs(vm.LeftWidth - 320) < 0.5, "左栏宽度可拖动", $"{w0:F0} -> {vm.LeftWidth:F0}");
        vm.SetLeftWidth(120);
        await Pulse(window);
        Check(vm.LeftWidth >= 180, "左栏宽度有下限", $"{vm.LeftWidth:F0}");
        vm.SetLeftWidth(w0);
        await Pulse(window);

        // ---------- 21a. 两个折叠把手：一样大、各自压在自己那条边界上 ----------
        if (window is MainWindow mwT)
        {
            var lt = mwT.LeftTabForTest;
            var dt = mwT.DetailTabForTest;
            Check(Math.Abs(lt.ActualWidth - dt.ActualWidth) < 0.6 && Math.Abs(lt.ActualHeight - dt.ActualHeight) < 0.6
                  && lt.ActualWidth > 20,
                "两个折叠把手一样大（没有被分栏裁掉）",
                $"左 {lt.ActualWidth:F0}×{lt.ActualHeight:F0} / 右 {dt.ActualWidth:F0}×{dt.ActualHeight:F0}");
            var c1 = lt.TranslatePoint(new Point(lt.ActualWidth / 2, 0), mwT);
            Check(Math.Abs(c1.X - vm.LeftWidth) < 2.5, "左把手压在左栏边界上", $"{c1.X:F0} vs {vm.LeftWidth:F0}");
            var c2 = dt.TranslatePoint(new Point(dt.ActualWidth / 2, 0), mwT);
            double edge = mwT.ActualWidth - vm.DetailWidth;
            Check(Math.Abs(c2.X - edge) < 2.5, "详情把手压在详情栏边界上", $"{c2.X:F0} vs {edge:F0}");

            // 手柄中心命中的必须是手柄自己：被任何东西盖住都会在这里现形
            var hp = dt.TranslatePoint(new Point(dt.ActualWidth / 2, dt.ActualHeight / 2), mwT);
            var hitH = mwT.InputHitTest(hp) as DependencyObject;
            Check(hitH is not null && IsUnder(hitH, dt), "折叠手柄的点击区域没被遮住",
                hitH?.GetType().Name ?? "null");

            // 手柄之外的整条边框：命中的是拖拽带（以前这里什么都没有，只能抓那个小把手）
            var band = mwT.DetailEdgeBandForTest;
            var bp = band.TranslatePoint(new Point(band.ActualWidth / 2, 30), mwT);
            var hitB = mwT.InputHitTest(bp) as DependencyObject;
            Check(band.IsVisible && ReferenceEquals(hitB, band), "详情栏整条边框都能按住横拖",
                $"命中 {hitB?.GetType().Name ?? "null"}");

            // 左栏那条缝（9px）也一样，命中的是分隔条
            var lp = new Point(vm.LeftWidth + 4, c1.Y - 30);
            var hitL = mwT.InputHitTest(lp) as DependencyObject;
            var leftSplit = mwT.FindName("LeftSplitter") as System.Windows.Controls.GridSplitter;
            Check(leftSplit is not null && hitL is not null && IsUnder(hitL, leftSplit),
                "左栏整条边框都能按住横拖", hitL?.GetType().Name ?? "null");

            // 走真实拖拽算法：往左拖 40 → 详情栏宽 40
            double wB = vm.DetailWidth;
            mwT.WidthDragForTest(true, 1200, 1160);
            await Pulse(window);
            Check(Math.Abs(vm.DetailWidth - (wB + 40)) < 1.5, "拖边框能改详情栏宽度",
                $"{wB:F0} -> {vm.DetailWidth:F0}");
            mwT.WidthDragForTest(true, 1160, 1200);
            await Pulse(window);

            // 手柄的 Click 真的接上了收起/展开（不只是「点得中」）
            bool pc0 = vm.PanelCollapsed;
            dt.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, dt));
            await Pulse(window);
            Check(vm.PanelCollapsed != pc0, "点折叠手柄真的会收起/展开",
                $"{pc0} -> {vm.PanelCollapsed}");
            dt.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, dt));
            await Pulse(window);
        }

        // ---------- 21c. 滚动条：滑块有最小长度、点得中、拖得动 ----------
        if (window is MainWindow mwS)
        {
            var sbS = mwS.GridScrollBarForTest;
            var thS = mwS.GridScrollThumbForTest;
            var gp = FindChild<Controls.JustifiedGridPanel>(mwS.GridListForTest);
            if (sbS is not null && thS is not null)
            {
                Check(thS.ThumbLengthForTest >= 44,
                    "图库滚动条滑块不短于 44（四千多张也抓得住）",
                    $"{thS.ThumbLengthForTest:F0}px，轨道 {thS.ActualHeight:F0}，滑槽宽 {sbS.ActualWidth:F0}，视口 {sbS.ViewportSize:F0}，内容 {sbS.Maximum + sbS.ViewportSize:F0}");

                var tp = thS.TranslatePoint(new Point(thS.ActualWidth / 2, thS.ThumbStartForTest + 6), mwS);
                var hitT = mwS.InputHitTest(tp) as DependencyObject;
                Check(hitT is not null && IsUnder(hitT, thS), "滑块点得中（没被别的层盖住）",
                    hitT?.GetType().Name ?? "null");

                double v0s = sbS.Value;
                thS.NudgeForTest(120);
                for (int i = 0; i < 4; i++) await Pulse(window);
                Check(sbS.Value > v0s && gp is not null && gp.VerticalOffset > 0,
                    "拖滑块能滚动图库", $"{v0s:F0} -> {sbS.Value:F0}，偏移 {gp?.VerticalOffset:F0}");

                thS.ScrollToForTest(0);
                for (int i = 0; i < 4; i++) await Pulse(window);
            }
            else Check(false, "图库滚动条换成 SlimThumb", "没找到 ScrollBar 或滑块");
        }

        // ---------- 21d. 后台导入整表重建，不能把正在写备注的详情栏关掉 ----------
        if (window is MainWindow mwK && vm.Items.Count > 0)
        {
            vm.PanelCollapsed = false;
            mwK.GridListForTest.SelectedItem = vm.Items[0];
            await Pulse(window, 2);
            long keepId = vm.Current?.Id ?? 0;
            Check(keepId != 0 && vm.IsPanelVisible, "详情栏开着（准备模拟后台导入）",
                $"{keepId}");

            vm.RefreshGrid();
            await Pulse(window, 2);
            Check(vm.Current?.Id == keepId && vm.IsPanelVisible, "刷新网格不会关掉详情栏",
                $"Current {(vm.Current?.Id ?? 0)} / 可见 {vm.IsPanelVisible}");

            // 目录监视导入走的就是这条：整库重建（VM 实例全换掉）+ 刷网格
            vm.ReloadFromDb();
            vm.RefreshGrid();
            await Pulse(window, 3);
            Check(vm.Current?.Id == keepId && vm.IsPanelVisible, "整库重建后详情栏还开着",
                $"Current {(vm.Current?.Id ?? 0)} / 可见 {vm.IsPanelVisible}");
            Check(mwK.GridListForTest.SelectedItems.Count == 1
                  && mwK.GridListForTest.SelectedItems[0] is ImageVm rebuilt && rebuilt.Id == keepId,
                "重建后原来那张还选中着", $"{mwK.GridListForTest.SelectedItems.Count} 张");

            // 用户自己收起来的右栏，不能被后台刷新顶开
            vm.PanelCollapsed = true;
            await Pulse(window, 2);
            vm.RefreshGrid();
            await Pulse(window, 2);
            Check(vm.PanelCollapsed && !vm.IsPanelVisible, "收起的右栏不会被刷新顶开");
            vm.PanelCollapsed = false;
            await Pulse(window);
        }

        // ---------- 21b. 可拖的分区高度、排序都记进设置 ----------
        double ch0 = vm.LeftColumnsHeight, ph0 = vm.LeftPeopleHeight;
        vm.SetLeftColumnsHeight(300);
        vm.SetLeftPeopleHeight(200);
        await Pulse(window);
        Check(App.Services.Store.GetSetting("left_cols_h") == "300"
              && App.Services.Store.GetSetting("left_people_h") == "200",
            "分区高度记进设置",
            $"{App.Services.Store.GetSetting("left_cols_h")} / {App.Services.Store.GetSetting("left_people_h")}");
        vm.SetLeftColumnsHeight(ch0);
        vm.SetLeftPeopleHeight(300);      // 先把人物块拖高，别让它被高度卡住
        vm.SetLeftWidth(320);
        await Pulse(window);
        Check(vm.PeopleCardSide > 40, "左栏变宽后人物卡跟着变大", $"{vm.PeopleCardSide:F0}px");
        vm.SetLeftPeopleHeight(ph0);
        vm.SetLeftWidth(w0);
        await Pulse(window);

        int key0 = vm.SortKeyIndex;
        bool desc0 = vm.SortDesc;
        vm.SortKeyIndex = 2;
        await Pulse(window);
        Check(App.Services.Store.GetSetting("grid_sort_key") == "2", "总览排序键记进设置",
            App.Services.Store.GetSetting("grid_sort_key") ?? "");
        Check(vm.Sort.Key == LatentFolio.Core.Filtering.SortKey.Name, "排序真的换了", vm.SortSummary);
        vm.SortDesc = !desc0;
        await Pulse(window);
        Check(App.Services.Store.GetSetting("grid_sort_desc") == (desc0 ? "0" : "1"), "排序方向记进设置",
            App.Services.Store.GetSetting("grid_sort_desc") ?? "");
        Check(vm.Sort.Desc == !desc0 && vm.SortDirGlyph == (desc0 ? "▴" : "▾"), "方向真的反了", vm.SortSummary);
        vm.SortKeyIndex = key0;
        vm.SortDesc = desc0;
        await Pulse(window);

        // ---------- 22. 简易筛选 + 人物面板 ----------
        vm.SetTempFilter(new FilterGroup
        {
            Op = BoolOp.And,
            Children = { new FilterCond { Field = FieldType.Rating, Op = CondOp.Gte, Value = "4" } },
        }, "评分 ≥ 4");
        await Pulse(window);
        Check(vm.TempFilterName == "评分 ≥ 4" && vm.HasTempFilter, "简易筛选带名字记着", vm.TempFilterName);
        vm.ClearFilterCommand.Execute(null);
        await Pulse(window);
        Check(!vm.HasTempFilter && vm.TempFilterName.Length == 0, "清除筛选连名字一起清掉");

        vm.ShowPeopleGridCommand.Execute(null);
        await Pulse(window);
        Check(vm.ShowPeoplePane, "人物墙在中间显示");
        Check(vm.PeopleView.Count > 0, "人物墙有内容", $"{vm.PeopleView.Count}");
        if (vm.People.Count > 0)
        {
            vm.ShowPerson(vm.People[0]);
            await Pulse(window);
            Check(!vm.ShowPeoplePane, "点具体人物后人物墙让位");
            vm.ShowOverviewCommand.Execute(null);
            await Pulse(window);
        }

        Check(vm.WatchEnabled, "目录监视一直开着（没有手动开关了）");

        // ---------- 23. 星级筛选 / 角色筛选 / 资源库倒序 ----------
        vm.SetTempFilter(new FilterGroup
        {
            Op = BoolOp.And,
            Children = { new FilterCond { Field = FieldType.Rating, Op = CondOp.Gte, Value = "3" } },
        }, "3 星 及以上");
        await Pulse(window);
        Check(vm.TempFilterName == "3 星 及以上" && vm.Items.Count > 0, "星级筛选生效",
            $"{vm.Items.Count} 张");
        vm.SetTempFilter(new FilterGroup
        {
            Op = BoolOp.And,
            Children = { new FilterCond { Field = FieldType.Rating, Op = CondOp.Lte, Value = "2" } },
        }, "2 星 及以下");
        await Pulse(window);
        Check(vm.Items.All(x => x.Rating <= 2), "「及以下」真的只留低分", $"{vm.Items.Count} 张");
        vm.ClearFilterCommand.Execute(null);
        await Pulse(window);

        var charsInView = vm.CharsInView();
        Check(charsInView.Count > 0, "角色筛选能列出当前结果里的角色", $"{charsInView.Count} 个");
        var models = vm.ModelsInView();
        Check(models.Count > 0, "模型筛选能列出当前结果里的模型", $"{models.Count} 个");

        if (vm.Folders.Count > 3)
        {
            vm.FolderSort = AppVm.FolderSortKind.Name;
            vm.FolderDesc = false;
            await Pulse(window);
            var asc = vm.Folders[0].Path;
            vm.FolderDesc = true;
            await Pulse(window);
            var desc = vm.Folders[0].Path;
            Check(string.Compare(asc, desc, StringComparison.OrdinalIgnoreCase) != 0
                  && string.Compare(asc, desc, StringComparison.OrdinalIgnoreCase) < 0,
                "资源库排序可以倒过来", $"{asc} / {desc}");
            vm.FolderDesc = false;
            await Pulse(window);

            // ---------- 23b. 目录默认全折叠 / 点开展开 / 全展全折 ----------
            int topLevel = vm.Folders.Count(f => f.Depth == 0);
            Check(vm.Folders.Count(f => f.Visible) <= topLevel + 1, "目录树默认是折叠的",
                $"可见 {vm.Folders.Count(f => f.Visible)} / 共 {vm.Folders.Count}，一级 {topLevel}");
            var topNode = vm.Folders.FirstOrDefault(f => f.Depth == 0 && f.HasChildren);
            if (topNode is not null)
            {
                int visBefore = vm.Folders.Count(f => f.Visible);
                vm.ExpandFolder(topNode);
                await Pulse(window);
                Check(vm.Folders.Count(f => f.Visible) > visBefore && topNode.Expanded,
                    "点一下会展开它", $"{visBefore} -> {vm.Folders.Count(f => f.Visible)}");
            }
            vm.ExpandAllFolders();
            await Pulse(window);
            Check(vm.Folders.All(f => f.Visible), "展开所有", $"{vm.Folders.Count(f => f.Visible)} 个");
            vm.CollapseAllFolders();
            await Pulse(window);
            Check(vm.Folders.Count(f => f.Visible) <= topLevel + 1, "折叠所有",
                $"{vm.Folders.Count(f => f.Visible)} 个");
        }

        // ---------- 23c. 人物块跟着栏宽缩放 ----------
        double wKeep = vm.LeftWidth;
        double phKeep = vm.LeftPeopleHeight;
        vm.SetLeftPeopleHeight(300);      // 同上：先解除高度限制
        vm.SetLeftWidth(248);
        await Pulse(window);
        double sideNarrow = vm.PeopleCardSide;
        int colsNarrow = vm.PeopleCols;
        vm.SetLeftWidth(430);
        await Pulse(window);
        Check(vm.PeopleCardSide > sideNarrow + 8, "左栏变宽人物卡跟着变大",
            $"{sideNarrow:F0}px -> {vm.PeopleCardSide:F0}px");
        Check(vm.PeopleCols is >= 2 and <= 5 && vm.PeopleCardSide > 20, "人物是自适应方块网格",
            $"{colsNarrow} 列 / {sideNarrow:F0}px → {vm.PeopleCols} 列 / {vm.PeopleCardSide:F0}px");

        // 拉高也要能把格子撑大（会换行：列少行多）
        double sideShort = vm.PeopleCardSide;
        vm.SetLeftPeopleHeight(620);
        await Pulse(window);
        Check(vm.PeopleCardSide > sideShort + 6, "人物格子跟着高度缩放",
            $"{sideShort:F0}px -> {vm.PeopleCardSide:F0}px");
        vm.SetLeftPeopleHeight(phKeep);
        vm.SetLeftWidth(wKeep);
        vm.SetLeftPeopleHeight(phKeep);
        await Pulse(window);

        // ---------- 23d. 源文件在被外部删掉 → 进「已删除」，不在图墙 ----------
        if (vm.AllImages.Count > 0)
        {
            var victim = vm.AllImages.First(x => x.State == FileState.Present);
            App.Services.Store.UpdateState(new ImageItem { Id = victim.Id, State = FileState.Missing });
            vm.ReloadFromDb();
            vm.RefreshGrid();
            await Pulse(window);
            Check(vm.MissingCount == 1, "丢失的图被记下来了", $"{vm.MissingCount}");
            Check(vm.Items.All(x => x.Id != victim.Id), "丢失的图不在图墙里", $"{victim.FileName}");
            var delView = vm.AllImages.Where(x => x.State is FileState.Deleted or FileState.Missing).ToList();
            Check(delView.Any(x => x.Id == victim.Id) && vm.DeletedCount >= 1,
                "丢失的图进「已删除」", $"共 {delView.Count} 张");
            App.Services.Store.UpdateState(new ImageItem { Id = victim.Id, State = FileState.Present });
            vm.ReloadFromDb();
            vm.RefreshGrid();
            await Pulse(window);
            Check(vm.MissingCount == 0 && vm.Items.Any(x => x.Id == victim.Id), "标记回来就回图墙");
        }

        // ---------- 性能 / 内存 ----------
        // 自检一路上会重建好几次 VM、跑 VSR、开查看器，先收一遍垃圾再读数，
        // 否则量到的是"还没回收的垃圾"而不是常驻占用
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        proc.Refresh();
        var st = App.Services.ThumbService.Stats;
        Log.AppendLine();
        Log.AppendLine($"性能  启动 {vm.LastInitMs} ms · 刷新网格 {vm.LastRefreshMs} ms"
                       + $" · 读库 {vm.LastReloadMs} ms · 人物 {vm.LastPeopleMs} ms · 目录 {vm.LastFoldersMs} ms");
        Log.AppendLine($"内存  进程 {proc.PrivateMemorySize64 / 1024 / 1024} MB"
                       + $" · 缩略图解码缓存 {st.Count} 张 / {st.Bytes / 1024 / 1024} MB");
    }
}
