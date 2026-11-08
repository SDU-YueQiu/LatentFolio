using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
    private static readonly double[] SimpleCols = [double.NaN, 76, 76, 76];
    private static readonly double[] RankCols = [double.NaN, 76, 76, 76, 76, 120];
    private static readonly double[] DictCols = [double.NaN, 76];
    private static readonly double[] HealthCols = [240, 76, double.NaN];

    private static AppVm _vm = null!;
    private static Panel _host = null!;
    private static List<ImageItem> _images = [];

    private static ContentPresenter? _tabHost;
    private static readonly List<Button> _tabButtons = [];

    private static int _rankDim;        // 0 角色 1 画师 2 提示词 3 模型
    private static int _rankSort;       // 0 平均分 1 张数 2 ≥4★

    public static void Build(Panel host, AppVm vm)
    {
        _host = host;
        _vm = vm;
        _images = vm.AllImages.Where(x => x.State == FileState.Present).ToList();

        host.Children.Clear();
        _tabButtons.Clear();

        host.Children.Add(BuildTopBar(out var tabHost));
        _tabHost = tabHost;
        host.Children.Add(tabHost);

        // 每次重建都回到概览：门槛切换沿用原来的「重画整页」行为。
        SelectTab(0);
    }

    private static UIElement BuildTopBar(out ContentPresenter tabHost)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tabNames = new[] { "概览", "排行", "词典", "体检" };
        var tabs = new (string Label, bool Active, Action Click)[tabNames.Length];
        for (int i = 0; i < tabNames.Length; i++)
        {
            int index = i;
            tabs[i] = (tabNames[i], index == 0, () => SelectTab(index));
        }
        var track = Segmented(tabs, 64, out var tabButtons);
        _tabButtons.AddRange(tabButtons);
        row.Children.Add(track);

        var threshold = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        threshold.Children.Add(new TextBlock
        {
            Text = "门槛",
            Style = (Style)Application.Current.FindResource("T.Label"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });

        var thresholds = new[]
        {
            (Value: 3, Label: "≥3 张"),
            (Value: 5, Label: "≥5 张"),
            (Value: 10, Label: "≥10 张"),
            (Value: 0, Label: "全部"),
        };
        for (int i = 0; i < thresholds.Length; i++)
        {
            int value = thresholds[i].Value;
            var pill = Pill(thresholds[i].Label, _vm.StatsThreshold == value, () =>
            {
                _vm.StatsThreshold = value;
                Build(_host, _vm);
            });
            pill.Margin = new Thickness(0, 0, i == thresholds.Length - 1 ? 0 : 6, 0);
            threshold.Children.Add(pill);
        }
        Grid.SetColumn(threshold, 2);
        row.Children.Add(threshold);

        tabHost = new ContentPresenter();
        return row;
    }

    private static void SelectTab(int index)
    {
        for (int i = 0; i < _tabButtons.Count; i++)
        {
            var button = _tabButtons[i];
            bool active = i == index;
            button.Background = active ? R("B.Window") : Brushes.Transparent;
            button.Foreground = active ? R("B.Accent") : R("B.Text2");
            button.Effect = active ? FxCard() : null;
        }

        if (_tabHost is null) return;
        _tabHost.Content = index switch
        {
            1 => BuildRank(),
            2 => BuildDict(),
            3 => BuildHealth(),
            _ => BuildOverview(),
        };
    }

    /// <summary>标准分段控件：B.Inset 底槽，选中项白底 + 主色字，未选中 Text2。</summary>
    private static Border Segmented(
        IReadOnlyList<(string Label, bool Active, Action Click)> items,
        double itemWidth,
        out List<Button> buttons)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        buttons = [];
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var button = new Button
            {
                Content = item.Label,
                Style = (Style)Application.Current.FindResource("Btn.Seg"),
                Width = itemWidth,
                Height = 24,
                Padding = new Thickness(14, 0, 14, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = item.Active ? R("B.Window") : Brushes.Transparent,
                Foreground = item.Active ? R("B.Accent") : R("B.Text2"),
                Effect = item.Active ? FxCard() : null,
            };
            button.Click += (_, _) => item.Click();
            buttons.Add(button);
            strip.Children.Add(button);
        }

        return new Border
        {
            Style = (Style)Application.Current.FindResource("Seg.Track"),
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = strip,
        };
    }

    /// <summary>
    /// 门槛胶囊。Btn.Pill 的模板把白底 / B.Border 写死，表现不了选中态的底色和描边；
    /// 这里用 Btn.Base 壳 + Border 内胆复制胶囊外观：
    /// 选中 = AccentSoft 底 + Accent 边 + Accent 字，未选中 = 白底 + B.Border + Text2 字。
    /// </summary>
    private static Button Pill(string label, bool active, Action onClick)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            Foreground = active ? R("B.Accent") : R("B.Text2"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var fill = new Border
        {
            Background = active ? R("B.AccentSoft") : R("B.Window"),
            BorderBrush = active ? R("B.Accent") : R("B.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(11, 0, 11, 0),
            Child = text,
            SnapsToDevicePixels = true,
        };
        var rest = fill.Background;
        var hover = active ? R("B.AccentSoft2") : R("B.Hover");

        var button = new Button
        {
            Content = fill,
            Style = (Style)Application.Current.FindResource("Btn.Base"),
            Height = 28,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        button.MouseEnter += (_, _) => fill.Background = hover;
        button.MouseLeave += (_, _) => fill.Background = rest;
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static int Min => _vm.StatsThreshold;

    private static Brush R(string key) => (Brush)Application.Current.FindResource(key);

    private static Effect FxCard() => (Effect)Application.Current.FindResource("Fx.Card");

    /// <summary>段落标题：13.5 SemiBold，上方 18、下方 8。</summary>
    private static TextBlock H(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource("T.CardTitle"),
        Margin = new Thickness(0, 18, 0, 8),
    };

    private static TextBlock Sub(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource("T.Sub"),
        Margin = new Thickness(0, 0, 0, 8),
        TextWrapping = TextWrapping.Wrap,
    };

    private static string Pct(int a, int b) => b == 0 ? "0.0%" : $"{a * 100.0 / b:0.0}%";

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

        panel.Children.Add(MetricCards(avg, withPrompt, rated, tagCount));

        // 评分分布
        panel.Children.Add(H("评分分布"));
        var hist = new int[6];
        foreach (var it in _images) hist[Math.Clamp(it.Rating, 0, 5)]++;
        int maxH = Math.Max(1, hist.Max());
        for (int r = 5; r >= 0; r--)
            panel.Children.Add(BarRow(r == 0 ? "未评分" : $"{r}★", hist[r], maxH, _images.Count, muted: r == 0));

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

    /// <summary>7 张等宽指标卡：白底、B.Border 1px、圆角 12、padding 14。</summary>
    private static Grid MetricCards(double avg, int withPrompt, int rated, int tagCount)
    {
        var cards = new Grid();
        for (int i = 0; i < 7; i++)
        {
            if (i > 0) cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        void AddCard(int index, string label, string value, string sub = "")
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = label,
                Style = (Style)Application.Current.FindResource("T.Sub"),
            });
            content.Children.Add(new TextBlock
            {
                Text = value,
                Style = (Style)Application.Current.FindResource("T.Num"),
                FontSize = 26,
                Margin = new Thickness(0, 6, 0, 0),
            });
            content.Children.Add(new TextBlock
            {
                Text = sub,
                FontSize = 11,
                Foreground = R("B.Text3"),
                Margin = new Thickness(0, 4, 0, 0),
            });

            var card = new Border
            {
                Background = R("B.Window"),
                BorderBrush = R("B.Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14),
                Child = content,
            };
            Grid.SetColumn(card, index * 2);
            cards.Children.Add(card);
        }

        int total = _images.Count;
        AddCard(0, "图片总数", total.ToString("N0"));
        AddCard(1, "有提示词", withPrompt.ToString("N0"), Pct(withPrompt, total));
        AddCard(2, "已评分", rated.ToString("N0"), Pct(rated, total));
        AddCard(3, "平均分", avg.ToString("0.00"), "已评分的平均");
        AddCard(4, "人物命中", _images.Count(x => x.Chars.Length > 0).ToString("N0"));
        AddCard(5, "画师命中", _images.Count(x => x.Artists.Length > 0).ToString("N0"));
        AddCard(6, "正向 tag", tagCount.ToString("N0"), "种");
        return cards;
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

        if (rows.Count == 0)
            return Sub(Min <= 1 ? "没有数据" : $"没有达到门槛（≥{Min} 张）的条目");

        var table = new StackPanel();
        table.Children.Add(TableHeader(SimpleCols, ("名称", false), ("张数", true), ("平均分", true), ("lift", true)));
        table.Children.Add(Rule());

        var vocab = Core.Text.Vocabulary.Instance;
        foreach (var x in rows)
        {
            var row = MakeGrid(SimpleCols);
            Place(row, 0, NameCell(vocab.Display(x.Tag), x.Tag));
            Place(row, 1, NumCell(x.Count.ToString("N0")));
            Place(row, 2, NumCell(x.Avg > 0 ? x.Avg.ToString("0.00") : "—"));

            double lift = x.Avg <= 0 || baseline <= 0 ? 0 : x.Avg / baseline;
            Place(row, 3, NumCell(lift > 0 ? $"{lift:0.00}×" : "—", LiftBrush(lift)));
            table.Children.Add(TableRow(row, withRule: true));
        }
        return table;
    }

    private static Brush LiftBrush(double lift) => lift switch
    {
        >= 1.15 => R("B.Ok"),
        > 0 and <= 0.85 => R("B.Warn"),
        _ => R("B.Text3"),
    };

    // ================= 表格零件 =================

    private static Grid MakeGrid(double[] widths)
    {
        var grid = new Grid();
        foreach (var width in widths)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = double.IsNaN(width)
                    ? new GridLength(1, GridUnitType.Star)
                    : new GridLength(width),
            });
        }
        return grid;
    }

    private static void Place(Grid grid, int column, UIElement cell)
    {
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static Grid TableHeader(double[] widths, params (string Text, bool Right)[] columns)
    {
        var grid = MakeGrid(widths);
        grid.Height = 24;
        for (int i = 0; i < columns.Length; i++)
        {
            var (text, right) = columns[i];
            var header = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                Foreground = R("B.Text3"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, right ? 0 : 12, 0),
            };
            Place(grid, i, header);
        }
        return grid;
    }

    private static TextBlock NameCell(string text, string? tooltip = null)
    {
        var cell = new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.FindResource("T.Value"),
            Foreground = R("B.Text"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 12, 0),
        };
        if (!string.IsNullOrEmpty(tooltip)) cell.ToolTip = tooltip;
        return cell;
    }

    private static TextBlock NumCell(string text, Brush? foreground = null) => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource("T.Value"),
        Foreground = foreground ?? R("B.Text"),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Border Rule() => new()
    {
        Style = (Style)Application.Current.FindResource("HLine"),
    };

    private static Border TableRow(Grid grid, bool withRule = false)
    {
        var row = new Border
        {
            Height = 28,
            Background = Brushes.Transparent,
            Child = grid,
        };
        if (withRule)
        {
            row.BorderBrush = R("B.Hairline");
            row.BorderThickness = new Thickness(0, 0, 0, 1);
        }
        row.MouseEnter += (_, _) => row.Background = R("B.Hover");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    /// <summary>分布条：标签 76 右对齐、条高 6 圆角 3、右列 104 显示「张数 · 百分比」、行高 26。</summary>
    private static UIElement BarRow(string label, int value, int max, int total, bool muted = false)
    {
        var grid = new Grid { Height = 26 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });

        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            Foreground = R("B.Text3"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var track = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center };
        track.Children.Add(new Border
        {
            Background = R("B.Inset"),
            CornerRadius = new CornerRadius(3),
        });
        double frac = max <= 0 ? 0 : Math.Clamp(value / (double)max, 0, 1);
        if (frac > 0)
        {
            var fillHost = new Grid();
            fillHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(frac, GridUnitType.Star) });
            fillHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - frac), GridUnitType.Star) });
            var fill = new Border
            {
                Background = muted ? R("B.BorderStrong") : R("B.Accent"),
                CornerRadius = new CornerRadius(3),
            };
            Grid.SetColumn(fill, 0);
            fillHost.Children.Add(fill);
            track.Children.Add(fillHost);
        }
        Grid.SetColumn(track, 2);
        grid.Children.Add(track);

        var figure = new TextBlock
        {
            Style = (Style)Application.Current.FindResource("T.Value"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        figure.Inlines.Add(new Run(value.ToString("N0")) { Foreground = R("B.Text2") });
        figure.Inlines.Add(new Run(" · ") { FontSize = 11.5, Foreground = R("B.Text3") });
        figure.Inlines.Add(new Run(Pct(value, total)) { FontSize = 11.5, Foreground = R("B.Text3") });
        Grid.SetColumn(figure, 4);
        grid.Children.Add(figure);

        return grid;
    }

    // ================= 排行 =================

    private static UIElement BuildRank()
    {
        var panel = new StackPanel();

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        toolbar.Children.Add(Caption("维度"));

        var dimensionNames = new[] { "角色", "画师", "提示词", "模型" };
        var dimensions = new (string Label, bool Active, Action Click)[dimensionNames.Length];
        for (int i = 0; i < dimensionNames.Length; i++)
        {
            int dimension = i;
            dimensions[i] = (dimensionNames[i], _rankDim == dimension, () =>
            {
                _rankDim = dimension;
                RebuildRank();
            });
        }
        toolbar.Children.Add(Segmented(dimensions, 68, out _));

        toolbar.Children.Add(new Border
        {
            Width = 1,
            Height = 16,
            Background = R("B.Border"),
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        toolbar.Children.Add(Caption("排序"));

        var sortNames = new[] { "平均分", "张数", "≥4★" };
        var sorts = new (string Label, bool Active, Action Click)[sortNames.Length];
        for (int i = 0; i < sortNames.Length; i++)
        {
            int sort = i;
            sorts[i] = (sortNames[i], _rankSort == sort, () =>
            {
                _rankSort = sort;
                RebuildRank();
            });
        }
        toolbar.Children.Add(Segmented(sorts, 68, out _));

        panel.Children.Add(toolbar);
        panel.Children.Add(BuildRankTable());
        return panel;
    }

    private static void RebuildRank()
    {
        // 就地换掉整块统计（门槛栏在上面，不动），随后切回排行页签。
        Build(_host, _vm);
        SelectTab(1);
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        Foreground = R("B.Text3"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0),
    };

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

        var table = new StackPanel();
        table.Children.Add(TableHeader(RankCols,
            ("名称", false), ("张数", true), ("平均分", true), ("≥4★", true), ("lift", true), ("评分分布", false)));
        table.Children.Add(Rule());

        var vocab = Core.Text.Vocabulary.Instance;
        foreach (var x in rows)
        {
            var row = MakeGrid(RankCols);
            Place(row, 0, NameCell(_rankDim == 2 ? x.Tag : vocab.Display(x.Tag), x.Tag));
            Place(row, 1, NumCell(x.Count.ToString("N0")));
            Place(row, 2, NumCell(x.Avg > 0 ? x.Avg.ToString("0.00") : "—"));
            Place(row, 3, NumCell(x.High > 0 ? $"{x.High * 100.0 / x.Count:F0}%" : "—"));

            double lift = x.Avg <= 0 || baseline <= 0 ? 0 : x.Avg / baseline;
            Place(row, 4, NumCell(lift > 0 ? $"{lift:0.00}×" : "—", LiftBrush(lift)));

            Place(row, 5, RatingDistBar(x.Hist));
            table.Children.Add(TableRow(row));
        }

        return table;
    }

    /// <summary>六段评分分布条：5★ 在左，未评分在右；高分为主色，低分为灰阶。</summary>
    private static Grid RatingDistBar(int[] hist)
    {
        var grid = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center };
        int total = Math.Max(1, hist.Sum());
        int[] order = [5, 4, 3, 2, 1, 0];
        int first = -1, last = -1;
        for (int i = 0; i < order.Length; i++)
        {
            if (hist[order[i]] > 0)
            {
                if (first < 0) first = i;
                last = i;
            }
        }

        for (int i = 0; i < order.Length; i++)
        {
            int rating = order[i];
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(0.0001, hist[rating] / (double)total), GridUnitType.Star),
            });
            if (hist[rating] == 0) continue;

            var cell = new Border
            {
                Background = DistFill(rating),
                CornerRadius = new CornerRadius(
                    i == first ? 3 : 0,
                    i == last ? 3 : 0,
                    i == last ? 3 : 0,
                    i == first ? 3 : 0),
                Opacity = rating switch { 5 => 1, 4 => 0.62, 3 => 0.36, _ => 1 },
            };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private static Brush DistFill(int rating) => rating switch
    {
        5 or 4 or 3 => R("B.Accent"),
        2 => R("B.BorderStrong"),
        1 => R("B.Border"),
        _ => R("B.Inset"),
    };

    // ================= 词典 =================

    private static UIElement BuildDict()
    {
        var (positiveImages, positiveCounter) = DictData(_images, negative: false);
        var (negativeImages, negativeCounter) = DictData(_images, negative: true);
        int negDup = _images.Count(x => x.NegIsDup);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var positiveTitle = H("正向词频");
        Grid.SetRow(positiveTitle, 0);
        root.Children.Add(positiveTitle);
        var positiveSub = Sub($"{positiveImages:N0} 张有正向 · {positiveCounter.Count:N0} 种");
        Grid.SetRow(positiveSub, 1);
        root.Children.Add(positiveSub);
        var positiveTable = DictTable(positiveCounter);
        Grid.SetRow(positiveTable, 2);
        root.Children.Add(positiveTable);

        var negativeTitle = H("负向词频");
        Grid.SetRow(negativeTitle, 0);
        Grid.SetColumn(negativeTitle, 2);
        root.Children.Add(negativeTitle);
        var negativeSub = Sub($"{negativeImages:N0} 张有独立负面（已排掉 {negDup:N0} 张负面=正面） · {negativeCounter.Count:N0} 种");
        Grid.SetRow(negativeSub, 1);
        Grid.SetColumn(negativeSub, 2);
        root.Children.Add(negativeSub);
        var negativeTable = DictTable(negativeCounter);
        Grid.SetRow(negativeTable, 2);
        Grid.SetColumn(negativeTable, 2);
        root.Children.Add(negativeTable);

        return root;
    }

    /// <summary>负面统计要排除「负面=正面」的图，否则 1girl / solo 会冲进负面榜。</summary>
    private static (int ImageCount, Dictionary<string, int> Counter) DictData(List<ImageItem> images, bool negative)
    {
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

        return (imageCount, counter);
    }

    private static UIElement DictTable(Dictionary<string, int> counter)
    {
        var table = new StackPanel();
        table.Children.Add(TableHeader(DictCols, ("词", false), ("次数", true)));
        table.Children.Add(Rule());

        foreach (var kv in counter.OrderByDescending(x => x.Value).Take(80))
        {
            var row = MakeGrid(DictCols);
            var word = NameCell(kv.Key, kv.Key);
            word.Foreground = R("B.Text2");
            Place(row, 0, word);
            Place(row, 1, NumCell(kv.Value.ToString("N0")));
            table.Children.Add(TableRow(row, withRule: true));
        }

        return table;
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

        var table = new StackPanel();
        table.Children.Add(TableHeader(HealthCols, ("项目", false), ("数量", true), ("说明", false)));
        table.Children.Add(Rule());

        void Item(string title, int number, string description)
        {
            var row = MakeGrid(HealthCols);
            Place(row, 0, NameCell(title));
            Place(row, 1, NumCell(number.ToString("N0")));

            var desc = new TextBlock
            {
                Text = description,
                FontSize = 11.5,
                Foreground = R("B.Text3"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 12, 0),
            };
            if (description.Length > 0) desc.ToolTip = description;
            Place(row, 2, desc);

            table.Children.Add(TableRow(row));
        }

        Item("负面提示词与正面完全相同", negDup, "同一条 CLIPTextEncode 接到了正负两个口，负面统计里已排除。");
        Item("没有提示词", noPrompt, "一般是放大 / 修复流程产出的图，本来就没有文本。");
        Item("未评分", noRating, "");
        Item("文件丢失", missing, "库里有记录但磁盘上找不到，元数据和评分都还在。");
        Item("已删除", deleted, "移到了「已删除」，随时可以还原。");
        Item("没有种子", noSeed, "工作流里没写 seed / noise_seed，没法复现。");
        Item("读不到尺寸", zeroSize, "文件可能损坏或被别的程序占用。");

        panel.Children.Add(table);
        return panel;
    }
}
