using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LatentFolio.App.Controls;

/// <summary>
/// 大图查看：等比铺满（scale=1）为基准，滚轮以光标为锚缩放，按住左键拖动平移，
/// 双击在「铺满 / 100%」之间切换。RTX VSR 出来的 2 倍图靠它才看得清。
/// </summary>
public sealed class ZoomBox : ContentControl
{
    private double _scale = 1;
    private double _tx, _ty;
    private Point _dragStart;
    private bool _dragging;

    public const double MaxScale = 8.0;

    private readonly TransformGroup _group = new();
    private readonly ScaleTransform _st = new();
    private readonly TranslateTransform _tt = new();

    public ZoomBox()
    {
        ClipToBounds = true;
        Focusable = true;
        // 要能拿键盘焦点（滚轮/方向键），但不要 WPF 那个虚线焦点框：
        // 它会把整个大图区框成一整圈，看着像选中了什么东西
        FocusVisualStyle = null;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        _group.Children.Add(_st);
        _group.Children.Add(_tt);
        RenderTransform = _group;
        RenderTransformOrigin = new Point(0.5, 0.5);
        Background = Brushes.Transparent;
        Cursor = Cursors.Arrow;
    }

    public double Zoom => _scale;

    /// <summary>缩放变了就喊一声：外面那个百分比标签跟着刷新。</summary>
    public event Action? ZoomChanged;

    /// <summary>相对原图像素的比例：1.0 = 100%（1 个图像像素对 1 个设备像素）。</summary>
    public double ActualScale
    {
        get
        {
            if (Content is not Image { Source: { } src } || src.Width <= 0.5) return _scale;
            var (cw, _) = ContentSize;
            return _scale * (cw / src.Width);
        }
    }

    /// <summary>给自检用：直接设一个缩放倍数。</summary>
    public void SetScale(double scale)
    {
        _scale = Math.Clamp(scale, 1.0, MaxScale);
        _tx = _ty = 0;
        Clamp();
        Apply();
        Cursor = _scale > 1.01 ? Cursors.Hand : Cursors.Arrow;
        ZoomChanged?.Invoke();
    }

    public void Reset()
    {
        _scale = 1;
        _tx = _ty = 0;
        Apply();
        Cursor = Cursors.Arrow;
    }

    public void ToggleActualSize()
    {
        if (_scale > 1.01) { Reset(); return; }

        // 1.0 = 铺满；真实 1:1 需要按渲染尺寸算
        if (Content is Image { Source: { } src } && ActualWidth > 0 && ActualHeight > 0)
        {
            var (cw, ch) = ContentSize;
            double fit = Math.Min(cw / src.Width, ch / src.Height);
            double actual = 1.0 / Math.Max(0.0001, fit);
            if (actual <= 1.02) { Reset(); return; }
            _scale = Math.Clamp(actual, 1.0, MaxScale);
            _tx = _ty = 0;
            Clamp();
            Apply();
            Cursor = Cursors.Hand;
        }
    }

    private void Apply()
    {
        _st.ScaleX = _st.ScaleY = _scale;
        _tt.X = _tx;
        _tt.Y = _ty;
    }

    private Point Center => new(ActualWidth / 2, ActualHeight / 2);

    /// <summary>
    /// 内容（图片）在 1 倍时实际渲染出来的大小。图片是 Uniform 摆放的，
    /// 竖图两侧有留白 —— 平移边界要按图片本身算，不能按控件算，
    /// 否则竖图会往两边拖出一片黑边，横图又拖不到边。
    /// </summary>
    internal (double W, double H) ContentSize
    {
        get
        {
            if (Content is not Image img || img.Source is not { } src || ActualWidth <= 0 || ActualHeight <= 0)
                return (ActualWidth, ActualHeight);
            var m = img.Margin;
            double aw = Math.Max(1, ActualWidth - m.Left - m.Right);
            double ah = Math.Max(1, ActualHeight - m.Top - m.Bottom);
            if (src.Width <= 0 || src.Height <= 0) return (aw, ah);
            double fit = Math.Min(aw / src.Width, ah / src.Height);
            return (src.Width * fit, src.Height * fit);
        }
    }

