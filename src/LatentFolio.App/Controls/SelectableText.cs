using System.Windows.Controls;
using System.Windows.Input;

namespace LatentFolio.App.Controls;

/// <summary>
/// 只读、但可以拖选 / Ctrl+C / 右键复制的文本。
///
/// WPF 的 TextBlock 不支持选择，TextBox 又默认带边框、底色、光标和 Tab 停靠，
/// 这里把后者那些全关掉，看着还是一段普通文字。样式在 Theme.xaml 的隐式样式里。
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
}
