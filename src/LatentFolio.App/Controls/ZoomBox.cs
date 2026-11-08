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
            double fit = Math.Min(ActualWidth / src.Width, ActualHeight / src.Height);
            _scale = Math.Clamp(1.0 / Math.Max(0.01, fit), 1, MaxScale);
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
    }

    private void Clamp()
    {
        if (_scale <= 1.01) { _tx = _ty = 0; return; }
        // 内容本身是铺满的，放大后允许平移的范围按比例放开
        double maxX = (ActualWidth / 2) * (_scale - 1) / _scale;
        double maxY = (ActualHeight / 2) * (_scale - 1) / _scale;
        _tx = Math.Clamp(_tx, -maxX, maxX);
        _ty = Math.Clamp(_ty, -maxY, maxY);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_scale <= 1.01 && e.Delta <= 0) return;
        ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        _dragStart = e.GetPosition(this);
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
            var p = e.GetPosition(this);
            _tx += (p.X - _dragStart.X) * 0.9;
            _ty += (p.Y - _dragStart.Y) * 0.9;
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
