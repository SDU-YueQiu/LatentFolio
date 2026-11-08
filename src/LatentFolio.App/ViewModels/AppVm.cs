using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LatentFolio.App.Services;
using LatentFolio.Core.Comfy;
using LatentFolio.Core.Db;
using LatentFolio.Core.Filtering;
using LatentFolio.Core.Models;
using LatentFolio.Core.Scan;
using LatentFolio.Core.Text;

namespace LatentFolio.App.ViewModels;

public enum ViewKind { Overview, Column, Folder, Deleted, Person }

public sealed partial class ColumnVm : ObservableObject
{
    // ListBoxItem 的无障碍名字默认取数据对象的 ToString()，不覆写的话
    // UIA / 读屏软件看到的是 "LatentFolio.App.ViewModels.ColumnVm"。
    public override string ToString() => Name;
    public long Id;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _color = "";
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _countDirty;
    public FilterNode Tree = new FilterGroup();
    public string TreeJson = "{}";
    public int Ord;

    public string CountText => Count < 0 ? "…" : Count.ToString("N0");

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(CountText));
}

public sealed partial class PersonVm : ObservableObject
{
    public override string ToString() => Display;
    public string Tag = "";
    [ObservableProperty] private string _display = "";
    [ObservableProperty] private int _count;
    [ObservableProperty] private System.Windows.Media.ImageSource? _avatar;
    public long AvatarImageId;
}

public sealed partial class FolderVm : ObservableObject
{
    public override string ToString() => Display;
    public string Path = "";
    [ObservableProperty] private string _display = "";
    public int Depth;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _expanded = true;
    public string CountText => Count.ToString();
    public Thickness DepthText => new(Depth * 14, 0, 0, 0);
}

public sealed partial class AppVm : ObservableObject
{
    private readonly AppServices _svc;
    private readonly ThumbService _thumbs;
    private readonly ImageStore _store;
    private readonly ColumnEngine _engine = new();

    public List<ImageItem> AllImages { get; private set; } = [];
    public Dictionary<long, ImageItem> ById { get; } = [];
    public Dictionary<long, ImageVm> VmById { get; } = [];
    public ObservableCollection<ImageVm> Items { get; } = [];

    public ObservableCollection<ColumnVm> Columns { get; } = [];
    public ObservableCollection<PersonVm> People { get; } = [];
    public ObservableCollection<PersonVm> PeoplePreview { get; } = [];
    public ObservableCollection<FolderVm> Folders { get; } = [];

    public List<ImageVm> Selection { get; } = [];

    public event Action? GridReloaded;
    public event Action? ViewerClosed;

    public AppVm(AppServices svc, ThumbService thumbs)
    {
        _svc = svc;
        _thumbs = thumbs;
        _store = svc.Store;
    }

    // ================= 状态 =================

    [ObservableProperty] private string _libraryRoot = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _title = "总览";
    [ObservableProperty] private string _statusLeft = "";
    [ObservableProperty] private string _statusRight = "";
    [ObservableProperty] private int _hitCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private double _tileSize = 190;    [ObservableProperty] private ViewKind _view = ViewKind.Overview;
    [ObservableProperty] private long _viewColumnId;
    [ObservableProperty] private string _viewFolder = "";
    [ObservableProperty] private string _viewPerson = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private double _busyFraction;
    [ObservableProperty] private string _sortLabel = "添加时间 · 新在前";

    public SortSpec Sort { get; private set; } = new(SortKey.Date, true);

    private LibraryWatcher? _watcher;

    [ObservableProperty] private bool _watchEnabled = true;

    partial void OnWatchEnabledChanged(bool value)
    {
        _svc.Store.SetSetting("watch", value ? "1" : "0");
        if (value) StartWatcher(); else _watcher?.Stop();
    }

    public void StartWatcher()
    {
        if (!WatchEnabled) return;
        try
        {
            _watcher?.Dispose();
            _watcher = new LibraryWatcher(LibraryRoot, _svc.Scanner);
            _watcher.Imported += OnWatcherImported;
            _watcher.Start();
        }
        catch
        {
            // 目录不可监视（网络盘/权限）就算了
        }
    }

