using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace LatentFolio.App.Controls;

/// <summary>
/// 固定格子尺寸的虚拟化换行面板。只实例化视口内的容器，其余靠索引算位置。
///
/// 关键：必须实现 OnItemsChanged。VirtualizingPanel 的容器生成器和 InternalChildren
/// 是两套账，集合增删时不一起改，下一次 Measure 里 Remove 就会空引用崩掉
/// （实测「删一张图就报错」正是这个）。
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(180.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(200.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty PadProperty = DependencyProperty.Register(
        nameof(Pad), typeof(Thickness), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(new Thickness(16), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public Thickness Pad { get => (Thickness)GetValue(PadProperty); set => SetValue(PadProperty, value); }

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private int _columns = 1;

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
                    {
                        RemoveInternalChildRange(index, count);
                        // 移动的目标位置由下一轮 Realize 重建
                    }
                    break;
                }
            case NotifyCollectionChangedAction.Reset:
                if (InternalChildren.Count > 0) RemoveInternalChildRange(0, InternalChildren.Count);
                break;
        }
        InvalidateMeasure();
    }

    // ---------------- 自检用 ----------------

    /// <summary>当前第一个已实例化容器对应的数据索引，-1 表示一个都没有。</summary>
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

    /// <summary>第一个已实例化容器在面板坐标系里的 Y。正常应该落在视口范围内。</summary>
    internal double FirstChildTop => InternalChildren.Count > 0
        ? InternalChildren[0].TranslatePoint(new Point(0, 0), this).Y
        : double.NaN;

    // ---------------- 测量 ----------------

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        int count = owner?.Items.Count ?? 0;

        double iw = ItemWidth, ih = ItemHeight, sp = Spacing;
        var pad = Pad;
        double vw = double.IsInfinity(availableSize.Width) ? iw + sp + pad.Left + pad.Right : availableSize.Width;
        double vh = double.IsInfinity(availableSize.Height) ? ih + sp + pad.Top + pad.Bottom : availableSize.Height;

        double innerW = Math.Max(iw, vw - pad.Left - pad.Right);
        _columns = Math.Max(1, (int)Math.Floor((innerW + sp) / (iw + sp)));

        int rows = (int)Math.Ceiling(count / (double)_columns);
        double extentH = pad.Top + pad.Bottom + rows * (ih + sp) - (rows > 0 ? sp : 0);
        double extentW = vw;

        UpdateScroll(new Size(extentW, Math.Max(extentH, vh)), new Size(vw, vh));

        var (first, last) = Range(count, ih, sp, pad);
        Realize(first, last, new Size(Math.Max(1, iw - 0.5), Math.Max(1, ih - 0.5)));

        return new Size(vw, vh);
    }

    private (int First, int Last) Range(int count, double ih, double sp, Thickness pad)
    {
        if (count == 0) return (0, -1);
        double top = _offset.Y;
        double bottom = top + _viewport.Height;

        int firstRow = (int)Math.Floor((top - pad.Top) / (ih + sp));
        int lastRow = (int)Math.Floor((bottom - pad.Top) / (ih + sp));
        firstRow = Math.Max(0, firstRow);
        lastRow = Math.Max(0, lastRow);

        int first = firstRow * _columns;
        int last = Math.Min(count - 1, (lastRow + 1) * _columns - 1);
        if (first > last) return (0, -1);
        return (first, last);
    }

    private void Realize(int first, int last, Size itemSize)
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

        // GeneratorPositionFromIndex 对「尚未实例化」的项返回 (最近容器的 child 索引, 前进多少项)。
        // 这个 Offset 就是用来 seek 的，不能被抹掉 —— 之前把 Offset 归零，
        // 于是滚动之后仍然从第 0 项开始生成，容器全排在视口上方，看起来就是「滚下去是空白」。
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
                child.Measure(itemSize);
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
        double iw = ItemWidth, ih = ItemHeight, sp = Spacing;
        var pad = Pad;
        var gen = ItemContainerGenerator;

        double innerW = Math.Max(iw, finalSize.Width - pad.Left - pad.Right);
        int cols = Math.Max(1, (int)Math.Floor((innerW + sp) / (iw + sp)));
        double used = cols * iw + (cols - 1) * sp;
        double left0 = pad.Left + Math.Max(0, (innerW - used) / 2);

        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            int idx;
            try { idx = gen.IndexFromGeneratorPosition(new GeneratorPosition(i, 0)); }
            catch { continue; }
            if (idx < 0) continue;
            int r = idx / cols, c = idx % cols;
            double x = left0 + c * (iw + sp);
            double y = pad.Top + r * (ih + sp) - _offset.Y;
            child.Arrange(new Rect(x, y, iw, ih));
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
    public void PageLeft() => SetHorizontalOffset(HorizontalOffset - ViewportWidth);
    public void PageRight() => SetHorizontalOffset(HorizontalOffset + ViewportWidth);
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

        double iw = ItemWidth, ih = ItemHeight, sp = Spacing;
        double innerW = Math.Max(iw, _viewport.Width - Pad.Left - Pad.Right);
        int cols = Math.Max(1, (int)Math.Floor((innerW + sp) / (iw + sp)));
        int row = idx / cols;
        double top = Pad.Top + row * (ih + sp);
        double bottom = top + ih;

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
