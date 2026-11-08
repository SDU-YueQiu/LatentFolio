using System.Diagnostics;
using LatentFolio.Core.Models;

namespace LatentFolio.Core.Filtering;

public enum SortKey
{
    Name, Date, Rating, Resolution, Size, Steps, Cfg, Seed, Model,
}

public sealed record SortSpec(SortKey Key, bool Desc);

/// <summary>筛选求值 + 命中缓存。命中集合按图片 id 存，列一变就重算。</summary>
public sealed class ColumnEngine
{
    private readonly Dictionary<long, HitSet> _cache = [];

    public sealed class HitSet
    {
        public long[] Ids = [];
        public int Count => Ids.Length;
        public long TreeHash;
        public long DataVersion;
        public TimeSpan Took;
    }

    public static long HashTree(string json)
    {
        unchecked
        {
            long h = 1469598103934665603;
            foreach (var c in json) { h ^= c; h *= 1099511628211; }
            return h;
        }
    }

    public HitSet? TryGet(long colId, long treeHash, long dataVersion)
        => _cache.TryGetValue(colId, out var h) && h.TreeHash == treeHash && h.DataVersion == dataVersion ? h : null;

    public static HitSet Evaluate(FilterNode tree, IReadOnlyList<ImageItem> all, FilterContext ctx, long treeHash, long dataVersion)
    {
        var sw = Stopwatch.StartNew();
        var ids = new List<long>(256);
        foreach (var it in all)
        {
            if (it.State != FileState.Present) continue;
            if (tree.Evaluate(it, ctx)) ids.Add(it.Id);
        }
        return new HitSet
        {
            Ids = ids.ToArray(),
            TreeHash = treeHash,
            DataVersion = dataVersion,
            Took = sw.Elapsed,
        };
    }

    public void Put(long colId, HitSet hits) => _cache[colId] = hits;
    public void Invalidate() => _cache.Clear();

    public static List<ImageItem> Filter(IReadOnlyList<ImageItem> src, FilterNode? tree, FilterContext ctx)
    {
        if (tree is null) return [.. src];
        var l = new List<ImageItem>(src.Count);
        foreach (var it in src) if (tree.Evaluate(it, ctx)) l.Add(it);
        return l;
    }

    public static void Sort(List<ImageItem> list, SortSpec spec)
    {
        Comparison<ImageItem> cmp = spec.Key switch
        {
            SortKey.Name => (a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase),
            SortKey.Date => (a, b) => a.AddedAt.CompareTo(b.AddedAt),
            SortKey.Rating => (a, b) => a.Rating.CompareTo(b.Rating),
            SortKey.Resolution => (a, b) => ((long)a.Width * a.Height).CompareTo((long)b.Width * b.Height),
            SortKey.Size => (a, b) => a.Size.CompareTo(b.Size),
            SortKey.Steps => (a, b) => (a.Steps ?? 0).CompareTo(b.Steps ?? 0),
            SortKey.Cfg => (a, b) => (a.Cfg ?? 0).CompareTo(b.Cfg ?? 0),
            SortKey.Seed => (a, b) => (a.Seed ?? 0).CompareTo(b.Seed ?? 0),
            SortKey.Model => (a, b) => string.Compare(a.Model ?? "", b.Model ?? "", StringComparison.OrdinalIgnoreCase),
            _ => (a, b) => a.Id.CompareTo(b.Id),
        };

        list.Sort((a, b) =>
        {
            int r = cmp(a, b);
            if (spec.Desc) r = -r;
            return r != 0 ? r : a.Id.CompareTo(b.Id);
        });
    }

    public static string SortLabel(SortKey k) => k switch
    {
        SortKey.Name => "文件名",
        SortKey.Date => "添加时间",
        SortKey.Rating => "评分",
        SortKey.Resolution => "分辨率",
        SortKey.Size => "文件大小",
        SortKey.Steps => "步数",
        SortKey.Cfg => "CFG",
        SortKey.Seed => "种子",
        SortKey.Model => "模型",
        _ => k.ToString(),
    };
}
