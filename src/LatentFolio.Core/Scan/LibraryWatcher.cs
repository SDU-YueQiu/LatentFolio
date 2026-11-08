using System.IO;

namespace LatentFolio.Core.Scan;

/// <summary>
/// 图库目录监视：ComfyUI 一出新图就自动入库，不用手动点扫描。
/// 用 FileSystemWatcher + 去抖，因为一次生成会连续抛好几个事件。
/// </summary>
public sealed class LibraryWatcher : IDisposable
{
    private readonly string _root;
    private readonly LibraryScanner _scanner;
    private readonly object _lock = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _gone = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _fsw;
    private Timer? _timer;
    private bool _busy;

    /// <summary>有图入库了（参数：本批导入数量）。</summary>
    public event Action<int>? Imported;

    /// <summary>有图的源文件不见了（参数：本批数量）。</summary>
    public event Action<int>? Removed;

    public bool Enabled { get; private set; }
    public int ImportedTotal { get; private set; }

    public LibraryWatcher(string root, LibraryScanner scanner)
    {
        _root = root;
        _scanner = scanner;
    }

    public void Start()
    {
        if (Enabled || !Directory.Exists(_root)) return;
        _fsw = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        _fsw.Created += OnChange;
        _fsw.Changed += OnChange;
        _fsw.Deleted += OnGone;
        // 改名 = 老路径没了 + 新路径来了；两边都要处理，否则删不掉老记录
        _fsw.Renamed += (s, e) =>
        {
            EnqueueGone(e.OldFullPath);
            Enqueue(e.FullPath);
        };
        _fsw.EnableRaisingEvents = true;
        _timer = new Timer(_ => Flush(), null, 1500, 1500);
        Enabled = true;
    }

    public void Stop()
    {
        if (_timer is not null) { _timer.Dispose(); _timer = null; }
        if (_fsw is not null) { _fsw.EnableRaisingEvents = false; _fsw.Dispose(); _fsw = null; }
        Enabled = false;
    }

    private void OnChange(object sender, FileSystemEventArgs e) => Enqueue(e.FullPath);

    private void OnGone(object sender, FileSystemEventArgs e) => EnqueueGone(e.FullPath);

    private void EnqueueGone(string path)
    {
        if (!LibraryScanner.IsImage(path)) return;
        lock (_lock) _gone.Add(path);
    }

    private void Enqueue(string path)
    {
        if (!LibraryScanner.IsImage(path)) return;
        lock (_lock) _pending.Add(path);
    }

    private void Flush()
    {
        List<string> batch;
        List<string> gone;
        lock (_lock)
        {
            if (_busy) return;
            if (_pending.Count == 0 && _gone.Count == 0) return;
            _busy = true;
            batch = [.. _pending];
            gone = [.. _gone];
            _pending.Clear();
            _gone.Clear();
        }

        try
        {
            // 删除是不需要等写盘的；先把"没了"处理掉，界面上那一格立刻就消失
            if (gone.Count > 0)
            {
                var ids = _scanner.MarkGone(gone);
                if (ids.Count > 0) Removed?.Invoke(ids.Count);
            }

            if (batch.Count == 0) return;
            // 文件可能还没写完，稍等一下再读
            Thread.Sleep(400);
            var n = _scanner.ImportPaths(_root, batch);
            if (n > 0)
            {
                ImportedTotal += n;
                Imported?.Invoke(n);
            }
        }
        catch
        {
            // 监视失败不该弄崩程序
        }
        finally
        {
            lock (_lock) _busy = false;
        }
    }

    public void Dispose() => Stop();
}
