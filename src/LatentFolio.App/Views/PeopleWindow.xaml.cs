using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LatentFolio.App.ViewModels;

namespace LatentFolio.App.Views;

public partial class PeopleWindow : Window
{
    private readonly AppVm _vm;
    private readonly bool _ready;

    public PeopleWindow(AppVm vm)
    {
        // 排序下拉框在 XAML 里带了 SelectedIndex，InitializeComponent 期间就会触发
        // SelectionChanged，那时 _vm 还是 null
        _vm = vm;
        InitializeComponent();
        _ready = true;
        Apply();
    }

    private void OnFilter(object sender, TextChangedEventArgs e) => Apply();

    private void OnSort(object sender, SelectionChangedEventArgs e) => Apply();

    private void OnFilterToggle(object sender, RoutedEventArgs e) => Apply();

    private void Apply()
    {
        if (!_ready) return;
        var q = FilterBox.Text.Trim();
        bool onlyZh = OnlyZhBox.IsChecked == true;
        var v = LatentFolio.Core.Text.Vocabulary.Instance;

        IEnumerable<PersonVm> src = _vm.People;
        if (onlyZh) src = src.Where(p => p.Display != p.Tag);
        if (q.Length > 0)
            src = src.Where(p => p.Display.Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || p.Tag.Contains(q, StringComparison.OrdinalIgnoreCase));

        src = SortBox.SelectedIndex switch
        {
            1 => src.OrderBy(p => p.Display, StringComparer.CurrentCulture),
            2 => src.OrderBy(p => p.Tag, StringComparer.Ordinal),
            _ => src.OrderByDescending(p => p.Count),
        };
        _ = v;
        Host.ItemsSource = src.ToList();
    }

    private void OnPick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PersonVm p })
        {
            _vm.ShowPerson(p);
            Close();
        }
    }

    /// <summary>右键：把当前在查看器里看的那张设成这个角色的头像，或者恢复默认。</summary>
    private void OnPickAvatar(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PersonVm p } el) return;
        e.Handled = true;

        var menu = new ContextMenu();

        var current = _vm.Current;
        var set = new MenuItem
        {
            Header = current is null ? "用当前查看的图作头像" : $"用「{current.FileName}」作头像",
            IsEnabled = current is not null,
        };
        set.Click += (_, _) => _vm.SetPersonAvatar(p.Tag, current!.Id);
        menu.Items.Add(set);

        var reset = new MenuItem { Header = "恢复默认头像（最高分那张）" };
        reset.Click += (_, _) => _vm.SetPersonAvatar(p.Tag, 0);
        menu.Items.Add(reset);

        menu.PlacementTarget = el;
        menu.IsOpen = true;
    }

    private void OnCandidates(object sender, RoutedEventArgs e)
    {
        var win = new TagsWindow(_vm) { Owner = this };
        win.ShowDialog();
        Apply();
    }
}
