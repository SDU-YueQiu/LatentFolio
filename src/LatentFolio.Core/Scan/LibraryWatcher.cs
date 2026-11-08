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
    private FileSystemWatcher? _fsw;
    private Timer? _timer;
    private bool _busy;

    /// <summary>有图入库了（参数：本批导入数量）。</summary>
    public event Action<int>? Imported;

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
        _fsw.Renamed += (s, e) => Enqueue(e.FullPath);
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

    private void Enqueue(string path)
    {
        if (!LibraryScanner.IsImage(path)) return;
        lock (_lock) _pending.Add(path);
    }

    private void Flush()
    {
        List<string> batch;
        lock (_lock)
        {
            if (_busy || _pending.Count == 0) return;
            _busy = true;
            batch = [.. _pending];
            _pending.Clear();
        }

        try
        {
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
