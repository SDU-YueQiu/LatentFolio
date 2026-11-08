using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LatentFolio.App.ViewModels;

namespace LatentFolio.App.Views;

public partial class PeopleWindow : Window
{
    private readonly AppVm _vm;

    public PeopleWindow(AppVm vm)
    {
        InitializeComponent();
        _vm = vm;
        Host.ItemsSource = vm.People;
    }

    private void OnFilter(object sender, TextChangedEventArgs e)
    {
        var q = FilterBox.Text.Trim();
        Host.ItemsSource = q.Length == 0
            ? _vm.People
            : _vm.People.Where(p => p.Display.Contains(q, StringComparison.OrdinalIgnoreCase)
                                    || p.Tag.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
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
}
