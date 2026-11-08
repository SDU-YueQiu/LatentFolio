using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LatentFolio.App.ViewModels;
using LatentFolio.Core.Db;
using LatentFolio.Core.Text;

namespace LatentFolio.App.Views;

/// <summary>左栏一个分类。</summary>
public sealed class CatVm
{
    public string Name { get; init; } = "";
    public int Count { get; init; }
    public string CountText => Count.ToString("N0");
    public override string ToString() => Name;
}

/// <summary>列表一行。候选行才有「确认/忽略」，人物行才有「编辑」。</summary>
public sealed class TagRowVm
{
    public string Key { get; init; } = "";      // 裸名（候选）或 tag
    public string Tag { get; init; } = "";      // 归到的正式 tag
    public string Title { get; init; } = "";
    public string Sub { get; init; } = "";
    public string Type { get; init; } = "";
    public int Hits { get; init; }
    public string HitsText => Hits.ToString("N0");
    public string Source { get; init; } = "";
    public bool IsCandidate { get; init; }
    public bool IsPerson { get; init; }
    public override string ToString() => Title;
}

/// <summary>标签体系：人物 / 画师 / 自建 / 候选池，加人物条目编辑（设计稿 §6）。</summary>
public partial class TagsWindow : Window
{
    private readonly AppVm _vm;
    private List<TagRowVm> _rows = [];
    private string _category = "人物";

    public TagsWindow(AppVm vm, string? category = null)
    {
        InitializeComponent();
        _vm = vm;
        if (category is not null) _category = category;
        BuildCategories();
        LoadRows();
    }

    private static ImageStore Store => App.Services.Store;

    // ================= 分类 =================

