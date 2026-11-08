using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LatentFolio.App.Controls;

/// <summary>
/// ScrollBar 模板里的滑块，顶替原生 Track。
///
/// 原生 Track 按「视口 / 内容」的比例算滑块长度，图库四千多张时只剩十几像素；
/// 而且它排布滑块时直接覆盖 Thumb.MinHeight，所以最小长度只能自己算。
/// 拖动 / 点轨道 / 滚轮都直接叫 ScrollViewer 滚（不是改 ScrollBar.Value ——
/// ScrollViewer 不会因为 Value 变了就自己跳过去），位置和范围也一律问它。
/// </summary>
public sealed class SlimThumb : FrameworkElement
{
    /// <summary>滑块最小长度（DIP）。48 是能稳稳抓住的下限。</summary>
    public double MinLength { get; set; } = 48;

    /// <summary>滑块粗细（DIP），贴着视口右/下边缘画。</summary>
    public double Thickness { get; set; } = 7;

    private static readonly Brush Rest = Frozen("#FFC9CFDB");
    private static readonly Brush Hover = Frozen("#FFAAB2C3");
    private static readonly Brush Pressed = Frozen("#FF8D95A8");

    private static Brush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
        b.Freeze();
        return b;
    }

    private bool _dragging;
    private bool _hoverThumb;
    private bool _paging;
    private bool _pageDown;
    private double _grabAt;
    private double _grabOffset;
    private DispatcherTimer? _pageTimer;
    private bool _hooked;
    private readonly List<(DependencyPropertyDescriptor D, EventHandler H)> _watchers = [];

    public SlimThumb()
    {
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();
    }

    private ScrollBar? Bar => TemplatedParent as ScrollBar;
    private ScrollViewer? _sv;

    private bool Vertical => Bar?.Orientation != Orientation.Horizontal;

    private double TrackLength => Math.Max(0, Vertical ? ActualHeight : ActualWidth);

    // 位置和范围一律问 ScrollViewer：不同面板的滚动单位不一样（像素 / 项），
    // ScrollViewer 给出的 Extent/Viewport/Offset 和它的 ScrollToXxxOffset 是同一套单位。
    private double Viewport => _sv is { } v ? (Vertical ? v.ViewportHeight : v.ViewportWidth) : Bar?.ViewportSize ?? 0;

    private double Extent => _sv is { } v
        ? (Vertical ? v.ExtentHeight : v.ExtentWidth)
        : Bar is { } b ? b.Maximum + b.ViewportSize : 0;

    private double Offset => _sv is { } v
        ? (Vertical ? v.VerticalOffset : v.HorizontalOffset)
        : Bar?.Value ?? 0;

    private double Range => Math.Max(0, Extent - Viewport);

    /// <summary>滑块长度；内容还没超出视口时为 0（不画、也不吃点击）。</summary>
    public double ThumbLength
    {
        get
        {
            double viewport = Viewport;
            double extent = Extent;
            double track = TrackLength;
            if (Range <= 0 || viewport <= 0 || extent <= 0 || track <= 0) return 0;
            return Math.Min(track, Math.Max(MinLength, track * viewport / extent));
        }
    }

    /// <summary>滑块沿着轨道的起点。</summary>
    public double ThumbStart
    {
        get
        {
            double free = TrackLength - ThumbLength;
            if (free <= 0 || Range <= 0) return 0;
            return Math.Clamp(Offset / Range, 0, 1) * free;
        }
    }

    // ================= 画 =================

    protected override void OnRender(DrawingContext dc)
    {
        double len = ThumbLength;
        if (len <= 0.5) return;

        bool vertical = Vertical;
        double cross = vertical ? ActualWidth : ActualHeight;
        // 悬停/拖动时滑块变粗一点：不用看颜色变化也知道抓到的是哪一根
        double thick = Math.Min(_dragging || _hoverThumb ? Thickness + 2 : Thickness, cross);
        // 贴着视口右/下边（留 3px）：ScrollBar 的槽宽是 17（ScrollViewer 模板定的），
        // 居中画会离窗口边老远，看着像浮在空地里
        double off = Math.Max(0, cross - thick - 3);

        // 透明底也要画：整条滑槽都能接住「点轨道翻页」和滚轮
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        Brush brush = _dragging ? Pressed : _hoverThumb ? Hover : Rest;
        Rect rect = vertical
            ? new Rect(off, ThumbStart, thick, len)
            : new Rect(ThumbStart, off, len, thick);
        dc.DrawRoundedRectangle(brush, null, rect, 3, 3);
    }

    // ================= 交互 =================

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        UpdateHover(e.GetPosition(this));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverThumb = false;
        if (!_dragging) InvalidateVisual();
    }

    private void UpdateHover(Point p)
    {
        double len = ThumbLength;
        double along = Along(p);
        bool on = len > 0.5 && along >= ThumbStart - 2 && along <= ThumbStart + len + 2;
        if (on == _hoverThumb) return;
        _hoverThumb = on;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (ThumbLength <= 0) return;

        double along = Along(e.GetPosition(this));
        double start = ThumbStart;
        bool onThumb = along >= start - 2 && along <= start + ThumbLength + 2;

        CaptureMouse();
        if (onThumb)
        {
            _dragging = true;
            _grabAt = along;
            _grabOffset = Offset;
        }
        else
        {
            bool up = along < start;
            Page(up);
            StartPageRepeat(up);
        }
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragging && e.LeftButton == MouseButtonState.Pressed)
        {
            DragBy(Along(p) - _grabAt);
            return;
        }
        UpdateHover(p);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        bool wasActive = _dragging || _paging;
        _dragging = false;
        _paging = false;
        _pageTimer?.Stop();
        _pageTimer = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (wasActive)
        {
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
        _paging = false;
        _pageTimer?.Stop();
        _pageTimer = null;
        InvalidateVisual();
    }

    private double Along(Point p) => Vertical ? p.Y : p.X;

    private void DragBy(double dx)
    {
        double free = TrackLength - ThumbLength;
        if (free <= 0.5 || Range <= 0) return;
        ScrollTo(_grabOffset + dx / free * Range);
    }

    private void Page(bool up)
    {
        double step = Math.Max(1, Viewport * 0.9);
        ScrollTo(Offset + (up ? -step : step));
    }

    // 只改 ScrollBar.Value 是不够的：ScrollViewer 并不会因为 Value 变了就来取位置。
    // 直接叫 ScrollViewer 滚，它自己会把 ScrollBar.Value 同步回去，滑块跟着重画。
    private void ScrollTo(double value)
    {
        double v = Math.Clamp(value, 0, Range);
        if (_sv is not null)
        {
            if (Vertical) _sv.ScrollToVerticalOffset(v);
            else _sv.ScrollToHorizontalOffset(v);
        }
        else if (Bar is { } bar)
        {
            bar.Value = Math.Clamp(v, bar.Minimum, bar.Maximum);
        }
    }

    private void StartPageRepeat(bool up)
    {
        _paging = true;
        _pageDown = !up;
        _pageTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _pageTimer.Tick += (_, _) =>
        {
            Page(!_pageDown);
            if (_pageTimer is not null) _pageTimer.Interval = TimeSpan.FromMilliseconds(80);
        };
        _pageTimer.Start();
    }

    // ================= 跟着 ScrollBar / ScrollViewer 刷新 =================

    private void Hook()
    {
        var bar = Bar;
        if (bar is null || _hooked) return;
        _hooked = true;
        _sv = FindAncestor<ScrollViewer>(this);
        bar.ValueChanged += OnBarValueChanged;
        bar.SizeChanged += OnBarSizeChanged;
        if (_sv is not null) _sv.ScrollChanged += OnScrollChanged;
        Watch(RangeBase.MaximumProperty);
        Watch(RangeBase.MinimumProperty);
        Watch(ScrollBar.ViewportSizeProperty);
        InvalidateVisual();

        void Watch(DependencyProperty dp)
        {
            var d = DependencyPropertyDescriptor.FromProperty(dp, typeof(ScrollBar));
            if (d is null) return;
            EventHandler h = (_, _) => InvalidateVisual();
            d.AddValueChanged(bar, h);
            _watchers.Add((d, h));
        }
    }

    private void Unhook()
    {
        var bar = Bar;
        if (bar is not null && _hooked)
        {
            bar.ValueChanged -= OnBarValueChanged;
            bar.SizeChanged -= OnBarSizeChanged;
            foreach (var (d, h) in _watchers) d.RemoveValueChanged(bar, h);
        }
        if (_sv is not null) _sv.ScrollChanged -= OnScrollChanged;
        _sv = null;
        _watchers.Clear();
        _hooked = false;
        _pageTimer?.Stop();
        _pageTimer = null;
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = VisualTreeHelper.GetParent(d); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    private void OnBarValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e) => InvalidateVisual();

    private void OnBarSizeChanged(object sender, SizeChangedEventArgs e) => InvalidateVisual();

    // ================= 自检用 =================

    public double ThumbLengthForTest => ThumbLength;

    public double ThumbStartForTest => ThumbStart;

    /// <summary>自检：按真实拖拽那条路径挪一下滑块，看看 ScrollViewer 有没有跟着走。</summary>
    internal void NudgeForTest(double dx)
    {
        _grabOffset = Offset;
        DragBy(dx);
    }

    internal void ScrollToForTest(double value) => ScrollTo(value);
}
