using System.Windows;
using System.Windows.Controls;
using LatentFolio.App.Services;

namespace LatentFolio.App.Views;

/// <summary>给选中的图加/去自建标签。</summary>
public partial class TagDialog : Window
{
    private readonly List<(long Id, string Name, CheckBox Box)> _boxes = [];
    public List<long> Selected { get; } = [];

    public TagDialog(int imageCount)
    {
        InitializeComponent();
        HeaderText.Text = imageCount > 1 ? $"给 {imageCount} 张图加标签" : "给这张图加标签";
        Reload();
    }

    private void Reload()
    {
        ListHost.Children.Clear();
        _boxes.Clear();
        foreach (var (id, name, _) in App.Services.Store.LoadTags())
        {
            var cb = new CheckBox { Content = name, Margin = new Thickness(4, 5, 0, 5), FontSize = 12.5 };
            ListHost.Children.Add(cb);
            _boxes.Add((id, name, cb));
        }
        if (_boxes.Count == 0)
        {
            ListHost.Children.Add(new TextBlock
            {
                Text = "还没有标签，在下面输入一个。",
                Foreground = (System.Windows.Media.Brush)FindResource("B.Text3"),
                Margin = new Thickness(4, 6, 0, 6),
                FontSize = 12,
            });
        }
    }

    public void PreCheck(IEnumerable<long> tagIds)
    {
        var set = tagIds.ToHashSet();
        foreach (var (id, _, box) in _boxes) box.IsChecked = set.Contains(id);
    }

    private void OnCreateTag(object sender, RoutedEventArgs e)
    {
        var name = NewTagBox.Text.Trim();
        if (name.Length == 0) return;
        try
        {
            using var c = App.Services.Db.Cmd("INSERT OR IGNORE INTO app_tag(name) VALUES($n)");
            c.Parameters.AddWithValue("$n", name);
            c.ExecuteNonQuery();
        }
        catch { }
        NewTagBox.Text = "";
        Reload();
        foreach (var (_, n, box) in _boxes) if (n == name) box.IsChecked = true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        foreach (var (id, _, box) in _boxes) if (box.IsChecked == true) Selected.Add(id);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
