using LatentFolio.Core.Imaging;
using LatentFolio.Core.Models;
using SkiaSharp;

namespace LatentFolio.Core.Scan;

/// <summary>
/// 归档缩略图：图被删除/丢失之后，还要能看见它长什么样。
/// 规格定的是短边 256 · WebP q70（约 12 KB/张，永久保留）。
/// 从已有的 320 网格缓存降采样，不重新读原图 —— 原图可能已经不在了。
/// </summary>
public static class ArchiveThumbs
{
    public const int Size = 256;

    /// <summary>确保这张图有归档缩略图。返回是否新写了一张。</summary>
    public static bool Ensure(ThumbCache cache, ImageItem item)
    {
        var dest = cache.ArchivePath(item.Id);
        if (File.Exists(dest)) return false;

        byte[]? bytes = null;

        // 1) 网格缓存（最常见，因为浏览过的图都有）
        var key = ThumbCache.Key(item.Path, item.MTime, item.Size);
        bytes = cache.ReadGrid(key);

        // 2) 没浏览过就直接从原图读（原图还在的话）
        if (bytes is null)
        {
            if (!File.Exists(item.Path)) return false;
            var made = Thumbnailer.Make(item.Path, Thumbnailer.GridSize);
            bytes = made.Webp;
            if (bytes is not null) cache.SaveGrid(key, bytes);
        }

        if (bytes is null) return false;

        try
        {
            if (!Shrink(bytes, dest)) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool Shrink(byte[] webp, string destPath)
    {
        using var src = SKBitmap.Decode(webp);
        if (src is null) return false;

        var fit = Thumbnailer.Fit(src, Size);
        try
        {
            using var img = SKImage.FromBitmap(fit);
            using var data = img.Encode(SKEncodedImageFormat.Webp, 70);
            if (data is null || data.Size == 0) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            var tmp = destPath + ".tmp";
            File.WriteAllBytes(tmp, data.ToArray());
            File.Move(tmp, destPath, true);
            return true;
        }
        finally
        {
            if (!ReferenceEquals(fit, src)) fit.Dispose();
        }
    }
}
