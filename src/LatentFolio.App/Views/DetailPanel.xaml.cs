using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using LatentFolio.App.ViewModels;

namespace LatentFolio.App.Views;

/// <summary>右侧详情栏。查看器和图库主页共用这一个，6 段顺序按设计稿 §12.3 固定。</summary>
public partial class DetailPanel : UserControl
{
    private static readonly TimeSpan CopiedDuration = TimeSpan.FromMilliseconds(1200);

    // 每个被点的按钮/标签一个计时器：连点时只重置自己那个，不会叠出多个把文字卡住
    private readonly Dictionary<ContentControl, DispatcherTimer> _copiedTimers = new();
    private readonly Dictionary<ContentControl, BindingBase?> _copiedBindings = new();
    private readonly Dictionary<ContentControl, object?> _copiedContent = new();

    public DetailPanel()
    {
        InitializeComponent();
    }

    private static AppVm Vm => App.Root;

    private void OnCopyPrompt(object sender, RoutedEventArgs e)
    {
        if (TryCopy(Vm.Current?.Prompt) && sender is ContentControl target) FlashCopied(target);
    }

    private void OnCopyNegative(object sender, RoutedEventArgs e)
    {
        if (TryCopy(Vm.Current?.Negative) && sender is ContentControl target) FlashCopied(target);
    }

    private void OnCopyWorkflow(object sender, RoutedEventArgs e) => Vm.CopyWorkflow();

    private void OnPersonTagClick(object sender, RoutedEventArgs e)
    {
        if (sender is ContentControl { DataContext: PersonChip chip } target && TryCopy(chip.Tag))
            FlashCopied(target);
    }

    private void OnArtistChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is ContentControl { DataContext: string artist } target && TryCopy(artist))
            FlashCopied(target);
    }

    private void OnPersonRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PersonChip chip }) Vm.ShowPersonByTag(chip.Tag);
    }

    /// <summary>右键角色行：拿当前这张图当这个角色的头像。</summary>
    private void OnPersonRowRightDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PersonChip chip } el) return;
        if (Vm.Current is null) return;

        e.Handled = true;
        var menu = new ContextMenu();

        var set = new MenuItem { Header = $"用这张图作「{chip.Display}」的头像" };
        set.Click += (_, _) => Vm.SetPersonAvatar(chip.Tag, Vm.Current.Id);
        menu.Items.Add(set);

        var reset = new MenuItem { Header = "恢复默认头像（最高分那张）" };
        reset.Click += (_, _) => Vm.SetPersonAvatar(chip.Tag, 0);
        menu.Items.Add(reset);

        menu.PlacementTarget = el;
        menu.IsOpen = true;
    }

    /// <summary>展开时才去读工作流，几十 KB 的 JSON 不用跟着每次切图都反序列化。</summary>
    private void OnWorkflowExpanded(object sender, RoutedEventArgs e)
    {
        if (Vm.Current is not null) Vm.EnsureWorkflow(Vm.Current);
    }

    private static bool TryCopy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把被点的标签/按钮文字临时换成「已复制」，1.2 秒后恢复原样；同一目标连点只重新计时。</summary>
    private void FlashCopied(ContentControl target)
    {
        if (_copiedTimers.TryGetValue(target, out var running))
        {
            running.Stop();
        }
        else
        {
            // 首次点击时才记下原内容：绑定目标记下 Binding，字面量目标记下文字
            _copiedBindings[target] = BindingOperations.GetBinding(target, ContentControl.ContentProperty);
            _copiedContent[target] = target.Content;
        }

        target.Content = "已复制";
        target.SetResourceReference(Control.ForegroundProperty, "B.Accent");

        var timer = new DispatcherTimer { Interval = CopiedDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _copiedTimers.Remove(target);

            _copiedBindings.Remove(target, out var binding);
            _copiedContent.Remove(target, out var content);
            if (binding is not null)
                BindingOperations.SetBinding(target, ContentControl.ContentProperty, binding);
            else
                target.Content = content;

            target.ClearValue(Control.ForegroundProperty);
        };

        _copiedTimers[target] = timer;
        timer.Start();
    }
}
