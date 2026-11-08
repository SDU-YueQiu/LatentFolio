using Microsoft.Data.Sqlite;

namespace LatentFolio.Core.Db;

/// <summary>SQLite 连接 + 建表 + 迁移。数据库是唯一事实来源，元数据永不删除。</summary>
public sealed class Db : IDisposable
{
    public string FilePath { get; }
    private readonly SqliteConnection _conn;

    public const int SchemaVersion = 3;

    public Db(string filePath)
    {
        FilePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        _conn = new SqliteConnection(csb.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("PRAGMA foreign_keys=ON;");
        Exec("PRAGMA temp_store=MEMORY;");
        Exec("PRAGMA cache_size=-64000;");
        Migrate();
    }

    public SqliteConnection Connection => _conn;

    public SqliteCommand Cmd(string sql)
    {
        var c = _conn.CreateCommand();
        c.CommandText = sql;
        return c;
    }

    public void Exec(string sql)
    {
        using var c = Cmd(sql);
        c.ExecuteNonQuery();
    }

    public T? Scalar<T>(string sql, params (string, object?)[] args)
    {
        using var c = Cmd(sql);
        foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        var r = c.ExecuteScalar();
        if (r is null || r is DBNull) return default;
        return (T)Convert.ChangeType(r, typeof(T));
    }

    public long GetVersion() => Scalar<long>("PRAGMA user_version;");

    public void SetVersion(int v) => Exec($"PRAGMA user_version={v};");

    private void Migrate()
    {
        var v = GetVersion();
        if (v >= SchemaVersion) return;

        if (v < 1)
        {
            Exec("""
            CREATE TABLE IF NOT EXISTS image (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                path          TEXT    NOT NULL UNIQUE,
                folder        TEXT    NOT NULL DEFAULT '',
                file_name     TEXT    NOT NULL,
                ext           TEXT    NOT NULL DEFAULT 'png',
                size          INTEGER NOT NULL DEFAULT 0,
                mtime         INTEGER NOT NULL DEFAULT 0,
                added_at      INTEGER NOT NULL DEFAULT 0,
                width         INTEGER NOT NULL DEFAULT 0,
                height        INTEGER NOT NULL DEFAULT 0,
                state         INTEGER NOT NULL DEFAULT 0,
                model         TEXT,
                sampler       TEXT,
                scheduler     TEXT,
                steps         INTEGER,
                cfg           REAL,
                seed          INTEGER,
                denoise       REAL,
                prompt        TEXT    NOT NULL DEFAULT '',
                negative      TEXT    NOT NULL DEFAULT '',
                prompt_source TEXT    NOT NULL DEFAULT '',
                neg_is_dup    INTEGER NOT NULL DEFAULT 0,
                rating        INTEGER NOT NULL DEFAULT 0,
                note          TEXT    NOT NULL DEFAULT '',
                chars         TEXT    NOT NULL DEFAULT '',
                artists       TEXT    NOT NULL DEFAULT '',
                search_text   TEXT    NOT NULL DEFAULT '',
                hash          INTEGER NOT NULL DEFAULT 0,
                dhash         INTEGER NOT NULL DEFAULT 0,
                aspect        INTEGER NOT NULL DEFAULT 0,
                has_thumb     INTEGER NOT NULL DEFAULT 0,
                archived      INTEGER NOT NULL DEFAULT 0,
                workflow_blob BLOB
            );
            CREATE INDEX IF NOT EXISTS ix_image_folder ON image(folder);
            CREATE INDEX IF NOT EXISTS ix_image_hash   ON image(hash);
            CREATE INDEX IF NOT EXISTS ix_image_dhash  ON image(dhash);
            CREATE INDEX IF NOT EXISTS ix_image_state  ON image(state);
            CREATE INDEX IF NOT EXISTS ix_image_rating ON image(rating);

            CREATE TABLE IF NOT EXISTS app_tag (
                id    INTEGER PRIMARY KEY AUTOINCREMENT,
                name  TEXT NOT NULL UNIQUE,
                color TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS image_app_tag (
                image_id INTEGER NOT NULL REFERENCES image(id) ON DELETE CASCADE,
                tag_id   INTEGER NOT NULL REFERENCES app_tag(id) ON DELETE CASCADE,
                PRIMARY KEY (image_id, tag_id)
            );
            CREATE INDEX IF NOT EXISTS ix_iat_tag ON image_app_tag(tag_id);

            CREATE TABLE IF NOT EXISTS col (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                name      TEXT    NOT NULL,
                color     TEXT    NOT NULL DEFAULT '',
                ord       INTEGER NOT NULL DEFAULT 0,
                tree_json TEXT    NOT NULL DEFAULT '{}',
                count     INTEGER NOT NULL DEFAULT -1,
                count_ver INTEGER NOT NULL DEFAULT -1,
                created_at INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS setting (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS folder (
                path TEXT PRIMARY KEY,
                ord  INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS vsr_cache (
                image_id INTEGER PRIMARY KEY,
                path     TEXT NOT NULL,
                at       INTEGER NOT NULL
            );
            """);
        }

        if (v < 2)
        {
            // 人物头像自定义：没设过的角色用「最高分那张」
            Exec("""
            CREATE TABLE IF NOT EXISTS person_avatar (
                tag      TEXT PRIMARY KEY,
                image_id INTEGER NOT NULL
            );
            """);
        }

        if (v < 3)
        {
            // 人物条目：danbooru tag + 中英文名 + 作品 + 简介 + 封面
            Exec("""
            CREATE TABLE IF NOT EXISTS person (
                tag       TEXT PRIMARY KEY,
                name_zh   TEXT    NOT NULL DEFAULT '',
                name_en   TEXT    NOT NULL DEFAULT '',
                work      TEXT    NOT NULL DEFAULT '',
                bio       TEXT    NOT NULL DEFAULT '',
                avatar_id INTEGER NOT NULL DEFAULT 0,
                source    TEXT    NOT NULL DEFAULT 'auto'
            );
            INSERT OR IGNORE INTO person(tag, avatar_id, source)
                SELECT tag, image_id, 'manual' FROM person_avatar;

            -- 裸名 → 正式 tag 的本地别名表（候选池「确认」后写这里）
            CREATE TABLE IF NOT EXISTS alias (
                key    TEXT PRIMARY KEY,
                tag    TEXT NOT NULL,
                source TEXT NOT NULL DEFAULT 'manual'
            );
            CREATE INDEX IF NOT EXISTS ix_alias_tag ON alias(tag);

            -- 候选池：提示词里出现、但词表匹配不上的裸名
            CREATE TABLE IF NOT EXISTS cand (
                key     TEXT PRIMARY KEY,
                suggest TEXT    NOT NULL DEFAULT '',
                hits    INTEGER NOT NULL DEFAULT 0,
                state   INTEGER NOT NULL DEFAULT 0,   -- 0 待定 / 1 已确认 / 2 已忽略
                at      INTEGER NOT NULL DEFAULT 0
            );

            -- 快捷模板：编辑器右栏「套用」，也存「另存为模板」
            CREATE TABLE IF NOT EXISTS tpl (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                name      TEXT NOT NULL,
                tree_json TEXT NOT NULL DEFAULT '{}',
                at        INTEGER NOT NULL DEFAULT 0
            );

            -- 按内容哈希判重时被跳过的文件，别再每次扫描重解析一遍
            CREATE TABLE IF NOT EXISTS skip (
                path TEXT PRIMARY KEY,
                hash INTEGER NOT NULL DEFAULT 0,
                at   INTEGER NOT NULL DEFAULT 0
            );
            """);
        }

        SetVersion(SchemaVersion);
    }

    public void Dispose() => _conn.Dispose();
}
