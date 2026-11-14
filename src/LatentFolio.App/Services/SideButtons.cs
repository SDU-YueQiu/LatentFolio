using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LatentFolio.App.Services;

/// <summary>
/// 鼠标侧键（XBUTTON1 = 返回，XBUTTON2 = 前进）。
///
/// 以前只挂了主窗口的 PreviewMouseDown，所以只在「指针正好压在图墙/查看器这类主窗口内容上」时有效：
/// 右键菜单、下拉框弹层是独立的 HWND，指针落在上面事件根本进不到主窗口；
/// 设置/标签/列编辑这些独立窗口又是另一条窗口树，同样收不到。
///
/// 现在注册在 FrameworkElement 的类处理上：所有 WPF 窗口和弹出层（PopupRoot 也是 FrameworkElement）
/// 的按下事件都会经过这里，一次按下只处理一次。
/// </summary>
internal static class SideButtons
{
    private static MouseButtonEventArgs? _last;

    public static void Install()
    {
        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnPreviewDown),
            handledEventsToo: true);
    }

    /// <summary>自检用：按一次侧键，不用真的动鼠标。</summary>
    internal static void PressForTest(bool back, IInputElement? target = null)
    {
        var ev = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, back ? MouseButton.XButton1 : MouseButton.XButton2)
        {
            RoutedEvent = UIElement.PreviewMouseDownEvent,
        };
        (target ?? Application.Current.MainWindow as IInputElement)?.RaiseEvent(ev);
    }

    private static void OnPreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2)) return;

        // 隧道事件会在路由上被每个元素叫一次；同一次按下只办一次
        if (ReferenceEquals(e, _last)) return;
        _last = e;
        e.Handled = true;

        bool back = e.ChangedButton == MouseButton.XButton1;
        var src = e.OriginalSource as DependencyObject;

        // 菜单/下拉开着：侧键先当 Esc 用，别在菜单底下偷偷换页
        if (CloseOverlay(src)) return;

        var win = Window.GetWindow(src);
        if (win is MainWindow mw)
        {
            mw.SideButton(back);
            return;
        }
        if (win is Window dialog)
        {
            // 独立窗口：返回 = 关掉它（等同取消），前进 = 什么都不做
            if (back) dialog.Close();
            return;
        }

        // 落在弹层上（Window.GetWindow 为 null）：按主窗口处理
        (Application.Current.MainWindow as MainWindow)?.SideButton(back);
    }

    /// <summary>指针下面是个还没收起的菜单/下拉时，先收掉它。</summary>
    private static bool CloseOverlay(DependencyObject? src)
    {
        int guard = 0;
        for (var d = src; d is not null && guard++ < 64; d = Parent(d))
        {
            if (d is ContextMenu { IsOpen: true } menu)
            {
                menu.IsOpen = false;
                return true;
            }
            if (d is ComboBox { IsDropDownOpen: true } combo)
            {
                combo.IsDropDownOpen = false;
                return true;
            }
        }
        return false;
    }

    private static DependencyObject? Parent(DependencyObject d)
    {
        if (d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
        {
            var v = System.Windows.Media.VisualTreeHelper.GetParent(d);
            if (v is not null) return v;
        }
        return LogicalTreeHelper.GetParent(d);
    }
}
