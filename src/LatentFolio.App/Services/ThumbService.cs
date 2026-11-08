using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LatentFolio.Core.Imaging;
using SkiaSharp;

namespace LatentFolio.App.Services;

/// <summary>
/// 缩略图加载：磁盘 WebP → BitmapSource。带 LRU 和并发闸门。
/// 缓存只留最近用过的，滚多远都不会把内存吃穿。
/// </summary>
public sealed class ThumbService : IDisposable
{
    private readonly ThumbCache _disk;
    private readonly SemaphoreSlim _gate = new(Math.Max(4, Environment.ProcessorCount));
    private readonly ConcurrentDictionary<string, Task<BitmapSource?>> _inflight = new();
    private readonly Dictionary<(long Id, int Bucket), LinkedListNode<CacheEntry>> _map = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly object _lock = new();
    private long _bytes;

    /// <summary>
    /// 解码后的位图很占地方，按字节数封顶，而不是按条数。
    /// 实测：一屏约 40 格 × ~1 MB，解码一次 4~6 ms（缩放省掉之后），
    /// 所以没必要为了"回滚不重算"占几百 MB。128 MB ≈ 三屏的历史。
    /// </summary>
    public const long MemoryBudgetMb = 128;
    private const long MemoryBudget = MemoryBudgetMb * 1024 * 1024;

    private sealed record CacheEntry((long Id, int Bucket) Key, BitmapSource Bitmap, long Bytes);

    /// <summary>
    /// 解码尺寸分桶。按「实际要显示的物理像素」解码，等于把缩放交给 Skia 的
    /// CatmullRom 去做；交给 WPF 在绘制时缩会明显发糊（之前就是这个毛病）。
    /// </summary>
    public static int Bucket(int px)
    {
        if (px <= 0) return 480;
        int b = (int)(Math.Ceiling(px / 32.0) * 32);
        return Math.Clamp(b, 96, 704);
    }

    public ThumbService(ThumbCache disk) => _disk = disk;

    public BitmapSource? Peek(long id, int px = 0)
    {
        var key = (id, Bucket(px));
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var n))
            {
                _lru.Remove(n);
                _lru.AddFirst(n);
                return n.Value.Bitmap;
            }
        }
        return null;
    }

    /// <summary>把渲染好的位图放回缓存（调用方自己解码时用）。</summary>
    public void Put(long id, int px, BitmapSource bmp)
    {
        var key = (id, Bucket(px));
        long bytes = (long)bmp.PixelWidth * bmp.PixelHeight * 4;
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var old))
            {
                _bytes -= old.Value.Bytes;
                _lru.Remove(old);
                _map.Remove(key);
            }
            var node = _lru.AddFirst(new CacheEntry(key, bmp, bytes));
            _map[key] = node;
            _bytes += bytes;
            while (_bytes > MemoryBudget && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
                _bytes -= last.Value.Bytes;
            }
        }
    }

    public Task<BitmapSource?> LoadAsync(long id, string path, long mtime, long size, int px = 0)
    {
        int bucket = Bucket(px);
        if (Peek(id, bucket) is { } hit) return Task.FromResult<BitmapSource?>(hit);

        var key = ThumbCache.Key(path, mtime, size) + "@" + bucket;
        return _inflight.GetOrAdd(key, k => Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Peek(id, bucket) is { } again) return again;

                var bytes = _disk.ReadGrid(ThumbCache.Key(path, mtime, size));
                if (bytes is null)
                {
                    var r = Thumbnailer.Make(path, Thumbnailer.GridSize, Thumbnailer.GridQuality);
                    if (r.Webp is null) return null;
                    _disk.SaveGrid(ThumbCache.Key(path, mtime, size), r.Webp);
                    bytes = r.Webp;
                }

                var bmp = DecodeScaled(bytes, bucket);
                if (bmp is not null) Put(id, bucket, bmp);
                return bmp;
            }
            catch
            {
                return null;
            }
            finally
            {
                _gate.Release();
                _inflight.TryRemove(k, out var _gone);
            }
        }));
    }

    /// <summary>解码到指定长边。缩在图库缓存里的时候本来就有损，再让 WPF 缩一次只会更糊。</summary>
    public static BitmapSource? DecodeScaled(byte[] data, int maxSide)
    {
        try
        {
            using var sk = SKBitmap.Decode(data);
            if (sk is null) return null;
            if (maxSide <= 0) return ToSource(sk);
            int ls = Math.Max(sk.Width, sk.Height);
            // 只差几个像素就别重采样了：WPF 按格子大小画的时候本来就会缩，
            // 而一次 CatmullRom 要 12 ms（实测），比解码本身还贵三倍。
            if (ls <= maxSide * 1.08) return ToSource(sk);
            using var small = Thumbnailer.Fit(sk, maxSide);
            return ToSource(small);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>全尺寸原图（查看器用）。</summary>
    public static BitmapSource? DecodeFile(string path, int maxSide = 0)
    {
        try
        {
            using var bmp = SKBitmap.Decode(path);
            if (bmp is null) return null;
            SKBitmap use = bmp;
            SKBitmap? scaled = null;
            if (maxSide > 0 && Math.Max(bmp.Width, bmp.Height) > maxSide)
            {
                scaled = Thumbnailer.Fit(bmp, maxSide);
                use = scaled;
            }
            try
            {
                return ToSource(use);
            }
            finally
            {
                scaled?.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    public static BitmapSource? Decode(byte[] data)
    {
        try
        {
            using var sk = SKBitmap.Decode(data);
            if (sk is null) return null;
            return ToSource(sk);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource ToSource(SKBitmap sk)
    {
        var info = new SKImageInfo(sk.Width, sk.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        SKBitmap src = sk;
        SKBitmap? converted = null;
        if (sk.ColorType != SKColorType.Bgra8888 || sk.AlphaType != SKAlphaType.Premul)
        {
            converted = new SKBitmap(info);
            if (!sk.CopyTo(converted, SKColorType.Bgra8888)) { converted.Dispose(); converted = null; }
            else src = converted;
        }
        try
        {
            int stride = src.RowBytes;
            int len = stride * src.Height;
            var buf = new byte[len];
            System.Runtime.InteropServices.Marshal.Copy(src.GetPixels(), buf, 0, len);
            var bmp = BitmapSource.Create(src.Width, src.Height, 96, 96,
                PixelFormats.Bgra32, null, buf, stride);
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            converted?.Dispose();
        }
    }

    public string DiskStats()
    {
        var (bytes, files) = _disk.Stat();
        return $"{bytes / 1024.0 / 1024.0:F0} MB / {files} 张";
    }

    /// <summary>换图库时把内存里的解码结果全丢掉。</summary>
    public void ClearMemory()
    {
        lock (_lock) { _map.Clear(); _lru.Clear(); _bytes = 0; }
        _inflight.Clear();
    }

    public (int Count, long Bytes) Stats
    {
        get { lock (_lock) return (_map.Count, _bytes); }
    }

    public void Dispose()
    {
        _gate.Dispose();
        lock (_lock) { _map.Clear(); _lru.Clear(); _bytes = 0; }
    }
}
