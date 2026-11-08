using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LatentFolio.App.ViewModels;

namespace LatentFolio.App.Controls;

/// <summary>
/// 缩略图懒加载。绑上 ImageVm / PersonVm，进入视野才开始解码，滚出去不占内存。
/// </summary>
public static class ThumbLoader
{
    public static readonly DependencyProperty SourceItemProperty = DependencyProperty.RegisterAttached(
        "SourceItem", typeof(object), typeof(ThumbLoader),
        new PropertyMetadata(null, OnSourceItemChanged));

    /// <summary>要显示的宽度（DIP）。0 表示按网格缩略图当前档位算。</summary>
    public static readonly DependencyProperty PixelWidthProperty = DependencyProperty.RegisterAttached(
        "PixelWidth", typeof(double), typeof(ThumbLoader),
        new PropertyMetadata(0.0, OnPixelWidthChanged));

    public static void SetSourceItem(DependencyObject o, object? v) => o.SetValue(SourceItemProperty, v);
    public static object? GetSourceItem(DependencyObject o) => o.GetValue(SourceItemProperty);

    public static void SetPixelWidth(DependencyObject o, double v) => o.SetValue(PixelWidthProperty, v);
    public static double GetPixelWidth(DependencyObject o) => (double)o.GetValue(PixelWidthProperty);

    /// <summary>
    /// 需要多大的缩略图：按「铺满这个盒子」倒推长边，而不是直接拿盒子宽度当长边。
    /// 竖图的长边是高，盒子宽度顶上去的话解出来的图比盒子还窄，WPF 再放大一次 —— 就是糊的根源。
    /// </summary>
    private static int NeedLongSide(Image img, object? item)
    {
        double dip = GetPixelWidth(img);
        if (dip <= 0) dip = App.Root?.TileSize ?? 188;

        double dpi = 1.0;
        try { dpi = System.Windows.Media.VisualTreeHelper.GetDpi(img).PixelsPerDip; } catch { }

        double boxW = img.ActualWidth > 1 ? img.ActualWidth : dip;
        double boxH = img.ActualHeight > 1 ? img.ActualHeight : dip;
        boxW *= dpi;
        boxH *= dpi;

        int sw = 0, sh = 0;
        if (item is ImageVm vm) { sw = vm.Width; sh = vm.Height; }
        if (sw <= 0 || sh <= 0) return (int)Math.Round(Math.Max(boxW, boxH));

        // UniformToFill：两轴各算一次放大倍数，取大的那个
        double need = Math.Max(boxW / sw, boxH / sh);
        return (int)Math.Round(Math.Max(sw, sh) * Math.Max(need, 0.05));
    }

    private static void OnPixelWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Image img) _ = Apply(img, GetSourceItem(img));
    }

    private static async void OnSourceItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Image img) await Apply(img, e.NewValue);
    }

    private static void OnImageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Image img && img.Source is not null) _ = Apply(img, GetSourceItem(img));
    }

    private static async Task Apply(Image img, object? item)
    {
        var svc = App.Services?.ThumbService;
        if (svc is null) return;

        img.SizeChanged -= OnImageSizeChanged;
        img.SizeChanged += OnImageSizeChanged;

        int px = Services.ThumbService.Bucket(NeedLongSide(img, item));

        switch (item)
        {
            case ImageVm vm:
                {
                    if (svc.Peek(vm.Id, px) is { } hit) { img.Source = hit; return; }
                    var bmp = await svc.LoadAsync(vm.Id, vm.Item.Path, vm.Item.MTime, vm.Item.Size, px);
                    if (ReferenceEquals(GetSourceItem(img), vm) && bmp is not null) img.Source = bmp;
                    break;
                }
            case LatentFolio.App.ViewModels.PersonChip chip:
                {
                    if (chip.AvatarImageId == 0) return;
                    if (!App.Root.ById.TryGetValue(chip.AvatarImageId, out var item2)) return;
                    if (svc.Peek(item2.Id, px) is { } h3) { img.Source = h3; return; }
                    var bmp3 = await svc.LoadAsync(item2.Id, item2.Path, item2.MTime, item2.Size, px);
                    if (ReferenceEquals(GetSourceItem(img), chip) && bmp3 is not null) img.Source = bmp3;
                    break;
                }
            case PersonVm p:
                {
                    if (p.AvatarImageId == 0) return;
                    if (!App.Root.ById.TryGetValue(p.AvatarImageId, out var item2)) return;
                    if (svc.Peek(item2.Id, px) is { } h2) { img.Source = h2; return; }
                    var bmp = await svc.LoadAsync(item2.Id, item2.Path, item2.MTime, item2.Size, px);
                    if (ReferenceEquals(GetSourceItem(img), p) && bmp is not null) img.Source = bmp;
                    break;
                }
        }
    }
}

