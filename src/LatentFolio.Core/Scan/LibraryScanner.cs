using System.Diagnostics;
using System.IO.Hashing;
using LatentFolio.Core.Comfy;
using LatentFolio.Core.Db;
using LatentFolio.Core.Imaging;
using LatentFolio.Core.Models;
using LatentFolio.Core.Text;

namespace LatentFolio.Core.Scan;

public sealed class ScanProgress
{
    public int Total;
    public int Done;
    public string Phase = "";
    public string Current = "";

    public double Fraction => Total <= 0 ? 0 : Math.Min(1.0, (double)Done / Total);
}

public sealed class ScanResult
{
    public int Added;
    public int Updated;
    public int Removed;
    public int Skipped;
    public int Failed;
    public List<(string Path, string Error)> Errors = [];
    public TimeSpan Elapsed;
}

/// <summary>图库扫描：读 PNG 元数据 → 解析提示词 → 生成缩略图 → 落库。</summary>
public sealed class LibraryScanner(Db.Db db, ImageStore store, ThumbCache thumbs)
{
    private static readonly string[] Exts = [".png", ".jpg", ".jpeg", ".webp", ".avif", ".gif"];

    public static bool IsImage(string path)
    {
        var e = Path.GetExtension(path);
        foreach (var x in Exts) if (e.Equals(x, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// 扫描整个图库。
    /// </summary>
    public async Task<ScanResult> ScanAsync(string root, IProgress<ScanProgress>? progress,
        bool makeThumbs = true, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new ScanResult();
        var prog = progress ?? new Progress<ScanProgress>();
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"图库目录不存在：{root}");

        prog.Report(new ScanProgress { Phase = "列出文件" });

        var files = await Task.Run(() => Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        }).Where(IsImage).ToList(), ct);

        prog.Report(new ScanProgress { Phase = "读取数据库", Total = files.Count });

        var existing = new Dictionary<string, ImageItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in store.LoadAll()) existing[it.Path] = it;

        var seen = new HashSet<string>(files.Count, StringComparer.OrdinalIgnoreCase);
        var toInsert = new List<(ImageItem Item, string? Workflow)>();
        var toUpdate = new List<(ImageItem Item, string? Workflow)>();
        var lockObj = new object();

        var vocab = Vocabulary.Instance;