    private void OnWatcherImported(int count)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            var selected = Selection.Select(x => x.Id).ToHashSet();
            ReloadFromDb();
            foreach (var vm in VmById.Values) vm.RefreshTags();
            _engine.Invalidate();
            _ = RunColumnCountsAsync();
            BuildPeople();
            BuildFolders();
            RefreshGrid();
            Selection.Clear();
            foreach (var vm in Items) if (selected.Contains(vm.Id)) Selection.Add(vm);
            StatusLeft = $"自动导入 {count} 张";
        });
    }

    public double TileHeight => TileSize + 46;

    partial void OnTileSizeChanged(double value) => OnPropertyChanged(nameof(TileHeight));

    public string LibrarySummary
    {
        get
        {
            int folders = Folders.Count(f => f.Depth > 0 && f.Path.Length > 0);
            int n = AllImages.Count(x => x.State != FileState.Deleted);
            return $"{n:N0} 张 · {folders:N0} 个文件夹";
        }
    }

    // 查看器
    [ObservableProperty] private bool _viewerOpen;
    [ObservableProperty] private bool _viewerLoading;
    [ObservableProperty] private ImageSourceHolder? _viewerImage;
    [ObservableProperty] private bool _showOriginal;
    [ObservableProperty] private string _vsrBadge = "";
    [ObservableProperty] private bool _vsrAvailable;
    [ObservableProperty] private string _vsrStatus = "";

    public sealed class ImageSourceHolder(System.Windows.Media.ImageSource src)
    {
        public System.Windows.Media.ImageSource Source { get; } = src;
    }

    [ObservableProperty] private ImageVm? _current;

    public event Action? CurrentChangedForViewer;

    /// <summary>采样参数列表，详情栏直接绑它（查看器和图库主页共用）。</summary>
    public ObservableCollection<KeyValuePair<string, string>> CurrentParams { get; } = [];

    partial void OnCurrentChanged(ImageVm? value)
    {
        PromptEditorText = value?.Prompt ?? "";
        WorkflowText = "";
        _workflowLoadedFor = 0;
        CurrentParams.Clear();
        if (value is not null)
        {
            CurrentParams.Add(new("模型", value.ModelText));
            CurrentParams.Add(new("采样器", value.SamplerText));
            CurrentParams.Add(new("调度器", value.SchedulerText));
            CurrentParams.Add(new("步数", value.StepsText));
            CurrentParams.Add(new("CFG", value.CfgText));
            CurrentParams.Add(new("种子", value.SeedText));
        }
        IsPanelVisible = value is not null && !PanelCollapsed;
        CurrentChangedForViewer?.Invoke();
    }

    [ObservableProperty] private string _promptEditorText = "";
    [ObservableProperty] private string _workflowText = "";

    // ================= 右侧详情栏 =================

    [ObservableProperty] private bool _panelCollapsed;
    [ObservableProperty] private bool _isPanelVisible;

    partial void OnPanelCollapsedChanged(bool value)
    {
        IsPanelVisible = Current is not null && !value;
        _store.SetSetting("panel_collapsed", value ? "1" : "0");
    }

    /// <summary>
    /// 图库主页上单选一张图 → 右边栏显示详情（不进放大模式）。
    /// 放大模式开着的时候不要动 Current，否则刷新网格会把正在看的图清掉。
    /// </summary>
    public void SelectSingle(ImageVm? vm)
    {
        if (ViewerOpen) return;
        Current = vm;
    }

    /// <summary>展开「工作流」时才读库，几十 KB 的 JSON 不跟着每次切图反序列化。</summary>
    public void EnsureWorkflow(ImageVm vm)
    {
        if (Current?.Id != vm.Id) return;
        if (_workflowLoadedFor == vm.Id) return;
        _ = LoadWorkflowAsync(vm);
    }

    private long _workflowLoadedFor;

    // ================= 启动 =================

    public async Task InitAsync()
    {
        LibraryRoot = _svc.LibraryRoot;
        AlwaysVsr = _store.GetSetting("always_vsr") == "1";
        WatchEnabled = _store.GetSetting("watch") != "0";
        PanelCollapsed = _store.GetSetting("panel_collapsed") == "1";
        LoadPersonAvatars();
        _ = _svc.Vsr.StartAsync().ContinueWith(_ =>
        {
            OnPropertyChanged(nameof(VsrReady));
            OnPropertyChanged(nameof(VsrStatus));
        }, TaskScheduler.FromCurrentSynchronizationContext());

        ReloadFromDb();
        await EnsureSeed();
        await RunColumnCountsAsync();
        BuildPeople();
        BuildFolders();
        await ScanAsync(initial: true);
        RefreshGrid();
        StartWatcher();
    }

    public void ReloadFromDb()
    {
        AllImages = _store.LoadAll();
        _similar = null;
        ById.Clear();
        VmById.Clear();
        foreach (var it in AllImages)
        {
            ById[it.Id] = it;
            var vm = new ImageVm(it) { RatingPersist = OnVmRating, NotePersist = OnVmNote };
            VmById[it.Id] = vm;
        }
        TotalCount = AllImages.Count(x => x.State != FileState.Deleted);
        LoadColumns();
    }

    /// <summary>评分/批注的唯一落库点。界面走哪条路改的都到这里。</summary>
    private void OnVmRating(ImageVm vm)
    {
        _store.UpdateUser(vm.Item);
        if (ReferenceEquals(vm, Current)) OnPropertyChanged(nameof(Current));
        _ = RunColumnCountsAsync();
    }

    private void OnVmNote(ImageVm vm) => _store.UpdateUser(vm.Item);

    private async Task EnsureSeed()
    {
        if (Columns.Count > 0) return;
        if (_store.GetSetting("seeded") == "1") return;
        await AddColumnAsync("高评分 ★4+", new FilterGroup
        {
            Op = BoolOp.And,
            Children = { new FilterCond { Field = FieldType.Rating, Op = CondOp.Gte, Value = "4" } },
        });
        await AddColumnAsync("未评分", new FilterGroup
        {
            Op = BoolOp.And,
            Children = { new FilterCond { Field = FieldType.Rating, Op = CondOp.Eq, Value = "0" } },
        });
        _store.SetSetting("seeded", "1");
    }

    // ================= 扫描 =================

    [RelayCommand]
    private async Task ScanAsync() => await ScanAsyncCore(false);

    private async Task ScanAsync(bool initial) => await ScanAsyncCore(initial);

    /// <summary>解析器改了就 +1，老库下次启动会强制重解析一遍元数据。</summary>
    public const int ParserVersion = 3;

    private async Task ScanAsyncCore(bool initial)
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = "扫描图库…";
        try
        {
            var prog = new Progress<ScanProgress>(p =>
            {
                BusyText = string.IsNullOrEmpty(p.Current) ? p.Phase : $"{p.Phase} · {p.Current}";
                BusyFraction = p.Fraction * 100;
            });

            bool force = _store.GetSetting("parser_version") != ParserVersion.ToString();
            var result = await Task.Run(() => _svc.Scanner.ScanAsync(
                LibraryRoot, prog, makeThumbs: true, ct: default, force: force));
            _store.SetSetting("parser_version", ParserVersion.ToString());

            // 词表是后台加载的；扫描时可能还没好，这里补一次打标
            var vocab = Vocabulary.Instance;
            if (!vocab.Ready) await Task.Run(() => vocab.WaitReady(TimeSpan.FromMinutes(2)));
            int tagged = await Task.Run(() => RetagMissing(vocab));

            var legacy = Path.Combine(_svc.AssetsDir, "legacy_ratings.tsv");
            ReloadFromDb();
            int imported = 0;
            if (_store.GetSetting("legacy_imported") != "1")
            {
                imported = _svc.Scanner.ImportLegacy(legacy, AllImages);
                _store.SetSetting("legacy_imported", "1");
                if (imported > 0) ReloadFromDb();
            }

            foreach (var vm in VmById.Values) vm.RefreshTags();
            _engine.Invalidate();
            await RunColumnCountsAsync();
            BuildPeople();
            BuildFolders();
            RefreshGrid();

            StatusLeft = $"新增 {result.Added} · 更新 {result.Updated} · 未变 {result.Skipped}"
                         + (result.Removed > 0 ? $" · 丢失 {result.Removed}" : "")
                         + (imported > 0 ? $" · 导入旧评分 {imported}" : "");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"扫描失败：{ex.Message}", "LatentFolio", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            BusyFraction = 0;
        }
    }

    // ================= 栏目 =================

    private void LoadColumns()
    {
        Columns.Clear();
        using var c = _svc.Db.Cmd("SELECT id,name,color,ord,tree_json,count FROM col ORDER BY ord,id");
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var vm = new ColumnVm
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                Color = r.GetString(2),
                Ord = r.GetInt32(3),
                TreeJson = r.GetString(4),
                Count = r.GetInt32(5),
            };
            vm.Tree = FilterNodeExtensions.FromJson(vm.TreeJson);
            Columns.Add(vm);
        }
    }

    public async Task AddColumnAsync(string name, FilterNode tree)
    {
        var json = tree.ToJson();
        using var c = _svc.Db.Cmd("INSERT INTO col(name,color,ord,tree_json,count) VALUES($n,'',$o,$t,-1); SELECT last_insert_rowid();");
        c.Parameters.AddWithValue("$n", name);
        c.Parameters.AddWithValue("$o", Columns.Count);
        c.Parameters.AddWithValue("$t", json);
        var id = (long)c.ExecuteScalar()!;
        LoadColumns();
        await RunColumnCountsAsync();
    }

    public void UpdateColumn(long id, string name, FilterNode tree)
    {
        using var c = _svc.Db.Cmd("UPDATE col SET name=$n, tree_json=$t, count=-1 WHERE id=$id");
        c.Parameters.AddWithValue("$n", name);
        c.Parameters.AddWithValue("$t", tree.ToJson());
        c.Parameters.AddWithValue("$id", id);
        c.ExecuteNonQuery();
        LoadColumns();
    }

    public void DeleteColumn(long id)
    {
        using var c = _svc.Db.Cmd("DELETE FROM col WHERE id=$id");
        c.Parameters.AddWithValue("$id", id);
        c.ExecuteNonQuery();
        LoadColumns();
    }

    /// <summary>拖拽排序：把 from 这一栏挪到 to 之前（to = Count 表示挪到最后）。</summary>
    public void MoveColumn(int from, int to)
    {
        if (from < 0 || from >= Columns.Count) return;
        if (to < 0) to = 0;
        if (to > Columns.Count) to = Columns.Count;
        if (to == from || to == from + 1) return;

        var ordered = Columns.ToList();
        var moving = ordered[from];
        ordered.RemoveAt(from);
        int dest = to > from ? to - 1 : to;
        if (dest < 0) dest = 0;
        if (dest > ordered.Count) dest = ordered.Count;
        ordered.Insert(dest, moving);

        using var tx = _svc.Db.Connection.BeginTransaction();
        for (int i = 0; i < ordered.Count; i++)
        {
            using var c = _svc.Db.Cmd("UPDATE col SET ord=$o WHERE id=$id");
            c.Parameters.AddWithValue("$o", i);
            c.Parameters.AddWithValue("$id", ordered[i].Id);
            c.ExecuteNonQuery();
        }
        tx.Commit();

        Columns.Clear();
        foreach (var c in ordered) Columns.Add(c);
    }

    public async Task RunColumnCountsAsync()
    {
        var ctx = MakeContext();
        long ver = DataVersion();
        using var tx = _svc.Db.Connection.BeginTransaction();
        foreach (var col in Columns.ToList())
        {
            var hash = ColumnEngine.HashTree(col.TreeJson);
            var cached = _engine.TryGet(col.Id, hash, ver);
            if (cached is null)
            {
                cached = ColumnEngine.Evaluate(col.Tree, AllImages, ctx, hash, ver);
                _engine.Put(col.Id, cached);
            }
            col.Count = cached.Count;
            using var c = _svc.Db.Cmd("UPDATE col SET count=$c, count_ver=$v WHERE id=$id");
            c.Parameters.AddWithValue("$c", col.Count);
            c.Parameters.AddWithValue("$v", ver);
            c.Parameters.AddWithValue("$id", col.Id);
            c.ExecuteNonQuery();
        }
        tx.Commit();
        await Task.CompletedTask;
    }

    public long DataVersion()
    {
        long h = AllImages.Count;
        foreach (var it in AllImages) { unchecked { h = h * 31 + it.Id + it.Rating * 7; } }
        return h;
    }

    // ================= 人物 / 文件夹 =================

    /// <summary>给「有提示词但还没打过角色/画师标」的图补打标并落库。</summary>
    private int RetagMissing(Vocabulary vocab)
    {
        if (!vocab.Ready) return 0;
        int n = 0;
        var all = _store.LoadAll();
        using var tx = _svc.Db.Connection.BeginTransaction();
        foreach (var it in all)
        {
            if (it.Prompt.Length == 0) continue;
            if (it.Chars.Length > 0 || it.Artists.Length > 0) continue;
            var (chars, artists) = vocab.Tag(it.Prompt);
            if (chars.Length == 0 && artists.Length == 0) continue;
            it.Chars = chars;
            it.Artists = artists;
            _store.Update(it);
            n++;
        }
        tx.Commit();
        return n;
    }

    private void BuildPeople()
    {
        var map = new Dictionary<string, (int Count, long BestId, int BestRating)>(StringComparer.Ordinal);
        foreach (var it in AllImages)
        {
            if (it.State == FileState.Deleted) continue;
            foreach (var c in it.Chars)
            {
                if (map.TryGetValue(c, out var cur))
                {
                    map[c] = (cur.Count + 1, cur.BestRating >= it.Rating ? cur.BestId : it.Id,
                        Math.Max(cur.BestRating, it.Rating));
                }
                else map[c] = (1, it.Id, it.Rating);
            }
        }

        var list = map.Select(kv => (Tag: kv.Key, kv.Value.Count, kv.Value.BestId, kv.Value.BestRating))
            .OrderByDescending(x => x.Count).Take(400).ToList();

        People.Clear();
        var vocab = Vocabulary.Instance;
        foreach (var x in list)
        {
            People.Add(new PersonVm
            {
                Tag = x.Tag,
                Display = vocab.Display(x.Tag),
                Count = x.Count,
                AvatarImageId = PersonAvatars.TryGetValue(x.Tag, out var custom) ? custom : x.BestId,
            });
        }
        PeoplePreview.Clear();
        foreach (var p in People.Take(10)) PeoplePreview.Add(p);
    }

    // ---------------- 人物头像自定义 ----------------

    /// <summary>tag → image_id。没设过的角色用「最高分那张」。</summary>
    public Dictionary<string, long> PersonAvatars { get; } = new(StringComparer.Ordinal);

    private void LoadPersonAvatars()
    {
        PersonAvatars.Clear();
        try
        {
            using var c = _svc.Db.Cmd("SELECT tag,image_id FROM person_avatar");
            using var r = c.ExecuteReader();
            while (r.Read()) PersonAvatars[r.GetString(0)] = r.GetInt64(1);
        }
        catch { }
    }

    /// <summary>imageId = 0 表示恢复默认。</summary>
    public void SetPersonAvatar(string tag, long imageId)
    {
        if (string.IsNullOrEmpty(tag)) return;
        if (imageId <= 0)
        {
            using var c = _svc.Db.Cmd("DELETE FROM person_avatar WHERE tag=$t");
            c.Parameters.AddWithValue("$t", tag);
            c.ExecuteNonQuery();
            PersonAvatars.Remove(tag);
        }
        else
        {
            using var c = _svc.Db.Cmd(
                "INSERT INTO person_avatar(tag,image_id) VALUES($t,$i) ON CONFLICT(tag) DO UPDATE SET image_id=$i");
            c.Parameters.AddWithValue("$t", tag);
            c.Parameters.AddWithValue("$i", imageId);
            c.ExecuteNonQuery();
            PersonAvatars[tag] = imageId;
        }

        // 就地更新，不用整个重建
        foreach (var p in People.Where(p => p.Tag == tag))
            p.AvatarImageId = imageId > 0 ? imageId : FallbackAvatar(tag);
        foreach (var p in PeoplePreview.Where(p => p.Tag == tag))
            p.AvatarImageId = imageId > 0 ? imageId : FallbackAvatar(tag);
        StatusLeft = imageId > 0 ? "已设为头像" : "已恢复默认头像";
    }

    private long FallbackAvatar(string tag)
    {
        long best = 0;
        int bestRating = -1;
        foreach (var it in AllImages)
        {
            if (it.State == FileState.Deleted) continue;
            if (Array.IndexOf(it.Chars, tag) < 0) continue;
            if (it.Rating > bestRating) { bestRating = it.Rating; best = it.Id; }
        }
        return best;
    }

    private void BuildFolders()
    {
        // 先只统计「自己直接装着几张」，再往上滚加给父目录。
        // 根目录（""）不参与展示成总计，否则一行 4820 会误导。
        var exact = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in AllImages)
        {
            if (it.State == FileState.Deleted) continue;
            exact[it.Folder] = exact.GetValueOrDefault(it.Folder) + 1;
        }

        var total = new Dictionary<string, int>(exact, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in exact)
        {
            var f = kv.Key;
            while (f.Length > 0)
            {
                var i = f.LastIndexOf('/');
                f = i < 0 ? "" : f[..i];
                if (f.Length == 0) break;
                total[f] = total.GetValueOrDefault(f) + kv.Value;
            }
        }

        var nodes = new List<FolderVm>();
        foreach (var kv in total)
        {
            if (kv.Key.Length == 0) continue;
            var name = kv.Key[(kv.Key.LastIndexOf('/') + 1)..];
            nodes.Add(new FolderVm
            {
                Path = kv.Key,
                Display = name,
                Depth = kv.Key.Count(ch => ch == '/'),
                Count = kv.Value,
            });
        }

        // 根目录单独一行，只放真正躺在根上的图
        if (exact.TryGetValue("", out var rootCount) && rootCount > 0)
        {
            nodes.Add(new FolderVm { Path = "", Display = "（根目录）", Depth = 0, Count = rootCount });
        }

        nodes.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

        Folders.Clear();
        foreach (var n in nodes) Folders.Add(n);
        OnPropertyChanged(nameof(LibrarySummary));
        OnPropertyChanged(nameof(DeletedCount));
    }

    public int DeletedCount => AllImages.Count(x => x.State == FileState.Deleted);

    // ================= 视图 =================

    /// <summary>
    /// 切视图前先退出放大模式。不这样的话在放大页点左栏只是把后面的网格换了，
    /// 用户还得再点一次返回。
    /// </summary>
    private void LeaveViewer()
    {
        if (!ViewerOpen) return;
        ViewerOpen = false;
        ViewerImage = null;
        VsrBadge = "";
        Current = null;
    }

    [RelayCommand]
    private void ShowOverview()
    {
        LeaveViewer();
        View = ViewKind.Overview;
        Title = "总览";
        RefreshGrid();
    }

    [RelayCommand]
    private void ShowDeleted()
    {
        LeaveViewer();
        View = ViewKind.Deleted;
        Title = "已删除";
        RefreshGrid();
    }

    [RelayCommand]
    private void ShowColumn(ColumnVm? col)
    {
        if (col is null) return;
        LeaveViewer();
        View = ViewKind.Column;
        ViewColumnId = col.Id;
        Title = col.Name;
        RefreshGrid();
    }

    public void ShowFolder(FolderVm? f)
    {
        if (f is null) return;
        LeaveViewer();
        View = ViewKind.Folder;
        ViewFolder = f.Path;
        Title = f.Path.Length == 0 ? "根目录" : f.Path;
        RefreshGrid();
    }

    public void ShowPerson(PersonVm? p)
    {
        if (p is null) return;
        LeaveViewer();
        View = ViewKind.Person;
        ViewPerson = p.Tag;
        Title = p.Display;
        RefreshGrid();
    }

    public void ShowPersonByTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return;
        var p = People.FirstOrDefault(x => x.Tag == tag);
        LeaveViewer();
        View = ViewKind.Person;
        ViewPerson = tag;
        Title = p?.Display ?? Vocabulary.Instance.Display(tag);
        RefreshGrid();
    }

    partial void OnSearchTextChanged(string value) => RefreshGrid();

    public IEnumerable<FilterNode> ActiveChips()
    {
        if (View == ViewKind.Column)
        {
            var col = Columns.FirstOrDefault(c => c.Id == ViewColumnId);
            if (col?.Tree is FilterGroup g)
                foreach (var c in g.Children) yield return c;
            else if (col is not null) yield return col.Tree;
        }
    }

    public FilterContext MakeContext()
    {
        _similar ??= SimilarityIndex.Build(AllImages);
        return new FilterContext { SimilarGroupIds = _similar.InGroup };
    }

    private SimilarityIndex? _similar;

    public FilterNode? CurrentTree()
    {
        FilterGroup root = new() { Op = BoolOp.And };

        switch (View)
        {
            case ViewKind.Column:
                {
                    var col = Columns.FirstOrDefault(c => c.Id == ViewColumnId);
                    if (col is not null) root.Children.Add(col.Tree);
                    break;
                }
            case ViewKind.Folder:
                // 点日期目录要把子目录里的图一起算进来，否则上面写着 62 张、
                // 点进去只有 3 张，很容易让人以为图丢了。
                root.Children.Add(new FilterGroup
                {
                    Op = BoolOp.Or,
                    Children =
                    {
                        new FilterCond { Field = FieldType.Folder, Op = CondOp.Eq, Value = ViewFolder },
                        new FilterCond
                        {
                            Field = FieldType.Folder,
                            Op = CondOp.StartsWith,
                            Value = ViewFolder.Length == 0 ? "" : ViewFolder + "/",
                        },
                    },
                });
                break;
            case ViewKind.Person:
                root.Children.Add(new FilterCond { Field = FieldType.Character, Op = CondOp.Eq, Value = ViewPerson });
                break;
            case ViewKind.Deleted:
                root.Children.Add(new FilterCond { Field = FieldType.Deleted, Op = CondOp.IsTrue });
                break;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var s = SearchText.Trim();
            root.Children.Add(new FilterGroup
            {
                Op = BoolOp.Or,
                Children =
                {
                    new FilterCond { Field = FieldType.Prompt, Op = CondOp.Contains, Value = s },
                    new FilterCond { Field = FieldType.Negative, Op = CondOp.Contains, Value = s },
                    new FilterCond { Field = FieldType.FileName, Op = CondOp.Contains, Value = s },
                    new FilterCond { Field = FieldType.Model, Op = CondOp.Contains, Value = s },
                    new FilterCond { Field = FieldType.Note, Op = CondOp.Contains, Value = s },
                },
            });
        }

        return root.Children.Count == 0 ? null : root;
    }

    public void RefreshGrid()
    {
        var tree = CurrentTree();
        var ctx = MakeContext();
        var list = tree is null ? [.. AllImages] : ColumnEngine.Filter(AllImages, tree, ctx);

        if (View == ViewKind.Deleted)
            list = list.Where(x => x.State == FileState.Deleted).ToList();
        else
            list = list.Where(x => x.State != FileState.Deleted).ToList();

        ColumnEngine.Sort(list, Sort);

        var vms = new List<ImageVm>(list.Count);
        foreach (var it in list)
            if (VmById.TryGetValue(it.Id, out var vm)) vms.Add(vm);

        Items.Clear();
        foreach (var v in vms) Items.Add(v);

        HitCount = Items.Count;
        IsEmpty = Items.Count == 0;
        OnPropertyChanged(nameof(Chips));
        GridReloaded?.Invoke();
        RefreshStatus();
    }

    [ObservableProperty] private bool _isEmpty;

    public List<string> Chips
    {
        get
        {
            var l = new List<string>();
            foreach (var n in ActiveChips()) l.Add(n is FilterCond c ? c.Describe() : n.ToExpression());
            if (!string.IsNullOrWhiteSpace(SearchText)) l.Add($"搜索 “{SearchText.Trim()}”");
            return l;
        }
    }

    private void RefreshStatus()
    {
        StatusRight = $"命中 {HitCount} / {TotalCount}";
        StatusLeft = Selection.Count == 0 ? "" : $"已选 {Selection.Count} 张";
        if (View == ViewKind.Deleted) StatusRight = $"已删除 {HitCount} 张";
    }

    public void RefreshSelectionStatus()
        => StatusLeft = Selection.Count == 0 ? "" : $"已选 {Selection.Count} 张";

    [RelayCommand]
    private void SetSort(string key)
    {
        var parts = key.Split('|');
        if (!Enum.TryParse<SortKey>(parts[0], out var k)) return;
        bool desc = parts.Length < 2 || parts[1] == "1";
        Sort = new SortSpec(k, desc);
        SortLabel = $"{ColumnEngine.SortLabel(k)} · {(desc ? "新/大在前" : "旧/小在前")}";
        RefreshGrid();
    }

    // ================= 评分 / 删除 =================

    public void SetRating(IEnumerable<ImageVm> vms, int rating)
    {
        // 每个 VM 的 Rating setter 自己会落库（ImageVm.RatingPersist）
        foreach (var vm in vms) vm.Rating = rating;
        RefreshStatus();
    }

    public void MoveToDeleted(IEnumerable<ImageVm> vms)
    {
        var list = vms.ToList();
        using var tx = _svc.Db.Connection.BeginTransaction();
        foreach (var vm in list)
        {
            vm.Item.State = FileState.Deleted;
            _store.UpdateState(vm.Item);
        }
        tx.Commit();
        Selection.RemoveAll(x => list.Contains(x));
        // 直接重算列表：删/还原都会改变「当前视图该有哪些图」，
        // 只 Remove 不移回来的话，还原之后图就从这个视图里消失了。
        TotalCount = AllImages.Count(x => x.State != FileState.Deleted);
        RefreshGrid();
        _ = RunColumnCountsAsync();
    }

    public void RestoreFromDeleted(IEnumerable<ImageVm> vms)
    {
        var list = vms.ToList();
        using var tx = _svc.Db.Connection.BeginTransaction();
        foreach (var vm in list)
        {
            vm.Item.State = FileState.Present;
            _store.UpdateState(vm.Item);
        }
        tx.Commit();
        Selection.RemoveAll(x => list.Contains(x));
        TotalCount = AllImages.Count(x => x.State != FileState.Deleted);
        RefreshGrid();
        _ = RunColumnCountsAsync();
    }

    // ================= 查看器 =================

    [RelayCommand]
    private async Task OpenViewer(ImageVm? vm)
    {
        if (vm is null) return;
        Current = vm;
        ViewerOpen = true;
        ShowOriginal = false;
        VsrBadge = "";
        WorkflowText = "";
        _workflowLoadedFor = 0;
        await LoadViewerImageAsync(vm);
    }

    private ImageSourceHolder? _origSource;

    public async Task LoadViewerImageAsync(ImageVm vm)
    {
        ViewerLoading = true;
        try
        {
            var path = vm.Item.Path;
            var bmp = await Task.Run(() => ThumbService.DecodeFile(path));
            _origSource = bmp is null ? null : new ImageSourceHolder(bmp);
            ViewerImage = _origSource;
            ShowOriginal = true;
            VsrBadge = "";
            if (AlwaysVsr && _svc.Vsr.Ready) await ApplyVsrAsync();
        }
        finally { ViewerLoading = false; }
    }

    [ObservableProperty] private bool _alwaysVsr;
    [ObservableProperty] private bool _vsrBusy;

    partial void OnAlwaysVsrChanged(bool value) => _svc.Store.SetSetting("always_vsr", value ? "1" : "0");

    public bool VsrReady => _svc.Vsr.Ready;

    /// <summary>当前图跑一遍 RTX VSR。结果只留内存。</summary>
    public async Task ApplyVsrAsync()
    {
        if (Current is null) return;
        if (!_svc.Vsr.Ready)
        {
            VsrBadge = _svc.Vsr.Status;
            return;
        }

        var cached = _svc.Vsr.Peek(Current.Id);
        if (cached is not null)
        {
            ViewerImage = new ImageSourceHolder(cached);
            ShowOriginal = false;
            VsrBadge = $"RTX VSR · 2× · {cached.PixelWidth}×{cached.PixelHeight}";
            return;
        }

        VsrBusy = true;
        VsrBadge = "RTX VSR 处理中…";
        try
        {
            var vm = Current;
            var bmp = await _svc.Vsr.ProcessAsync(vm.Item);
            if (Current?.Id != vm.Id) return;
            if (bmp is null)
            {
                VsrBadge = "RTX VSR 没成功（图太大或显存不够）";
                return;
            }
            ViewerImage = new ImageSourceHolder(bmp);
            ShowOriginal = false;
            VsrBadge = $"RTX VSR · 2× · {bmp.PixelWidth}×{bmp.PixelHeight} · {_svc.Vsr.Engine.LastRunMs:F0} ms";
        }
        finally
        {
            VsrBusy = false;
        }
    }

    [RelayCommand]
    private async Task VsrProcess()
    {
        await ApplyVsrAsync();
    }

    [RelayCommand]
    private void ToggleAlwaysVsr()
    {
        AlwaysVsr = !AlwaysVsr;
        if (AlwaysVsr) _ = ApplyVsrAsync();
    }

    private async Task LoadWorkflowAsync(ImageVm vm)
    {
        _workflowLoadedFor = vm.Id;
        WorkflowText = "读取中…";
        var raw = await Task.Run(() =>
        {
            var gz = _store.GetWorkflow(vm.Id);
            return PngReader.Gunzip(gz) ?? "";
        });
        if (Current?.Id != vm.Id) return;
        WorkflowText = raw.Length == 0 ? "这张图没有内嵌工作流。" : Pretty(raw);
    }

    private static string Pretty(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch { return json; }
    }

    [RelayCommand]
    private void CloseViewer()
    {
        ViewerOpen = false;
        Current = null;
        ViewerImage = null;
        ViewerClosed?.Invoke();
    }

    [RelayCommand]
    private async Task NextImage()
    {
        var i = Items.IndexOf(Current!);
        if (i < 0 || i + 1 >= Items.Count) return;
        await OpenViewer(Items[i + 1]);
    }

    [RelayCommand]
    private async Task PrevImage()
    {
        var i = Items.IndexOf(Current!);
        if (i <= 0) return;
        await OpenViewer(Items[i - 1]);
    }

    [RelayCommand]
    private void ToggleOriginal()
    {
        if (ShowOriginal) { _ = ApplyVsrAsync(); return; }
        ViewerImage = _origSource;
        ShowOriginal = true;
    }

    // ================= 杂项 =================

    [RelayCommand]
    private void RevealInExplorer(ImageVm? vm)
    {
        if (vm is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{vm.Item.Path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch { }
    }

    public void CopyPrompt(ImageVm? vm) => CopyText(vm?.Prompt);
    public void CopyNegative(ImageVm? vm) => CopyText(vm?.Negative);
    public void CopyParams(ImageVm? vm)
    {
        if (vm is null) return;
        var it = vm.Item;
        CopyText($"模型 {it.Model ?? "—"}\n采样器 {it.Sampler ?? "—"} / {it.Scheduler ?? "—"}\n步数 {it.Steps?.ToString() ?? "—"}  CFG {it.Cfg?.ToString("0.##") ?? "—"}  种子 {it.Seed?.ToString() ?? "—"}\n{it.Resolution}");
    }

    [RelayCommand]
    private void ShowStats() => StatsRequested?.Invoke();

    public event Action? StatsRequested;

    public void SetTileSize(double v) => TileSize = Math.Clamp(v, 100, 420);

    [RelayCommand]
    private void OpenColumnEditor(ColumnVm? col) => ColumnEditorRequested?.Invoke(col);

    public event Action<ColumnVm?>? ColumnEditorRequested;

    public void SaveColumnFromEditor(ColumnVm? existing, string name, FilterNode tree)
    {
        if (existing is null) _ = AddColumnAsync(name, tree);
        else { UpdateColumn(existing.Id, name, tree); _ = RunColumnCountsAsync(); RefreshGrid(); }
    }
}



