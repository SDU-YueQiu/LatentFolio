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

    // ---------------- 人物条目 / 别名 / 候选池 / 模板 / 跳过表 ----------------

    public sealed class PersonRow
    {
        public string Tag = "";
        public string NameZh = "";
        public string NameEn = "";
        public string Work = "";
        public string Bio = "";
        public long AvatarId;
        public string Source = "auto";
    }

    public List<PersonRow> LoadPeople()
    {
        var l = new List<PersonRow>();
        using var c = db.Cmd("SELECT tag,name_zh,name_en,work,bio,avatar_id,source FROM person");
        using var r = c.ExecuteReader();
        while (r.Read())
            l.Add(new PersonRow
            {
                Tag = r.GetString(0),
                NameZh = r.GetString(1),
                NameEn = r.GetString(2),
                Work = r.GetString(3),
                Bio = r.GetString(4),
                AvatarId = r.GetInt64(5),
                Source = r.GetString(6),
            });
        return l;
    }

    public void SavePerson(PersonRow p)
    {
        using var c = db.Cmd("""
            INSERT INTO person(tag,name_zh,name_en,work,bio,avatar_id,source)
            VALUES($t,$zh,$en,$w,$b,$a,$s)
            ON CONFLICT(tag) DO UPDATE SET
                name_zh=$zh, name_en=$en, work=$w, bio=$b, avatar_id=$a, source=$s
            """);
        c.Parameters.AddWithValue("$t", p.Tag);
        c.Parameters.AddWithValue("$zh", p.NameZh);
        c.Parameters.AddWithValue("$en", p.NameEn);
        c.Parameters.AddWithValue("$w", p.Work);
        c.Parameters.AddWithValue("$b", p.Bio);
        c.Parameters.AddWithValue("$a", p.AvatarId);
        c.Parameters.AddWithValue("$s", p.Source);
        c.ExecuteNonQuery();
    }

    public Dictionary<string, string> LoadAliases()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        using var c = db.Cmd("SELECT key,tag FROM alias");
        using var r = c.ExecuteReader();
        while (r.Read()) d[r.GetString(0)] = r.GetString(1);
        return d;
    }

    public void UpsertAlias(string key, string tag, string source = "manual")
    {
        using var c = db.Cmd("INSERT INTO alias(key,tag,source) VALUES($k,$t,$s) ON CONFLICT(key) DO UPDATE SET tag=$t, source=$s");
        c.Parameters.AddWithValue("$k", key);
        c.Parameters.AddWithValue("$t", tag);
        c.Parameters.AddWithValue("$s", source);
        c.ExecuteNonQuery();
    }

    public void DeleteAlias(string key)
    {
        using var c = db.Cmd("DELETE FROM alias WHERE key=$k");
        c.Parameters.AddWithValue("$k", key);
        c.ExecuteNonQuery();
    }

    /// <summary>清掉自动合并出来的别名（手工的不动），合并规则改了就重来一遍。</summary>
    public int DeleteAutoAliases()
    {
        using var c = db.Cmd("DELETE FROM alias WHERE source='auto'");
        return c.ExecuteNonQuery();
    }

    public sealed class CandRow
    {
        public string Key = "";
        public string Suggest = "";
        public int Hits;
        public int State;      // 0 待定 / 1 已确认 / 2 已忽略
        public long At;
    }

    public List<CandRow> LoadCandidates(int state = 0)
    {
        var l = new List<CandRow>();
        using var c = db.Cmd("SELECT key,suggest,hits,state,at FROM cand WHERE state=$s ORDER BY hits DESC,key");
        c.Parameters.AddWithValue("$s", state);
        using var r = c.ExecuteReader();
        while (r.Read())
            l.Add(new CandRow
            {
                Key = r.GetString(0),
                Suggest = r.GetString(1),
                Hits = r.GetInt32(2),
                State = r.GetInt32(3),
                At = r.GetInt64(4),
            });
        return l;
    }

    /// <summary>把一批候选写进池子；已存在的只更新命中数，不动状态。</summary>
    public void MergeCandidates(IEnumerable<(string Key, string Suggest, int Hits)> rows)
    {
        using var tx = db.Connection.BeginTransaction();
        using var c = db.Cmd("""
            INSERT INTO cand(key,suggest,hits,state,at) VALUES($k,$s,$h,0,$at)
            ON CONFLICT(key) DO UPDATE SET hits=$h, suggest=CASE WHEN $s<>'' THEN $s ELSE suggest END
            """);
        var pk = c.Parameters.Add("$k", Microsoft.Data.Sqlite.SqliteType.Text);
        var ps = c.Parameters.Add("$s", Microsoft.Data.Sqlite.SqliteType.Text);
        var ph = c.Parameters.Add("$h", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pa = c.Parameters.Add("$at", Microsoft.Data.Sqlite.SqliteType.Integer);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var (key, sug, hits) in rows)
        {
            pk.Value = key;
            ps.Value = sug;
            ph.Value = hits;
            pa.Value = now;
            c.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void SetCandidateState(string key, int state)
    {
        using var c = db.Cmd("UPDATE cand SET state=$s WHERE key=$k");
        c.Parameters.AddWithValue("$s", state);
        c.Parameters.AddWithValue("$k", key);
        c.ExecuteNonQuery();
    }

    public int CandidateCount(int state = 0)
        => ScalarInt("SELECT COUNT(*) FROM cand WHERE state=$s", state);

    private int ScalarInt(string sql, int arg)
    {
        using var c = db.Cmd(sql);
        c.Parameters.AddWithValue("$s", arg);
        var o = c.ExecuteScalar();
        return o is null or DBNull ? 0 : Convert.ToInt32(o);
    }

    public List<(long Id, string Name, string Json)> LoadTemplates()
    {
        var l = new List<(long, string, string)>();
        using var c = db.Cmd("SELECT id,name,tree_json FROM tpl ORDER BY id");
        using var r = c.ExecuteReader();
        while (r.Read()) l.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return l;
    }

    public void AddTemplate(string name, string json)
    {
        using var c = db.Cmd("INSERT INTO tpl(name,tree_json,at) VALUES($n,$j,$a)");
        c.Parameters.AddWithValue("$n", name);
        c.Parameters.AddWithValue("$j", json);
        c.Parameters.AddWithValue("$a", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        c.ExecuteNonQuery();
    }

    public void DeleteTemplate(long id)
    {
        using var c = db.Cmd("DELETE FROM tpl WHERE id=$id");
        c.Parameters.AddWithValue("$id", id);
        c.ExecuteNonQuery();
    }

    public Dictionary<string, long> LoadSkipped()
    {
        var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var c = db.Cmd("SELECT path,hash FROM skip");
        using var r = c.ExecuteReader();
        while (r.Read()) d[r.GetString(0)] = r.GetInt64(1);
        return d;
    }

    public void AddSkipped(string path, long hash)
    {
        using var c = db.Cmd("INSERT OR REPLACE INTO skip(path,hash,at) VALUES($p,$h,$a)");
        c.Parameters.AddWithValue("$p", path);
        c.Parameters.AddWithValue("$h", hash);
        c.Parameters.AddWithValue("$a", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        c.ExecuteNonQuery();
    }

    public void ClearSkipped() => Exec("DELETE FROM skip");

    public void Exec(string sql)
    {
        using var c = db.Cmd(sql);
        c.ExecuteNonQuery();
    }
}
