namespace LatentFolio.Core.Text;

using LatentFolio.Core.Comfy;

/// <summary>角色 / 画师词表。从 Assets 里的紧凑词表加载，后台线程预热。</summary>
public sealed class Vocabulary
{
    public HashSet<string> CharTags { get; private set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> CharByName { get; private set; } = new(StringComparer.Ordinal);
    public HashSet<string> Artists { get; private set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ZhNames { get; private set; } = new(StringComparer.Ordinal);

    public bool Ready { get; private set; }
    public string? Error { get; private set; }

    private readonly object _lock = new();
    private Task? _task;

    public static Vocabulary Instance { get; } = new();

    public void Warm(string assetsDir)
    {
        lock (_lock)
        {
            if (_task is not null) return;
            _task = Task.Run(() =>
            {
                try
                {
                    var chars = ReadPairs(Path.Combine(assetsDir, "characters.tsv"));
                    var tags = new HashSet<string>(chars.Count, StringComparer.Ordinal);
                    var byName = new Dictionary<string, string>(chars.Count, StringComparer.Ordinal);
                    foreach (var (tag, name) in chars)
                    {
                        tags.Add(tag);
                        var n = name.Length > 0 ? name.Replace(' ', '_').ToLowerInvariant() : BaseName(tag);
                        if (n.Length == 0) continue;
                        byName.TryAdd(n, tag);
                    }
                    CharTags = tags;
                    CharByName = byName;

                    var artists = ReadTags(Path.Combine(assetsDir, "artists.txt"));
                    Artists = new HashSet<string>(artists, StringComparer.Ordinal);

                    var zh = ReadPairs(Path.Combine(assetsDir, "zh_names.tsv"));
                    var zd = new Dictionary<string, string>(zh.Count, StringComparer.Ordinal);
                    foreach (var (k, v) in zh) zd[k] = v;
                    ZhNames = zd;
                    Ready = true;
                }
                catch (Exception ex)
                {
                    Error = ex.Message;
                    Ready = true;
                }
            });
        }
    }

    public void WaitReady(TimeSpan? timeout = null)
    {
        try { _task?.Wait(timeout ?? TimeSpan.FromSeconds(30)); } catch { }
    }

    public static string BaseName(string tag)
    {
        var i = tag.IndexOf("_(", StringComparison.Ordinal);
        return i > 0 ? tag[..i] : tag;
    }

    public string Display(string tag)
        => ZhNames.TryGetValue(tag, out var zh) && zh.Length > 0 ? zh : tag;

    /// <summary>匹配角色：只认 exact / 去系列后缀；裸名进候选池。</summary>
    public (string? Tag, string? Candidate) MatchCharacter(string token)
    {
        if (CharTags.Contains(token)) return (token, null);

        var t = token;
        for (int i = 0; i < 3; i++)
        {
            var m = System.Text.RegularExpressions.Regex.Match(t, @"_\([^()]*\)$");
            if (!m.Success) break;
            var t2 = t[..m.Index] + t[(m.Index + m.Length)..];
            if (CharTags.Contains(t2)) return (t2, null);
            t = t2;
        }

        if (!token.Contains('('))
        {
            var baseName = BaseName(token);
            if (CharByName.TryGetValue(baseName, out var sug)) return (null, sug);
        }
        return (null, null);
    }

    private static List<string> ReadTags(string path)
    {
        var lines = File.ReadAllLines(path);
        var l = new List<string>(lines.Length);
        foreach (var s in lines)
        {
            var t = s.Trim();
            if (t.Length > 0) l.Add(t);
        }
        return l;
    }

    private static List<(string, string)> ReadPairs(string path)
    {
        var lines = File.ReadAllLines(path);
        var l = new List<(string, string)>(lines.Length);
        foreach (var s in lines)
        {
            if (s.Length == 0) continue;
            var i = s.IndexOf('\t');
            if (i <= 0) { l.Add((s.Trim(), "")); continue; }
            l.Add((s[..i].Trim(), s[(i + 1)..].Trim()));
        }
        return l;
    }

    /// <summary>对一条提示词做角色/画师识别。</summary>
    public (string[] Chars, string[] Artists) Tag(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return ([], []);
        var (tokens, artists) = GraphParser.Tokenize(prompt);
        var chars = new List<string>(4);
        var arts = new List<string>(4);

        foreach (var t in tokens)
        {
            if (CharTags.Count > 0)
            {
                var (c, _) = MatchCharacter(t);
                if (c is not null && !chars.Contains(c)) chars.Add(c);
            }
            if (Artists.Contains(t) && !arts.Contains(t)) arts.Add(t);
        }
        foreach (var a in artists) if (!arts.Contains(a)) arts.Add(a);

        return (chars.ToArray(), arts.ToArray());
    }
}
