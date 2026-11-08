using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace LatentFolio.Core.Comfy;

/// <summary>PNG tEXt / iTXt / zTXt 元数据读取。ComfyUI 把 API 图存进 "prompt"，UI 图存进 "workflow"。</summary>
public sealed class PngMeta
{
    public string? Prompt;
    public string? Workflow;
    public int Width;
    public int Height;
}

public static class PngReader
{
    private static readonly byte[] Sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static PngMeta Read(string path)
    {
        var meta = new PngMeta();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, false);

        var head = new byte[8];
        if (fs.Read(head, 0, 8) != 8 || !head.AsSpan().SequenceEqual(Sig)) return meta;

        var lenBuf = new byte[4];
        var typeBuf = new byte[4];

        while (true)
        {
            if (fs.Read(lenBuf, 0, 4) != 4) break;
            uint len = BinaryPrimitives.ReadUInt32BigEndian(lenBuf);
            if (fs.Read(typeBuf, 0, 4) != 4) break;
            var type = Encoding.ASCII.GetString(typeBuf);
            if (len > 64u * 1024 * 1024) break;

            if (type == "IHDR")
            {
                var b = new byte[8];
                if (fs.Read(b, 0, 8) == 8)
                {
                    meta.Width = (int)BinaryPrimitives.ReadUInt32BigEndian(b);
                    meta.Height = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4));
                }
                Skip(fs, len - 8 + 4);
                continue;
            }

            if (type is "tEXt" or "iTXt" or "zTXt")
            {
                var data = new byte[len];
                if (fs.Read(data, 0, (int)len) != (int)len) break;
                Skip(fs, 4);
                ParseTextChunk(type, data, meta);
                continue;
            }

            if (type == "IEND") break;
            Skip(fs, (long)len + 4);
        }
        return meta;
    }

    private static void Skip(Stream s, long n)
    {
        if (n <= 0) return;
        if (s.CanSeek) { s.Seek(n, SeekOrigin.Current); return; }
        var buf = new byte[Math.Min(n, 8192)];
        while (n > 0) { var r = s.Read(buf, 0, (int)Math.Min(buf.Length, n)); if (r <= 0) break; n -= r; }
    }

    private static void ParseTextChunk(string type, byte[] data, PngMeta meta)
    {
        int z = Array.IndexOf(data, (byte)0);
        if (z <= 0) return;
        var key = Encoding.Latin1.GetString(data, 0, z);
        try
        {
            if (type == "tEXt")
            {
                var val = Encoding.Latin1.GetString(data, z + 1, data.Length - z - 1);
                Store(key, val, meta);
            }
            else if (type == "zTXt")
            {
                // key\0 method(1) compressed
                if (z + 2 > data.Length) return;
                var val = Inflate(data.AsSpan(z + 2));
                Store(key, Encoding.Latin1.GetString(val), meta);
            }
            else // iTXt: key\0 flag(1) method(1) lang\0 trans\0 text
            {
                int p = z + 1;
                if (p + 2 > data.Length) return;
                bool compressed = data[p] != 0;
                p += 2;
                int l1 = Array.IndexOf(data, (byte)0, p); if (l1 < 0) return; p = l1 + 1;
                int l2 = Array.IndexOf(data, (byte)0, p); if (l2 < 0) return; p = l2 + 1;
                byte[] raw = data[p..];
                if (compressed) raw = Inflate(raw);
                Store(key, Encoding.UTF8.GetString(raw), meta);
            }
        }
        catch
        {
            // 坏块忽略
        }
    }

    private static void Store(string key, string val, PngMeta meta)
    {
        if (key.Equals("prompt", StringComparison.OrdinalIgnoreCase)) meta.Prompt = val;
        else if (key.Equals("workflow", StringComparison.OrdinalIgnoreCase)) meta.Workflow = val;
        else if (key.Equals("negative_prompt", StringComparison.OrdinalIgnoreCase) && meta.Prompt is null)
            meta.Prompt = "\n negative prompt: " + val;
    }

    private static byte[] Inflate(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(data.ToArray());
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        z.CopyTo(outMs);
        return outMs.ToArray();
    }

    public static byte[]? Gzip(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        using var outMs = new MemoryStream();
        using (var gz = new GZipStream(outMs, CompressionLevel.SmallestSize, true))
        {
            var b = Encoding.UTF8.GetBytes(s);
            gz.Write(b, 0, b.Length);
        }
        return outMs.ToArray();
    }

    public static string? Gunzip(byte[]? b)
    {
        if (b is null || b.Length == 0) return null;
        using var ms = new MemoryStream(b);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        gz.CopyTo(outMs);
        return Encoding.UTF8.GetString(outMs.ToArray());
    }
}