    /// <summary>当前能平移的最大距离（像素）。给自检和 Clamp 共用。</summary>
    internal (double X, double Y) PanLimit
    {
        get
        {
            if (_scale <= 1.01) return (0, 0);
            var (cw, ch) = ContentSize;
            return (Math.Max(0, (cw * _scale - ActualWidth) / 2),
                    Math.Max(0, (ch * _scale - ActualHeight) / 2));
        }
    }

    internal double OffsetX => _tx;
    internal double OffsetY => _ty;

    /// <summary>
    /// 鼠标位置要用「父容器坐标」算，不能用 this。
    /// this 身上挂着 RenderTransform，GetPosition(this) 会把屏幕点反变换回
    /// 缩放前的局部坐标 —— 2 倍时拖 100 像素只算出 50，图只走一半，
    /// 就是「从头顶拖到脚也拖不动」的根因。
    /// </summary>
    private IInputElement Space => Parent as IInputElement ?? this;

    /// <summary>给自检用：按像素平移，内部会夹到边界。</summary>
    internal void PanBy(double dx, double dy)
    {
        _tx += dx;
        _ty += dy;
        Clamp();
        Apply();
    }

    /// <summary>给自检用：跟鼠标拖动走同一条路径，返回实际位移。</summary>
    internal double DragBy(double dx, double dy)
    {
        double before = _ty;
        _tx += dx;
        _ty += dy;
        Clamp();
        Apply();
        return _ty - before;
    }

    /// <summary>以某个点为锚点缩放。</summary>
    private void ZoomAt(Point p, double factor)
    {
        double ns = Math.Clamp(_scale * factor, 1.0, MaxScale);
        if (Math.Abs(ns - _scale) < 1e-6) return;

        var c = Center;
        // 变换后锚点相对中心的偏移 = (p - c) * (ns/scale)
        double k = ns / _scale;
        _tx = (p.X - c.X) * (1 - k) + _tx * k;
        _ty = (p.Y - c.Y) * (1 - k) + _ty * k;
        _scale = ns;
        Clamp();
        Apply();
        Cursor = _scale > 1.01 ? Cursors.Hand : Cursors.Arrow;
        ZoomChanged?.Invoke();
    }

    private void Clamp()
    {
        if (_scale <= 1.01) { _tx = _ty = 0; return; }
        // 缩放是绕中心做的，平移量是「缩放后」的像素：边界 = (缩放后尺寸 - 视口) / 2
        var (maxX, maxY) = PanLimit;
        _tx = Math.Clamp(_tx, -maxX, maxX);
        _ty = Math.Clamp(_ty, -maxY, maxY);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // 普通滚轮换图，Ctrl+滚轮缩放
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (_scale > 1.01 || e.Delta > 0)
                ZoomAt(e.GetPosition(Space), e.Delta > 0 ? 1.15 : 1 / 1.15);
            e.Handled = true;
            return;
        }

        WheelNavigate?.Invoke(e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    /// <summary>滚轮换图：-1 上一张 / +1 下一张。</summary>
    public event Action<int>? WheelNavigate;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        _dragStart = e.GetPosition(Space);
        _dragging = _scale > 1.01;
        if (_dragging)
        {
            Cursor = Cursors.SizeAll;
            CaptureMouse();
        }
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging && e.LeftButton == MouseButtonState.Pressed)
        {
            var p = e.GetPosition(Space);
            // 1:1 跟手：鼠标移动多少像素，图就移多少像素
            _tx += p.X - _dragStart.X;
            _ty += p.Y - _dragStart.Y;
            _dragStart = p;
            Clamp();
            Apply();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            Cursor = Cursors.Hand;
            ReleaseMouseCapture();
            e.Handled = true;
        }
        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        ToggleActualSize();
        e.Handled = true;
        base.OnMouseDoubleClick(e);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (Math.Abs(_scale - 1) < 1e-6) Reset();
        else Clamp();
    }
}
