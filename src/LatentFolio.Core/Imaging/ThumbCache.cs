using System.Collections.Concurrent;
using System.IO.Hashing;

namespace LatentFolio.Core.Imaging;

/// <summary>
/// 磁盘缩略图缓存。网格图（320）按 (路径, mtime, 大小) 哈希命名，自动失效。
/// 档案图（256）按图片 id 命名，永久保留（原图删了也还在）。
/// </summary>
public sealed class ThumbCache
{
    public string Root { get; }
    public string GridDir { get; }
    public string ArchiveDir { get; }

    private readonly ConcurrentDictionary<string, byte> _memory = new(StringComparer.OrdinalIgnoreCase);

    public ThumbCache(string root)
    {
        Root = root;
        GridDir = Path.Combine(root, "grid");
        ArchiveDir = Path.Combine(root, "archive");
        Directory.CreateDirectory(GridDir);
        Directory.CreateDirectory(ArchiveDir);
    }

    public static string Key(string path, long mtime, long size)
    {
        var s = $"{path.ToLowerInvariant()}|{mtime}|{size}";
        var b = System.Text.Encoding.UTF8.GetBytes(s);
        var h = XxHash64.Hash(b);
        return Convert.ToHexString(h).ToLowerInvariant();
    }

    public string GridPath(string key)
        => Path.Combine(GridDir, key[..2], key + ".webp");

    public string ArchivePath(long imageId)
        => Path.Combine(ArchiveDir, (imageId % 256).ToString("x2"), imageId + ".webp");

    public bool HasGrid(string key) => File.Exists(GridPath(key)) || _memory.ContainsKey(key);

    public void RememberGrid(string key) => _memory[key] = 1;

    public void SaveGrid(string key, byte[] bytes)
    {
        var p = GridPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var tmp = p + ".tmp" + Environment.CurrentManagedThreadId;
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, p, true);
        _memory[key] = 1;
    }

    public void SaveArchive(long id, byte[] bytes)
    {
        var p = ArchivePath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        if (File.Exists(p)) return;
        var tmp = p + ".tmp" + Environment.CurrentManagedThreadId;
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, p, true);
    }

    public byte[]? ReadGrid(string key)
    {
        try
        {
            var p = GridPath(key);
            return File.Exists(p) ? File.ReadAllBytes(p) : null;
        }
        catch { return null; }
    }

    /// <summary>当前缓存占用（字节）+ 文件数。</summary>
    public (long Bytes, int Files) Stat()
    {
        long total = 0;
        int n = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(GridDir, "*.webp", SearchOption.AllDirectories))
            {
                total += new FileInfo(f).Length;
                n++;
            }
        }
        catch { }
        return (total, n);
    }

    /// <summary>超出预算时按最后访问时间删最老的。</summary>
    public void Trim(long budgetBytes)
    {
        try
        {
            var files = new List<(string Path, long Size, DateTime At)>();
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(GridDir, "*.webp", SearchOption.AllDirectories))
            {
                var fi = new FileInfo(f);
                files.Add((f, fi.Length, fi.LastAccessTimeUtc));
                total += fi.Length;
            }
            if (total <= budgetBytes) return;

            files.Sort((a, b) => a.At.CompareTo(b.At));
            foreach (var f in files)
            {
                if (total <= budgetBytes) break;
                try { File.Delete(f.Path); total -= f.Size; } catch { }
            }
        }
        catch { }
    }
}
