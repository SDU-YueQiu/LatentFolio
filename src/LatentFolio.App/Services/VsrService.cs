using System.Windows.Media;
using System.Windows.Media.Imaging;
using LatentFolio.Core.Imaging;
using LatentFolio.Core.Models;
using LatentFolio.Vsr;
using SkiaSharp;

namespace LatentFolio.App.Services;

/// <summary>
/// RTX VSR 的应用层封装：解码 → 超分 → BitmapSource。
/// 结果只放在本次运行的内存里，**不落盘**（用户明确要求默认不保存）。
/// </summary>
public sealed class VsrService : IDisposable
{
    public VsrEngine Engine { get; } = new();

    private readonly Dictionary<long, BitmapSource> _cache = [];
    private readonly SemaphoreSlim _one = new(1, 1);

    public bool Ready => Engine.Available;
    public string Status => Engine.Status;
    public int CachedCount { get { lock (_cache) return _cache.Count; } }

    public Task<bool> StartAsync() => Task.Run(() => Engine.Start());

    public BitmapSource? Peek(long id)
    {
        lock (_cache) return _cache.GetValueOrDefault(id);
    }

    public void Clear()
    {
        lock (_cache) _cache.Clear();
    }

    /// <summary>处理一张图，返回超分后的位图；失败返回 null。</summary>
    public async Task<BitmapSource?> ProcessAsync(ImageItem item)
    {
        if (!Ready) return null;
        if (Peek(item.Id) is { } hit) return hit;

        await _one.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Peek(item.Id) is { } again) return again;

            var bmp = await Task.Run(() => RunOne(item)).ConfigureAwait(false);
            if (bmp is null) return null;
            lock (_cache) _cache[item.Id] = bmp;
            return bmp;
        }
        finally
        {
            _one.Release();
        }
    }

    private BitmapSource? RunOne(ImageItem item)
    {
        SKBitmap? sk = null;
        SKBitmap? converted = null;
        try
        {
            sk = SKBitmap.Decode(item.Path);
            if (sk is null) return null;

            int w = sk.Width, h = sk.Height;
            if (w <= 0 || h <= 0) return null;

            // 输出超上限就先缩输入，保证内存不炸
            long outPx = (long)w * 2 * h * 2;
            if (outPx > VsrEngine.MaxOutputPixels)
            {
                double k = Math.Sqrt((double)VsrEngine.MaxOutputPixels / outPx);
                int nw = Math.Max(2, (int)(w * k)) & ~1;
                int nh = Math.Max(2, (int)(h * k)) & ~1;
                var info = new SKImageInfo(nw, nh, SKColorType.Bgra8888, SKAlphaType.Premul);
                converted = sk.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
                if (converted is null) return null;
                w = nw; h = nh;
            }

            var src = converted ?? sk;
            SKBitmap? bgra = null;
            if (src.ColorType != SKColorType.Bgra8888 || src.AlphaType != SKAlphaType.Premul)
            {
                bgra = new SKBitmap(new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                if (!src.CopyTo(bgra, SKColorType.Bgra8888)) return null;
                src = bgra;
            }

            try
            {
                int pitch = src.RowBytes;
                var buf = new byte[(long)pitch * h];
                System.Runtime.InteropServices.Marshal.Copy(src.GetPixels(), buf, 0, buf.Length);

                // 去掉行尾多余 padding，喂给 VSR 的必须是紧凑行
                byte[] packed;
                int row = w * 4;
                if (pitch == row) packed = buf;
                else
                {
                    packed = new byte[(long)row * h];
                    for (int y = 0; y < h; y++)
                        Buffer.BlockCopy(buf, y * pitch, packed, y * row, row);
                }

                var res = Engine.Process(packed, w, h);
                if (res is null) return null;

                var outBmp = BitmapSource.Create(res.Width, res.Height, 96, 96,
                    PixelFormats.Bgra32, null, res.Bgra, res.Width * 4);
                outBmp.Freeze();
                return outBmp;
            }
            finally
            {
                bgra?.Dispose();
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            converted?.Dispose();
            sk?.Dispose();
        }
    }

    public void Dispose()
    {
        _one.Dispose();
        Engine.Dispose();
    }
}
