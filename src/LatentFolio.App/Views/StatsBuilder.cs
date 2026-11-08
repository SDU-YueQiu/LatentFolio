using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LatentFolio.App.ViewModels;
using LatentFolio.Core.Models;

namespace LatentFolio.App.Views;

/// <summary>统计面板：概览 / 排行 / 词典 / 体检。全部按需从内存算，几万张也是毫秒级。</summary>
public static class StatsBuilder
{
    public static void Build(Panel host, AppVm vm)
    {
        host.Children.Clear();
        var images = vm.AllImages.Where(x => x.State != FileState.Deleted).ToList();
        double avg = images.Count == 0 ? 0 : images.Where(x => x.Rating > 0).Select(x => (double)x.Rating).DefaultIfEmpty(0).Average();

        var tabs = new TabControl
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };

        tabs.Items.Add(Tab("概览", BuildOverview(images, avg)));
        tabs.Items.Add(Tab("排行", BuildRank(images, avg)));
        tabs.Items.Add(Tab("词典", BuildDict(images)));
        tabs.Items.Add(Tab("体检", BuildHealth(images)));
        host.Children.Add(tabs);
    }

    private static TabItem Tab(string header, UIElement content)
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content,
            Padding = new Thickness(0, 12, 8, 24),
        };
        return new TabItem
        {
            Header = header,
            Content = scroll,
            FontSize = 13,
            Padding = new Thickness(14, 6, 14, 6),
        };
    }

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

    // ---------------- 概览 ----------------

    private static UIElement BuildOverview(List<ImageItem> images, double avg)
    {
        var panel = new StackPanel();

        var cards = new WrapPanel();
        void Card(string label, string value, string sub = "")
        {
            var b = new Border
            {
                Background = Brushes.White,
                BorderBrush = R("B.Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 10, 10),
                Width = 168,
            };
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
                sp.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = R("B.Text3"), Margin = new Thickness(0, 2, 0, 0) });
            b.Child = sp;
            cards.Children.Add(b);
        }

        int withPrompt = images.Count(x => x.Prompt.Length > 0);
        int rated = images.Count(x => x.Rating > 0);
        Card("图片总数", images.Count.ToString("N0"));
        Card("有提示词", withPrompt.ToString("N0"), $"{Pct(withPrompt, images.Count)}");
        Card("已评分", rated.ToString("N0"), $"{Pct(rated, images.Count)}");
        Card("平均分", avg.ToString("0.00"), "全部图片");
        Card("人物命中", images.Count(x => x.Chars.Length > 0).ToString("N0"));
        Card("画师命中", images.Count(x => x.Artists.Length > 0).ToString("N0"));
        panel.Children.Add(cards);

        // 评分分布
        panel.Children.Add(H("评分分布"));
        var hist = new int[6];
        foreach (var it in images) hist[Math.Clamp(it.Rating, 0, 5)]++;
        int maxH = hist.Max();
        for (int r = 5; r >= 0; r--)
        {
            panel.Children.Add(BarRow(r == 0 ? "未评分" : new string('★', r), hist[r], maxH, images.Count));
        }

        // 模型
        panel.Children.Add(H("模型 Top 12"));
        foreach (var (name, n) in Top(images.Select(x => x.Model), 12))
            panel.Children.Add(BarRow(name ?? "未知", n, Top(images.Select(x => x.Model), 1).FirstOrDefault().Count, images.Count));

        // 分辨率
        panel.Children.Add(H("分辨率 Top 8"));
        foreach (var (name, n) in Top(images.Select(x => x.Resolution), 8))
            panel.Children.Add(BarRow(name ?? "未知", n, Top(images.Select(x => x.Resolution), 1).FirstOrDefault().Count, images.Count));

        // 采样器
        panel.Children.Add(H("采样器 Top 8"));
        foreach (var (name, n) in Top(images.Select(x => x.Sampler), 8))
            panel.Children.Add(BarRow(name ?? "未知", n, Top(images.Select(x => x.Sampler), 1).FirstOrDefault().Count, images.Count));

        return panel;
    }

    private static string Pct(int a, int b) => b == 0 ? "0%" : $"{a * 100.0 / b:F1}%";

    private static List<(string? Name, int Count)> Top(IEnumerable<string?> src, int take)
        => src.Where(x => x is not null)
              .GroupBy(x => x!)
              .Select(g => ((string?)g.Key, g.Count()))
              .OrderByDescending(x => x.Item2)
              .Take(take)
              .ToList();

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
        // 用星号列按比例切，比给 Border 设 Width 靠谱（NaN 宽度会画不出来）
        var fillGrid = new Grid();
        fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(frac, GridUnitType.Star) });
        fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - frac), GridUnitType.Star) });
        var fill = new Border { Background = R("B.Accent"), CornerRadius = new CornerRadius(4), Margin = new Thickness(0) };
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

    // ---------------- 排行 ----------------

    private static UIElement BuildRank(List<ImageItem> images, double overall)
    {
        var panel = new StackPanel();
        double baseline = overall <= 0 ? 1 : overall;

        panel.Children.Add(Sub($"lift = 该词图片的平均分 / 全库平均分 {baseline:F2}。绿色明显高于平均，橙色明显低于。"));

        panel.Children.Add(H("角色 Top 30"));
        panel.Children.Add(RankTable(images, x => x.Chars, baseline, 30));

        panel.Children.Add(H("画师 Top 30"));
        panel.Children.Add(RankTable(images, x => x.Artists, baseline, 30));

        panel.Children.Add(H("高频提示词 Top 40"));
        panel.Children.Add(RankTable(images, x => PromptTokens(x), baseline, 40, minCount: 3));

        return panel;
    }

    private static string[] PromptTokens(ImageItem it)
    {
        if (it.SearchText.Length == 0) return [];
        return it.SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static UIElement RankTable(List<ImageItem> images, Func<ImageItem, string[]> selector,
        double baseline, int take, int minCount = 1)
    {
        var stats = new Dictionary<string, (int Count, int Sum, int Rated)>(StringComparer.Ordinal);
        foreach (var it in images)
        {
            foreach (var t in selector(it).Distinct())
            {
                var cur = stats.GetValueOrDefault(t);
                stats[t] = (cur.Count + 1, cur.Sum + it.Rating, cur.Rated + (it.Rating > 0 ? 1 : 0));
            }
        }

        var rows = stats
            .Where(kv => kv.Value.Count >= minCount)
            .Select(kv => (Tag: kv.Key, kv.Value.Count, Avg: kv.Value.Rated == 0 ? 0 : kv.Value.Sum / (double)kv.Value.Rated))
            .OrderByDescending(x => x.Count)
            .Take(take)
            .ToList();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

        void Head(string text, int col)
        {
            var t = new TextBlock { Text = text, FontSize = 11, Foreground = R("B.Text3") };
            Grid.SetColumn(t, col);
            if (col > 0) t.HorizontalAlignment = HorizontalAlignment.Right;
            grid.Children.Add(t);
        }
        Head("名称", 0); Head("张数", 1); Head("平均分", 2); Head("lift", 3);

        int row = 1;
        var vocab = Core.Text.Vocabulary.Instance;
        foreach (var x in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = vocab.Display(x.Tag);
            var nm = new TextBlock
            {
                Text = name,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 4),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = x.Tag,
            };
            Grid.SetRow(nm, row);
            grid.Children.Add(nm);

            void Num(string s, int col, Brush? brush = null)
            {
                var t = new TextBlock
                {
                    Text = s,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = brush ?? R("B.Text2"),
                    Margin = new Thickness(0, 4, 0, 4),
                };
                Grid.SetRow(t, row);
                Grid.SetColumn(t, col);
                grid.Children.Add(t);
            }
            Num(x.Count.ToString("N0"), 1);
            Num(x.Avg > 0 ? x.Avg.ToString("0.00") : "—", 2);

            double lift = x.Avg <= 0 ? 0 : x.Avg / baseline;
            Brush b = lift >= 1.15 ? new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A))
                    : lift > 0 && lift <= 0.85 ? new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06))
                    : R("B.Text3");
            Num(lift > 0 ? $"{lift:0.00}×" : "—", 3, b);
            row++;
        }

        return grid;
    }

    // ---------------- 词典 ----------------

    private static UIElement BuildDict(List<ImageItem> images)
    {
        var panel = new StackPanel();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var it in images)
            foreach (var c in it.Chars)
                counts[c] = counts.GetValueOrDefault(c) + 1;

        panel.Children.Add(Sub($"库内出现过的角色 {counts.Count} 种，按张数排序。中文名来自 danbooru wiki / 萌娘百科，可能不准。"));
        var vocab = Core.Text.Vocabulary.Instance;
        foreach (var kv in counts.OrderByDescending(k => k.Value))
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            var zh = vocab.Display(kv.Key);
            g.Children.Add(new TextBlock { Text = zh, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            var tag = new TextBlock
            {
                Text = kv.Key,
                FontSize = 11,
                Foreground = R("B.Text3"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(tag, 1);
            g.Children.Add(tag);
            var n = new TextBlock
            {
                Text = kv.Value.ToString(),
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = R("B.Text2"),
            };
            Grid.SetColumn(n, 2);
            g.Children.Add(n);
            panel.Children.Add(g);
        }
        return panel;
    }

    // ---------------- 体检 ----------------

    private static UIElement BuildHealth(List<ImageItem> images)
    {
        var panel = new StackPanel();
        panel.Children.Add(Sub("只列出需要你判断的东西。"));

        int negDup = images.Count(x => x.NegIsDup);
        int noPrompt = images.Count(x => x.Prompt.Length == 0);
        int missing = App.Root.AllImages.Count(x => x.State == FileState.Missing);
        int deleted = App.Root.AllImages.Count(x => x.State == FileState.Deleted);
        int noSeed = images.Count(x => x.Seed is null);
        int zeroSize = images.Count(x => x.Width == 0 || x.Height == 0);

        void Item(string title, int n, string desc)
        {
            var b = new Border
            {
                Background = Brushes.White,
                BorderBrush = R("B.Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 8),
            };
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
            b.Child = sp;
            panel.Children.Add(b);
        }

        Item("负面提示词与正面完全相同", negDup, "同一条 CLIPTextEncode 接到了正负两个口，统计负面词时已排除。");
        Item("没有提示词", noPrompt, "一般是放大 / 修复流程产出的图，本来就没有文本。");
        Item("文件丢失", missing, "库里有记录但磁盘上找不到。元数据和评分都还在，文件回来就会自动接上。");
        Item("已删除", deleted, "移到了「已删除」，随时可以还原。");
        Item("没有种子", noSeed, "工作流里没写 seed / noise_seed，没法复现。");
        Item("读不到尺寸", zeroSize, "文件可能损坏或被别的程序占用。");
        return panel;
    }
}
