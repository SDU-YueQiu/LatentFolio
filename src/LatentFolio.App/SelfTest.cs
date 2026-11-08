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

        // ---------- 2. 滚动要能换出新的一批格子 ----------
        if (window is MainWindow mw0)
        {
            var panel = FindChild<Controls.VirtualizingWrapPanel>(mw0.GridListForTest);
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
                Check(panel.RealizedCount > 0 && panel.FirstChildTop > -panel.ItemHeight
                      && panel.FirstChildTop < panel.ViewportHeight,
                    "滚动后视野里真的有格子（不是空白）",
                    $"首个容器 Y={panel.FirstChildTop:F0}，视口高 {panel.ViewportHeight:F0}，共 {panel.RealizedCount} 个");
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
        int before = vm.Items.Count;
        vm.MoveToDeleted([first]);
        await Pulse(window, 3);
        Check(vm.Items.Count == before - 1, "删除后网格少一张", $"{before} -> {vm.Items.Count}");
        Check(vm.AllImages.First(x => x.Id == first.Id).State == FileState.Deleted, "状态改成已删除");

        // 批量删 50 张：逐条 Remove 会把面板的 OnItemsChanged 跑 50 遍
        var batch = vm.Items.Take(50).ToList();
        int before2 = vm.Items.Count;
        vm.MoveToDeleted(batch);
        await Pulse(window, 3);
        Check(vm.Items.Count == before2 - batch.Count, "一次删 50 张不崩", $"{before2} -> {vm.Items.Count}");
        vm.RestoreFromDeleted(batch);
        await Pulse(window, 3);
        Check(vm.Items.Count == before2, "一次还原 50 张", $"{vm.Items.Count}");

        vm.RestoreFromDeleted([first]);
        await Pulse(window, 3);
        Check(vm.AllImages.First(x => x.Id == first.Id).State == FileState.Present, "还原后状态恢复");

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
    }
}

