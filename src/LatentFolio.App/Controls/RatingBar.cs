using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LatentFolio.App.Controls;

/// <summary>5 个小圆点评分条：点一下就设分，鼠标悬停预览。</summary>
public sealed class RatingBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(RatingBar),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    public static readonly DependencyProperty DotSizeProperty = DependencyProperty.Register(
        nameof(DotSize), typeof(double), typeof(RatingBar),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(RatingBar),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly), typeof(bool), typeof(RatingBar), new PropertyMetadata(false));

    public static readonly DependencyProperty AllowClearProperty = DependencyProperty.Register(
        nameof(AllowClear), typeof(bool), typeof(RatingBar), new PropertyMetadata(true));

    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double DotSize { get => (double)GetValue(DotSizeProperty); set => SetValue(DotSizeProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public bool IsReadOnly { get => (bool)GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public bool AllowClear { get => (bool)GetValue(AllowClearProperty); set => SetValue(AllowClearProperty, value); }

    public event EventHandler<int>? RatingChanged;

    private int _hover = -1;

    private static readonly Brush On = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
    private static readonly Brush Off = Freeze(new SolidColorBrush(Color.FromRgb(0xDD, 0xE0, 0xE6)));
    private static readonly Brush OffHover = Freeze(new SolidColorBrush(Color.FromRgb(0xF7, 0xC9, 0x7A)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RatingBar)d).InvalidateVisual();

    public RatingBar()
    {
        Height = 12;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = 5 * DotSize + 4 * Gap;
        return new Size(double.IsInfinity(availableSize.Width) ? w : Math.Min(w, availableSize.Width), DotSize);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double d = DotSize, g = Gap;
        double total = 5 * d + 4 * g;
        double x0 = 0;
        int shown = _hover >= 0 ? _hover : Value;

        for (int i = 0; i < 5; i++)
        {
            double cx = x0 + i * (d + g) + d / 2;
            double cy = d / 2;
            Brush b = i < shown ? On : (_hover > i ? OffHover : Off);
            dc.DrawEllipse(b, null, new Point(cx, cy), d / 2, d / 2);
        }
        _ = total;
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
        var p = e.GetPosition(this);
        int idx = IndexAt(p.X);
        int newVal = idx < 0 ? 0 : idx + 1;
        if (AllowClear && newVal == Value) newVal = 0;
        Value = newVal;
        RatingChanged?.Invoke(this, newVal);
    }

    private int IndexAt(double x)
    {
        double d = DotSize, g = Gap;
        if (x < 0) return -1;
        int i = (int)(x / (d + g));
        return i is >= 0 and < 5 ? i : -1;
    }
}
