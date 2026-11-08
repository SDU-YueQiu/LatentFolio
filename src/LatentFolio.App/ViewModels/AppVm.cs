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
using LatentFolio.Core.Imaging;
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

    // 这两个必须是属性：WPF 绑定不认 public 字段，写成字段界面上就是空的
    public string Tag { get; set; } = "";

    /// <summary>卡片副标题：中文名和 tag 不一样时显示原始 tag，一样时留空。</summary>
    public string TagSub { get; set; } = "";

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
    [ObservableProperty] private bool _expanded;
    [ObservableProperty] private bool _visible = true;
    [ObservableProperty] private bool _hasChildren;
    public long NewestMTime;
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

    /// <summary>网格数据源。用批量替换版：一次 Reset 代替几千次 Add 通知。</summary>
    public RangeObservableCollection<ImageVm> Items { get; } = [];

    // 性能观测：自检会把这几项打出来
    public long LastRefreshMs;
    public long LastReloadMs;
    public long LastPeopleMs;
    public long LastFoldersMs;
    public long LastInitMs;

    public ObservableCollection<ColumnVm> Columns { get; } = [];
    public ObservableCollection<PersonVm> People { get; } = [];
    public ObservableCollection<PersonVm> PeoplePreview { get; } = [];
    public ObservableCollection<FolderVm> Folders { get; } = [];

    /// <summary>胶片条：当前这张前后的一批图。高度可调，每张宽度按原图比例。</summary>
    public ObservableCollection<ImageVm> Filmstrip { get; } = [];

    [ObservableProperty] private double _filmHeight = 96;

    partial void OnFilmHeightChanged(double value)
    {
        _store.SetSetting("film_height", ((int)Math.Round(value)).ToString());
        OnPropertyChanged(nameof(FilmImageHeight));
    }

    /// <summary>去掉内边距和横向滚动条之后，胶片条里一张图实际的高度。</summary>
    public double FilmImageHeight => Math.Max(24, FilmHeight - 32);

    /// <summary>角色/画师全量计数（人物墙只显示前 400，标签体系要全的）。</summary>
    public Dictionary<string, int> CharCounts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ArtistCounts { get; } = new(StringComparer.Ordinal);

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
    [ObservableProperty] private double _tileSize = 176;

    /// <summary>缩略图 4 档（设计稿 §11.1「密度可调」）。</summary>
    public static readonly double[] TileSizes = [136, 188, 252, 330];

    [ObservableProperty] private int _tileStep = 1;

    partial void OnTileStepChanged(int value)
    {
        int i = Math.Clamp(value, 0, TileSizes.Length - 1);
        TileSize = TileSizes[i];
        _store.SetSetting("tile_step", i.ToString());
    }

    [ObservableProperty] private ViewKind _view = ViewKind.Overview;
    [ObservableProperty] private long _viewColumnId;
    [ObservableProperty] private string _viewFolder = "";
    [ObservableProperty] private string _viewPerson = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private double _busyFraction;
    [ObservableProperty] private string _sortLabel = "添加时间 · 新在前";

    public SortSpec Sort { get; private set; } = new(SortKey.Date, true);

    private LibraryWatcher? _watcher;

    /// <summary>目录监视永远开着 —— 它就是「自动导入新图」，没有关掉的理由。</summary>
    [ObservableProperty] private bool _watchEnabled = true;

    partial void OnWatchEnabledChanged(bool value)
    {
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
            _watcher.Removed += OnWatcherRemoved;
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
            _engine.Invalidate();
            _ = RunColumnCountsAsync();
            BuildPeople();
            // 头像表要先生成好，RefreshTags 才能拿到每个角色的头像
            foreach (var vm in VmById.Values) vm.RefreshTags(AvatarFor);
            BuildFolders();
            RefreshGrid();
            Selection.Clear();
            foreach (var vm in Items) if (selected.Contains(vm.Id)) Selection.Add(vm);
            StatusLeft = $"自动导入 {count} 张";
        });
    }

    /// <summary>
    /// 在资源管理器里删/改名 → 库里这些图标成「源文件丢失」，从图墙消失、进「已删除」。
    /// </summary>
    private void OnWatcherRemoved(int count)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            var selected = Selection.Select(x => x.Id).ToHashSet();
            ReloadFromDb();
            _engine.Invalidate();
            _ = RunColumnCountsAsync();
            BuildPeople();
            foreach (var vm in VmById.Values) vm.RefreshTags(AvatarFor);
            BuildFolders();
            RefreshGrid();
            Selection.Clear();
            foreach (var vm in Items) if (selected.Contains(vm.Id)) Selection.Add(vm);
            StatusLeft = $"{count} 张源文件丢失（已移到「已删除」）";
        });
    }

    public double TileHeight => TileSize + 46;

    partial void OnTileSizeChanged(double value) => OnPropertyChanged(nameof(TileHeight));

    public string LibrarySummary
    {
        get
        {
            int folders = Folders.Count(f => f.Depth > 0 && f.Path.Length > 0);
            int n = AllImages.Count(x => x.State == FileState.Present);
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
        RaiseDetailWidth();
        RebuildFilmstrip();
        CurrentChangedForViewer?.Invoke();
    }

    [ObservableProperty] private string _promptEditorText = "";
    [ObservableProperty] private string _workflowText = "";
    [ObservableProperty] private string _workflowHeader = "工作流";

    // ================= 右侧详情栏 =================

    /// <summary>统计门槛：条目至少出现这么多张才参与排名，0 = 全部。</summary>
    [ObservableProperty] private int _statsThreshold = 5;

    partial void OnStatsThresholdChanged(int value)
        => _store.SetSetting("stats_threshold", value.ToString());

    /// <summary>VSR 默认放大倍数，2 或 4（4 是跑两遍 2×）。</summary>
    [ObservableProperty] private int _vsrScale = 2;

    partial void OnVsrScaleChanged(int value)
    {
        _store.SetSetting("vsr_scale", value.ToString());
        if (_svc is not null) _svc.Vsr.Scale = value;
    }

    [ObservableProperty] private bool _panelCollapsed;
    [ObservableProperty] private bool _isPanelVisible;

    /// <summary>右栏占的宽度。查看器里用它做可折叠的列宽，折叠时是 0。</summary>
    public double DetailWidth => IsPanelVisible ? 380 : 0;

    /// <summary>左栏折叠把手的位置：贴着左栏和网格之间那道缝的中点。</summary>
    public Thickness LeftHandleMargin => new(LeftWidth - 11, 0, 0, 0);

    /// <summary>详情栏折叠把手：贴着面板左缘；面板收起时贴窗口右边。</summary>
    public Thickness DetailHandleMargin => new(0, 0, DetailWidth > 0 ? DetailWidth - 11 : 0, 0);

    private void RaiseDetailWidth()
    {
        OnPropertyChanged(nameof(DetailWidth));
        OnPropertyChanged(nameof(DetailHandleMargin));
    }

    partial void OnPanelCollapsedChanged(bool value)
    {
        IsPanelVisible = Current is not null && !value;
        RaiseDetailWidth();
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
        var swInit = Stopwatch.StartNew();
        LibraryRoot = _svc.LibraryRoot;
        AlwaysVsr = _store.GetSetting("always_vsr") == "1";
        WatchEnabled = true;
        PanelCollapsed = _store.GetSetting("panel_collapsed") == "1";
        LeftCollapsed = _store.GetSetting("left_collapsed") == "1";
        if (double.TryParse(_store.GetSetting("left_width"), out var lw) && lw >= 180)
        {
            _leftExpanded = Math.Clamp(lw, 180, 460);
            // 直接改字段不会发通知，绑定在 XAML 载入时已经用默认值算过一次了，这里补上
            OnPropertyChanged(nameof(LeftWidth));
            RaisePeopleCard();
        }
        if (double.TryParse(_store.GetSetting("film_height"), out var fh) && fh >= 40)
            FilmHeight = Math.Clamp(fh, 48, 300);
        FolderDesc = _store.GetSetting("folder_desc") == "1";
        StatsThreshold = int.TryParse(_store.GetSetting("stats_threshold"), out var st) ? st : 5;
        VsrScale = int.TryParse(_store.GetSetting("vsr_scale"), out var vs) && vs == 4 ? 4 : 2;
        _svc.Vsr.Scale = VsrScale;
        _svc.Scanner.ArchiveThumbsEnabled = _store.GetSetting("archive_thumbs") != "0";
        _svc.Scanner.DedupeByHash = _store.GetSetting("dedupe") != "0";
        if (int.TryParse(_store.GetSetting("tile_step"), out var ts))
            TileStep = Math.Clamp(ts, 0, TileSizes.Length - 1);
        TileSize = TileSizes[TileStep];
        if (int.TryParse(_store.GetSetting("folder_sort"), out var fs))
            FolderSort = (FolderSortKind)Math.Clamp(fs, 0, 2);
        if (int.TryParse(_store.GetSetting("left_cols_h"), out var lch) && lch >= 88)
            LeftColumnsHeight = Math.Clamp(lch, 88, 900);
        if (int.TryParse(_store.GetSetting("left_people_h"), out var lph) && lph >= 64)
            LeftPeopleHeight = Math.Clamp(lph, 64, 900);
        _sortKeyIndex = int.TryParse(_store.GetSetting("grid_sort_key"), out var gsk) ? Math.Clamp(gsk, 0, 4) : 0;
        _sortDesc = _store.GetSetting("grid_sort_desc") != "0";
        ApplySort();
        LoadAliases();
        LoadPersonAvatars();
        LoadPersonMeta();
        // VSR 常驻：启动后台线程就把引擎加载好，第一次点右键不用等 3 秒
        _ = _svc.Vsr.StartAsync().ContinueWith(_ =>
        {
            OnPropertyChanged(nameof(VsrReady));
            OnPropertyChanged(nameof(VsrStatus));
            OnPropertyChanged(nameof(VsrStarted));
        }, TaskScheduler.FromCurrentSynchronizationContext());

        ReloadFromDb();
        await EnsureSeed();
        await RunColumnCountsAsync();
        BuildPeople();
        foreach (var vm in VmById.Values) vm.RefreshTags(AvatarFor);
        BuildFolders();
        await ScanAsync(initial: true);

        // 一次性把「同一个人、不同写法」合并掉（写自动别名 + 重打标）
        if (_store.GetSetting("merge_version") != MergeVersion.ToString())
        {
            int merged = MergeDuplicateCharacters();
            if (merged > 0) RetagAll();
            _store.SetSetting("merge_version", MergeVersion.ToString());
        }

        RefreshGrid();
        StartWatcher();
        LastInitMs = swInit.ElapsedMilliseconds;
    }

    /// <summary>角色合并规则升过级就重跑一次。</summary>
    public const int MergeVersion = 3;

    public void ReloadFromDb()
    {
        var sw = Stopwatch.StartNew();
        // 重建 VM 会把 Current / Selection 换成悬空引用（自动导入、重新识别都会走到这里），
        // 按 id 接回来，否则右栏和图边条会突然只剩一张。
        long curId = Current?.Id ?? 0;
        var selIds = Selection.Select(x => x.Id).ToHashSet();

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
        TotalCount = AllImages.Count(x => x.State == FileState.Present);

        if (selIds.Count > 0)
        {
            Selection.Clear();
            foreach (var id in selIds) if (VmById.TryGetValue(id, out var vm)) Selection.Add(vm);
        }
        if (curId != 0 && VmById.TryGetValue(curId, out var cv)) Current = cv;

        LoadColumns();
        LastReloadMs = sw.ElapsedMilliseconds;
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
    public const int ParserVersion = 4;

    /// <summary>角色/画师识别逻辑改了就 +1，老库下次启动会整库重打标并重建候选池。</summary>
    public const int RetagVersion = 3;

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

            // 词表是后台加载的；扫描时可能还没好，这里补一次打标。
            // 判定词/别名逻辑升过级就整库过一遍，否则只补没打过标的。
            ReloadFromDb();
            await EnsureThumbPresetAsync();
            await BackfillDHashAsync();

            var vocab = Vocabulary.Instance;
            if (!vocab.Ready) await Task.Run(() => vocab.WaitReady(TimeSpan.FromMinutes(2)));
            bool fullRetag = _store.GetSetting("retag_version") != RetagVersion.ToString();
            int tagged = 0, candN = 0;
            await Task.Run(() =>
            {
                (tagged, candN) = _svc.Scanner.RetagAll(AllImages, onlyMissing: !fullRetag);
            });
            if (fullRetag) _store.SetSetting("retag_version", RetagVersion.ToString());

            var legacy = Path.Combine(_svc.AssetsDir, "legacy_ratings.tsv");
            ReloadFromDb();
            int imported = 0;
            if (_store.GetSetting("legacy_imported") != "1")
            {
                imported = _svc.Scanner.ImportLegacy(legacy, AllImages);
                _store.SetSetting("legacy_imported", "1");
                if (imported > 0) ReloadFromDb();
            }

            foreach (var vm in VmById.Values) vm.RefreshTags(AvatarFor);
            _engine.Invalidate();
            await RunColumnCountsAsync();
            BuildPeople();
            BuildFolders();
            RefreshGrid();

            StatusLeft = $"新增 {result.Added} · 更新 {result.Updated} · 未变 {result.Skipped}"
                         + (result.DupSkipped > 0 ? $" · 重复 {result.DupSkipped}" : "")
                         + (result.Removed > 0 ? $" · 丢失 {result.Removed}" : "")
                         + (imported > 0 ? $" · 导入旧评分 {imported}" : "");

            LastScanLost = result.Removed;
            if (initial && result.Removed > 0) LostFilesDetected?.Invoke(result.Removed);
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

    /// <summary>本次启动扫描发现的「源文件不见了」张数（留给主窗口弹窗问一句）。</summary>
    public int LastScanLost { get; private set; }

    /// <summary>启动扫描发现源文件丢失时抛出来，让界面问「标记为已删除还是丢失」。</summary>
    public event Action<int>? LostFilesDetected;

    /// <summary>把当前所有「丢失」的图直接改成「已删除」。</summary>
    public int MarkMissingAsDeleted()
    {
        var lost = AllImages.Where(x => x.State == FileState.Missing).ToList();
        foreach (var it in lost)
        {
            it.State = FileState.Deleted;
            _store.UpdateState(it);
        }
        if (lost.Count == 0) return 0;
        foreach (var it in lost) if (VmById.TryGetValue(it.Id, out var vm)) vm.RefreshState();
        _engine.Invalidate();
        _ = RunColumnCountsAsync();
        RefreshGrid();
        return lost.Count;
    }

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

    public async Task AddColumnAsync(string name, FilterNode tree, string color = "")
    {
        var json = tree.ToJson();
        using var c = _svc.Db.Cmd("INSERT INTO col(name,color,ord,tree_json,count) VALUES($n,$c,$o,$t,-1); SELECT last_insert_rowid();");
        c.Parameters.AddWithValue("$n", name);
        c.Parameters.AddWithValue("$c", color);
        c.Parameters.AddWithValue("$o", Columns.Count);
        c.Parameters.AddWithValue("$t", json);
        var id = (long)c.ExecuteScalar()!;
        LoadColumns();
        await RunColumnCountsAsync();
    }

    public void UpdateColumn(long id, string name, FilterNode tree, string? color = null)
    {
        using var c = _svc.Db.Cmd("UPDATE col SET name=$n, tree_json=$t, color=COALESCE($c,color), count=-1 WHERE id=$id");
        c.Parameters.AddWithValue("$n", name);
        c.Parameters.AddWithValue("$t", tree.ToJson());
        c.Parameters.AddWithValue("$c", (object?)color ?? DBNull.Value);
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

    /// <summary>老库的 dHash 是 0，相似图分不出来。这里补一遍，补完重建相似索引。</summary>
    private async Task BackfillDHashAsync()
    {
        int missing = AllImages.Count(x => x.DHash == 0 && x.State == FileState.Present);
        if (missing == 0) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        BusyText = $"补算相似指纹 · {missing:N0} 张";
        int n = await Task.Run(() => _svc.Scanner.BackfillDHash(AllImages));
        _similar = null;
        if (n > 0)
            StatusLeft = $"补算相似指纹 {n:N0} 张 · {sw.Elapsed.TotalSeconds:F0} 秒";
    }

    /// <summary>缩略图规格变了（比如分辨率翻倍）就把缓存整个重建一遍，只跑一次。</summary>
    private async Task EnsureThumbPresetAsync()
    {
        var want = Thumbnailer.GridSize.ToString();
        if (_store.GetSetting("thumb_preset") == want) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        BusyText = "重建缩略图…";
        _svc.ThumbService.ClearMemory();
        await Task.Run(() => _svc.Thumbs.ClearDiskAndMemory());
        int n = await Task.Run(() => _svc.Scanner.RebuildGridThumbs(AllImages));
        _store.SetSetting("thumb_preset", want);
        StatusLeft = $"缩略图重建 {n:N0} 张 · {sw.Elapsed.TotalSeconds:F0} 秒";
    }

    public long DataVersion()
    {
        long h = AllImages.Count;
        foreach (var it in AllImages) { unchecked { h = h * 31 + it.Id + it.Rating * 7; } }
        return h;
    }

    // ================= 人物 / 文件夹 =================

    /// <summary>角色 tag → 头像用的图片 id（自定义优先，否则最高分那张）。</summary>
    public long AvatarFor(string tag)
        => People.FirstOrDefault(p => p.Tag == tag)?.AvatarImageId ?? 0;

    private void BuildPeople()
    {
        var sw = Stopwatch.StartNew();
        var map = new Dictionary<string, (int Count, long BestId, int BestRating)>(StringComparer.Ordinal);
        CharCounts.Clear();
        ArtistCounts.Clear();
        foreach (var it in AllImages)
        {
            if (it.State == FileState.Deleted) continue;
            foreach (var c in it.Chars)
            {
                CharCounts[c] = CharCounts.GetValueOrDefault(c) + 1;
                if (map.TryGetValue(c, out var cur))
                {
                    map[c] = (cur.Count + 1, cur.BestRating >= it.Rating ? cur.BestId : it.Id,
                        Math.Max(cur.BestRating, it.Rating));
                }
                else map[c] = (1, it.Id, it.Rating);
            }
            foreach (var a in it.Artists) ArtistCounts[a] = ArtistCounts.GetValueOrDefault(a) + 1;
        }

        var list = map.Select(kv => (Tag: kv.Key, kv.Value.Count, kv.Value.BestId, kv.Value.BestRating))
            .OrderByDescending(x => x.Count).Take(400).ToList();

        People.Clear();
        var vocab = Vocabulary.Instance;
        foreach (var x in list)
        {
            var meta = PersonMeta.GetValueOrDefault(x.Tag);
            var display = meta is { NameZh.Length: > 0 } ? meta.NameZh : vocab.Display(x.Tag);
            People.Add(new PersonVm
            {
                Tag = x.Tag,
                Display = display,
                TagSub = display != x.Tag ? x.Tag : "",
                Count = x.Count,
                AvatarImageId = PersonAvatars.TryGetValue(x.Tag, out var custom) ? custom
                    : meta is { AvatarId: > 0 } ? meta.AvatarId : x.BestId,
            });
        }
        PeoplePreview.Clear();
        foreach (var p in People.Take(10)) PeoplePreview.Add(p);
        OnPropertyChanged(nameof(PeopleCountText));
        LastPeopleMs = sw.ElapsedMilliseconds;
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

        // 人物条目里也存一份，标签体系按人物查封面时用它
        try
        {
            using var c2 = _svc.Db.Cmd("UPDATE person SET avatar_id=$i WHERE tag=$t");
            c2.Parameters.AddWithValue("$i", imageId > 0 ? imageId : FallbackAvatar(tag));
            c2.Parameters.AddWithValue("$t", tag);
            c2.ExecuteNonQuery();
        }
        catch { }

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
        var sw = Stopwatch.StartNew();
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
        var newest = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in AllImages)
        {
            if (it.State == FileState.Deleted) continue;
            for (var f = it.Folder; ; f = f.Length == 0 ? "" : f[..Math.Max(0, f.LastIndexOf('/'))])
            {
                long cur = newest.GetValueOrDefault(f);
                if (it.MTime > cur) newest[f] = it.MTime;
                if (f.Length == 0) break;
            }
        }
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
                NewestMTime = newest.GetValueOrDefault(kv.Key),
            });
        }

        // 根目录单独一行，只放真正躺在根上的图
        FolderVm? rootRow = null;
        if (exact.TryGetValue("", out var rootCount) && rootCount > 0)
        {
            rootRow = new FolderVm
            {
                Path = "", Display = "（根目录）", Depth = 0, Count = rootCount,
                NewestMTime = newest.GetValueOrDefault(""),
            };
        }

        // 父子关系：谁有子目录才显示折叠三角
        var children = new Dictionary<string, List<FolderVm>>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes)
        {
            int i = n.Path.LastIndexOf('/');
            var parent = i < 0 ? "" : n.Path[..i];
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
            list.Add(n);
        }
        var parents = new HashSet<string>(children.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes) n.HasChildren = parents.Contains(n.Path);

        // 同级之间按「键 + 正/倒序」排，父目录永远在它自己的子树前面
        Comparison<FolderVm> cmp = FolderSort switch
        {
            FolderSortKind.Count => (a, b) => a.Count.CompareTo(b.Count),
            FolderSortKind.Date => (a, b) => a.NewestMTime.CompareTo(b.NewestMTime),
            _ => (a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase),
        };
        Comparison<FolderVm> order = (a, b) =>
        {
            int r = cmp(a, b);
            if (FolderDesc) r = -r;
            return r != 0 ? r : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        };

        var ordered = new List<FolderVm>(nodes.Count + 1);
        if (rootRow is not null) ordered.Add(rootRow);   // 根那一行在树顶
        void Walk(string p)
        {
            if (!children.TryGetValue(p, out var kids)) return;
            kids.Sort(order);
            foreach (var k in kids)
            {
                ordered.Add(k);
                Walk(k.Path);
            }
        }
        Walk("");
        // 兜底：万一有子树没被走到（数据结构异常），也别把它们丢了
        if (ordered.Count < nodes.Count)
        {
            var seen = new HashSet<string>(ordered.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var n in nodes.Where(n => !seen.Contains(n.Path)).OrderBy(n => n.Path, StringComparer.OrdinalIgnoreCase))
                ordered.Add(n);
        }

        Folders.Clear();
        foreach (var n in ordered) Folders.Add(n);
        ApplyFolderVisibility();
        OnPropertyChanged(nameof(LibrarySummary));
        OnPropertyChanged(nameof(DeletedCount));
        OnPropertyChanged(nameof(FolderSortLabel));
        LastFoldersMs = sw.ElapsedMilliseconds;
    }

    public string FolderSortLabel => (FolderSort switch
    {
        FolderSortKind.Count => "张数",
        FolderSortKind.Date => "最近更新",
        _ => "名称",
    }) + (FolderDesc ? " ▾" : " ▴");

    /// <summary>「已删除」那一栏里装的是：手动删的 + 源文件丢失的。</summary>
    public int DeletedCount => AllImages.Count(x => x.State is FileState.Deleted or FileState.Missing);

    public int MissingCount => AllImages.Count(x => x.State == FileState.Missing);

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
        ShowPeoplePane = false;
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
        ShowPeoplePane = false;
        View = ViewKind.Person;
        ViewPerson = tag;
        Title = p?.Display ?? Vocabulary.Instance.Display(tag);
        RefreshGrid();
    }

    partial void OnSearchTextChanged(string value) => RefreshGrid();

    public IEnumerable<FilterNode> ActiveChips()
    {
        if (TempFilter is FilterGroup tg)
            foreach (var c in tg.Children) yield return c;
        else if (TempFilter is not null) yield return TempFilter;

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
        return new FilterContext { SimilarGroupIds = _similar.InGroup, GroupSize = _similar.GroupSize };
    }

    private SimilarityIndex? _similar;

    public FilterNode? CurrentTree(bool includeTemp = true)
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

        if (includeTemp && TempFilter is not null) root.Children.Add(TempFilter);

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
        var sw = Stopwatch.StartNew();
        var tree = CurrentTree();
        var ctx = MakeContext();
        var list = tree is null ? [.. AllImages] : ColumnEngine.Filter(AllImages, tree, ctx);

        if (View == ViewKind.Deleted)
            list = list.Where(x => x.State is FileState.Deleted or FileState.Missing).ToList();
        else
            list = list.Where(x => x.State == FileState.Present).ToList();

        ColumnEngine.Sort(list, Sort);

        var vms = new List<ImageVm>(list.Count);
        foreach (var it in list)
            if (VmById.TryGetValue(it.Id, out var vm)) vms.Add(vm);

        Items.ReplaceAll(vms);

        HitCount = Items.Count;
        IsEmpty = Items.Count == 0;
        IsLibraryEmpty = TotalCount == 0;
        EmptyText = IsLibraryEmpty ? "图库还是空的，先把 ComfyUI 的 output 目录指过来" : "这里一张都没有";
        RebuildFilmstrip();
        OnPropertyChanged(nameof(Chips));
        GridReloaded?.Invoke();
        RefreshStatus();
        LastRefreshMs = sw.ElapsedMilliseconds;
    }

    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _isLibraryEmpty;
    [ObservableProperty] private string _emptyText = "这里一张都没有";

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

    /// <summary>总览排序：一个键 + 正/倒序，而不是把两种方向拆成两条菜单项。</summary>
    public static readonly string[] SortKeyNames = ["添加时间", "评分", "文件名", "分辨率", "文件大小"];

    private static readonly SortKey[] SortKeyValues =
        [SortKey.Date, SortKey.Rating, SortKey.Name, SortKey.Resolution, SortKey.Size];

    private int _sortKeyIndex;
    private bool _sortDesc = true;

    public int SortKeyIndex
    {
        get => _sortKeyIndex;
        set
        {
            int i = Math.Clamp(value, 0, SortKeyNames.Length - 1);
            if (_sortKeyIndex == i) return;
            _sortKeyIndex = i;
            _store.SetSetting("grid_sort_key", i.ToString());
            ApplySort();
            RefreshGrid();
        }
    }

    public bool SortDesc
    {
        get => _sortDesc;
        set
        {
            if (_sortDesc == value) return;
            _sortDesc = value;
            _store.SetSetting("grid_sort_desc", value ? "1" : "0");
            ApplySort();
            OnPropertyChanged(nameof(SortDirGlyph));
            RefreshGrid();
        }
    }

    /// <summary>方向按钮上的箭头。</summary>
    public string SortDirGlyph => _sortDesc ? "▾" : "▴";

    /// <summary>状态栏/无障碍用的一句话，比如「文件名 · Z-A」。</summary>
    public string SortSummary => $"{SortKeyNames[_sortKeyIndex]} · {DirWord(_sortKeyIndex, _sortDesc)}";

    public static string DirWord(int keyIndex, bool desc) => keyIndex switch
    {
        0 => desc ? "新在前" : "旧在前",
        1 => desc ? "高在前" : "低在前",
        2 => desc ? "Z-A" : "A-Z",
        _ => desc ? "大在前" : "小在前",
    };

    /// <summary>下拉框的 5 个键 → 排序键。启动恢复时不重排网格，只把状态摆好。</summary>
    private void ApplySort()
    {
        Sort = new SortSpec(SortKeyValues[_sortKeyIndex], _sortDesc);
        SortLabel = SortSummary;
    }

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
        // 删之前先留一张 256 的归档缩略图，之后原图没了也还能看见
        if (_svc.Scanner.ArchiveThumbsEnabled)
        {
            foreach (var vm in list) ArchiveThumbs.Ensure(_svc.Thumbs, vm.Item);
        }

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
        TotalCount = AllImages.Count(x => x.State == FileState.Present);
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
        TotalCount = AllImages.Count(x => x.State == FileState.Present);
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
    /// <summary>状态胶囊只写"RTX VSR 2×"，别堆分辨率耗时（设计稿 §12.7 明确要求）。</summary>
    private string VsrBadgeText() => (AlwaysVsr ? "☑ " : "") + $"RTX VSR {VsrScale}×";

    [ObservableProperty] private bool _vsrBusy;

    partial void OnAlwaysVsrChanged(bool value) => _svc.Store.SetSetting("always_vsr", value ? "1" : "0");

    /// <summary>能不能用 VSR。引擎没加载也算能用（按需加载），只要依赖文件在。</summary>
    public bool VsrReady => _svc.Vsr.Ready || _svc.Vsr.Capable;

    /// <summary>引擎是否真的加载了（加载过一次才会显示「就绪」）。</summary>
    public bool VsrStarted => _svc.Vsr.Started;

    /// <summary>当前图跑一遍 RTX VSR。结果只留内存。</summary>
    public async Task ApplyVsrAsync()
    {
        if (Current is null) return;
        if (!VsrReady)
        {
            VsrBadge = _svc.Vsr.Status;
            return;
        }

        var cached = _svc.Vsr.Peek(Current.Id);
        if (cached is not null)
        {
            ViewerImage = new ImageSourceHolder(cached);
            ShowOriginal = false;
            VsrBadge = VsrBadgeText();
            return;
        }

        VsrBusy = true;
        VsrBadge = "处理中…";
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
            VsrBadge = VsrBadgeText();
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
        if (raw.Length == 0)
        {
            WorkflowText = "这张图没有内嵌工作流。";
            WorkflowHeader = "工作流";
            return;
        }
        WorkflowHeader = $"工作流 · {CountNodes(raw):N0} 个节点";
        WorkflowText = Pretty(raw);
    }

    /// <summary>UI 格式数 nodes 数组，API 格式数顶层键。</summary>
    private static int CountNodes(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (root.TryGetProperty("nodes", out var nodes)
                    && nodes.ValueKind == System.Text.Json.JsonValueKind.Array)
                    return nodes.GetArrayLength();
                int n = 0;
                foreach (var _ in root.EnumerateObject()) n++;
                return n;
            }
        }
        catch { }
        return 0;
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

    public void CopyWorkflow()
    {
        if (WorkflowText.Length == 0 || WorkflowText.StartsWith("这张图没有")) return;
        CopyText(WorkflowText);
    }

    [RelayCommand]
    private void CloseViewer()
    {
        ViewerOpen = false;
        ViewerImage = null;
        VsrBadge = "";
        // 退出放大回到网格：把右栏还给「当前选中的那张」，不然面板会莫名其妙空着
        Current = Selection.Count > 0 ? Selection[^1] : null;
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
    private void SetTileStep(string key)
    {
        if (int.TryParse(key, out var i)) TileStep = i;
    }

    // ================= 左栏（可拖动改宽 / 可收成 56px 图标轨） =================

    [ObservableProperty] private bool _leftCollapsed;

    /// <summary>展开时的宽度，拖分隔条改，记进设置。</summary>
    private double _leftExpanded = 248;

    public double LeftWidth => LeftCollapsed ? 56 : _leftExpanded;

    public void SetLeftWidth(double w)
    {
        var v = Math.Clamp(w, 180, 460);
        if (Math.Abs(v - _leftExpanded) < 0.5) return;
        _leftExpanded = v;
        OnPropertyChanged(nameof(LeftWidth));
        RaisePeopleCard();
        _store.SetSetting("left_width", ((int)Math.Round(v)).ToString());
    }

    partial void OnLeftCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(LeftWidth));
        RaisePeopleCard();
        _store.SetSetting("left_collapsed", value ? "1" : "0");
    }

    [RelayCommand]
    private void ToggleLeftRail() => LeftCollapsed = !LeftCollapsed;

    // ================= 左栏分区高度（拖动记进设置，下次开还在原地） =================

    /// <summary>左栏「我的栏」那一块的高度。</summary>
    [ObservableProperty] private double _leftColumnsHeight = 150;

    public void SetLeftColumnsHeight(double v)
    {
        var h = Math.Clamp(v, 88, 1200);
        if (Math.Abs(h - LeftColumnsHeight) < 0.5) return;
        LeftColumnsHeight = h;
        _store.SetSetting("left_cols_h", ((int)Math.Round(h)).ToString());
    }

    /// <summary>左栏「人物」那一块的高度（拖出来的值）。</summary>
    [ObservableProperty] private double _leftPeopleHeight = 118;

    partial void OnLeftPeopleHeightChanged(double value) => RaisePeopleCard();

    public void SetLeftPeopleHeight(double v)
    {
        var h = Math.Clamp(v, 64, 1200);
        if (Math.Abs(h - LeftPeopleHeight) < 0.5) return;
        LeftPeopleHeight = h;
        _store.SetSetting("left_people_h", ((int)Math.Round(h)).ToString());
    }

    /// <summary>
    /// 人物块：固定 5 列 2 行装 10 个，卡片是正方形，边长跟着栏宽走
    /// （栏拖宽 → 卡片变大 → 这一块自己长高，不会裁）。
    /// </summary>
    public int PeopleCols => 5;

    public double PeopleCardSide
    {
        get
        {
            double w = Math.Max(60, LeftWidth - 20);
            return Math.Max(16, Math.Floor(w / 5 - 4));
        }
    }

    /// <summary>整块的高度：两行 × (卡片 + 上下边距)。</summary>
    public double PeopleBlockHeight => 2 * (PeopleCardSide + 4);

    /// <summary>人物那一行实际占多高：至少能装下卡片，用户拖得更矮也不会裁。</summary>
    public double LeftPeopleRowHeight => Math.Max(LeftPeopleHeight, PeopleBlockHeight + 44);

    private void RaisePeopleCard()
    {
        OnPropertyChanged(nameof(PeopleCols));
        OnPropertyChanged(nameof(PeopleCardSide));
        OnPropertyChanged(nameof(PeopleBlockHeight));
        OnPropertyChanged(nameof(LeftPeopleRowHeight));
        OnPropertyChanged(nameof(LeftHandleMargin));
    }

    // ================= 临时筛选（顶部「筛选」按钮） =================

    [ObservableProperty] private FilterNode? _tempFilter;

    /// <summary>简易筛选选中的那一项，用来在菜单里打勾。</summary>
    [ObservableProperty] private string _tempFilterName = "";

    public bool HasTempFilter => TempFilter is not null;

    partial void OnTempFilterChanged(FilterNode? value)
    {
        if (value is null && TempFilterName.Length > 0) TempFilterName = "";
        OnPropertyChanged(nameof(HasTempFilter));
        RefreshGrid();
    }

    public void SetTempFilter(FilterNode? tree, string name = "")
    {
        TempFilterName = name;
        TempFilter = tree;
    }

    /// <summary>
    /// 当前视图（含搜索）但**不含**临时筛选的那批图。
    /// 角色/模型菜单要从这里统计：从筛选后的结果里统计的话，选了「模型 A」之后
    /// 菜单里就只剩 A 了，别的模型全被藏起来。
    /// </summary>
    public List<ImageItem> BaseItems()
    {
        var tree = CurrentTree(includeTemp: false);
        var ctx = MakeContext();
        var list = tree is null ? [.. AllImages] : ColumnEngine.Filter(AllImages, tree, ctx);
        return View == ViewKind.Deleted
            ? list.Where(x => x.State is FileState.Deleted or FileState.Missing).ToList()
            : list.Where(x => x.State == FileState.Present).ToList();
    }

    /// <summary>当前网格里出现的角色，按张数排序（简易筛选用）。</summary>
    public List<(string Tag, string Display, int Count)> CharsInView(int limit = 200)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var it in BaseItems())
            foreach (var c in it.Chars)
                map[c] = map.GetValueOrDefault(c) + 1;
        var v = Vocabulary.Instance;
        return map.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(limit)
            .Select(kv => (kv.Key, v.Display(kv.Key), kv.Value))
            .ToList();
    }

    /// <summary>当前视图里出现的模型。</summary>
    public List<(string Model, int Count)> ModelsInView(int limit = 60)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in BaseItems())
        {
            var m = it.Model;
            if (string.IsNullOrWhiteSpace(m)) continue;
            map[m] = map.GetValueOrDefault(m) + 1;
        }
        return map.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    [RelayCommand]
    private void ClearFilter()
    {
        TempFilterName = "";
        TempFilter = null;
    }

    public bool IsColumnView => View == ViewKind.Column;

    partial void OnViewChanged(ViewKind value)
    {
        OnPropertyChanged(nameof(IsColumnView));
        if (value != ViewKind.Person) ShowPeoplePane = false;
    }

    // ================= 人物面板（中间显示，不弹窗） =================

    [ObservableProperty] private bool _showPeoplePane;
    [ObservableProperty] private string _peopleQuery = "";
    [ObservableProperty] private int _peopleSort;
    [ObservableProperty] private bool _peopleOnlyZh;

    public ObservableCollection<PersonVm> PeopleView { get; } = [];

    public string PeopleCountText => $"{PeopleView.Count:N0} / {People.Count:N0}";

    partial void OnPeopleQueryChanged(string value) => ApplyPeopleFilter();
    partial void OnPeopleSortChanged(int value) => ApplyPeopleFilter();
    partial void OnPeopleOnlyZhChanged(bool value) => ApplyPeopleFilter();

    [RelayCommand]
    private void ShowPeopleGrid()
    {
        LeaveViewer();
        View = ViewKind.Person;
        ShowPeoplePane = true;
        Title = "人物";
        ApplyPeopleFilter();
    }

    [RelayCommand]
    private void ClosePeopleGrid() => ShowPeoplePane = false;

    public void ApplyPeopleFilter()
    {
        if (PeopleView is null) return;
        IEnumerable<PersonVm> src = People;
        if (PeopleOnlyZh) src = src.Where(p => p.Display != p.Tag);
        var q = PeopleQuery.Trim();
        if (q.Length > 0)
            src = src.Where(p => p.Display.Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || p.Tag.Contains(q, StringComparison.OrdinalIgnoreCase));
        src = PeopleSort switch
        {
            1 => src.OrderBy(p => p.Display, StringComparer.CurrentCulture),
            2 => src.OrderBy(p => p.Tag, StringComparer.Ordinal),
            _ => src.OrderByDescending(p => p.Count),
        };
        PeopleView.Clear();
        foreach (var p in src) PeopleView.Add(p);
        OnPropertyChanged(nameof(PeopleCountText));
    }

    // ================= 标签体系 =================

    /// <summary>人物条目（中文名/英文名/作品/简介/封面），tag → 行。</summary>
    public Dictionary<string, ImageStore.PersonRow> PersonMeta { get; } = new(StringComparer.Ordinal);

    public void LoadPersonMeta()
    {
        PersonMeta.Clear();
        foreach (var p in _store.LoadPeople()) PersonMeta[p.Tag] = p;
        var v = Vocabulary.Instance;
        v.NameOverride.Clear();
        foreach (var p in PersonMeta.Values)
            if (p.NameZh.Length > 0) v.NameOverride[p.Tag] = p.NameZh;
    }

    public void SavePersonMeta(ImageStore.PersonRow row)
    {
        row.Source = "manual";
        _store.SavePerson(row);
        PersonMeta[row.Tag] = row;
        var v = Vocabulary.Instance;
        if (row.NameZh.Length > 0) v.NameOverride[row.Tag] = row.NameZh;
        else v.NameOverride.Remove(row.Tag);

        foreach (var p in People.Where(p => p.Tag == row.Tag))
        {
            p.Display = v.Display(row.Tag);
            p.TagSub = p.Display != p.Tag ? p.Tag : "";
        }
        foreach (var p in PeoplePreview.Where(p => p.Tag == row.Tag))
        {
            p.Display = v.Display(row.Tag);
            p.TagSub = p.Display != p.Tag ? p.Tag : "";
        }
        foreach (var vm in VmById.Values) vm.RefreshTags(AvatarFor);
    }

    /// <summary>把别名表灌进词表，并读出被忽略的裸名。</summary>
    public void LoadAliases()
    {
        var v = Vocabulary.Instance;
        v.Alias.Clear();
        foreach (var kv in _store.LoadAliases()) v.Alias[kv.Key] = kv.Value;
        v.Ignored.Clear();
        foreach (var c in _store.LoadCandidates(2)) v.Ignored.Add(c.Key);
    }

    /// <summary>确认一个候选裸名：写别名表 → 全库重打标 → 池子里标成已确认。</summary>
    public void ConfirmCandidate(string key, string tag)
    {
        if (key.Length == 0 || tag.Length == 0) return;
        _store.UpsertAlias(key, tag);
        Vocabulary.Instance.Alias[key] = tag;
        Vocabulary.Instance.Ignored.Remove(key);
        _store.SetCandidateState(key, 1);
        RetagAll();
    }

    public void IgnoreCandidate(string key)
    {
        if (key.Length == 0) return;
        _store.SetCandidateState(key, 2);
        Vocabulary.Instance.Ignored.Add(key);
        RetagAll();
    }

    /// <summary>
    /// 「同一个人被认成不同关键词」：按 base 名（括号前那段）和词表英文名分组，
    /// 每组取张数最多的那个当正式 tag，其余写成自动别名，然后重打一遍标。
    /// 已有别名（手工确认过的）不动。
    /// </summary>
    private int MergeDuplicateCharacters()
    {
        var v = Vocabulary.Instance;
        if (!v.Ready || AllImages.Count == 0) return 0;

        // 用「没有别名时的原始识别结果」来算分组：库里存的 chars 可能已经被上一轮
        // 合并过了，直接拿它统计会看到一片干净、永远算不出重复
        var saved = new Dictionary<string, string>(v.Alias, StringComparer.Ordinal);
        v.Alias.Clear();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            foreach (var it in AllImages)
            {
                if (it.Prompt.Length == 0) continue;
                var (chars, _) = v.Tag(it.Prompt);
                foreach (var c in chars) counts[c] = counts.GetValueOrDefault(c) + 1;
            }
        }
        finally
        {
            v.Alias.Clear();
            foreach (var kv in saved) v.Alias[kv.Key] = kv.Value;
        }
        if (counts.Count == 0) return 0;

        _store.DeleteAutoAliases();
        LoadAliases();

        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        string Find(string x)
        {
            while (parent.TryGetValue(x, out var p) && p != x) x = p;
            return x;
        }
        void Union(string a, string b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb) return;
            int ca = counts.GetValueOrDefault(ra), cb = counts.GetValueOrDefault(rb);
            bool aWins = ca > cb || (ca == cb && ra.Length <= rb.Length);
            parent[aWins ? rb : ra] = aWins ? ra : rb;
        }

        foreach (var t in counts.Keys) parent[t] = t;

        var byBase = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var t in counts.Keys)
        {
            var b = Vocabulary.BaseName(t);
            if (!byBase.TryGetValue(b, out var l)) byBase[b] = l = [];
            l.Add(t);
            if (v.NameOf.TryGetValue(t, out var en) && en.Length > 0)
            {
                if (!byName.TryGetValue(en, out var l2)) byName[en] = l2 = [];
                l2.Add(t);
            }
        }

        foreach (var list in byBase.Values) for (int i = 1; i < list.Count; i++) Union(list[0], list[i]);
        foreach (var list in byName.Values) for (int i = 1; i < list.Count; i++) Union(list[0], list[i]);

        var existing = _store.LoadAliases();
        int n = 0;
        foreach (var t in counts.Keys)
        {
            var root = Find(t);
            if (root == t) continue;
            if (existing.ContainsKey(t)) continue;   // 手工别名优先
            _store.UpsertAlias(t, root, "auto");
            v.Alias[t] = root;
            n++;
        }
        return n;
    }

    /// <summary>手工把一个角色 tag 合并到另一个（写别名 + 重打标）。</summary>
    public void MergePerson(string from, string to)
    {
        if (from.Length == 0 || to.Length == 0 || from == to) return;
        _store.UpsertAlias(from, to, "manual");
        Vocabulary.Instance.Alias[from] = to;
        RetagAll();
    }

    /// <summary>
    /// 全库重新识别。确认别名、改词表之后跑。
    /// 会重建所有 ImageVm，所以选中项和当前图按 id 接回来，不然右栏会突然空掉。
    /// </summary>
    public void RetagAll()
    {
        var selIds = Selection.Select(x => x.Id).ToHashSet();
        long curId = Current?.Id ?? 0;
        bool viewer = ViewerOpen;

        var (n, c) = _svc.Scanner.RetagAll(AllImages);
        ReloadFromDb();
        foreach (var vm in VmById.Values) vm.RefreshTags(AvatarFor);
        _engine.Invalidate();
        _ = RunColumnCountsAsync();
        BuildPeople();
        BuildFolders();
        RefreshGrid();

        Selection.Clear();
        foreach (var id in selIds) if (VmById.TryGetValue(id, out var vm)) Selection.Add(vm);
        if (curId != 0 && VmById.TryGetValue(curId, out var cv)) Current = cv;
        if (viewer) ViewerOpen = true;
        RefreshSelectionStatus();
        StatusLeft = $"重新识别 {n:N0} 张 · 候选 {c:N0} 种";
    }

    // ================= 我的栏：改名/标记色/复制/上移下移/模板 =================

    public void RenameColumn(long id, string name)
    {
        if (name.Length == 0) return;
        using var c = _svc.Db.Cmd("UPDATE col SET name=$n WHERE id=$id");
        c.Parameters.AddWithValue("$n", name);
        c.Parameters.AddWithValue("$id", id);
        c.ExecuteNonQuery();
        LoadColumns();
        if (View == ViewKind.Column && ViewColumnId == id) Title = name;
    }

    public void SetColumnColor(long id, string color)
    {
        using var c = _svc.Db.Cmd("UPDATE col SET color=$c WHERE id=$id");
        c.Parameters.AddWithValue("$c", color);
        c.Parameters.AddWithValue("$id", id);
        c.ExecuteNonQuery();
        LoadColumns();
    }

    public void DuplicateColumn(long id)
    {
        var col = Columns.FirstOrDefault(x => x.Id == id);
        if (col is null) return;
        _ = AddColumnAsync(col.Name + " 副本", col.Tree.Clone());
    }

    public void MoveColumnBy(long id, int delta)
    {
        int i = Columns.ToList().FindIndex(c => c.Id == id);
        if (i < 0) return;
        int to = i + delta;
        if (to < 0 || to >= Columns.Count) return;
        MoveColumn(i, delta > 0 ? to + 1 : to);
    }

    public void SaveAsTemplate(string name, FilterNode tree)
    {
        if (name.Length == 0) return;
        _store.AddTemplate(name, tree.ToJson());
        StatusLeft = $"已存模板「{name}」";
    }

    public List<(long Id, string Name, string Json)> LoadTemplates() => _store.LoadTemplates();

    /// <summary>诊断：把内存构成打成一段文本。--mem-report 写进 %TEMP%\lf-mem.txt。</summary>
    public string BuildMemReport()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        var p = System.Diagnostics.Process.GetCurrentProcess();
        p.Refresh();
        var gi = GC.GetGCMemoryInfo();
        var (thumbCount, thumbBytes) = _thumbs.Stats;

        long chars = 0;
        foreach (var it in AllImages)
            chars += it.Path.Length + it.Folder.Length + it.FileName.Length + it.Prompt.Length
                   + it.Negative.Length + it.SearchText.Length + it.Note.Length;

        var sb = new System.Text.StringBuilder();
        void L(string s) => sb.AppendLine(s);

        L($"LatentFolio memory report   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        L("");
        L($"[process]  WS={p.WorkingSet64 >> 20} MB   private={p.PrivateMemorySize64 >> 20} MB   peakWS={p.PeakWorkingSet64 >> 20} MB   handles={p.HandleCount}   threads={p.Threads.Count}   modules={p.Modules.Count}");
        L($"[gc]       live={GC.GetTotalMemory(false) >> 20} MB   heapSize={gi.HeapSizeBytes >> 20} MB   committed={gi.TotalCommittedBytes >> 20} MB   fragmented={gi.FragmentedBytes >> 20} MB");
        L($"[gc.cfg]   server={System.Runtime.GCSettings.IsServerGC}   latency={System.Runtime.GCSettings.LatencyMode}   collect0={GC.CollectionCount(0)} collect1={GC.CollectionCount(1)} collect2={GC.CollectionCount(2)}");
        L($"[thumbs]   cached={thumbCount}   bytes={thumbBytes >> 20} MB   budget={Services.ThumbService.MemoryBudgetMb} MB");
        L($"[vsr]      capable={_svc.Vsr.Capable}   started={_svc.Vsr.Started}   cachedFrames={_svc.Vsr.CachedCount}");
        L($"[render]   tier={System.Windows.Media.RenderCapability.Tier >> 16}   swCaps={System.Windows.Media.RenderCapability.IsPixelShaderVersionSupported(2, 0)}");
        L($"[model]    images={AllImages.Count}   view={Items.Count}   people={People.Count}   columns={Columns.Count}   filmstrip={Filmstrip.Count}   selection={Selection.Count}");
        L($"[strings]  {chars:N0} chars  ≈ {chars * 2 >> 20} MB");
        return sb.ToString();
    }

    /// <summary>胶片条内容：以当前这张为中心取前后各 40 张（多了靠横向滚）。</summary>
    private void RebuildFilmstrip()
    {
        foreach (var vm in Filmstrip) vm.IsCurrent = false;
        Filmstrip.Clear();
        if (Current is null) return;
        int i = Items.IndexOf(Current);
        if (i < 0) { Current.IsCurrent = true; Filmstrip.Add(Current); return; }
        const int span = 40;
        int start = Math.Max(0, i - span);
        int end = Math.Min(Items.Count, i + span + 1);
        for (int k = start; k < end; k++) Filmstrip.Add(Items[k]);
        Current.IsCurrent = true;
    }

    /// <summary>当前图在胶片条里的位置，用来把它滚进视野。</summary>
    public int FilmstripIndex => Current is null ? -1 : Filmstrip.IndexOf(Current);

    // ================= 资源库：排序 / 折叠 =================

    public enum FolderSortKind { Name, Count, Date }

    [ObservableProperty] private FolderSortKind _folderSort = FolderSortKind.Name;

    /// <summary>排序方向。名称默认正序，张数/日期默认倒序。</summary>
    [ObservableProperty] private bool _folderDesc;

    partial void OnFolderSortChanged(FolderSortKind value)
    {
        _store.SetSetting("folder_sort", ((int)value).ToString());
        BuildFolders();
    }

    partial void OnFolderDescChanged(bool value)
    {
        _store.SetSetting("folder_desc", value ? "1" : "0");
        BuildFolders();
    }

    /// <summary>
    /// 记「展开」而不是「折叠」：默认一个都不展开，所以整棵树一开就是收起的。
    /// </summary>
    private readonly HashSet<string> _expandedFolders = new(StringComparer.OrdinalIgnoreCase);

    public bool IsFolderExpanded(string path) => _expandedFolders.Contains(path);

    public void ToggleFolder(FolderVm f)
    {
        if (!f.HasChildren) return;
        if (!_expandedFolders.Remove(f.Path)) _expandedFolders.Add(f.Path);
        ApplyFolderVisibility();
    }

    /// <summary>点一下目录行：选中它，同时把它展开（有子目录才展开）。</summary>
    public void ExpandFolder(FolderVm f)
    {
        if (!f.HasChildren || f.Expanded) return;
        _expandedFolders.Add(f.Path);
        ApplyFolderVisibility();
    }

    public void ExpandAllFolders()
    {
        foreach (var f in Folders) if (f.HasChildren) _expandedFolders.Add(f.Path);
        ApplyFolderVisibility();
    }

    public void CollapseAllFolders()
    {
        _expandedFolders.Clear();
        ApplyFolderVisibility();
    }

    private void ApplyFolderVisibility()
    {
        foreach (var f in Folders)
        {
            bool hidden = false;
            var p = f.Path;
            while (p.Length > 0)
            {
                int i = p.LastIndexOf('/');
                p = i < 0 ? "" : p[..i];
                if (p.Length == 0) break;
                if (!_expandedFolders.Contains(p)) { hidden = true; break; }
            }
            f.Visible = !hidden;
            f.Expanded = _expandedFolders.Contains(f.Path);
        }
    }

    // ================= 资源库：排序 / 折叠 =================

    [RelayCommand]
    private void OpenColumnEditor(ColumnVm? col) => ColumnEditorRequested?.Invoke(col);

    public event Action<ColumnVm?>? ColumnEditorRequested;

    public void SaveColumnFromEditor(ColumnVm? existing, string name, FilterNode tree, string color = "")
    {
        if (existing is null) _ = AddColumnAsync(name, tree, color);
        else { UpdateColumn(existing.Id, name, tree, color); _ = RunColumnCountsAsync(); RefreshGrid(); }
    }
}














