using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LatentFolio.App.Controls;

/// <summary>
/// 只读、但可以拖选 / Ctrl+C / 右键复制的文本。
///
/// WPF 的 TextBlock 不支持选择，TextBox 又默认带边框、底色、光标和 Tab 停靠，
/// 这里把后者那些全关掉，看着还是一段普通文字。样式在 Theme.xaml 的隐式样式里。
///
/// 双击整段选中：这些字段都是只读的，双击选一个词没有用处，反而还要再 Ctrl+A 一次。
/// </summary>
public sealed class SelectableText : TextBox
{
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    /// <summary>两次落点差多少以内才算同一次双击。</summary>
    private const double SameClickSlop = 4.0;

    private long _lastDownTicks;   // Environment.TickCount64，0 = 没有上一次
    private Point _lastDownPos;

    public SelectableText()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsTabStop = false;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
    }

    /// <summary>双击（含三击）整段选中，方便直接 Ctrl+C。</summary>
    public void SelectAllOnDoubleClick()
    {
        if (Text.Length == 0) return;
        SelectAll();
        Focus();
    }

    /// <summary>
    /// 自己判断双击，不用 e.ClickCount。
    ///
    /// ClickCount 是「同一个窗口里的两次快速点击」，跟落在哪个控件、哪一行没关系：
    /// 先点上一行的值、再点下一行的值，第二下照样报 2，于是那一行莫名其妙整行被选中。
    /// 这里要求「同一个控件 + 上一次按下之后系统双击时间内 + 落点差 4px 以内」。
    /// </summary>
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            base.OnPreviewMouseLeftButtonDown(e);
            return;
        }

        var pos = e.GetPosition(this);
        long now = Environment.TickCount64;
        bool sameSpot = _lastDownTicks != 0
                        && now - _lastDownTicks <= GetDoubleClickTime()
                        && Math.Abs(pos.X - _lastDownPos.X) <= SameClickSlop
                        && Math.Abs(pos.Y - _lastDownPos.Y) <= SameClickSlop;

        _lastDownTicks = sameSpot ? 0 : now;   // 双击之后重新计，免得第三下又算一次
        _lastDownPos = pos;

        if (sameSpot && Text.Length > 0)
        {
            SelectAllOnDoubleClick();
            e.Handled = true;   // 挡掉 TextBox 自己的「选一个词」
            return;
        }

        base.OnPreviewMouseLeftButtonDown(e);
    }
}
