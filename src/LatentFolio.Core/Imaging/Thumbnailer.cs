using SkiaSharp;

namespace LatentFolio.Core.Imaging;

/// <summary>缩略图生成 + dHash。网格缓存 320 WebP q70，档案图 256 WebP q70 永久。</summary>
public static class Thumbnailer
{
    /// <summary>
    /// 网格缩略图长边。最大档的格子是 322x291 DIP，125% 缩放下要 402x364 物理像素；
    /// 2:3 竖图按 UniformToFill 铺满要 603 的长边，所以取 640 —— 480 在最大档会不够。
    /// </summary>
    public const int GridSize = 640;

    public const int ArchiveSize = 256;
    public const int Quality = 70;
    public const int GridQuality = 86;

    public sealed record Result(byte[]? Webp, int Width, int Height, long DHash, bool Ok, string? Error);

    /// <summary>生成缩略图字节。长边缩到 maxSide，保持比例。</summary>
    public static Result Make(string path, int maxSide, int quality = GridQuality)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, false);
            using var codec = SKCodec.Create(fs);
            if (codec is null) return new Result(null, 0, 0, 0, false, "不是可解码的图片");

            var info = codec.Info;
            int ow = info.Width, oh = info.Height;
            if (ow <= 0 || oh <= 0) return new Result(null, 0, 0, 0, false, "尺寸为 0");

            int longSide = Math.Max(ow, oh);
            float scale = longSide <= maxSide ? 1f : (float)maxSide / longSide;

            SKBitmap bmp;
            if (scale < 1f)
            {
                var sd = codec.GetScaledDimensions(scale);
                int dw = Math.Max(1, sd.Width), dh = Math.Max(1, sd.Height);
                // 缩后可能小于目标，再补一次精确缩放
                var dstInfo = new SKImageInfo(dw, dh, SKColorType.Bgra8888, SKAlphaType.Premul);
                bmp = new SKBitmap(dstInfo);
                var err = codec.GetPixels(dstInfo, bmp.GetPixels());
                if (err is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    bmp.Dispose();
                    bmp = SKBitmap.Decode(path) ?? throw new InvalidOperationException("解码失败");
                }
            }
            else
            {
                bmp = SKBitmap.Decode(path) ?? throw new InvalidOperationException("解码失败");
            }

            using (bmp)
            {
                var scaled = Fit(bmp, maxSide);
                using (scaled)
                {
                    long dh = DHashOf(scaled);
                    using var img = SKImage.FromBitmap(scaled);
                    using var data = img.Encode(SKEncodedImageFormat.Webp, quality);
                    if (data is not null && data.Size > 0)
                        return new Result(data.ToArray(), scaled.Width, scaled.Height, dh, true, null);

                    using var jpg = img.Encode(SKEncodedImageFormat.Jpeg, 82);
                    if (jpg is not null && jpg.Size > 0)
                        return new Result(jpg.ToArray(), scaled.Width, scaled.Height, dh, true, null);

                    return new Result(null, 0, 0, dh, false, "编码失败");
                }
            }
        }
        catch (Exception ex)
        {
            return new Result(null, 0, 0, 0, false, ex.Message);
        }
    }

    /// <summary>等比缩放到长边 = maxSide（不放大）。</summary>
    public static SKBitmap Fit(SKBitmap src, int maxSide)
    {
        int sw = src.Width, sh = src.Height;
        int longSide = Math.Max(sw, sh);
        if (longSide == 0) return new SKBitmap(1, 1);

        int dw, dh;
        if (longSide <= maxSide) { dw = sw; dh = sh; }
        else
        {
            double k = (double)maxSide / longSide;
            dw = Math.Max(1, (int)Math.Round(sw * k));
            dh = Math.Max(1, (int)Math.Round(sh * k));
        }
        if (dw == sw && dh == sh) return src.Copy();

        var info = new SKImageInfo(dw, dh, SKColorType.Bgra8888, SKAlphaType.Premul);
        // CatmullRom 比 Mitchell 保边一点：缩略图是给眼睛看的小图，边缘软了就是「糊」
        var sampling = new SKSamplingOptions(SKCubicResampler.CatmullRom);
        var resized = src.Resize(info, sampling);
        return resized ?? src.Copy();
    }

    /// <summary>64 位差值哈希：9×8 灰度，比较相邻像素。</summary>
    public static long DHashOf(SKBitmap src)
    {
        try
        {
            var info = new SKImageInfo(9, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var small = new SKBitmap(info);
            using (var img = SKImage.FromBitmap(src))
            using (var canvas = new SKCanvas(small))
            {
                canvas.Clear(SKColors.Black);
                canvas.DrawImage(img, new SKRect(0, 0, 9, 8),
                    new SKSamplingOptions(SKCubicResampler.Mitchell));
                canvas.Flush();
            }

            Span<byte> gray = stackalloc byte[72];
            var px = small.GetPixels();
            unsafe
            {
                var p = (byte*)px;
                for (int i = 0; i < 72; i++)
                {
                    byte b = p[i * 4 + 0], g = p[i * 4 + 1], r = p[i * 4 + 2];
                    gray[i] = (byte)((r * 77 + g * 150 + b * 29) >> 8);
                }
            }

            long hash = 0;
            int bit = 0;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    if (gray[y * 9 + x] > gray[y * 9 + x + 1]) hash |= 1L << bit;
                    bit++;
                }
            return hash;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>从已有的缩略图文件直接算 dHash（比重新解原图快两个数量级）。</summary>
    public static long DHashOfFile(string path)
    {
        try
        {
            using var bmp = SKBitmap.Decode(path);
            return bmp is null ? 0 : DHashOf(bmp);
        }
        catch { return 0; }
    }

    public static int Hamming(long a, long b) => System.Numerics.BitOperations.PopCount((ulong)(a ^ b));
}
