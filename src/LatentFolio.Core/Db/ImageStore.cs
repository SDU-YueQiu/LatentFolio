using System.Text.Json;
using LatentFolio.Core.Models;

namespace LatentFolio.Core.Db;

/// <summary>图片表读写。全量索引在内存里，写回是增量。</summary>
public sealed class ImageStore(Db db)
{
    public Db Db => db;

    /// <summary>读出全量图片（不含工作流）。</summary>
    public List<ImageItem> LoadAll()
    {
        var list = new List<ImageItem>(8192);
        using var c = db.Cmd("""
            SELECT id,path,folder,file_name,ext,size,mtime,added_at,width,height,state,
                   model,sampler,scheduler,steps,cfg,seed,denoise,
                   prompt,negative,prompt_source,neg_is_dup,rating,note,
                   chars,artists,search_text,hash,dhash,aspect
            FROM image ORDER BY id
            """);
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var it = new ImageItem
            {
                Id = r.GetInt64(0),
                Path = r.GetString(1),
                Folder = r.GetString(2),
                FileName = r.GetString(3),
                Ext = r.GetString(4),
                Size = r.GetInt64(5),
                MTime = r.GetInt64(6),
                AddedAt = r.GetInt64(7),
                Width = r.GetInt32(8),
                Height = r.GetInt32(9),
                State = (FileState)r.GetInt32(10),
                Model = r.IsDBNull(11) ? null : r.GetString(11),
                Sampler = r.IsDBNull(12) ? null : r.GetString(12),
                Scheduler = r.IsDBNull(13) ? null : r.GetString(13),
                Steps = r.IsDBNull(14) ? null : r.GetInt32(14),
                Cfg = r.IsDBNull(15) ? null : r.GetDouble(15),
                Seed = r.IsDBNull(16) ? null : r.GetInt64(16),
                Denoise = r.IsDBNull(17) ? null : r.GetDouble(17),
                Prompt = r.GetString(18),
                Negative = r.GetString(19),
                PromptSource = r.GetString(20),
                NegIsDup = r.GetInt32(21) != 0,
                Rating = r.GetInt32(22),
                Note = r.GetString(23),
                Chars = Split(r.GetString(24)),
                Artists = Split(r.GetString(25)),
                SearchText = r.GetString(26),
                Hash = r.GetInt64(27),
                DHash = r.GetInt64(28),
                Aspect = (AspectBucket)r.GetInt32(29),
            };
            list.Add(it);
        }
        return list;
    }

    private static string[] Split(string s)
        => s.Length == 0 ? [] : s.Split('\u0001', StringSplitOptions.RemoveEmptyEntries);

    public static string Join(IEnumerable<string> items) => string.Join('\u0001', items);

    public long Insert(ImageItem it)
    {
        using var c = db.Cmd("""
            INSERT INTO image(path,folder,file_name,ext,size,mtime,added_at,width,height,state,
                model,sampler,scheduler,steps,cfg,seed,denoise,prompt,negative,prompt_source,neg_is_dup,
                rating,note,chars,artists,search_text,hash,dhash,aspect,workflow_blob)
            VALUES($p,$f,$n,$e,$s,$m,$a,$w,$h,$st,$mo,$sa,$sc,$sp,$cf,$se,$de,$pr,$ne,$ps,$nd,$ra,$no,$ch,$ar,$tx,$ha,$dh,$as,$wb);
            SELECT last_insert_rowid();
            """);
        Bind(c, it, includeWorkflow: true);
        it.Id = (long)c.ExecuteScalar()!;
        return it.Id;
    }

    public void Update(ImageItem it)
    {
        using var c = db.Cmd("""
            UPDATE image SET
                path=$p, folder=$f, file_name=$n, ext=$e, size=$s, mtime=$m, width=$w, height=$h, state=$st,
                model=$mo, sampler=$sa, scheduler=$sc, steps=$sp, cfg=$cf, seed=$se, denoise=$de,
                prompt=$pr, negative=$ne, prompt_source=$ps, neg_is_dup=$nd,
                chars=$ch, artists=$ar, search_text=$tx, hash=$ha, dhash=$dh, aspect=$as
            WHERE id=$id
            """);
        Bind(c, it, includeWorkflow: false);
        c.Parameters.AddWithValue("$id", it.Id);
        c.ExecuteNonQuery();
    }

    public void UpdateUser(ImageItem it)
    {
        using var c = db.Cmd("UPDATE image SET rating=$ra, note=$no WHERE id=$id");
        c.Parameters.AddWithValue("$ra", it.Rating);
        c.Parameters.AddWithValue("$no", it.Note);
        c.Parameters.AddWithValue("$id", it.Id);
        c.ExecuteNonQuery();
    }

    public void UpdateState(ImageItem it)
    {
        using var c = db.Cmd("UPDATE image SET state=$st WHERE id=$id");
        c.Parameters.AddWithValue("$st", (int)it.State);
        c.Parameters.AddWithValue("$id", it.Id);
        c.ExecuteNonQuery();
    }

    private static void Bind(Microsoft.Data.Sqlite.SqliteCommand c, ImageItem it, bool includeWorkflow)
    {
        c.Parameters.AddWithValue("$p", it.Path);
        c.Parameters.AddWithValue("$f", it.Folder);
        c.Parameters.AddWithValue("$n", it.FileName);
        c.Parameters.AddWithValue("$e", it.Ext);
        c.Parameters.AddWithValue("$s", it.Size);
        c.Parameters.AddWithValue("$m", it.MTime);
        c.Parameters.AddWithValue("$a", it.AddedAt);
        c.Parameters.AddWithValue("$w", it.Width);
        c.Parameters.AddWithValue("$h", it.Height);
        c.Parameters.AddWithValue("$st", (int)it.State);
        c.Parameters.AddWithValue("$mo", (object?)it.Model ?? DBNull.Value);
        c.Parameters.AddWithValue("$sa", (object?)it.Sampler ?? DBNull.Value);
        c.Parameters.AddWithValue("$sc", (object?)it.Scheduler ?? DBNull.Value);
        c.Parameters.AddWithValue("$sp", (object?)it.Steps ?? DBNull.Value);
        c.Parameters.AddWithValue("$cf", (object?)it.Cfg ?? DBNull.Value);
        c.Parameters.AddWithValue("$se", (object?)it.Seed ?? DBNull.Value);
        c.Parameters.AddWithValue("$de", (object?)it.Denoise ?? DBNull.Value);
        c.Parameters.AddWithValue("$pr", it.Prompt);
        c.Parameters.AddWithValue("$ne", it.Negative);
        c.Parameters.AddWithValue("$ps", it.PromptSource);
        c.Parameters.AddWithValue("$nd", it.NegIsDup ? 1 : 0);
        c.Parameters.AddWithValue("$ra", it.Rating);
        c.Parameters.AddWithValue("$no", it.Note);
        c.Parameters.AddWithValue("$ch", Join(it.Chars));
        c.Parameters.AddWithValue("$ar", Join(it.Artists));
        c.Parameters.AddWithValue("$tx", it.SearchText);
        c.Parameters.AddWithValue("$ha", it.Hash);
        c.Parameters.AddWithValue("$dh", it.DHash);
        c.Parameters.AddWithValue("$as", (int)it.Aspect);
        if (includeWorkflow) c.Parameters.AddWithValue("$wb", DBNull.Value);
    }

    public void SetWorkflow(long id, byte[]? gzip)
    {
        using var c = db.Cmd("UPDATE image SET workflow_blob=$b WHERE id=$id");
        c.Parameters.AddWithValue("$b", (object?)gzip ?? DBNull.Value);
        c.Parameters.AddWithValue("$id", id);
        c.ExecuteNonQuery();
    }

    public byte[]? GetWorkflow(long id)
    {
        using var c = db.Cmd("SELECT workflow_blob FROM image WHERE id=$id");
        c.Parameters.AddWithValue("$id", id);
        var o = c.ExecuteScalar();
        return o is byte[] b ? b : null;
    }

    public void DeleteMissing(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0) return;
        using var tx = db.Connection.BeginTransaction();
        using var c = db.Cmd("DELETE FROM image WHERE id=$id");
        var p = c.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
        foreach (var id in ids) { p.Value = id; c.ExecuteNonQuery(); }
        tx.Commit();
    }

    // ---------------- 文件夹 ----------------

    public Dictionary<string, int> FolderCounts()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var c = db.Cmd("SELECT folder, COUNT(*) FROM image WHERE state<>2 GROUP BY folder");
        using var r = c.ExecuteReader();
        while (r.Read()) d[r.GetString(0)] = r.GetInt32(1);
        return d;
    }

    // ---------------- 自建标签 ----------------

    public List<(long Id, string Name, string Color)> LoadTags()
    {
        var l = new List<(long, string, string)>();
        using var c = db.Cmd("SELECT id,name,color FROM app_tag ORDER BY name");
        using var r = c.ExecuteReader();
        while (r.Read()) l.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return l;
    }

    public Dictionary<long, List<long>> LoadImageTags()
    {
        var d = new Dictionary<long, List<long>>();
        using var c = db.Cmd("SELECT image_id,tag_id FROM image_app_tag");
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var img = r.GetInt64(0);
            if (!d.TryGetValue(img, out var l)) d[img] = l = [];
            l.Add(r.GetInt64(1));
        }
        return d;
    }

    // ---------------- 设置 ----------------

    public string? GetSetting(string key)
    {
        using var c = db.Cmd("SELECT value FROM setting WHERE key=$k");
        c.Parameters.AddWithValue("$k", key);
        return c.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var c = db.Cmd("INSERT INTO setting(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v");
        c.Parameters.AddWithValue("$k", key);
        c.Parameters.AddWithValue("$v", value);
        c.ExecuteNonQuery();
    }

    public int[] LoadRatingHistogram()
    {
        var a = new int[6];
        using var c = db.Cmd("SELECT rating, COUNT(*) FROM image WHERE state<>2 GROUP BY rating");
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var k = r.GetInt32(0);
            if (k is >= 0 and <= 5) a[k] = r.GetInt32(1);
        }
        return a;
    }
}
