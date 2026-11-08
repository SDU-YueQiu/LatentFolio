using System.Windows;

namespace LatentFolio.App.Views;

/// <summary>一行文字的输入框：重命名、存模板名都靠它。</summary>
public partial class PromptWindow : Window
{
    public string Value => Input.Text.Trim();

    public PromptWindow(string title, string initial)
    {
        InitializeComponent();
        HeaderText.Text = title;
        Input.Text = initial;
        Loaded += (_, _) => { Input.SelectAll(); Input.Focus(); };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0) return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
