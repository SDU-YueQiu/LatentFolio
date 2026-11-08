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

    public static void SetSourceItem(DependencyObject o, object? v) => o.SetValue(SourceItemProperty, v);
    public static object? GetSourceItem(DependencyObject o) => o.GetValue(SourceItemProperty);

    private static async void OnSourceItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image img) return;
        img.Source = null;

        var svc = App.Services?.ThumbService;
        if (svc is null) return;

        switch (e.NewValue)
        {
            case ImageVm vm:
                {
                    if (svc.Peek(vm.Id) is { } hit) { img.Source = hit; return; }
                    var bmp = await svc.LoadAsync(vm.Id, vm.Item.Path, vm.Item.MTime, vm.Item.Size);
                    if (ReferenceEquals(GetSourceItem(img), vm) && bmp is not null) img.Source = bmp;
                    break;
                }
            case PersonVm p:
                {
                    if (p.AvatarImageId == 0) return;
                    if (!App.Root.ById.TryGetValue(p.AvatarImageId, out var item)) return;
                    if (svc.Peek(item.Id) is { } h2) { img.Source = h2; return; }
                    var bmp = await svc.LoadAsync(item.Id, item.Path, item.MTime, item.Size);
                    if (ReferenceEquals(GetSourceItem(img), p) && bmp is not null) img.Source = bmp;
                    break;
                }
        }
    }
}
