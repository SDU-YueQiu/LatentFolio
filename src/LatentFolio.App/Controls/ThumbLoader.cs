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
        new PropertyMetadata(0.0, OnSourceItemChanged));

    public static void SetSourceItem(DependencyObject o, object? v) => o.SetValue(SourceItemProperty, v);
    public static object? GetSourceItem(DependencyObject o) => o.GetValue(SourceItemProperty);

    public static void SetPixelWidth(DependencyObject o, double v) => o.SetValue(PixelWidthProperty, v);
    public static double GetPixelWidth(DependencyObject o) => (double)o.GetValue(PixelWidthProperty);

    /// <summary>按 DPI 把 DIP 换成物理像素，解码尺寸就是屏幕上真正要用的尺寸。</summary>
    private static int TargetPx(Image img)
    {
        double dip = GetPixelWidth(img);
        if (dip <= 0) dip = App.Root?.TileSize ?? 188;
        double scale = 1.0;
        try { scale = System.Windows.Media.VisualTreeHelper.GetDpi(img).PixelsPerDip; } catch { }
        return (int)Math.Round(dip * scale);
    }

    private static async void OnSourceItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image img) return;
        img.Source = null;

        var svc = App.Services?.ThumbService;
        if (svc is null) return;
        int px = TargetPx(img);

        switch (e.NewValue)
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
                    if (!App.Root.ById.TryGetValue(chip.AvatarImageId, out var item)) return;
                    if (svc.Peek(item.Id, px) is { } h3) { img.Source = h3; return; }
                    var bmp3 = await svc.LoadAsync(item.Id, item.Path, item.MTime, item.Size, px);
                    if (ReferenceEquals(GetSourceItem(img), chip) && bmp3 is not null) img.Source = bmp3;
                    break;
                }
            case PersonVm p:
                {
                    if (p.AvatarImageId == 0) return;
                    if (!App.Root.ById.TryGetValue(p.AvatarImageId, out var item)) return;
                    if (svc.Peek(item.Id, px) is { } h2) { img.Source = h2; return; }
                    var bmp = await svc.LoadAsync(item.Id, item.Path, item.MTime, item.Size, px);
                    if (ReferenceEquals(GetSourceItem(img), p) && bmp is not null) img.Source = bmp;
                    break;
                }
        }
    }
}

