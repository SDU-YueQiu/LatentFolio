using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace LatentFolio.App.Controls;

/// <summary>面板要从数据项上拿到宽高比才排得出版面。</summary>
public interface IAspectItem
{
    /// <summary>宽 / 高（原图像素）。</summary>
    double PixelAspect { get; }
}

/// <summary>
/// 等高、按原图比例定宽的虚拟化面板。
///
/// 一行里的图一样高、宽度各按自己的比例，所以既不裁切也不变形；整行再按可用宽度
/// 反算一次行高（justified），右边就永远是齐的。这跟胶片条是同一个道理，只是
/// 胶片条只排一行、这里要排很多行还要虚拟化。
/// </summary>
public sealed class JustifiedGridPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty TargetHeightProperty = DependencyProperty.Register(
        nameof(TargetHeight), typeof(double), typeof(JustifiedGridPanel),
        new FrameworkPropertyMetadata(188.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(JustifiedGridPanel),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty PadProperty = DependencyProperty.Register(
        nameof(Pad), typeof(Thickness), typeof(JustifiedGridPanel),
        new FrameworkPropertyMetadata(new Thickness(18, 4, 18, 24), FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>图片下面那两块（文件名 + 星级）的固定高度，行高要把它加进去。</summary>
    public static readonly DependencyProperty CaptionHeightProperty = DependencyProperty.Register(
        nameof(CaptionHeight), typeof(double), typeof(JustifiedGridPanel),
        new FrameworkPropertyMetadata(38.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double TargetHeight { get => (double)GetValue(TargetHeightProperty); set => SetValue(TargetHeightProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public Thickness Pad { get => (Thickness)GetValue(PadProperty); set => SetValue(PadProperty, value); }
    public double CaptionHeight { get => (double)GetValue(CaptionHeightProperty); set => SetValue(CaptionHeightProperty, value); }

    private Size _extent;
    private Size _viewport;
    private Point _offset;

    // 一次算好的版面：每一项的矩形（内容坐标），以及每行的上下沿和索引区间
    private Rect[] _rects = [];
    private readonly List<double> _rowTop = [];
    private readonly List<double> _rowBottom = [];
    private readonly List<int> _rowStart = [];
    private readonly List<int> _rowEnd = [];
    private double _layoutWidth = -1;
    private int _layoutCount = -1;
    private double _layoutTarget = -1;
    private double _layoutCaption = -1;
    private double _layoutSpacing = -1;

    // ---------------- 集合变化 ----------------

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
                {
                    var index = args.Position.Index;
                    var count = args.ItemUICount;
                    if (index >= 0 && count > 0 && index + count <= InternalChildren.Count)
                        RemoveInternalChildRange(index, count);
                    break;
                }
            case NotifyCollectionChangedAction.Move:
                {
                    var index = args.Position.Index;
                    var count = args.ItemUICount;
                    if (index >= 0 && count > 0 && index + count <= InternalChildren.Count)
                        RemoveInternalChildRange(index, count);
                    break;
                }
            case NotifyCollectionChangedAction.Reset:
                if (InternalChildren.Count > 0) RemoveInternalChildRange(0, InternalChildren.Count);
                break;
        }
        _layoutWidth = -1;   // 强制重排
        InvalidateMeasure();
    }

    // ---------------- 自检用 ----------------

    internal int FirstRealizedIndex
    {
        get
        {
            if (InternalChildren.Count == 0) return -1;
            try { return ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(0, 0)); }
            catch { return -1; }
        }
    }

    internal int RealizedCount => InternalChildren.Count;

    /// <summary>第一个已实例化容器所在那一行有几张（等高布局没有固定列数）。</summary>
    internal int Columns
    {
        get
        {
            int i = FirstRealizedIndex;
            if (i < 0) return 1;
            int r = RowOfIndex(i);
            return r < 0 ? 1 : _rowEnd[r] - _rowStart[r] + 1;
        }
    }

    /// <summary>行高 + 图注，滚动一格用。</summary>
    internal double ItemHeight => TargetHeight + CaptionHeight;

    /// <summary>第一个已实例化容器所在那一行的总高（行高不固定，自检要用它做容差）。</summary>
    internal double FirstRowHeight
    {
        get
        {
            int i = FirstRealizedIndex;
            int r = i < 0 ? -1 : RowOfIndex(i);
            return r < 0 ? ItemHeight : _rowBottom[r] - _rowTop[r];
        }
    }

    internal double FirstChildTop => InternalChildren.Count > 0
        ? InternalChildren[0].TranslatePoint(new Point(0, 0), this).Y
        : double.NaN;

    // ---------------- 版面计算 ----------------

    private int RowOfIndex(int idx)
    {
        // 行少时线性扫就够；行多时二分行起点
        int lo = 0, hi = _rowStart.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (idx < _rowStart[mid]) hi = mid - 1;
            else if (idx > _rowEnd[mid]) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    private double AspectOf(ItemsControl? owner, int i)
    {
        double a = 1;
        if (owner is not null && i < owner.Items.Count && owner.Items[i] is IAspectItem x)
            a = x.PixelAspect;
        if (double.IsNaN(a) || double.IsInfinity(a) || a <= 0) return 1;
        return Math.Clamp(a, 1.0 / 6.0, 6.0);
    }

    /// <summary>
    /// 把整批项排成行。不用实例化容器 —— 宽高比从数据上就有，
    /// 所以几千张也只是一次加减法（实测 4900 张 < 1 ms）。
    /// </summary>
    private void BuildLayout(ItemsControl? owner, int count, double availW)
    {
        if (count != _layoutCount) { _rects = new Rect[count]; _layoutCount = count; }

        _rowTop.Clear(); _rowBottom.Clear(); _rowStart.Clear(); _rowEnd.Clear();
        if (count == 0)
        {
            _layoutWidth = availW; _layoutTarget = TargetHeight;
            _layoutCaption = CaptionHeight; _layoutSpacing = Spacing;
            return;
        }

        double sp = Spacing, target = Math.Max(24, TargetHeight), cap = CaptionHeight;
        double y = Pad.Top;
        int i = 0;

        while (i < count)
        {
            // 先按目标行高试排，看这一行能放几张
            int j = i, k = 0;
            double sumAspect = 0, sumW = 0;
            while (j < count)
            {
                double a = AspectOf(owner, j);
                double w = target * a;
                double add = k == 0 ? w : w + sp;
                if (k > 0 && sumW + add > availW) break;
                sumW += add;
                sumAspect += a;
                k++;
                j++;
            }
            if (k == 0) { k = 1; sumAspect = AspectOf(owner, i); j = i + 1; }

            // 整行铺满 → 反算这一行真正的高度；最后一行不满（或只有一张）就保持目标高度，
            // 否则一张超宽图会被压成一根条
            bool justify = k > 1 && j < count;
            double rowH = justify
                ? (availW - (k - 1) * sp) / Math.Max(sumAspect, 0.01)
                : target;
            rowH = Math.Clamp(rowH, target * 0.45, target * 1.8);

            double x = Pad.Left;
            for (int t = 0; t < k; t++)
            {
                int idx = i + t;
                double w = Math.Max(1, rowH * AspectOf(owner, idx));
                _rects[idx] = new Rect(x, y, w, rowH + cap);
                x += w + sp;
            }

            _rowTop.Add(y);
            _rowBottom.Add(y + rowH + cap);
            _rowStart.Add(i);
            _rowEnd.Add(i + k - 1);
            y += rowH + cap + sp;
            i += k;
        }

        _layoutWidth = availW;
        _layoutTarget = TargetHeight;
        _layoutCaption = CaptionHeight;
        _layoutSpacing = Spacing;
    }

    // ---------------- 测量 ----------------

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        int count = owner?.Items.Count ?? 0;

        var pad = Pad;
        double vw = double.IsInfinity(availableSize.Width) ? Math.Max(320, TargetHeight * 2) : availableSize.Width;
        double vh = double.IsInfinity(availableSize.Height) ? ItemHeight * 2 : availableSize.Height;
        double availW = Math.Max(1, vw - pad.Left - pad.Right);

        // 宽度、数量、行高、图注高度、间距，任何一个变了都要重排（换档位就是改行高）
        bool relayout = Math.Abs(availW - _layoutWidth) > 0.4
                        || count != _layoutCount
                        || _rects.Length != count
                        || Math.Abs(TargetHeight - _layoutTarget) > 0.01
                        || Math.Abs(CaptionHeight - _layoutCaption) > 0.01
                        || Math.Abs(Spacing - _layoutSpacing) > 0.01;
        int anchorIndex = -1;
        double anchorDelta = 0;
        if (relayout && count > 0 && _rowTop.Count > 0)
        {
            int r = RowAtOffset(_offset.Y);
            if (r >= 0) { anchorIndex = _rowStart[r]; anchorDelta = _rowTop[r] - _offset.Y; }
        }

        if (relayout) BuildLayout(owner, count, availW);

        if (anchorIndex >= 0)
        {
            int r = RowOfIndex(anchorIndex);
            if (r >= 0) _offset.Y = Math.Max(0, _rowTop[r] - anchorDelta);
        }

        double extentH = _rowBottom.Count > 0
            ? _rowBottom[^1] + pad.Bottom
            : pad.Top + pad.Bottom;
        UpdateScroll(new Size(vw, Math.Max(extentH, vh)), new Size(vw, vh));

        var (first, last) = Range(count);
        Realize(first, last);
        return new Size(vw, vh);
    }

    /// <summary>视口顶边落在哪一行。</summary>
    private int RowAtOffset(double offY)
    {
        if (_rowBottom.Count == 0) return -1;
        int lo = 0, hi = _rowBottom.Count - 1, ans = _rowBottom.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_rowBottom[mid] > offY) { ans = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        return ans;
    }

    private (int First, int Last) Range(int count)
    {
        if (count == 0 || _rowTop.Count == 0) return (0, -1);
        int r0 = RowAtOffset(_offset.Y);
        int r1 = RowAtOffset(_offset.Y + _viewport.Height);
        if (r0 < 0) return (0, -1);
        r1 = Math.Max(r0, Math.Min(r1, _rowTop.Count - 1));
        return (_rowStart[r0], _rowEnd[r1]);
    }

    private void Realize(int first, int last)
    {
        var gen = ItemContainerGenerator;

        if (last < first)
        {
            if (InternalChildren.Count > 0)
            {
                gen.RemoveAll();
                RemoveInternalChildRange(0, InternalChildren.Count);
            }
            return;
        }

        CleanUpItems(first, last);

        var startPos = gen.GeneratorPositionFromIndex(first);
        int childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;
        if (childIndex < 0) childIndex = 0;

        using (gen.StartAt(startPos, GeneratorDirection.Forward, true))
        {
            for (int i = first; i <= last; i++, childIndex++)
            {
                if (gen.GenerateNext(out bool isNew) is not UIElement child) break;
                if (isNew)
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                    gen.PrepareItemContainer(child);
                }
                var r = i < _rects.Length ? _rects[i] : new Rect(0, 0, TargetHeight, ItemHeight);
                child.Measure(new Size(Math.Max(1, r.Width - 0.5), Math.Max(1, r.Height - 0.5)));
            }
        }
    }

    private void CleanUpItems(int minIndex, int maxIndex)
    {
        var gen = ItemContainerGenerator;
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            int itemIndex;
            try { itemIndex = gen.IndexFromGeneratorPosition(new GeneratorPosition(i, 0)); }
            catch { itemIndex = -1; }

            if (itemIndex < minIndex || itemIndex > maxIndex || itemIndex < 0)
            {
                try { gen.Remove(new GeneratorPosition(i, 0), 1); } catch { }
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    // ---------------- 排列 ----------------

    protected override Size ArrangeOverride(Size finalSize)
    {
        var gen = ItemContainerGenerator;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            int idx;
            try { idx = gen.IndexFromGeneratorPosition(new GeneratorPosition(i, 0)); }
            catch { continue; }
            if (idx < 0 || idx >= _rects.Length) continue;
            var r = _rects[idx];
            child.Arrange(new Rect(r.X, r.Y - _offset.Y, r.Width, r.Height));
        }
        return finalSize;
    }

    // ---------------- IScrollInfo ----------------

    private void UpdateScroll(Size extent, Size viewport)
    {
        bool changed = false;
        if (_extent != extent) { _extent = extent; changed = true; }
        if (_viewport != viewport) { _viewport = viewport; changed = true; }
        if (changed)
        {
            ScrollOwner?.InvalidateScrollInfo();
            CoerceOffset();
        }
    }

    private void CoerceOffset()
    {
        double maxY = Math.Max(0, _extent.Height - _viewport.Height);
        double maxX = Math.Max(0, _extent.Width - _viewport.Width);
        var o = new Point(Math.Clamp(_offset.X, 0, maxX), Math.Clamp(_offset.Y, 0, maxY));
        if (o != _offset) { _offset = o; ScrollOwner?.InvalidateScrollInfo(); }
    }

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(VerticalOffset - ItemHeight / 3);
    public void LineDown() => SetVerticalOffset(VerticalOffset + ItemHeight / 3);
    public void LineLeft() => SetHorizontalOffset(HorizontalOffset - 24);
    public void LineRight() => SetHorizontalOffset(HorizontalOffset + 24);
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void PageLeft() => SetVerticalOffset(VerticalOffset - ViewportWidth);
    public void PageRight() => SetVerticalOffset(VerticalOffset + ViewportWidth);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - ItemHeight * 0.9);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + ItemHeight * 0.9);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is not UIElement child) return rectangle;
        int i = InternalChildren.IndexOf(child);
        if (i < 0) return rectangle;

        int idx;
        try { idx = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0)); }
        catch { return rectangle; }
        if (idx < 0) return rectangle;

        int r = RowOfIndex(idx);
        if (r < 0) return rectangle;
        double top = _rowTop[r], bottom = _rowBottom[r];

        if (top < _offset.Y) SetVerticalOffset(top - Pad.Top);
        else if (bottom > _offset.Y + _viewport.Height) SetVerticalOffset(bottom - _viewport.Height + Pad.Bottom);
        return rectangle;
    }

    public void SetHorizontalOffset(double offset)
    {
        if (!CanHorizontallyScroll) return;
        _offset.X = Math.Max(0, offset);
        CoerceOffset();
        InvalidateArrange();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public void SetVerticalOffset(double offset)
    {
        _offset.Y = Math.Max(0, offset);
        CoerceOffset();
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }
}
