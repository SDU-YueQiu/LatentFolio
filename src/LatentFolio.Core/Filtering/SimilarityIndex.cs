using LatentFolio.Core.Models;

namespace LatentFolio.Core.Filtering;

/// <summary>
/// 相似图分组：用 64 位 dHash 的汉明距离找近似重复。
/// 直接两两比较是 O(n²)，10 万张就是 10^10 次，跑不动。
/// 这里把 64 位拆成 4 个 16 位段做 LSH 分桶，只在同桶里比，实际近似线性。
/// </summary>
public sealed class SimilarityIndex
{
    public Dictionary<long, int> GroupOf { get; } = [];
    public int GroupCount { get; private set; }
    public HashSet<long> InGroup { get; } = [];

    /// <summary>图片 id → 所在组一共几张。给「同组 ≥N 张」条件用。</summary>
    public Dictionary<long, int> GroupSize { get; } = [];

    public const int DefaultMaxDistance = 6;

    public static SimilarityIndex Build(IReadOnlyList<ImageItem> images, int maxDistance = DefaultMaxDistance)
    {
        var idx = new SimilarityIndex();
        var items = images.Where(x => x.State != FileState.Deleted && x.DHash != 0).ToList();
        if (items.Count < 2) return idx;

        // 4 段 LSH 桶
        var buckets = new Dictionary<ushort, List<int>>(items.Count * 2);
        for (int i = 0; i < items.Count; i++)
        {
            ulong h = (ulong)items[i].DHash;
            for (int q = 0; q < 4; q++)
            {
                var key = (ushort)((h >> (q * 16)) & 0xFFFF);
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<int>(4);
                list.Add(i);
            }
        }

        var parent = new int[items.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;

        int Find(int a)
        {
            while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; }
            return a;
        }
        void Union(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a != b) parent[b] = a;
        }

        for (int i = 0; i < items.Count; i++)
        {
            ulong h = (ulong)items[i].DHash;
            for (int q = 0; q < 4; q++)
            {
                var key = (ushort)((h >> (q * 16)) & 0xFFFF);
                if (!buckets.TryGetValue(key, out var list)) continue;
                foreach (var j in list)
                {
                    if (j <= i) continue;
                    if (System.Numerics.BitOperations.PopCount(h ^ (ulong)items[j].DHash) <= maxDistance)
                        Union(i, j);
                }
            }
        }

        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < items.Count; i++)
        {
            var r = Find(i);
            if (!groups.TryGetValue(r, out var l)) groups[r] = l = new List<int>(2);
            l.Add(i);
        }

        int gid = 0;
        foreach (var kv in groups)
        {
            if (kv.Value.Count < 2) continue;
            gid++;
            foreach (var i in kv.Value)
            {
                idx.GroupOf[items[i].Id] = gid;
                idx.InGroup.Add(items[i].Id);
                idx.GroupSize[items[i].Id] = kv.Value.Count;
            }
        }
        idx.GroupCount = gid;
        return idx;
    }
}
