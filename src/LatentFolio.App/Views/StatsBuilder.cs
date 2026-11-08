using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using LatentFolio.App.ViewModels;
using LatentFolio.Core.Models;
using WpfPath = System.Windows.Shapes.Path;

namespace LatentFolio.App.Views;

/// <summary>
/// 统计面板。画布底 + 白卡片，按设计稿分四页签：
///   概览 = KPI + 评分分布环形图 + 月度产出条形列表 + 角色/画师/tag 三张表
///   排行 = 四维度 × 三种排序，列固定为 名称·张数·平均分·≥4★·lift·分布条
///   词典 = 正向 / 负向词频两栏（负面排除「负面=正面」的图）
///   体检 = 负面=正面 / 无提示词 / 未评分 …
/// 顶部门槛（≥3 / ≥5 / ≥10 / 全部）同时作用于概览三表与排行。
///
/// 词条跳转：角色/画师/模型/tag/词典词都是直接写 SearchText，再 ShowOverview。
/// 注意 AppVm.CurrentTree 的搜索只做「提示词(SearchText)/负面/文件名/模型/批注」的
/// 子串匹配，既不认识 rating: 这种查询语法，也不搜路径/日期；所以月份和星级改用
/// 图库现成的临时筛选（Folder/Date、Rating），比硬拼一个搜不到东西的串可靠。
/// </summary>
public static class StatsBuilder
{
    private static readonly double[] SimpleCols = [double.NaN, 76, 76, 76];
    private static readonly double[] RankCols = [double.NaN, 76, 76, 76, 76, 120];
    private static readonly double[] DictCols = [double.NaN, 76];
    private static readonly double[] HealthCols = [240, 76, double.NaN];

    private const double ChartLeftWidth = 380;
    private const double DonutSize = 160;
    private const double DonutThickness = 14;
    private const double TableRowHeight = 29;

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

        ApplyChrome();

        host.Children.Add(BuildTopBar(out var tabHost));
        _tabHost = tabHost;
        host.Children.Add(tabHost);

