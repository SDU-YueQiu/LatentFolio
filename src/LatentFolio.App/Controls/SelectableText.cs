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

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2 && Text.Length > 0)
        {
            SelectAllOnDoubleClick();
            e.Handled = true;   // 挡掉 TextBox 自己的「选一个词」
            return;
        }
        base.OnPreviewMouseLeftButtonDown(e);
    }
}