        int done = 0;
        await Parallel.ForEachAsync(files, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount),
            CancellationToken = ct,
        }, (path, token) =>
        {
            try
            {
                var fi = new FileInfo(path);
                long mtime = fi.LastWriteTimeUtc.Ticks;
                var key = ThumbCache.Key(path, mtime, fi.Length);

                bool unchanged = existing.TryGetValue(path, out var old)
                                 && old.MTime == mtime && old.Size == fi.Length;

                if (unchanged)
                {
                    lock (lockObj) { seen.Add(path); result.Skipped++; Interlocked.Increment(ref done); }
                    return ValueTask.CompletedTask;
                }

                var meta = PngReader.Read(path);
                var parsed = meta.Prompt is null ? null : GraphParser.ParseWorkflowApiJson(meta.Prompt);

                var item = new ImageItem
                {
                    Path = path,
                    Folder = RelativeFolder(root, path),
                    FileName = Path.GetFileName(path),
                    Ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                    Size = fi.Length,
                    MTime = mtime,
                    AddedAt = new DateTimeOffset(fi.CreationTimeUtc).ToUnixTimeMilliseconds(),
                    Width = meta.Width,
                    Height = meta.Height,
                    State = FileState.Present,
                };

                if (parsed is not null)
                {
                    item.Prompt = parsed.Positive;
                    item.Negative = parsed.Negative;
                    item.PromptSource = parsed.Method;
                    item.Model = Clean(parsed.Model);
                    item.Sampler = Clean(parsed.Sampler);
                    item.Scheduler = Clean(parsed.Scheduler);
                    item.Steps = parsed.Steps;
                    item.Cfg = parsed.Cfg;
                    item.Seed = parsed.Seed;
                    item.Denoise = parsed.Denoise;
                    if (item.Width == 0 && parsed.Width is > 0) item.Width = parsed.Width.Value;
                    if (item.Height == 0 && parsed.Height is > 0) item.Height = parsed.Height.Value;
                    item.NegIsDup = IsNegDup(parsed.Positive, parsed.Negative);
                }

                if (item.Width <= 0 || item.Height <= 0)
                {
                    // PNG 头没读到就靠缩略图拿尺寸
                    var probe = Thumbnailer.Make(path, Thumbnailer.GridSize);
                    if (probe.Ok && probe.Width > 0 && probe.Height > 0)
                    {
                        // 缩略图是按比例缩的，反推原尺寸
                        double k = Math.Max(1.0, (double)Math.Max(item.Width, item.Height) / Math.Max(probe.Width, probe.Height));
                        if (item.Width <= 0) item.Width = (int)Math.Round(probe.Width * k);
                        if (item.Height <= 0) item.Height = (int)Math.Round(probe.Height * k);
                    }
                }

                item.Aspect = ImageItem.BucketOf(item.AspectRatio);
                item.SearchText = GraphParser.BuildSearchText(item.Prompt, item.Negative);
                item.Hash = QuickHash(path, fi.Length);

                if (vocab.Ready)
                {
                    var (chars, artists) = vocab.Tag(item.Prompt);
                    item.Chars = chars;
                    item.Artists = artists;
                }

                if (makeThumbs && !thumbs.HasGrid(key))
                {
                    var t = Thumbnailer.Make(path, Thumbnailer.GridSize);
                    if (t.Webp is not null)
                    {
                        thumbs.SaveGrid(key, t.Webp);
                        item.DHash = t.DHash;
                    }
                }

                lock (lockObj)
                {
                    seen.Add(path);
                    if (old is not null)
                    {
                        item.Id = old.Id;
                        item.Rating = old.Rating;
                        item.Note = old.Note;
                        item.AppTags = old.AppTags;
                        toUpdate.Add((item, meta.Workflow));
                        result.Updated++;
                    }
                    else
                    {
                        toInsert.Add((item, meta.Workflow));
                        result.Added++;
                    }
                    Interlocked.Increment(ref done);
                    if (done % 25 == 0 || done == files.Count)
                        prog.Report(new ScanProgress
                        {
                            Phase = "扫描", Total = files.Count, Done = done,
                            Current = Path.GetFileName(path),
                        });
                }
            }
            catch (Exception ex)
            {
                lock (lockObj) { result.Failed++; if (result.Errors.Count < 50) result.Errors.Add((path, ex.Message)); }
            }
            return ValueTask.CompletedTask;
        });

        ct.ThrowIfCancellationRequested();

        prog.Report(new ScanProgress { Phase = "写库", Total = files.Count, Done = files.Count });

        // 文件不见了：标 missing（元数据保留）
        var missing = new List<long>();
        foreach (var kv in existing)
        {
            if (seen.Contains(kv.Key)) continue;
            if (kv.Value.State == FileState.Present) { kv.Value.State = FileState.Missing; missing.Add(kv.Value.Id); }
        }
        foreach (var id in missing) store.UpdateState(new ImageItem { Id = id, State = FileState.Missing });
        if (missing.Count > 0) result.Removed = missing.Count;

        // 批量落库
        await Task.Run(() =>
        {
            using var tx = db.Connection.BeginTransaction();
            foreach (var (item, wf) in toUpdate)
            {
                store.Update(item);
                if (wf is not null) store.SetWorkflow(item.Id, PngReader.Gzip(wf));
            }
            foreach (var (item, wf) in toInsert)
            {
                store.Insert(item);
                if (wf is not null) store.SetWorkflow(item.Id, PngReader.Gzip(wf));
            }
            tx.Commit();
        }, ct);

        result.Elapsed = sw.Elapsed;
        prog.Report(new ScanProgress { Phase = "完成", Total = files.Count, Done = files.Count });
        return result;
    }

    private static string? Clean(string? s)
    {
        if (s is null) return null;
        s = s.Trim();
        return s.Length switch { 0 => null, > 200 => s[..200], _ => s };
    }

    /// <summary>负面和正面几乎一样 —— 同一条 CLIPTextEncode 接了正负两口。</summary>
    public static bool IsNegDup(string pos, string neg)
    {
        if (pos.Length == 0 || neg.Length == 0) return false;
        var n = Normalize(pos);
        var m = Normalize(neg);
        if (n.Length == 0 || m.Length == 0) return false;
        if (n == m) return true;
        int min = Math.Min(n.Length, m.Length), max = Math.Max(n.Length, m.Length);
        if (max > min * 1.15) return false;
        int same = 0;
        for (int i = 0; i < min; i++) if (n[i] == m[i]) same++;
        return same >= min * 0.9;
    }

    private static string Normalize(string s)
        => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    public static string RelativeFolder(string root, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is null) return "";
        var rel = Path.GetRelativePath(root, dir);
        return rel == "." ? "" : rel.Replace('\\', '/');
    }

    /// <summary>便宜的内容指纹：前 64KB + 长度 + 尾部 64KB。</summary>
    public static long QuickHash(string path, long size)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192);
            var buf = new byte[64 * 1024];
            int n = fs.Read(buf, 0, buf.Length);
            var h = XxHash64.HashToUInt64(buf.AsSpan(0, n));
            if (size > 128 * 1024)
            {
                fs.Seek(-64 * 1024, SeekOrigin.End);
                int m = fs.Read(buf, 0, buf.Length);
                var h2 = XxHash64.HashToUInt64(buf.AsSpan(0, m));
                h = XxHash64.HashToUInt64(BitConverter.GetBytes(h).Concat(BitConverter.GetBytes(h2)).ToArray());
            }
            return unchecked((long)h ^ size);
        }
        catch
        {
            return size;
        }
    }

    /// <summary>导入旧图库（whale-library）的评分/批注。</summary>
    public int ImportLegacy(string tsvPath, IReadOnlyList<ImageItem> images)
    {
        if (!File.Exists(tsvPath)) return 0;
        var byRel = new Dictionary<string, ImageItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in images)
        {
            var rel = string.IsNullOrEmpty(it.Folder) ? it.FileName : $"{it.Folder}/{it.FileName}";
            byRel[rel] = it;
            byRel[it.FileName] = it;
        }

        int applied = 0;
        using var tx = db.Connection.BeginTransaction();
        foreach (var line in File.ReadLines(tsvPath))
        {
            if (line.Length == 0) continue;
            var p = line.Split('\t');
            if (p.Length < 2) continue;
            if (!byRel.TryGetValue(p[0], out var it)) continue;
            int rating = int.TryParse(p[1], out var r) ? Math.Clamp(r, 0, 5) : 0;
            var note = p.Length > 2 ? p[2] : "";
            bool deleted = p.Length > 3 && p[3] == "1";
            if (rating == 0 && note.Length == 0 && !deleted) continue;
            it.Rating = rating;
            it.Note = note;
            if (deleted && it.State == FileState.Present) it.State = FileState.Deleted;
            store.UpdateUser(it);
            store.UpdateState(it);
            applied++;
        }
        tx.Commit();
        return applied;
    }
}
