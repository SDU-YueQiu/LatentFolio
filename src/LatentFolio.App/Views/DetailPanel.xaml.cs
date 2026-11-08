using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LatentFolio.App.ViewModels;

namespace LatentFolio.App.Views;

/// <summary>右侧详情栏。查看器和图库主页共用这一个，6 段顺序按设计稿 §12.3 固定。</summary>
public partial class DetailPanel : UserControl
{
    public DetailPanel()
    {
        InitializeComponent();
    }

    private static AppVm Vm => App.Root;

    private void OnCopyPrompt(object sender, RoutedEventArgs e) => Vm.CopyPrompt(Vm.Current);
    private void OnCopyNegative(object sender, RoutedEventArgs e) => Vm.CopyNegative(Vm.Current);
    private void OnCopyWorkflow(object sender, RoutedEventArgs e) => Vm.CopyWorkflow();

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
}
