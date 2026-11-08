using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LatentFolio.App.Controls;

/// <summary>
/// 五颗星的评分条。左键点一下设分，右键清空，鼠标移过去就预览要设的分。
/// 值走 TwoWay 绑定，所以外部改动（切图、右键菜单清分）会立刻反映出来。
/// </summary>
public sealed class RatingBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(RatingBar),
        new FrameworkPropertyMetadata(0,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnValueChanged));

    public static readonly DependencyProperty StarSizeProperty = DependencyProperty.Register(
        nameof(StarSize), typeof(double), typeof(RatingBar),
        new FrameworkPropertyMetadata(9.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(RatingBar),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly), typeof(bool), typeof(RatingBar), new PropertyMetadata(false));

    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double StarSize { get => (double)GetValue(StarSizeProperty); set => SetValue(StarSizeProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public bool IsReadOnly { get => (bool)GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }

    public event EventHandler<int>? RatingChanged;

    private int _hover = -1;

    private static readonly Brush On = Freeze(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush Off = Freeze(Color.FromRgb(0xD3, 0xD7, 0xDE));
    private static readonly Brush OffHover = Freeze(Color.FromRgb(0xF8, 0xD0, 0x8A));

    private static readonly Geometry Star = BuildStar();

    private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static Geometry BuildStar()
    {
        // 单位正方形里画一个五角星，用的时候按尺寸缩放
        const double cx = 0.5, cy = 0.52, outer = 0.5, inner = 0.5 * 0.382;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            var pts = new Point[10];
            for (int i = 0; i < 10; i++)
            {
                double ang = -Math.PI / 2 + i * Math.PI / 5;
                double r = (i % 2 == 0) ? outer : inner;
                pts[i] = new Point(cx + r * Math.Cos(ang), cy + r * Math.Sin(ang));
            }
            ctx.BeginFigure(pts[0], true, true);
            ctx.PolyLineTo(pts[1..], true, false);
        }
        g.Freeze();
        return g;
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RatingBar)d).InvalidateVisual();

    public RatingBar()
    {
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = 5 * StarSize + 4 * Gap;
        double h = StarSize;
        if (!double.IsInfinity(availableSize.Width)) w = Math.Min(w, availableSize.Width);
        if (!double.IsInfinity(availableSize.Height)) h = Math.Min(h, availableSize.Height);
        return new Size(w, h);
    }

    /// <summary>
    /// 自定义绘制的元素默认只在自己画了东西的地方能点中，点在星与星之间就丢事件。
    /// 整个矩形都算命中。
    /// </summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        var r = new Rect(0, 0,
            Math.Max(ActualWidth, 5 * StarSize + 4 * Gap),
            Math.Max(ActualHeight, StarSize));
        return r.Contains(hitTestParameters.HitPoint)
            ? new PointHitTestResult(this, hitTestParameters.HitPoint)
            : null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double d = StarSize, g = Gap;
        double top = Math.Max(0, (RenderSize.Height - d) / 2);
        int shown = _hover >= 0 ? _hover : Value;

        for (int i = 0; i < 5; i++)
        {
            Brush b = i < shown ? On : (_hover > i ? OffHover : Off);
            dc.PushTransform(new TranslateTransform(i * (d + g), top));
            dc.PushTransform(new ScaleTransform(d, d));
            dc.DrawGeometry(b, null, Star);
            dc.Pop();
            dc.Pop();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsReadOnly) return;
        var p = e.GetPosition(this);
        int idx = IndexAt(p.X);
        if (idx != _hover) { _hover = idx; InvalidateVisual(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover != -1) { _hover = -1; InvalidateVisual(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (IsReadOnly) return;
        e.Handled = true;
        int idx = IndexAt(e.GetPosition(this).X);
        if (idx < 0) return;
        Set(Math.Clamp(idx + 1, 0, 5));
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (IsReadOnly) return;
        e.Handled = true;
        Set(0);
    }

    private void Set(int v)
    {
        if (v == Value) return;
        Value = v;
        RatingChanged?.Invoke(this, v);
    }

    private int IndexAt(double x)
    {
        double d = StarSize, g = Gap;
        if (x < 0) return -1;
        int i = (int)(x / (d + g));
        return i is >= 0 and < 5 ? i : -1;
    }
}