        // 每次重建都回到概览：门槛切换沿用原来的「重画整页」行为。
        SelectTab(0);
    }

    /// <summary>
    /// 统计页自己的画布底：MainWindow 里的宿主是白底 + 20 边距，这里改成
    /// B.Canvas + 24 边距，四周留白才对得上设计稿。只动统计页的容器，不动别的页。
    /// </summary>
    private static void ApplyChrome()
    {
        _host.Background = R("B.Canvas");
        _host.Margin = new Thickness(24, 14, 24, 24);

        for (DependencyObject? node = VisualTreeHelper.GetParent(_host); node is not null;
             node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer viewer)
            {
                viewer.Background = R("B.Canvas");
                // 进统计页要落在页首。用 ApplicationIdle（比 Loaded 更晚）：
                // 页面构建完、焦点/布局都落定之后再把滚动位置压回 0，
                // 否则会停在半空（实测：进来时环形图和月度的上半截在视口外）。
                var v = viewer;
                v.Dispatcher.BeginInvoke(new Action(() =>
                {
                    v.UpdateLayout();
                    v.ScrollToVerticalOffset(0);
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            if (node is Grid { Name: "StatsPane" } pane)
            {
                pane.Background = R("B.Canvas");
                break;
            }
        }
    }

    private static UIElement BuildTopBar(out ContentPresenter tabHost)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        string[] tabNames = ["概览", "排行", "词典", "体检"];
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
            Style = St("T.Sub"),
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
        var items = new (string Label, bool Active, Action Click)[thresholds.Length];
        for (int i = 0; i < thresholds.Length; i++)
        {
            int value = thresholds[i].Value;
            items[i] = (thresholds[i].Label, _vm.StatsThreshold == value, () =>
            {
                _vm.StatsThreshold = value;
                Build(_host, _vm);
            });
        }
        threshold.Children.Add(Segmented(items, 72, out _));
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
                Style = St("Btn.Seg"),
                Width = itemWidth,
                Height = 24,
                Padding = new Thickness(14, 0, 14, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = item.Active ? R("B.Window") : Brushes.Transparent,
                Foreground = item.Active ? R("B.Accent") : R("B.Text2"),
                Effect = item.Active ? FxCard() : null,
            };
            AutomationProperties.SetName(button, item.Label);
            button.Click += (_, _) => item.Click();
            buttons.Add(button);
            strip.Children.Add(button);
        }

        return new Border
        {
            Style = St("Seg.Track"),
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = strip,
        };
    }

    private static int Min => _vm.StatsThreshold;

    private static Brush R(string key) => (Brush)Application.Current.FindResource(key);

    private static Style St(string key) => (Style)Application.Current.FindResource(key);

    private static Effect FxCard() => (Effect)Application.Current.FindResource("Fx.Card");

    private static TextBlock EmptyText(string text) => new()
    {
        Text = text,
        Style = St("T.Empty"),
        Margin = new Thickness(0, 16, 0, 16),
    };

    private static string Pct(int a, int b) => b == 0 ? "0.0%" : $"{a * 100.0 / b:0.0}%";

    private static double OverallAvg()
    {
        int sum = 0, n = 0;
        foreach (var it in _images) if (it.Rating > 0) { sum += it.Rating; n++; }
        return n == 0 ? 0 : (double)sum / n;
    }

    // ================= 卡片 =================

    private static Border MetricCard(
        double avg, int total, int withPrompt, int rated, int chars, int artists,
        int missing, int deleted)
    {
        var grid = new Grid();
        for (int i = 0; i < 5; i++)
        {
            bool separator = i % 2 == 1;
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = separator ? new GridLength(1) : new GridLength(1, GridUnitType.Star),
            });
        }
        for (int i = 0; i < 3; i++)
        {
            bool separator = i % 2 == 1;
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = separator ? new GridLength(1) : GridLength.Auto,
            });
        }

        void AddCell(int column, int row, string label, string value, string sub)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                Foreground = R("B.Text3"),
            });
            stack.Children.Add(new TextBlock
            {
                Text = value,
                Style = St("T.Num"),
                Margin = new Thickness(0, 6, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (sub.Length > 0)
            {
                var subText = new TextBlock
                {
                    Text = sub,
                    FontSize = 11,
                    Foreground = R("B.Text3"),
                    Margin = new Thickness(0, 4, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                subText.ToolTip = sub;
                stack.Children.Add(subText);
            }

            var cell = new Border
            {
                Padding = new Thickness(16, 14, 16, 14),
                Child = stack,
            };
            Grid.SetColumn(cell, column);
            Grid.SetRow(cell, row);
            grid.Children.Add(cell);
        }

        AddCell(0, 0, "图片总数", total.ToString("N0"), LibraryHealthText(missing, deleted));
        AddCell(2, 0, "有提示词", withPrompt.ToString("N0"), Pct(withPrompt, total));
        AddCell(4, 0, "已评分", rated.ToString("N0"), Pct(rated, total));
        AddCell(0, 2, "平均分", rated == 0 ? "—" : avg.ToString("0.00"), $"{rated:N0} 张已评分");
        AddCell(2, 2, "人物命中", chars.ToString("N0"), Pct(chars, total));
        AddCell(4, 2, "画师命中", artists.ToString("N0"), Pct(artists, total));

        for (int column = 1; column <= 3; column += 2)
        {
            var line = new Border { Width = 1, Background = R("B.Hairline") };
            Grid.SetColumn(line, column);
            Grid.SetRow(line, 0);
            Grid.SetRowSpan(line, 3);
            grid.Children.Add(line);
        }
        var horizontal = new Border { Height = 1, Background = R("B.Hairline") };
        Grid.SetRow(horizontal, 1);
        Grid.SetColumnSpan(horizontal, 5);
        grid.Children.Add(horizontal);

        return new Border
        {
            Style = St("Card"),
            Padding = new Thickness(0),
            Child = grid,
        };
    }

    private static string LibraryHealthText(int missing, int deleted)
    {
        if (missing == 0 && deleted == 0) return "无丢失";
        if (missing == 0) return $"{deleted:N0} 张已删除";
        if (deleted == 0) return $"{missing:N0} 张丢失";
        return $"{missing:N0} 张丢失 · {deleted:N0} 张已删除";
    }

    /// <summary>带标题栏的白卡片：标题 13.5 SemiBold，右侧统计值 11.5 Text3，下接一条 Hairline。</summary>
    private static Border TitledCard(string title, string stat, UIElement body)
    {
        var header = new Grid { Height = 42, Margin = new Thickness(16, 0, 16, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = title,
            Style = St("T.CardTitle"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var statText = new TextBlock
        {
            Text = stat,
            Style = St("T.Sub"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(statText, 1);
        header.Children.Add(statText);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var rule = new Border { Style = St("HLine") };
        Grid.SetRow(rule, 1);
        root.Children.Add(rule);

        var content = new Border
        {
            Padding = new Thickness(16),
            Child = body,
        };
        Grid.SetRow(content, 2);
        root.Children.Add(content);

        return new Border { Style = St("Card"), Child = root };
    }

    private static Grid TwoColumn(UIElement left, UIElement right, double leftWidth)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = leftWidth > 0
                ? new GridLength(leftWidth)
                : new GridLength(1, GridUnitType.Star),
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    // ================= 概览 =================

    private static UIElement BuildOverview()
    {
        var panel = new StackPanel();
        double avg = OverallAvg();
        int total = _images.Count;
        int withPrompt = _images.Count(x => x.Prompt.Length > 0);
        int rated = _images.Count(x => x.Rating > 0);
        int chars = _images.Count(x => x.Chars.Length > 0);
        int artists = _images.Count(x => x.Artists.Length > 0);
        int missing = _vm.AllImages.Count(x => x.State == FileState.Missing);
        int deleted = _vm.AllImages.Count(x => x.State == FileState.Deleted);

        var kpi = MetricCard(avg, total, withPrompt, rated, chars, artists, missing, deleted);
        kpi.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(kpi);

        var hist = new int[6];
        foreach (var it in _images) hist[Math.Clamp(it.Rating, 0, 5)]++;

        var months = _images
            .GroupBy(MonthKey)
            .Select(g => (Label: g.Key, Count: g.Count()))
            .OrderBy(x => x.Label)
            .TakeLast(12)
            .ToList();

        var charts = TwoColumn(
            TitledCard("评分分布", $"已评分 {rated:N0} 张", DonutBody(hist, total)),
            TitledCard("月度产出", $"{months.Count:N0} 个月 · {months.Sum(x => x.Count):N0} 张", MonthBody(months, total)),
            ChartLeftWidth);
        charts.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(charts);

        var (charRows, charDistinct) = SimpleRows(x => x.Chars, 12);
        var (artistRows, artistDistinct) = SimpleRows(x => x.Artists, 12);
        var (tagRows, tagDistinct) = SimpleRows(PromptTags, 12);

        var roleCard = TitledCard("角色", $"{charDistinct:N0} 个", SimpleTable(charRows, avg));
        roleCard.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(roleCard);

        var artistCard = TitledCard("画师", $"{artistDistinct:N0} 个", SimpleTable(artistRows, avg));
        artistCard.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(artistCard);

        panel.Children.Add(TitledCard("正向 tag", $"{tagDistinct:N0} 种", SimpleTable(tagRows, avg)));
        return panel;
    }

    private static UIElement DonutBody(int[] hist, int total)
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DonutSize) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Donut(hist, total));

        var legend = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        for (int rating = 5; rating >= 0; rating--)
            legend.Children.Add(LegendRow(rating, hist[rating], total));
        Grid.SetColumn(legend, 2);
        grid.Children.Add(legend);

        return grid;
    }

    private static Grid Donut(int[] hist, int total)
    {
        var grid = new Grid
        {
            Width = DonutSize,
            Height = DonutSize,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        double radius = DonutSize / 2 - DonutThickness / 2;

        if (total <= 0)
        {
            grid.Children.Add(Ring(radius, R("B.Inset")));
        }
        else
        {
            int nonZero = 0, only = 0;
            for (int rating = 0; rating < hist.Length; rating++)
            {
                if (hist[rating] <= 0) continue;
                nonZero++;
                only = rating;
            }

            if (nonZero == 1)
            {
                grid.Children.Add(Ring(radius, RatingBrush(only)));
            }
            else
            {
                double angle = -90;
                for (int rating = 5; rating >= 0; rating--)
                {
                    int count = hist[rating];
                    if (count == 0) continue;
                    double sweep = count * 360.0 / total;
                    grid.Children.Add(Arc(radius, angle, sweep, RatingBrush(rating)));
                    angle += sweep;
                }
            }
        }

        var center = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        center.Children.Add(new TextBlock
        {
            Text = total.ToString("N0"),
            Style = St("T.Num"),
            FontSize = 26,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        center.Children.Add(new TextBlock
        {
            Text = "张",
            FontSize = 11,
            Foreground = R("B.Text3"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        });
        grid.Children.Add(center);
        return grid;
    }

    private static Ellipse Ring(double radius, Brush brush) => new()
    {
        Width = radius * 2,
        Height = radius * 2,
        Stroke = brush,
        StrokeThickness = DonutThickness,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
    };

    private static WpfPath Arc(double radius, double start, double sweep, Brush brush)
    {
        var center = new Point(DonutSize / 2, DonutSize / 2);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(PointAt(center, radius, start), false, false);
            context.ArcTo(PointAt(center, radius, start + sweep), new Size(radius, radius),
                0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();

        return new WpfPath
        {
            Data = geometry,
            Width = DonutSize,
            Height = DonutSize,
            Stretch = Stretch.None,
            Stroke = brush,
            StrokeThickness = DonutThickness,
            StrokeStartLineCap = PenLineCap.Flat,
            StrokeEndLineCap = PenLineCap.Flat,
            IsHitTestVisible = false,
        };
    }

    private static Point PointAt(Point center, double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static Brush RatingBrush(int rating) => rating switch
    {
        5 => R("B.Accent"),
        4 => R("B.Ok"),
        3 => R("B.Star"),
        2 => R("B.Text3"),
        1 => R("B.BorderStrong"),
        _ => R("B.Border"),
    };

    private static Border LegendRow(int rating, int count, int total)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });

        grid.Children.Add(new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = RatingBrush(rating),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var name = rating == 0 ? "未评分" : $"{rating}★";
        var nameText = new TextBlock
        {
            Text = name,
            FontSize = 12,
            Foreground = R("B.Text2"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(nameText, 2);
        grid.Children.Add(nameText);

        var countText = new TextBlock
        {
            Text = count.ToString("N0"),
            Style = St("T.Value"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(countText, 3);
        grid.Children.Add(countText);

        var pct = new TextBlock
        {
            Text = Pct(count, total),
            Style = St("T.Sub"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(pct, 4);
        grid.Children.Add(pct);

        var row = new Border
        {
            Height = 22,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = grid,
        };
        row.MouseEnter += (_, _) => row.Background = R("B.Hover");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            JumpToFilter(RatingFilter(rating), rating == 0 ? "未评分" : $"{rating} 星");
            e.Handled = true;
        };
        AutomationProperties.SetName(row, name);
        return row;
    }

    private static StackPanel MonthBody(List<(string Label, int Count)> months, int total)
    {
        var panel = new StackPanel();
        if (months.Count == 0)
        {
            panel.Children.Add(EmptyText("没有数据"));
            return panel;
        }

        int max = Math.Max(1, months.Max(x => x.Count));
        foreach (var month in months)
            panel.Children.Add(MonthRow(month.Label, month.Count, max, total));
        return panel;
    }

    private static Border MonthRow(string label, int count, int max, int total)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });

        grid.Children.Add(new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = R("B.Accent"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = R("B.Text2"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(labelText, 2);
        grid.Children.Add(labelText);

        double fraction = Math.Clamp(count / (double)max, 0, 1);
        var bar = Bar(fraction, R("B.Accent"));
        Grid.SetColumn(bar, 3);
        grid.Children.Add(bar);

        var figure = new TextBlock
        {
            Text = $"{count:N0} 张 · {Pct(count, total)}",
            Style = St("T.Sub"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(figure, 5);
        grid.Children.Add(figure);

        var row = new Border
        {
            Height = 24,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = grid,
        };
        row.MouseEnter += (_, _) => row.Background = R("B.Hover");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            JumpToFilter(MonthFilter(label), label);
            e.Handled = true;
        };
        AutomationProperties.SetName(row, label);
        return row;
    }

    private static Grid Bar(double fraction, Brush fill)
    {
        var track = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center };
        track.Children.Add(new Border
        {
            Background = R("B.Inset"),
            CornerRadius = new CornerRadius(3),
        });
        if (fraction <= 0) return track;

        var host = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(fraction, GridUnitType.Star),
        });
        host.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(0.0001, 1 - fraction), GridUnitType.Star),
        });
        var bar = new Border
        {
            Background = fill,
            CornerRadius = new CornerRadius(3),
        };
        Grid.SetColumn(bar, 0);
        host.Children.Add(bar);
        track.Children.Add(host);
        return track;
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

    private static (List<(string Tag, int Count, double Avg)> Rows, int Distinct) SimpleRows(
        Func<ImageItem, string[]> selector, int take)
    {
        var stats = new Dictionary<string, (int Count, int Sum, int Rated)>(StringComparer.Ordinal);
        foreach (var it in _images)
        {
            foreach (var tag in selector(it).Distinct())
            {
                var cur = stats.GetValueOrDefault(tag);
                stats[tag] = (cur.Count + 1, cur.Sum + (it.Rating > 0 ? it.Rating : 0),
                    cur.Rated + (it.Rating > 0 ? 1 : 0));
            }
        }

        var qualifying = stats
            .Where(kv => kv.Value.Count >= Math.Max(1, Min))
            .Select(kv => (Tag: kv.Key, Count: kv.Value.Count,
                Avg: kv.Value.Rated == 0 ? 0 : (double)kv.Value.Sum / kv.Value.Rated))
            .OrderByDescending(x => x.Count)
            .ToList();

        return (qualifying.Take(take).ToList(), stats.Count);
    }

    /// <summary>名称 · 张数 · 平均分 · lift，按张数取前 N，受门槛限制。</summary>
    private static UIElement SimpleTable(List<(string Tag, int Count, double Avg)> rows, double baseline)
    {
        if (rows.Count == 0)
            return EmptyText(Min <= 1 ? "没有数据" : $"没有达到门槛（≥{Min} 张）的条目");

        var table = new StackPanel();
        table.Children.Add(TableHeader(SimpleCols, ("名称", false), ("张数", true), ("平均分", true), ("lift", true)));
        table.Children.Add(Rule());

        var vocab = Core.Text.Vocabulary.Instance;
        foreach (var x in rows)
        {
            var tag = x.Tag;
            var row = MakeGrid(SimpleCols);
            Place(row, 0, NameCell(vocab.Display(tag), tag));
            Place(row, 1, NumCell(x.Count.ToString("N0")));
            Place(row, 2, NumCell(x.Avg > 0 ? x.Avg.ToString("0.00") : "—"));

            double lift = x.Avg <= 0 || baseline <= 0 ? 0 : x.Avg / baseline;
            Place(row, 3, NumCell(lift > 0 ? $"{lift:0.00}×" : "—", LiftBrush(lift)));
            table.Children.Add(TableRow(row, () => JumpToSearch(tag), tag));
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
        grid.Height = 26;
        for (int i = 0; i < columns.Length; i++)
        {
            var (text, right) = columns[i];
            var header = new TextBlock
            {
                Text = text,
                Style = St("T.Sub"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, right ? 0 : 12, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
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
            Style = St("T.Value"),
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
        Style = St("T.Value"),
        Foreground = foreground ?? R("B.Text"),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Border Rule() => new() { Style = St("HLine") };

    private static Border TableRow(Grid grid, Action? onClick = null, string? automationName = null)
    {
        var row = new Border
        {
            Height = TableRowHeight,
            Background = Brushes.Transparent,
            BorderBrush = R("B.Hairline"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
        };
        row.MouseEnter += (_, _) => row.Background = R("B.Hover");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        if (onClick is not null)
        {
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, e) =>
            {
                onClick();
                e.Handled = true;
            };
            if (!string.IsNullOrEmpty(automationName))
                AutomationProperties.SetName(row, automationName);
        }
        return row;
    }

    // ================= 跳回图库 =================

    /// <summary>
    /// 搜索框只做子串匹配（见 AppVm.CurrentTree），所以角色/画师/模型/tag/词典条目
    /// 直接写 SearchText 就行；先清空再赋值，保证点同一个词条也能重新触发刷新。
    /// </summary>
    private static void JumpToSearch(string query)
    {
        if (query.Length == 0) return;

        _vm.SearchText = "";
        if (_vm.HasTempFilter) _vm.SetTempFilter(null);
        _vm.SearchText = query;
        _vm.ShowOverviewCommand.Execute(null);
        CloseStatsPane();
    }

    /// <summary>月份/星级这种搜索框表达不了的，用图库现成的临时筛选，口径一样。</summary>
    private static void JumpToFilter(FilterNode tree, string name)
    {
        _vm.SearchText = "";
        _vm.SetTempFilter(tree, name);
        _vm.ShowOverviewCommand.Execute(null);
        CloseStatsPane();
    }

    private static FilterNode RatingFilter(int rating) => new FilterGroup
    {
        Op = BoolOp.And,
        Children =
        {
            new FilterCond { Field = FieldType.Rating, Op = CondOp.Eq, Value = rating.ToString() },
        },
    };

    /// <summary>
    /// 月份可能来自文件夹名里的 yyyy-MM，也可能来自入库时间（见 MonthKey），
    /// 所以两条都认：路径包含 2026-07，或者入库时间落在该月。
    /// </summary>
    private static FilterNode MonthFilter(string month)
    {
        var first = new DateTime(int.Parse(month.AsSpan(0, 4)), int.Parse(month.AsSpan(5, 2)), 1);
        var next = first.AddMonths(1);

        return new FilterGroup
        {
            Op = BoolOp.Or,
            Children =
            {
                new FilterCond { Field = FieldType.Folder, Op = CondOp.Contains, Value = month },
                new FilterGroup
                {
                    Op = BoolOp.And,
                    Children =
                    {
                        new FilterCond
                        {
                            Field = FieldType.Date,
                            Op = CondOp.Gte,
                            Value = first.ToString("yyyy-MM-dd"),
                        },
                        new FilterCond
                        {
                            Field = FieldType.Date,
                            Op = CondOp.Lt,
                            Value = next.ToString("yyyy-MM-dd"),
                        },
                    },
                },
            },
        };
    }

    /// <summary>ShowOverviewCommand 只切视图，不会把统计浮层收起来；这里把它收掉。</summary>
    private static void CloseStatsPane()
    {
        for (DependencyObject? node = _host; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not Grid { Name: "StatsPane" } pane) continue;
            pane.Visibility = Visibility.Collapsed;
            return;
        }
    }

    // ================= 排行 =================

    private static UIElement BuildRank()
    {
        var panel = new StackPanel();
        string[] dimensionNames = ["角色", "画师", "提示词", "模型"];

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12),
        };
        toolbar.Children.Add(Caption("维度"));

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

        string[] sortNames = ["平均分", "张数", "≥4★"];
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

        var (rows, total) = RankStats();
        panel.Children.Add(TitledCard($"{dimensionNames[_rankDim]}排行", $"{total:N0} 条", BuildRankTable(rows)));
        return panel;
    }

    private static void RebuildRank()
    {
        // 只有排行内容变，顶部页签和门槛都不动。
        if (_tabHost is not null) _tabHost.Content = BuildRank();
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = St("T.Sub"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0),
    };

    private static (List<(string Tag, int Count, double Avg, int High, int[] Hist)> Rows, int Total) RankStats()
    {
        Func<ImageItem, string[]> selector = _rankDim switch
        {
            0 => x => x.Chars,
            1 => x => x.Artists,
            2 => PromptTags,
            _ => x => x.Model is { Length: > 0 } m ? [m] : [],
        };

        var stats = new Dictionary<string, (int Count, int Sum, int Rated, int High, int[] Hist)>(
            StringComparer.Ordinal);
        foreach (var it in _images)
        {
            foreach (var tag in selector(it).Distinct())
            {
                var cur = stats.GetValueOrDefault(tag);
                var hist = cur.Hist ?? new int[6];
                hist[Math.Clamp(it.Rating, 0, 5)]++;
                stats[tag] = (cur.Count + 1,
                    cur.Sum + (it.Rating > 0 ? it.Rating : 0),
                    cur.Rated + (it.Rating > 0 ? 1 : 0),
                    cur.High + (it.Rating >= 4 ? 1 : 0),
                    hist);
            }
        }

        var qualifying = stats
            .Where(kv => kv.Value.Count >= Math.Max(1, Min))
            .Select(kv => (Tag: kv.Key, kv.Value.Count,
                Avg: kv.Value.Rated == 0 ? 0 : (double)kv.Value.Sum / kv.Value.Rated,
                kv.Value.High, kv.Value.Hist))
            .ToList();

        qualifying = _rankSort switch
        {
            1 => qualifying.OrderByDescending(x => x.Count).ToList(),
            2 => qualifying.OrderByDescending(x => x.High).ThenByDescending(x => x.Count).ToList(),
            _ => qualifying.OrderByDescending(x => x.Avg).ThenByDescending(x => x.Count).ToList(),
        };

        return (qualifying.Take(60).ToList(), qualifying.Count);
    }

    private static UIElement BuildRankTable(List<(string Tag, int Count, double Avg, int High, int[] Hist)> rows)
    {
        if (rows.Count == 0)
            return EmptyText(Min <= 1 ? "没有数据" : $"没有达到门槛（≥{Min} 张）的条目");

        double baseline = OverallAvg();
        var table = new StackPanel();
        table.Children.Add(TableHeader(RankCols,
            ("名称", false), ("张数", true), ("平均分", true), ("≥4★", true), ("lift", true), ("评分分布", false)));
        table.Children.Add(Rule());

        var vocab = Core.Text.Vocabulary.Instance;
        foreach (var x in rows)
        {
            var tag = x.Tag;
            var row = MakeGrid(RankCols);
            Place(row, 0, NameCell(_rankDim == 2 ? tag : vocab.Display(tag), tag));
            Place(row, 1, NumCell(x.Count.ToString("N0")));
            Place(row, 2, NumCell(x.Avg > 0 ? x.Avg.ToString("0.00") : "—"));
            Place(row, 3, NumCell(x.High > 0 ? $"{x.High * 100.0 / x.Count:F0}%" : "—"));

            double lift = x.Avg <= 0 || baseline <= 0 ? 0 : x.Avg / baseline;
            Place(row, 4, NumCell(lift > 0 ? $"{lift:0.00}×" : "—", LiftBrush(lift)));

            Place(row, 5, RatingDistBar(x.Hist));
            table.Children.Add(TableRow(row, () => JumpToSearch(tag), tag));
        }

        return table;
    }

    /// <summary>六段评分分布条：5★ 在左，未评分在右；与环形图同一套颜色。</summary>
    private static Grid RatingDistBar(int[] hist)
    {
        var grid = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center };
        int total = Math.Max(1, hist.Sum());
        int[] order = [5, 4, 3, 2, 1, 0];
        int first = -1, last = -1;
        for (int i = 0; i < order.Length; i++)
        {
            if (hist[order[i]] <= 0) continue;
            if (first < 0) first = i;
            last = i;
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
                Background = RatingBrush(rating),
                CornerRadius = new CornerRadius(
                    i == first ? 3 : 0,
                    i == last ? 3 : 0,
                    i == last ? 3 : 0,
                    i == first ? 3 : 0),
            };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    // ================= 词典 =================

    private static UIElement BuildDict()
    {
        var (positiveImages, positiveCounter) = DictData(_images, negative: false);
        var (negativeImages, negativeCounter) = DictData(_images, negative: true);

        var positiveCard = TitledCard("正向词频",
            $"{positiveImages:N0} 张 · {positiveCounter.Count:N0} 种",
            DictTable(positiveCounter));
        var negativeCard = TitledCard("负向词频",
            $"{negativeImages:N0} 张 · {negativeCounter.Count:N0} 种",
            DictTable(negativeCounter));

        return TwoColumn(positiveCard, negativeCard, 0);
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
            foreach (var t in Core.Comfy.GraphParser.Tokenize(text).Tags)
                counter[t] = counter.GetValueOrDefault(t) + 1;
        }

        return (imageCount, counter);
    }

    private static UIElement DictTable(Dictionary<string, int> counter)
    {
        if (counter.Count == 0) return EmptyText("没有数据");

        var table = new StackPanel();
        table.Children.Add(TableHeader(DictCols, ("词", false), ("次数", true)));
        table.Children.Add(Rule());

        foreach (var kv in counter.OrderByDescending(x => x.Value).Take(80))
        {
            var word = kv.Key;
            var row = MakeGrid(DictCols);
            var name = NameCell(word, word);
            name.Foreground = R("B.Text2");
            Place(row, 0, name);
            Place(row, 1, NumCell(kv.Value.ToString("N0")));
            table.Children.Add(TableRow(row, () => JumpToSearch(word), word));
        }

        return table;
    }

    // ================= 体检 =================

    private static UIElement BuildHealth()
    {
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

        var panel = new StackPanel();
        panel.Children.Add(TitledCard("体检", $"{_images.Count:N0} 张已归档", table));
        return panel;
    }
}
