using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using WinForms = System.Windows.Forms;

namespace LatentFolio.App.Services;

/// <summary>
/// 托盘图标：关窗口后程序还留在这里。
///
/// 图标本身借 WinForms 的 NotifyIcon（Shell_NotifyIcon 那套结构体不值得手写），
/// 菜单还是 WPF 的 ContextMenu，跟界面里其他菜单一个样式。
/// 图标/菜单都活在 WPF 的 UI 线程上，WPF 的 Dispatcher 自己会泵消息。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly WinForms.NotifyIcon _icon;
    private readonly ContextMenu _menu;
    private readonly HwndSource _hwnd;
    private bool _disposed;

    public bool Visible => !_disposed && _icon.Visible;
    public ContextMenu Menu => _menu;

    public TrayIcon(string tooltip, Action show, Action openFolder, Action exit)
    {
        _menu = new ContextMenu { Placement = PlacementMode.MousePoint };

        var showItem = new MenuItem { Header = "显示窗口", FontWeight = FontWeights.SemiBold };
        showItem.Click += (_, _) => show();
        var folderItem = new MenuItem { Header = "打开图库目录" };
        folderItem.Click += (_, _) => openFolder();
        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => exit();

        _menu.Items.Add(showItem);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(folderItem);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(exitItem);

        // 0×0 的隐藏窗口：菜单弹出前把前台给它，否则点别处菜单不会关（WPF 弹层抓不住鼠标）
        _hwnd = new HwndSource(new HwndSourceParameters("LatentFolioTray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        });

        _icon = new WinForms.NotifyIcon
        {
            Text = tooltip,
            Icon = LoadIcon(),
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) show();
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Right) OpenMenu();
        };
    }

    /// <summary>右键弹出菜单（自检里也直接调这个）。</summary>
    public void OpenMenu()
    {
        if (_disposed) return;
        SetForegroundWindow(_hwnd.Handle);
        _menu.IsOpen = false;
        _menu.IsOpen = true;
    }

    private static System.Drawing.Icon? LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico is not null) return ico;
            }
        }
        catch
        {
            // 下面还有兜底
        }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _menu.IsOpen = false;
        _menu.Items.Clear();

        _icon.Visible = false;
        _icon.Dispose();
        _hwnd.Dispose();
    }
}
