using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LatentFolio.App.ViewModels;
using LatentFolio.Core.Models;

namespace LatentFolio.App.Views;

/// <summary>
/// 统计面板。按设计稿 §12.5 / §14 分四页签：
///   概览 = KPI + 评分分布 + 月度产出 + 角色/画师/tag 三张表
///   排行 = 四维度 × 三种排序，列固定为 名称·张数·平均分·≥4★·lift·分布条
///   词典 = 正向 / 负向词频两栏（负面排除「负面=正面」的图）
///   体检 = 负面=正面 / 无提示词 / 未评分 …
/// 顶部门槛（≥3 / ≥5 / ≥10 / 全部）同时作用于概览三表与排行。
/// </summary>
public static class StatsBuilder
{
    private static AppVm _vm = null!;
    private static Panel _host = null!;
    private static List<ImageItem> _images = [];

    private static int _rankDim;        // 0 角色 1 画师 2 提示词 3 模型
    private static int _rankSort;       // 0 平均分 1 张数 2 ≥4★

    public static void Build(Panel host, AppVm vm)
    {
        _host = host;
        _vm = vm;
        _images = vm.AllImages.Where(x => x.State == FileState.Present).ToList();

        host.Children.Clear();

        // ---- 顶部门槛 ----
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(new TextBlock
        {
            Text = "门槛",
            FontSize = 12,
            Foreground = R("B.Text2"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        foreach (var (value, label) in new[] { (3, "≥3 张"), (5, "≥5 张"), (10, "≥10 张"), (0, "全部") })
        {
            int v = value;
            var b = new Button
            {
                Content = label,
                Style = (Style)Application.Current.FindResource("Btn.Outline"),
                FontSize = 11.5,
                Height = 27,
                Padding = new Thickness(11, 0, 11, 0),
                Margin = new Thickness(0, 0, 6, 0),
                Background = _vm.StatsThreshold == v
                    ? (Brush)Application.Current.FindResource("B.AccentSoft")
                    : Brushes.White,
                BorderBrush = _vm.StatsThreshold == v
                    ? (Brush)Application.Current.FindResource("B.Accent")
                    : (Brush)Application.Current.FindResource("B.BorderStrong"),
                Foreground = _vm.StatsThreshold == v
                    ? (Brush)Application.Current.FindResource("B.Accent")
                    : (Brush)Application.Current.FindResource("B.Text2"),
            };
            b.Click += (_, _) => { _vm.StatsThreshold = v; Build(_host, _vm); };
            bar.Children.Add(b);
        }
        host.Children.Add(bar);

        var tabs = new TabControl
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };
        tabs.Items.Add(Tab("概览", BuildOverview()));
        tabs.Items.Add(Tab("排行", BuildRank()));
        tabs.Items.Add(Tab("词典", BuildDict()));
        tabs.Items.Add(Tab("体检", BuildHealth()));
        host.Children.Add(tabs);
    }

    private static int Min => _vm.StatsThreshold;

    private static TabItem Tab(string header, UIElement content) => new()
    {
        Header = header,
        FontSize = 13,
        Padding = new Thickness(14, 6, 14, 6),
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content,
            Padding = new Thickness(0, 12, 8, 24),
        },
    };

    private static Brush R(string key) => (Brush)Application.Current.FindResource(key);

    private static TextBlock H(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Foreground = R("B.Text"),
        Margin = new Thickness(0, 18, 0, 8),
    };

    private static TextBlock Sub(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        Foreground = R("B.Text3"),
        Margin = new Thickness(0, 0, 0, 8),
        TextWrapping = TextWrapping.Wrap,
    };

    private static string Pct(int a, int b) => b == 0 ? "0%" : $"{a * 100.0 / b:F1}%";

    private static double OverallAvg()
    {
        int sum = 0, n = 0;
        foreach (var it in _images) if (it.Rating > 0) { sum += it.Rating; n++; }
        return n == 0 ? 0 : (double)sum / n;
    }

    // ================= 概览 =================

    private static UIElement BuildOverview()
    {
        var panel = new StackPanel();
        double avg = OverallAvg();
        int withPrompt = _images.Count(x => x.Prompt.Length > 0);
        int rated = _images.Count(x => x.Rating > 0);
        var tagCount = DistinctPromptTags();

        var cards = new WrapPanel();
        void Card(string label, string value, string sub = "")
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = R("B.Text3") });
            sp.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 0),
            });
            if (sub.Length > 0)
                sp.Children.Add(new TextBlock
                {
                    Text = sub,
                    FontSize = 11,
                    Foreground = R("B.Text3"),
                    Margin = new Thickness(0, 2, 0, 0),
                });
            cards.Children.Add(new Border
            {
                Background = Brushes.White,
                BorderBrush = R("B.Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 10, 10),
                Width = 168,
                Child = sp,
            });
        }

        Card("图片总数", _images.Count.ToString("N0"));
        Card("有提示词", withPrompt.ToString("N0"), Pct(withPrompt, _images.Count));
        Card("已评分", rated.ToString("N0"), Pct(rated, _images.Count));
        Card("平均分", avg.ToString("0.00"), "已评分的平均");
        Card("人物命中", _images.Count(x => x.Chars.Length > 0).ToString("N0"));
        Card("画师命中", _images.Count(x => x.Artists.Length > 0).ToString("N0"));
        Card("正向 tag", tagCount.ToString("N0"), "种");
        panel.Children.Add(cards);

        // 评分分布
        panel.Children.Add(H("评分分布"));
        var hist = new int[6];
        foreach (var it in _images) hist[Math.Clamp(it.Rating, 0, 5)]++;
        int maxH = Math.Max(1, hist.Max());
        for (int r = 5; r >= 0; r--)
            panel.Children.Add(BarRow(r == 0 ? "未评分" : new string('★', r), hist[r], maxH, _images.Count));

        // 月度产出
        panel.Children.Add(H("月度产出"));
        var months = _images
            .GroupBy(MonthKey)
            .Select(g => (Label: g.Key, Count: g.Count()))
            .OrderBy(x => x.Label)
            .TakeLast(18)
            .ToList();
        if (months.Count == 0) panel.Children.Add(Sub("没有数据"));
        else
        {
            int maxM = Math.Max(1, months.Max(x => x.Count));
            foreach (var m in months)
                panel.Children.Add(BarRow(m.Label, m.Count, maxM, _images.Count));
        }

        // 三张表
        panel.Children.Add(H("角色"));
        panel.Children.Add(SimpleTable(x => x.Chars, avg, 12));

        panel.Children.Add(H("画师"));
        panel.Children.Add(SimpleTable(x => x.Artists, avg, 12));

        panel.Children.Add(H("正向 tag"));
        panel.Children.Add(SimpleTable(PromptTags, avg, 12));

        return panel;
    }

    /// <summary>
    /// 产出月份优先取文件夹名里的日期（ComfyUI 按日期建目录），否则退回入库时间。
    /// 只认 yyyy-MM 形态的段，认不出就不瞎猜。
    /// </summary>
    private static string MonthKey(ImageItem it)
    {
        foreach (var seg in it.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg.Length >= 7 && seg[4] == '-'
                && int.TryParse(seg.AsSpan(0, 4), out var y) && y is > 2000 and < 2100
                && int.TryParse(seg.AsSpan(5, 2), out var mo) && mo is >= 1 and <= 12)
                return seg[..7];
        }
        return it.Added.ToString("yyyy-MM");
    }

    private static string[] PromptTags(ImageItem it) =>
        it.SearchText.Length == 0 ? [] : it.SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static int DistinctPromptTags()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var it in _images) foreach (var t in PromptTags(it)) set.Add(t);
        return set.Count;
    }

    /// <summary>名称 · 张数 · 平均分 · lift，按张数取前 N，受门槛限制。</summary>
    private static UIElement SimpleTable(Func<ImageItem, string[]> selector, double baseline, int take)
    {
        var stats = new Dictionary<string, (int Count, int Sum, int Rated)>(StringComparer.Ordinal);
        foreach (var it in _images)
        {
            foreach (var t in selector(it).Distinct())
            {
                var cur = stats.GetValueOrDefault(t);
                stats[t] = (cur.Count + 1, cur.Sum + (it.Rating > 0 ? it.Rating : 0), cur.Rated + (it.Rating > 0 ? 1 : 0));
            }
        }

        var rows = stats
            .Where(kv => kv.Value.Count >= Math.Max(1, Min))
            .Select(kv => (Tag: kv.Key, kv.Value.Count,
                Avg: kv.Value.Rated == 0 ? 0 : (double)kv.Value.Sum / kv.Value.Rated))
            .OrderByDescending(x => x.Count)
            .Take(take)
            .ToList();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Head(grid, "名称", 0);
        Head(grid, "张数", 1);
        Head(grid, "平均分", 2);
        Head(grid, "lift", 3);

        int row = 1;
        var vocab = Core.Text.Vocabulary.Instance;
        foreach (var x in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var name = new TextBlock
            {
                Text = vocab.Display(x.Tag),
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 4),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = x.Tag,
            };
            Grid.SetRow(name, row);
            grid.Children.Add(name);

            Num(grid, row, 1, x.Count.ToString("N0"));
            Num(grid, row, 2, x.Avg > 0 ? x.Avg.ToString("0.00") : "—");

            double lift = x.Avg <= 0 || baseline <= 0 ? 0 : x.Avg / baseline;
            Num(grid, row, 3, lift > 0 ? $"{lift:0.00}×" : "—", LiftBrush(lift));
            row++;
        }

        if (rows.Count == 0)
            return Sub(Min <= 1 ? "没有数据" : $"没有达到门槛（≥{Min} 张）的条目");
        return grid;
    }

    private static Brush LiftBrush(double lift) => lift switch
    {
        >= 1.15 => new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)),
        > 0 and <= 0.85 => new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)),
        _ => R("B.Text3"),
    };

    private static void Head(Grid g, string text, int col)
    {
        var t = new TextBlock { Text = text, FontSize = 11, Foreground = R("B.Text3") };
        Grid.SetColumn(t, col);
        if (col > 0) t.HorizontalAlignment = HorizontalAlignment.Right;
        g.Children.Add(t);
    }

    private static void Num(Grid g, int row, int col, string text, Brush? brush = null)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = brush ?? R("B.Text2"),
            Margin = new Thickness(0, 4, 0, 4),
        };
        Grid.SetRow(t, row);
        Grid.SetColumn(t, col);
        g.Children.Add(t);
    }

    private static UIElement BarRow(string label, int value, int max, int total)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 5), Height = 22 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

        g.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            Foreground = R("B.Text2"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var barHost = new Grid { VerticalAlignment = VerticalAlignment.Center, Height = 8 };
        barHost.Children.Add(new Border { Background = R("B.Tile"), CornerRadius = new CornerRadius(4) });
        double frac = max <= 0 ? 0 : Math.Clamp(value / (double)max, 0, 1);
        var fillGrid = new Grid();
        fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(frac, GridUnitType.Star) });
        fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - frac), GridUnitType.Star) });
        var fill = new Border { Background = R("B.Accent"), CornerRadius = new CornerRadius(4) };
        Grid.SetColumn(fill, 0);
        fillGrid.Children.Add(fill);
        barHost.Children.Add(fillGrid);
        Grid.SetColumn(barHost, 1);
        barHost.Margin = new Thickness(0, 0, 10, 0);
        g.Children.Add(barHost);

        var t = new TextBlock
        {
            Text = $"{value:N0}  {Pct(value, total)}",
            FontSize = 11,
            Foreground = R("B.Text3"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(t, 2);
        g.Children.Add(t);
        return g;
    }

    // ================= 排行 =================

    private static UIElement BuildRank()
    {
        var panel = new StackPanel();

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        toolbar.Children.Add(Caption("维度"));
        foreach (var (i, name) in new[] { (0, "角色"), (1, "画师"), (2, "提示词"), (3, "模型") })
            toolbar.Children.Add(Chip(name, _rankDim == i, () => { _rankDim = i; RebuildRank(); }));
        toolbar.Children.Add(new Border { Width = 1, Background = R("B.Border"), Margin = new Thickness(8, 4, 12, 4) });
        toolbar.Children.Add(Caption("排序"));
        foreach (var (i, name) in new[] { (0, "平均分"), (1, "张数"), (2, "≥4★") })
            toolbar.Children.Add(Chip(name, _rankSort == i, () => { _rankSort = i; RebuildRank(); }));
        panel.Children.Add(toolbar);

        panel.Children.Add(BuildRankTable());
        return panel;
    }

    private static void RebuildRank()
    {
        // 就地换掉整块统计（门槛栏在上面，不动）
        Build(_host, _vm);
        // 切回排行页签
        if (_host.Children.Count > 1 && _host.Children[1] is TabControl tc) tc.SelectedIndex = 1;
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        Foreground = R("B.Text3"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0),
    };

    private static Button Chip(string text, bool active, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)Application.Current.FindResource("Btn.Outline"),
            FontSize = 11.5,
            Height = 27,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 0, 6, 0),
            Background = active ? (Brush)Application.Current.FindResource("B.AccentSoft") : Brushes.White,
            BorderBrush = active ? (Brush)Application.Current.FindResource("B.Accent")
                                 : (Brush)Application.Current.FindResource("B.BorderStrong"),
            Foreground = active ? (Brush)Application.Current.FindResource("B.Accent")
                                : (Brush)Application.Current.FindResource("B.Text2"),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static UIElement BuildRankTable()
    {
        Func<ImageItem, string[]> selector = _rankDim switch
        {
            0 => x => x.Chars,
            1 => x => x.Artists,
            2 => PromptTags,
            _ => x => x.Model is { Length: > 0 } m ? [m] : [],
        };

        var stats = new Dictionary<string, (int Count, int Sum, int Rated, int High, int[] Hist)>(StringComparer.Ordinal);
        foreach (var it in _images)
        {
            foreach (var t in selector(it).Distinct())
            {
                var cur = stats.GetValueOrDefault(t);
                var h = cur.Hist ?? new int[6];
                h[Math.Clamp(it.Rating, 0, 5)]++;
                stats[t] = (cur.Count + 1,
                    cur.Sum + (it.Rating > 0 ? it.Rating : 0),
                    cur.Rated + (it.Rating > 0 ? 1 : 0),
                    cur.High + (it.Rating >= 4 ? 1 : 0),
                    h);
            }
        }

        var rows = stats
            .Where(kv => kv.Value.Count >= Math.Max(1, Min))
            .Select(kv => (Tag: kv.Key, kv.Value.Count,
                Avg: kv.Value.Rated == 0 ? 0 : (double)kv.Value.Sum / kv.Value.Rated,
                kv.Value.High, kv.Value.Hist))
            .ToList();

        rows = _rankSort switch
        {
            1 => rows.OrderByDescending(x => x.Count).ToList(),
            2 => rows.OrderByDescending(x => x.High).ThenByDescending(x => x.Count).ToList(),
            _ => rows.OrderByDescending(x => x.Avg).ThenByDescending(x => x.Count).ToList(),
        };
        rows = rows.Take(60).ToList();

        if (rows.Count == 0)
            return Sub(Min <= 1 ? "没有数据" : $"没有达到门槛（≥{Min} 张）的条目");

        double baseline = OverallAvg();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Head(grid, "名称", 0);
        Head(grid, "张数", 1);
        Head(grid, "平均分", 2);
        Head(grid, "≥4★", 3);
        Head(grid, "lift", 4);
        Head(grid, "评分分布", 5);

        int row = 1;
        var vocab = Core.Text.Vocabulary.Instance;
        foreach (var x in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var name = new TextBlock
            {
                Text = _rankDim == 2 ? x.Tag : vocab.Display(x.Tag),
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 4),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = x.Tag,
            };
            Grid.SetRow(name, row);
            grid.Children.Add(name);

            Num(grid, row, 1, x.Count.ToString("N0"));
            Num(grid, row, 2, x.Avg > 0 ? x.Avg.ToString("0.00") : "—");
            Num(grid, row, 3, x.High > 0 ? $"{x.High * 100.0 / x.Count:F0}%" : "—");

            double lift = x.Avg <= 0 || baseline <= 0 ? 0 : x.Avg / baseline;
            Num(grid, row, 4, lift > 0 ? $"{lift:0.00}×" : "—", LiftBrush(lift));

            var dist = RatingDistBar(x.Hist, x.Count);
            Grid.SetRow(dist, row);
            Grid.SetColumn(dist, 5);
            dist.Margin = new Thickness(6, 7, 10, 7);
            grid.Children.Add(dist);
            row++;
        }

        return grid;
    }

    /// <summary>五段评分分布条：橙→浅，越靠左分越高。</summary>
    private static Grid RatingDistBar(int[] hist, int total)
    {
        var g = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Center };
        var colors = new[]
        {
            Color.FromRgb(0xF5, 0x9E, 0x0B),   // 5
            Color.FromRgb(0xF7, 0xB4, 0x55),   // 4
            Color.FromRgb(0xE5, 0xCB, 0x9E),   // 3
            Color.FromRgb(0xD8, 0xDB, 0xE2),   // 2
            Color.FromRgb(0xEC, 0xEE, 0xF2),   // 1
            Color.FromRgb(0xF4, 0xF5, 0xF7),   // 未评分
        };
        int sum = Math.Max(1, total);
        for (int i = 0; i < 6; i++)
        {
            double w = hist[i] / (double)sum;
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, w), GridUnitType.Star) });
            var b = new SolidColorBrush(colors[i]);
            b.Freeze();
            var cell = new Border { Background = b, Margin = new Thickness(i == 0 ? 0 : 0.5, 0, 0, 0) };
            Grid.SetColumn(cell, i);
            g.Children.Add(cell);
        }
        return g;
    }

    // ================= 词典 =================

    private static UIElement BuildDict()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        root.Children.Add(DictColumn("正向词频", _images, negative: false));
        var right = DictColumn("负向词频", _images, negative: true);
        Grid.SetColumn(right, 2);
        root.Children.Add(right);
        return root;
    }

    /// <summary>负面统计要排除「负面=正面」的图，否则 1girl / solo 会冲进负面榜。</summary>
    private static UIElement DictColumn(string title, List<ImageItem> images, bool negative)
    {
        var panel = new StackPanel();
        var counter = new Dictionary<string, int>(StringComparer.Ordinal);
        int imageCount = 0;

        foreach (var it in images)
        {
            if (negative && it.NegIsDup) continue;
            var text = negative ? it.Negative : it.Prompt;
            if (text.Length == 0) continue;
            imageCount++;
            foreach (var t in Core.Comfy.GraphParser.Tokenize(text).Tags) counter[t] = counter.GetValueOrDefault(t) + 1;
        }

        panel.Children.Add(H(title));
        panel.Children.Add(Sub(negative
            ? $"{imageCount:N0} 张有独立负面（已排掉 {images.Count(x => x.NegIsDup):N0} 张负面=正面） · {counter.Count:N0} 种"
            : $"{imageCount:N0} 张有正向 · {counter.Count:N0} 种"));

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        Head(g, "词", 0);
        Head(g, "次数", 1);

        int row = 1;
        foreach (var kv in counter.OrderByDescending(x => x.Value).Take(80))
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var t = new TextBlock
            {
                Text = kv.Key,
                FontSize = 12,
                Margin = new Thickness(0, 3, 0, 3),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = negative ? new SolidColorBrush(Color.FromRgb(0x9A, 0x5B, 0x1E)) : R("B.Text2"),
            };
            Grid.SetRow(t, row);
            g.Children.Add(t);
            Num(g, row, 1, kv.Value.ToString("N0"));
            row++;
        }
        panel.Children.Add(g);
        return panel;
    }

    // ================= 体检 =================

    private static UIElement BuildHealth()
    {
        var panel = new StackPanel();

        int negDup = _images.Count(x => x.NegIsDup);
        int noPrompt = _images.Count(x => x.Prompt.Length == 0);
        int noRating = _images.Count(x => x.Rating == 0);
        int missing = _vm.AllImages.Count(x => x.State == FileState.Missing);
        int deleted = _vm.AllImages.Count(x => x.State == FileState.Deleted);
        int noSeed = _images.Count(x => x.Seed is null);
        int zeroSize = _images.Count(x => x.Width == 0 || x.Height == 0);

        void Item(string title, int n, string desc)
        {
            var sp = new StackPanel();
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new TextBlock { Text = n.ToString("N0"), FontSize = 18, FontWeight = FontWeights.SemiBold });
            line.Children.Add(new TextBlock
            {
                Text = "  " + title,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 3),
            });
            sp.Children.Add(line);
            sp.Children.Add(new TextBlock
            {
                Text = desc,
                FontSize = 11.5,
                Foreground = R("B.Text3"),
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new Border
            {
                Background = Brushes.White,
                BorderBrush = R("B.Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = sp,
            });
        }

        Item("负面提示词与正面完全相同", negDup, "同一条 CLIPTextEncode 接到了正负两个口，负面统计里已排除。");
        Item("没有提示词", noPrompt, "一般是放大 / 修复流程产出的图，本来就没有文本。");
        Item("未评分", noRating, "");
        Item("文件丢失", missing, "库里有记录但磁盘上找不到，元数据和评分都还在。");
        Item("已删除", deleted, "移到了「已删除」，随时可以还原。");
        Item("没有种子", noSeed, "工作流里没写 seed / noise_seed，没法复现。");
        Item("读不到尺寸", zeroSize, "文件可能损坏或被别的程序占用。");
        return panel;
    }
}

