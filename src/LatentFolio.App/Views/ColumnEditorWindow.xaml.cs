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
    public string ColumnColor { get; private set; } = "";
    public FilterNode Tree { get; private set; }
    public bool DeleteRequested { get; private set; }

    /// <summary>「筛选」按钮用：不存栏，只把条件树交回去。</summary>
    public bool FilterMode { get; }

    private readonly ColumnVm? _existing;
    private bool _rebuilding;
    private FilterGroup _activeGroup;

    /// <summary>快捷模板的一条。必须公开属性，不然 ItemTemplate 绑不上。</summary>
    public sealed class TemplateItem
    {
        public string Name { get; init; } = "";
        public FilterNode Data { get; init; } = new FilterGroup();
        public bool Builtin { get; init; }
        public override string ToString() => Name;
    }

    public ColumnEditorWindow(ColumnVm? existing, bool filterMode = false)
    {
        // FilterMode 必须在 InitializeComponent 之前定，后面几个分支都看它
        FilterMode = filterMode;
        InitializeComponent();
        _existing = existing;
        Tree = existing?.Tree.Clone() ?? new FilterGroup
        {
            Op = BoolOp.And,
            Children = { new FilterCond { Field = FieldType.Prompt, Op = CondOp.Contains, Value = "" } },
        };
        _activeGroup = Tree as FilterGroup ?? new FilterGroup();

        if (existing is not null)
        {
            HeaderText.Text = "编辑自定义栏";
            NameBox.Text = existing.Name;
            ColumnColor = existing.Color;
            DeleteBtn.Visibility = Visibility.Visible;
        }
        else
        {
            HeaderText.Text = "新建自定义栏";
            NameBox.Text = "新栏";
        }

        if (FilterMode)
        {
            Title = "筛选";
            HeaderText.Text = "筛选";
            NameLabel.Visibility = Visibility.Collapsed;
            NameBox.Visibility = Visibility.Collapsed;
            ColorLabel.Visibility = Visibility.Collapsed;
            ColorBar.Visibility = Visibility.Collapsed;
            SaveTemplateBtn.Visibility = Visibility.Visible;
            SaveAsColumnBtn.Visibility = Visibility.Visible;
            OkBtn.Content = "应用";
        }

        BuildColorBar();
        BuildFieldPanel();
        BuildTemplates();
        Rebuild();
    }

    // ================= 标记色 =================

    private static readonly (string Name, string Hex)[] Colors =
    [
        ("无", ""), ("红", "#E5484D"), ("橙", "#F5A524"),
        ("绿", "#30A46C"), ("蓝", "#3E63DD"), ("紫", "#8E4EC6"),
    ];

    private void BuildColorBar()
    {
        ColorBar.Children.Clear();
        foreach (var (_, hex) in Colors)
        {
            var dot = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Margin = new Thickness(0, 0, 6, 0),
                BorderThickness = new Thickness(hex.Length == 0 ? 1 : 2),
                BorderBrush = (Brush)FindResource(
                    string.Equals(hex, ColumnColor, StringComparison.OrdinalIgnoreCase)
                        ? "B.Accent" : "B.Border"),
                Background = hex.Length == 0 ? Brushes.Transparent : HexBrush(hex),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = hex,
            };
            dot.MouseLeftButtonDown += (s, _) =>
            {
                ColumnColor = (string)((Border)s).Tag;
                BuildColorBar();
            };
            ColorBar.Children.Add(dot);
        }
    }

    private static Brush HexBrush(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return Brushes.Gray; }
    }

    // ================= 右栏：可用字段 =================

    private void BuildFieldPanel()
    {
        FieldHost.Children.Clear();
        foreach (var (name, fields) in FieldNames.Groups)
        {
            FieldHost.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 11,
                Foreground = (Brush)FindResource("B.Text3"),
                Margin = new Thickness(0, 6, 0, 4),
            });
            var wrap = new WrapPanel();
            foreach (var f in fields)
            {
                var b = new Button
                {
                    Content = FieldNames.Label(f),
                    Style = (Style)FindResource("Btn.Outline"),
                    Height = 26,
                    Padding = new Thickness(8, 0, 8, 0),
                    Margin = new Thickness(0, 0, 5, 5),
                    FontSize = 11,
                    Tag = f,
                };
                b.Click += (s, _) => InsertCondition((FieldType)((Button)s).Tag);
                wrap.Children.Add(b);
            }
            FieldHost.Children.Add(wrap);
        }
    }

    private void InsertCondition(FieldType f)
    {
        var g = ResolveActiveGroup();
        g.Children.Add(new FilterCond { Field = f, Op = FieldNames.OpsFor(f)[0], Value = "" });
        Rebuild();
    }

    /// <summary>选中的组可能已经被删掉了，兜底回根组。</summary>
    private FilterGroup ResolveActiveGroup()
    {
        if (Tree is FilterGroup root && Contains(root, _activeGroup)) return _activeGroup;
        _activeGroup = Tree as FilterGroup ?? new FilterGroup();
        return _activeGroup;
    }

    private static bool Contains(FilterGroup hay, FilterGroup needle)
    {
        if (ReferenceEquals(hay, needle)) return true;
        foreach (var c in hay.Children)
            if (c is FilterGroup g && Contains(g, needle)) return true;
        return false;
    }

    // ================= 快捷模板 =================

    private void BuildTemplates()
    {
        var list = new List<TemplateItem>();
        foreach (var (name, tree) in Builtins)
            list.Add(new TemplateItem { Name = name, Data = tree, Builtin = true });

        foreach (var (id, name, json) in App.Root.LoadTemplates())
            list.Add(new TemplateItem { Name = name, Data = FilterNodeExtensions.FromJson(json) });

        TemplateHost.ItemsSource = list;
    }

    private static readonly (string Name, FilterNode Tree)[] Builtins =
    [
        ("高分 ★4+", new FilterGroup
        {
            Op = BoolOp.And,
            Children = { new FilterCond { Field = FieldType.Rating, Op = CondOp.Gte, Value = "4" } },
        }),
        ("未评分", new FilterGroup
        {
            Op = BoolOp.And,
            Children = { new FilterCond { Field = FieldType.Rating, Op = CondOp.Eq, Value = "0" } },
        }),
        ("人物特写 · 4K", new FilterGroup
        {
            Op = BoolOp.And,
            Children =
            {
                new FilterCond { Field = FieldType.Resolution, Op = CondOp.Gte, Value = "2048" },
                // 角色 包含 空串 = 「有角色」，空数组时 TagMatch 返回 false，正好是「非空」
                new FilterCond { Field = FieldType.Character, Op = CondOp.Contains, Value = "" },
            },
        }),
        ("相似堆叠 ≥2", new FilterGroup
        {
            Op = BoolOp.And,
            Children = { new FilterCond { Field = FieldType.SimilarGroup, Op = CondOp.Gte, Value = "2" } },
        }),
        ("有 LoRA", new FilterGroup
        {
            Op = BoolOp.And,
            Children = { new FilterCond { Field = FieldType.Lora, Op = CondOp.Contains, Value = "" } },
        }),
    ];

    private void OnApplyTemplate(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TemplateItem t }) return;
        Tree = t.Data.Clone();
        _activeGroup = Tree as FilterGroup ?? new FilterGroup();
        Rebuild();
    }

    private void OnSaveAsTemplate(object sender, RoutedEventArgs e)
    {
        var dlg = new PromptWindow("存为模板", NameBox.Text.Trim()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        App.Root.SaveAsTemplate(dlg.Value, Tree);
        BuildTemplates();
    }

    private void OnSaveAsColumn(object sender, RoutedEventArgs e)
    {
        var dlg = new PromptWindow("存为一栏", "筛选结果") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _ = App.Root.AddColumnAsync(dlg.Value, Tree.Clone(), ColumnColor);
        DialogResult = true;
    }

    // ================= 条件树 =================

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_existing is null) return;
        var r = MessageBox.Show($"删除「{_existing.Name}」这一栏？图片不会动。", "LatentFolio",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;
        DeleteRequested = true;
        DialogResult = true;
    }

    private void Rebuild()
    {
        if (_rebuilding) return;
        _rebuilding = true;
        try
        {
            var g = ResolveActiveGroup();
            TreeHost.Children.Clear();
            AddGroup(TreeHost, (FilterGroup)Tree, 0, isRoot: true);
            RefreshPreview();
            _ = g;
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private void RefreshPreview()
    {
        var images = App.Root.AllImages.Where(x => x.State == FileState.Present).ToList();
        var hits = ColumnEngine.Filter(images, Tree, App.Root.MakeContext());
        double pct = images.Count == 0 ? 0 : hits.Count * 100.0 / images.Count;
        PreviewText.Text = $"{hits.Count:N0} 张 · {pct:F1}%";

        var vms = new List<ImageVm>(24);
        foreach (var it in hits)
        {
            if (vms.Count >= 24) break;
            if (App.Root.VmById.TryGetValue(it.Id, out var vm)) vms.Add(vm);
        }
        PreviewHost.ItemsSource = vms;
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
            _activeGroup = g;
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
            _activeGroup = ng;
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

        bool active = ReferenceEquals(g, _activeGroup);
        var card = new Border
        {
            Background = isRoot ? Brushes.Transparent : Brushes.White,
            BorderBrush = (Brush)FindResource(active && !isRoot ? "B.Accent" : "B.Border"),
            BorderThickness = isRoot ? new Thickness(0) : new Thickness(active ? 2 : 1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(isRoot ? 0 : 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack,
        };
        // 点组里的空白处 = 把这一组设成「插入目标」，右栏点字段就加到这里
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (!ReferenceEquals(_activeGroup, g))
            {
                _activeGroup = g;
                Rebuild();
            }
            _ = e;
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

        // 直接用字符串当选项，SelectedIndex 取下标回查枚举。
        // DisplayMemberPath / ItemTemplate 在这套自定义 ComboBox 模板上会退化成 ToString，
        // 界面上就是一行 "LatentFolio.Views..."，所以干脆绕开。
        var fields = FieldNames.All;
        if (Array.IndexOf(fields, cond.Field) < 0) fields = [.. FieldNames.All, cond.Field];
        var fieldBox = new ComboBox
        {
            MinHeight = 30,
            Margin = new Thickness(0, 0, 6, 0),
            ItemsSource = fields.Select(FieldNames.Label).ToList(),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(fieldBox);

        var ops = FieldNames.OpsFor(cond.Field);
        if (!ops.Contains(cond.Op)) cond.Op = ops[0];
        var opBox = new ComboBox
        {
            MinHeight = 30,
            Margin = new Thickness(0, 0, 6, 0),
            ItemsSource = ops.Select(FieldNames.OpLabel).ToList(),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(opBox, 1);
        grid.Children.Add(opBox);

        bool needsValue = FieldNames.NeedsValue(cond.Field, cond.Op);
        var valueBox = new TextBox
        {
            MinHeight = 30,
            Text = needsValue ? cond.Value : "—",
            IsEnabled = needsValue,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (cond.Field == FieldType.SimilarGroup && needsValue)
        {
            valueBox.Width = 42;
            valueBox.HorizontalAlignment = HorizontalAlignment.Left;
            valueBox.TextAlignment = TextAlignment.Center;
        }
        Grid.SetColumn(valueBox, 2);
        grid.Children.Add(valueBox);

        var del = Link("移除", () => { parent.Children.Remove(cond); Rebuild(); });
        Grid.SetColumn(del, 3);
        del.Margin = new Thickness(6, 0, 0, 0);
        del.Foreground = (Brush)FindResource("B.Text3");
        grid.Children.Add(del);

        // 先设初值再挂事件：反过来设置 SelectedIndex 会立刻触发 SelectionChanged，
        // 把 Op 从 ≥ 改成 =，还会在递归里重建整棵树。
        fieldBox.SelectedIndex = Array.IndexOf(fields, cond.Field);
        opBox.SelectedIndex = ops.IndexOf(cond.Op);

        fieldBox.SelectionChanged += (_, _) =>
        {
            if (_rebuilding) return;
            int i = fieldBox.SelectedIndex;
            if (i < 0 || i >= fields.Length) return;
            var f = fields[i];
            if (f == cond.Field) return;
            cond.Field = f;
            cond.Op = FieldNames.OpsFor(f)[0];
            if (!FieldNames.NeedsValue(f, cond.Op)) cond.Value = "";
            Rebuild();
        };
        opBox.SelectionChanged += (_, _) =>
        {
            if (_rebuilding) return;
            int i = opBox.SelectedIndex;
            if (i < 0 || i >= ops.Length) return;
            if (ops[i] != cond.Op) { cond.Op = ops[i]; Rebuild(); }
        };
        valueBox.TextChanged += (_, _) =>
        {
            cond.Value = valueBox.Text;
            RefreshPreview();
        };

        return grid;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (FilterMode)
        {
            DialogResult = true;
            return;
        }
        var name = NameBox.Text.Trim();
        if (name.Length == 0) return;
        ColumnName = name;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
