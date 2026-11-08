using System.Windows;
using System.Windows.Input;
using LatentFolio.App.ViewModels;

namespace LatentFolio.App.Views;

public partial class PeopleWindow : Window
{
    private readonly AppVm _vm;
    public PersonVm? Picked { get; private set; }

    public PeopleWindow(AppVm vm)
    {
        InitializeComponent();
        _vm = vm;
        Host.ItemsSource = vm.People;
    }

    private void OnFilter(object sender, System.Windows.Controls.TextChangedEventArgs e)
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
            Picked = p;
            _vm.ShowPerson(p);
            Close();
        }
    }
}
