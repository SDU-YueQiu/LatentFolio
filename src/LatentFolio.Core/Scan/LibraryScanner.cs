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
    public int DupSkipped;
    public List<(string Path, string Error)> Errors = [];
    public TimeSpan Elapsed;
}

/// <summary>图库扫描：读 PNG 元数据 → 解析提示词 → 生成缩略图 → 落库。</summary>
public sealed class LibraryScanner(Db.Db db, ImageStore store, ThumbCache thumbs)
{
    private static readonly string[] Exts = [".png", ".jpg", ".jpeg", ".webp", ".avif", ".gif"];

    /// <summary>删除/丢失时是否生成归档缩略图（设置里可关）。</summary>
    public bool ArchiveThumbsEnabled { get; set; } = true;

    /// <summary>内容哈希相同的文件只收一份（设计稿 §7）。</summary>
    public bool DedupeByHash { get; set; } = true;

    public static bool IsImage(string path)
    {
        var e = Path.GetExtension(path);
        foreach (var x in Exts) if (e.Equals(x, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>解析单张图（不落库）。扫描和监视导入共用。</summary>
    public (ImageItem Item, string? Workflow)? ParseOne(string root, string path, bool makeThumb = true)
    {
        try
        {
            var fi = new FileInfo(path);
            var meta = PngReader.Read(path);
            var parsed = meta.Prompt is null ? null : GraphParser.ParseWorkflowApiJson(meta.Prompt);

            var item = new ImageItem
            {
                Path = path,
                Folder = RelativeFolder(root, path),
                FileName = Path.GetFileName(path),
                Ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                Size = fi.Length,
                MTime = fi.LastWriteTimeUtc.Ticks,
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

            item.Aspect = ImageItem.BucketOf(item.AspectRatio);
            item.SearchText = GraphParser.BuildSearchText(item.Prompt, item.Negative);
            item.Hash = QuickHash(path, fi.Length);

            var vocab = Vocabulary.Instance;
            if (vocab.Ready)
            {
                var (chars, artists) = vocab.Tag(item.Prompt);
                item.Chars = chars;
                item.Artists = artists;
            }

            if (makeThumb)
            {
                var key = ThumbCache.Key(path, item.MTime, fi.Length);
                if (!thumbs.HasGrid(key))
                {
                    var t = Thumbnailer.Make(path, Thumbnailer.GridSize);
                    if (t.Webp is not null) thumbs.SaveGrid(key, t.Webp);
                    item.DHash = t.DHash;
                    if (item.Width <= 0 && t.Width > 0) { item.Width = t.Width; item.Height = t.Height; }
                }
            }

            return (item, meta.Workflow);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>监视器触发的单批导入。返回新增/更新的张数。</summary>
    public int ImportPaths(string root, IEnumerable<string> paths)
    {
        var existing = new Dictionary<string, ImageItem>(StringComparer.OrdinalIgnoreCase);
        var byHash = new HashSet<long>();
        foreach (var it in store.LoadAll())
        {
            existing[it.Path] = it;
            if (it.Hash != 0 && it.State != FileState.Deleted) byHash.Add(it.Hash);
        }
        var skipped = DedupeByHash ? store.LoadSkipped() : new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        int n = 0;
        using var tx = db.Connection.BeginTransaction();
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            // 撞过的路径只在内容没变时才跳过；被换成别的图要照常收
            if (skipped.TryGetValue(path, out var skipHash)
                && QuickHash(path, new FileInfo(path).Length) == skipHash) continue;
            var parsed = ParseOne(root, path);
            if (parsed is null) continue;
            var (item, wf) = parsed.Value;

            if (existing.TryGetValue(path, out var old))
            {
                if (old.MTime == item.MTime && old.Size == item.Size) continue;
                item.Id = old.Id;
                item.Rating = old.Rating;
                item.Note = old.Note;
                store.Update(item);
                if (item.Hash != 0) byHash.Add(item.Hash);
            }
            else
            {
                // 内容一模一样的两张只收一张（ComfyUI 重跑同种子会出完全相同的图）
                if (DedupeByHash && item.Hash != 0 && !byHash.Add(item.Hash))
                {
                    store.AddSkipped(path, item.Hash);
                    continue;
                }
                store.Insert(item);
            }
            if (wf is not null) store.SetWorkflow(item.Id, PngReader.Gzip(wf));
            n++;
        }
        tx.Commit();
        return n;
    }

    /// <summary>按当前规格重建网格缩略图（只有换分辨率那一次会跑）。</summary>
    public int RebuildGridThumbs(IReadOnlyList<ImageItem> images, CancellationToken ct = default)
    {
        int n = 0;
        Parallel.ForEach(images, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount),
            CancellationToken = ct,
        }, it =>
        {
            if (it.State == FileState.Deleted) return;
            var key = ThumbCache.Key(it.Path, it.MTime, it.Size);
            if (thumbs.HasGrid(key)) return;
            try
            {
                if (!File.Exists(it.Path)) return;
                var t = Thumbnailer.Make(it.Path, Thumbnailer.GridSize, Thumbnailer.GridQuality);
                if (t.Webp is null) return;
                thumbs.SaveGrid(key, t.Webp);
                Interlocked.Increment(ref n);
            }
            catch { }
        });
        return n;
    }

    /// <summary>
    /// 补算缺失的 dHash。老库里 4,800 张的 dHash 是 0（当年读的是已有缩略图，
    /// 只在新建缓存时才顺带算一次），相似图分组因此几乎分不出来。
    /// 先从网格缓存算，缓存没有才回去解原图。
    /// </summary>
    public int BackfillDHash(IReadOnlyList<ImageItem> images)
    {
        var todo = images.Where(x => x.DHash == 0 && x.State != FileState.Deleted).ToList();
        if (todo.Count == 0) return 0;

        var results = new System.Collections.Concurrent.ConcurrentBag<(long Id, long Dh)>();
        Parallel.ForEach(todo, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount),
        }, it =>
        {
            long dh = 0;
            var key = ThumbCache.Key(it.Path, it.MTime, it.Size);
            try
            {
                var gp = thumbs.GridPath(key);
                if (File.Exists(gp)) dh = Thumbnailer.DHashOfFile(gp);
            }
            catch { }

            if (dh == 0)
            {
                try
                {
                    if (File.Exists(it.Path))
                    {
                        var t = Thumbnailer.Make(it.Path, Thumbnailer.GridSize);
                        dh = t.DHash;
                        if (t.Webp is not null) thumbs.SaveGrid(key, t.Webp);
                    }
                }
                catch { }
            }

            if (dh != 0)
            {
                it.DHash = dh;
                results.Add((it.Id, dh));
            }
        });

        using var tx = db.Connection.BeginTransaction();
        using var c = db.Cmd("UPDATE image SET dhash=$d WHERE id=$id");
        var pd = c.Parameters.Add("$d", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pi = c.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
        foreach (var (id, dh) in results)
        {
            pd.Value = dh;
            pi.Value = id;
            c.ExecuteNonQuery();
        }
        tx.Commit();
        return results.Count;
    }

    /// <summary>
    /// 全库重新识别：重算角色/画师，顺手把匹配不上的裸名收进候选池。
    /// 确认了别名、或词表更新之后跑这个。onlyMissing=true 时只补还没打过标的。
    /// </summary>
    public (int Retagged, int Candidates) RetagAll(IReadOnlyList<ImageItem> images, bool onlyMissing = false)
    {
        var vocab = Vocabulary.Instance;
        if (!vocab.Ready) return (0, 0);

        var cand = new Dictionary<string, (string Suggest, int Hits)>(StringComparer.Ordinal);
        int n = 0;
        using (var tx = db.Connection.BeginTransaction())
        {
            foreach (var it in images)
            {
                if (it.Prompt.Length == 0) continue;
                if (onlyMissing && (it.Chars.Length > 0 || it.Artists.Length > 0)) continue;
                var (chars, artists) = vocab.Tag(it.Prompt, cand);
                if (chars.SequenceEqual(it.Chars) && artists.SequenceEqual(it.Artists)) continue;
                it.Chars = chars;
                it.Artists = artists;
                store.Update(it);
                n++;
            }
            tx.Commit();
        }
        if (cand.Count > 0) store.MergeCandidates(cand.Select(kv => (kv.Key, kv.Value.Suggest, kv.Value.Hits)));
        return (n, cand.Count);
    }

    /// <summary>
    /// 扫描整个图库。force=true 时忽略 mtime/大小，把元数据全部重新解析一遍
    /// （解析器升级后需要它，否则老记录里的 null 永远补不上）。
    /// </summary>
    public async Task<ScanResult> ScanAsync(string root, IProgress<ScanProgress>? progress,
        bool makeThumbs = true, CancellationToken ct = default, bool force = false)
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

        // 上次按内容判重跳过的文件，这次不用再解析一遍
        var skipped = DedupeByHash ? store.LoadSkipped() : new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

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

                // 撞过的路径只在内容没变时才跳过；被换成别的图要照常收
                if (skipped.TryGetValue(path, out var skipHash) && QuickHash(path, fi.Length) == skipHash)
                {
                    lock (lockObj) { seen.Add(path); result.Skipped++; Interlocked.Increment(ref done); }
                    return ValueTask.CompletedTask;
                }

                long mtime = fi.LastWriteTimeUtc.Ticks;
                var key = ThumbCache.Key(path, mtime, fi.Length);

                existing.TryGetValue(path, out var old);
                bool unchanged = !force && old is not null
                                 && old.MTime == mtime && old.Size == fi.Length;

                if (unchanged)
                {
                    lock (lockObj) { seen.Add(path); result.Skipped++; Interlocked.Increment(ref done); }
                    return ValueTask.CompletedTask;
                }

                var parsedOne = ParseOne(root, path, makeThumbs);
                if (parsedOne is null) throw new InvalidOperationException("解析失败");
                var (item, workflow) = parsedOne.Value;

                lock (lockObj)
                {
                    seen.Add(path);
                    if (old is not null)
                    {
                        item.Id = old.Id;
                        item.Rating = old.Rating;
                        item.Note = old.Note;
                        item.AppTags = old.AppTags;
                        toUpdate.Add((item, workflow));
                        result.Updated++;
                    }
                    else
                    {
                        toInsert.Add((item, workflow));
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
            if (kv.Value.State == FileState.Present)
            {
                // 文件没了也要留一张 256 的归档缩略图，否则图墙里只剩一个空白格
                if (ArchiveThumbsEnabled) ArchiveThumbs.Ensure(thumbs, kv.Value);
                kv.Value.State = FileState.Missing;
                missing.Add(kv.Value.Id);
            }
        }
        foreach (var id in missing) store.UpdateState(new ImageItem { Id = id, State = FileState.Missing });
        if (missing.Count > 0) result.Removed = missing.Count;

        // 内容判重：同样的字节只收一份，其余记进 skip 表免得下次重解析。
        // 按「先加的、路径靠前的」保留，保证多次扫描结果一致。
        if (DedupeByHash && toInsert.Count > 0)
        {
            var known = new HashSet<long>();
            // 只拿「还在库里」的图判重：把一张图删了又重新生成到别的目录，
            // 不应该因为老记录还留着一个同样的哈希就被跳过
            foreach (var it in existing.Values)
                if (it.Hash != 0 && it.State != FileState.Deleted) known.Add(it.Hash);

            var keep = new List<(ImageItem Item, string? Workflow)>(toInsert.Count);
            foreach (var t in toInsert.OrderBy(x => x.Item.AddedAt)
                                      .ThenBy(x => x.Item.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (t.Item.Hash != 0 && !known.Add(t.Item.Hash))
                {
                    store.AddSkipped(t.Item.Path, t.Item.Hash);
                    result.DupSkipped++;
                    continue;
                }
                keep.Add(t);
            }
            toInsert = keep;
            result.Added = keep.Count;
        }

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