    private void BuildCategories()
    {
        int cands = Store.CandidateCount();
        int myTags = Store.LoadTags().Count;
        var cats = new List<CatVm>
        {
            new() { Name = "人物", Count = _vm.CharCounts.Count },
            new() { Name = "画师", Count = _vm.ArtistCounts.Count },
            new() { Name = "自建", Count = myTags },
            new() { Name = "候选", Count = cands },
        };
        CatList.ItemsSource = cats;
        CatList.SelectedIndex = _category switch
        {
            "画师" => 1,
            "自建" => 2,
            "候选" => 3,
            _ => 0,
        };
    }

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CatList.SelectedItem is not CatVm c) return;
        _category = c.Name;
        // 没选中具体人物之前不显示空的编辑框
        PersonPane.Visibility = Visibility.Collapsed;
        BatchBar.Visibility = _category == "候选" ? Visibility.Visible : Visibility.Collapsed;
        LoadRows();
    }

    private List<TagRowVm> SelectedRows => RowList.SelectedItems.OfType<TagRowVm>().ToList();

    private void UpdateBatchButtons()
    {
        if (BatchIgnoreBtn is null || BatchConfirmBtn is null) return;
        int n = SelectedRows.Count;
        BatchIgnoreBtn.Content = n > 1 ? $"忽略选中 {n}" : "忽略选中";
        BatchConfirmBtn.Content = n > 1 ? $"确认选中 {n}" : "确认选中";
        BatchIgnoreBtn.IsEnabled = n > 0;
        BatchConfirmBtn.IsEnabled = n > 0;
    }

    private void OnBatchIgnore(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) return;
        foreach (var r in rows)
        {
            App.Services.Store.SetCandidateState(r.Key, 2);
            Vocabulary.Instance.Ignored.Add(r.Key);
        }
        // 只重打一次标，不是每行一次
        RunBusy(() => _vm.RetagAll());
        RefreshAll();
    }

    private void OnBatchConfirm(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) return;
        var suggest = rows.Select(r => r.Tag).Where(t => t.Length > 0).Distinct().ToList();
        string target;
        if (suggest.Count == 1) target = suggest[0];
        else
        {
            var dlg = new PromptWindow($"把 {rows.Count} 个裸名归到哪个 tag", suggest.FirstOrDefault() ?? "")
            { Owner = this };
            if (dlg.ShowDialog() != true) return;
            target = dlg.Value;
        }

        foreach (var r in rows)
        {
            App.Services.Store.UpsertAlias(r.Key, target);
            Vocabulary.Instance.Alias[r.Key] = target;
            Vocabulary.Instance.Ignored.Remove(r.Key);
            App.Services.Store.SetCandidateState(r.Key, 1);
        }
        RunBusy(() => _vm.RetagAll());
        RefreshAll();
    }

    private static void RunBusy(Action act)
    {
        var cursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.Wait;
        try { act(); }
        finally { Mouse.OverrideCursor = cursor; }
    }

    // ================= 行数据 =================

    private void LoadRows()
    {
        var v = Vocabulary.Instance;
        switch (_category)
        {
            case "人物":
                _rows = _vm.CharCounts
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv =>
                    {
                        var zh = v.Display(kv.Key);
                        var meta = _vm.PersonMeta.GetValueOrDefault(kv.Key);
                        var sub = zh != kv.Key ? kv.Key : "";
                        if (meta is { Work.Length: > 0 }) sub = (sub.Length > 0 ? sub + " · " : "") + meta.Work;
                        return new TagRowVm
                        {
                            Key = kv.Key,
                            Tag = kv.Key,
                            Title = zh,
                            Sub = sub,
                            Type = "人物",
                            Hits = kv.Value,
                            Source = meta is null ? "词表" : "人工",
                            IsPerson = true,
                        };
                    }).ToList();
                break;

            case "画师":
                _rows = _vm.ArtistCounts
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => new TagRowVm
                    {
                        Key = kv.Key,
                        Tag = kv.Key,
                        Title = kv.Key,
                        Type = "画师",
                        Hits = kv.Value,
                        Source = "词表",
                    }).ToList();
                break;

            case "自建":
                {
                    var counts = new Dictionary<long, int>();
                    using (var c = App.Services.Db.Cmd(
                               "SELECT tag_id, COUNT(*) FROM image_app_tag GROUP BY tag_id"))
                    using (var r = c.ExecuteReader())
                        while (r.Read()) counts[r.GetInt64(0)] = r.GetInt32(1);

                    _rows = Store.LoadTags()
                        .Select(t => new TagRowVm
                        {
                            Key = t.Name,
                            Tag = t.Name,
                            Title = t.Name,
                            Type = "自建",
                            Hits = counts.GetValueOrDefault(t.Id),
                            Source = "手工",
                        })
                        .OrderByDescending(x => x.Hits)
                        .ToList();
                    break;
                }

            default:
                _rows = Store.LoadCandidates()
                    .Select(c => new TagRowVm
                    {
                        Key = c.Key,
                        Tag = c.Suggest,
                        Title = c.Key,
                        Sub = c.Suggest.Length > 0 ? "→ " + c.Suggest : "没有建议",
                        Type = "候选",
                        Hits = c.Hits,
                        Source = c.Suggest.Length > 0 ? "裸名候选" : "裸名",
                        IsCandidate = true,
                    }).ToList();
                break;
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = FilterBox.Text.Trim();
        var list = q.Length == 0
            ? _rows
            : _rows.Where(r => r.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                               || r.Sub.Contains(q, StringComparison.OrdinalIgnoreCase)
                               || r.Key.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        RowList.ItemsSource = list;
        CountText.Text = q.Length == 0 ? $"{_rows.Count:N0}" : $"{list.Count:N0} / {_rows.Count:N0}";
        UpdateBatchButtons();
    }

    private void OnFilter(object sender, TextChangedEventArgs e) => ApplyFilter();

    // ================= 候选池 =================

    private void OnConfirmCandidate(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TagRowVm row }) return;
        var target = row.Tag;
        if (target.Length == 0)
        {
            var dlg = new PromptWindow("归到哪个 tag", "") { Owner = this };
            if (dlg.ShowDialog() != true) return;
            target = dlg.Value;
        }
        Confirm(row.Key, target);
    }

    private void Confirm(string key, string tag)
    {
        var cursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            _vm.ConfirmCandidate(key, tag);
        }
        finally { Mouse.OverrideCursor = cursor; }
        RefreshAll();
    }

    private void OnIgnoreCandidate(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TagRowVm row }) return;
        _vm.IgnoreCandidate(row.Key);
        RefreshAll();
    }

    private void RefreshAll()
    {
        BuildCategories();
        CatList.SelectedIndex = _category switch
        {
            "画师" => 1,
            "自建" => 2,
            "候选" => 3,
            _ => 0,
        };
        LoadRows();
    }

    // ================= 人物条目 =================

    private TagRowVm? Selected => RowList.SelectedItem as TagRowVm;

    private void OnEditPerson(object sender, RoutedEventArgs e)
    {
        RowList.SelectedItem = (sender as FrameworkElement)?.DataContext;
        FillPerson();
    }

    private void OnRowSelected(object sender, SelectionChangedEventArgs e)
    {
        UpdateBatchButtons();
        if (Selected is { IsPerson: true }) FillPerson();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is { IsPerson: true }) { FillPerson(); return; }
        if (Selected is not { IsCandidate: true } row) return;
        var target = row.Tag;
        if (target.Length == 0)
        {
            var dlg = new PromptWindow("归到哪个 tag", "") { Owner = this };
            if (dlg.ShowDialog() != true) return;
            target = dlg.Value;
        }
        Confirm(row.Key, target);
    }

    private void FillPerson()
    {
        if (Selected is not { IsPerson: true } row) return;
        PersonPane.Visibility = Visibility.Visible;
        PersonTagText.Text = row.Key;
        var meta = _vm.PersonMeta.GetValueOrDefault(row.Key);
        NameZhBox.Text = meta?.NameZh ?? (Vocabulary.Instance.Display(row.Key) == row.Key ? "" : Vocabulary.Instance.Display(row.Key));
        NameEnBox.Text = meta?.NameEn ?? "";
        WorkBox.Text = meta?.Work ?? "";
        BioBox.Text = meta?.Bio ?? "";

        var aliases = Store.LoadAliases().Where(kv => kv.Value == row.Key).Select(kv => kv.Key).ToList();
        AliasText.Text = aliases.Count == 0 ? "—" : string.Join("、", aliases);

        long avatarId = _vm.PersonAvatars.TryGetValue(row.Key, out var a) ? a : meta?.AvatarId ?? 0;
        Controls.ThumbLoader.SetSourceItem(PersonAvatar,
            new PersonVm { Tag = row.Key, Display = row.Title, AvatarImageId = avatarId });
    }

    private void OnSavePerson(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var meta = _vm.PersonMeta.TryGetValue(row.Key, out var m) ? m : new ImageStore.PersonRow { Tag = row.Key };
        meta.Tag = row.Key;
        meta.NameZh = NameZhBox.Text.Trim();
        meta.NameEn = NameEnBox.Text.Trim();
        meta.Work = WorkBox.Text.Trim();
        meta.Bio = BioBox.Text.Trim();
        _vm.SavePersonMeta(meta);
        LoadRows();
    }

    private void OnAvatarFromCurrent(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        if (_vm.Current is null) return;
        _vm.SetPersonAvatar(row.Key, _vm.Current.Id);
        FillPerson();
    }

    private void OnAvatarReset(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        _vm.SetPersonAvatar(row.Key, 0);
        FillPerson();
    }

    // ================= 批量 =================

    private void OnRetag(object sender, RoutedEventArgs e)
    {
        var cursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.Wait;
        try { _vm.RetagAll(); }
        finally { Mouse.OverrideCursor = cursor; }
        RefreshAll();
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出词表",
            FileName = "latentfolio-tags.csv",
            Filter = "CSV|*.csv",
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("tag,type,hits,source,zh");
        foreach (var r in _rows)
            sb.AppendLine($"\"{r.Key}\",\"{r.Type}\",{r.Hits},\"{r.Source}\",\"{r.Title.Replace("\"", "\"\"")}\"");
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
