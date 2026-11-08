using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LatentFolio.App.ViewModels;
using LatentFolio.Core.Filtering;
using LatentFolio.Core.Models;

namespace LatentFolio.App.Views;

public partial class ColumnEditorWindow : Window
{
    public string ColumnName { get; private set; } = "";
    public FilterNode Tree { get; private set; }

    private sealed record Opt(object Value, string Text);

    public ColumnEditorWindow(ColumnVm? existing)
    {
        InitializeComponent();
        Tree = existing?.Tree.Clone() ?? new FilterGroup
        {
            Op = BoolOp.And,
            Children = { new FilterCond { Field = FieldType.Prompt, Op = CondOp.Contains, Value = "" } },
        };

        if (existing is not null)
        {
            HeaderText.Text = "编辑自定义栏";
            NameBox.Text = existing.Name;
        }
        else
        {
            HeaderText.Text = "新建自定义栏";
            NameBox.Text = "新栏";
        }
        Rebuild();
    }

    private void Rebuild()
    {
        TreeHost.Children.Clear();
        AddGroup(TreeHost, (FilterGroup)Tree, 0, isRoot: true);
        RefreshFooter();
    }

    private void RefreshFooter()
    {
        ExprText.Text = Tree.ToExpression();
        var images = App.Root.AllImages.Where(x => x.State != FileState.Deleted).ToList();
        var hits = ColumnEngine.Filter(images, Tree, new FilterContext());
        double pct = images.Count == 0 ? 0 : hits.Count * 100.0 / images.Count;
        PreviewText.Text = $"命中 {hits.Count:N0} 张 · {pct:F1}%";
    }

    private void AddGroup(StackPanel host, FilterGroup g, int depth, bool isRoot)
    {
        var stack = new StackPanel();

        var head = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(Toggle("全部满足", g.Op == BoolOp.And, () => { g.Op = BoolOp.And; Rebuild(); }));
        head.Children.Add(Toggle("任一满足", g.Op == BoolOp.Or, () => { g.Op = BoolOp.Or; Rebuild(); }));
        head.Children.Add(Toggle("反选", g.Op == BoolOp.Not, () =>
        {
            g.Op = BoolOp.Not;
            if (g.Children.Count > 1) g.Children.RemoveRange(1, g.Children.Count - 1);
            if (g.Children.Count == 0)
                g.Children.Add(new FilterCond { Field = FieldType.Prompt, Op = CondOp.Contains, Value = "" });
            Rebuild();
        }));
        head.Children.Add(Link("+ 条件", () =>
        {
            g.Children.Add(new FilterCond { Field = FieldType.Prompt, Op = CondOp.Contains, Value = "" });
            Rebuild();
        }));
        head.Children.Add(Link("+ 条件组", () =>
        {
            var ng = new FilterGroup
            {
                Op = BoolOp.And,
                Children = { new FilterCond { Field = FieldType.Prompt, Op = CondOp.Contains, Value = "" } },
            };
            if (g.Op == BoolOp.Not && g.Children.Count >= 1) g.Children[0] = ng;
            else g.Children.Add(ng);
            Rebuild();
        }));
        if (!isRoot) head.Children.Add(Link("删除组", () => { RemoveNode(g); }));

        stack.Children.Add(head);

        foreach (var child in g.Children.ToList())
        {
            if (child is FilterCond cd) stack.Children.Add(MakeCondRow(cd, g));
            else if (child is FilterGroup sg)
            {
                var inner = new StackPanel();
                AddGroup(inner, sg, depth + 1, false);
                stack.Children.Add(new Border
                {
                    Margin = new Thickness(14, 2, 0, 2),
                    Padding = new Thickness(10, 8, 0, 2),
                    BorderBrush = (Brush)FindResource("B.Border"),
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Child = inner,
                });
            }
        }

        var card = new Border
        {
            Background = isRoot ? Brushes.Transparent : Brushes.White,
            BorderBrush = (Brush)FindResource("B.Border"),
            BorderThickness = isRoot ? new Thickness(0) : new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(isRoot ? 0 : 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack,
        };
        host.Children.Add(card);
    }

    private void RemoveNode(FilterNode node)
    {
        if (Tree is FilterGroup root && RemoveFrom(root, node)) Rebuild();
    }

    private static bool RemoveFrom(FilterGroup g, FilterNode target)
    {
        if (g.Children.Remove(target)) return true;
        foreach (var sub in g.Children.OfType<FilterGroup>())
            if (RemoveFrom(sub, target)) return true;
        return false;
    }

    private Button Toggle(string text, bool active, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)FindResource("Btn.Outline"),
            Height = 27,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 6, 4),
            FontSize = 11.5,
        };
        if (active)
        {
            b.Background = (Brush)FindResource("B.AccentSoft");
            b.BorderBrush = (Brush)FindResource("B.Accent");
            b.Foreground = (Brush)FindResource("B.Accent");
        }
        b.Click += (_, _) => onClick();
        return b;
    }

    private Button Link(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)FindResource("Btn.Ghost"),
            Height = 27,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(0, 0, 6, 4),
            FontSize = 11.5,
            Foreground = (Brush)FindResource("B.Accent"),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private FrameworkElement MakeCondRow(FilterCond cond, FilterGroup parent)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var fieldOpts = FieldNames.All.Select(f => new Opt(f, FieldNames.Label(f))).ToList();
        var fieldBox = new ComboBox
        {
            Height = 30,
            Margin = new Thickness(0, 0, 6, 0),
            ItemsSource = fieldOpts,
            DisplayMemberPath = "Text",
            SelectedValuePath = "Value",
            SelectedValue = cond.Field,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(fieldBox);

        var ops = FieldNames.OpsFor(cond.Field);
        var opOpts = ops.Select(o => new Opt(o, FieldNames.OpLabel(o))).ToList();
        var opBox = new ComboBox
        {
            Height = 30,
            Margin = new Thickness(0, 0, 6, 0),
            ItemsSource = opOpts,
            DisplayMemberPath = "Text",
            SelectedValuePath = "Value",
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (!ops.Contains(cond.Op)) cond.Op = ops[0];
        opBox.SelectedValue = cond.Op;
        Grid.SetColumn(opBox, 1);
        grid.Children.Add(opBox);

        bool needsValue = FieldNames.NeedsValue(cond.Field);
        var valueBox = new TextBox
        {
            Height = 30,
            Text = needsValue ? cond.Value : "—",
            IsEnabled = needsValue,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(valueBox, 2);
        grid.Children.Add(valueBox);

        var del = Link("移除", () => { parent.Children.Remove(cond); Rebuild(); });
        Grid.SetColumn(del, 3);
        del.Margin = new Thickness(6, 0, 0, 0);
        del.Foreground = (Brush)FindResource("B.Text3");
        grid.Children.Add(del);

        fieldBox.SelectionChanged += (_, _) =>
        {
            if (fieldBox.SelectedValue is not FieldType f) return;
            cond.Field = f;
            cond.Op = FieldNames.OpsFor(f)[0];
            Rebuild();
        };
        opBox.SelectionChanged += (_, _) =>
        {
            if (opBox.SelectedValue is CondOp o && o != cond.Op) { cond.Op = o; Rebuild(); }
        };
        valueBox.TextChanged += (_, _) =>
        {
            cond.Value = valueBox.Text;
            RefreshFooter();
        };

        return grid;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show("给这一栏起个名字。", "LatentFolio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ColumnName = name;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
