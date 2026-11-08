using System.Text;
using System.Text.RegularExpressions;

namespace LatentFolio.Core.Comfy;

/// <summary>解析结果。</summary>
public sealed class ParseResult
{
    public string Positive = "";
    public string Negative = "";
    public string Method = "none";
    public bool Split;
    public string[] PosClasses = [];
    public string[] NegClasses = [];

    public string? Model;
    public string? Sampler;
    public string? Scheduler;
    public int? Steps;
    public double? Cfg;
    public long? Seed;
    public double? Denoise;
    public int? Width;
    public int? Height;

    public bool Ok => Positive.Length > 0 || Negative.Length > 0;
}

/// <summary>
/// ComfyUI API 图（PNG 里的 prompt 段）正负面提示词识别。
/// 参考实现见 tools/ref/prompt_parse2.py —— 行为必须一致。
/// </summary>
public static partial class GraphParser
{
    private static readonly HashSet<string> NoFollow = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "clip", "vae", "clip_vision", "image", "images", "pixels", "latent", "latent_image",
        "samples", "control_net", "noise", "sigmas", "guider", "sampler", "scheduler", "mask",
        "filename_prefix", "upload", "video", "audio", "style_model", "pipe", "basic_pipe",
        "lora_name", "ckpt_name", "vae_name", "positive_ref", "negative_ref",
        "clip_l", "clip_g", "t5xxl", "refiner_model", "style_model_name", "control_net_name",
        "upscale_model", "booster", "model_name", "unet_name", "dtype", "device", "weight_dtype",
        "sampler_name", "embedding", "images_out", "filename",
    };

    private static readonly string[] TextKeys =
    [
        "text", "text_g", "text_l", "value", "string", "prompt", "wildcard_text",
        "populated_text", "text_0", "text_1", "text_positive", "text_negative",
        "positive_prompt", "negative_prompt", "text_a", "text_b",
    ];

    private static readonly HashSet<string> PosKeys = new(StringComparer.OrdinalIgnoreCase)
    { "positive", "text_positive", "positive_prompt" };

    private static readonly HashSet<string> NegKeys = new(StringComparer.OrdinalIgnoreCase)
    { "negative", "text_negative", "negative_prompt" };

    private static readonly string[] ConcatStrings = ["string_a", "string_b", "text_a", "text_b", "a", "b"];

    [GeneratedRegex("concat|join|append|stringfunction|combine|text_?merge", RegexOptions.IgnoreCase)]
    private static partial Regex ConcatRe();

    [GeneratedRegex(@"negative\s*prompt\s*[:：]|【\s*负面\s*】|#{2,}\s*negative|^\s*neg(?:ative)?\s*[:：]", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex NegMarkerRe();

    [GeneratedRegex(@"<lora:[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LoraRe();

    // ---------- 图模型 ----------

    private sealed record Node(string Id, string ClassType, Dictionary<string, object?> Inputs);

    private static Dictionary<string, Node> BuildGraph(System.Text.Json.JsonElement root)
    {
        var map = new Dictionary<string, Node>(64, StringComparer.Ordinal);
        foreach (var p in root.EnumerateObject())
        {
            if (p.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            string cls = "";
            var inputs = new Dictionary<string, object?>(8, StringComparer.OrdinalIgnoreCase);
            foreach (var q in p.Value.EnumerateObject())
            {
                if (q.NameEquals("class_type") || q.NameEquals("type"))
                {
                    cls = q.Value.ValueKind == System.Text.Json.JsonValueKind.String ? q.Value.GetString() ?? "" : "";
                }
                else if (q.NameEquals("inputs") && q.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var iv in q.Value.EnumerateObject())
                        inputs[iv.Name] = ToObj(iv.Value);
                }
            }
            map[p.Name] = new Node(p.Name, cls, inputs);
        }
        return map;
    }

    private static object? ToObj(System.Text.Json.JsonElement v) => v.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => v.GetString(),
        System.Text.Json.JsonValueKind.Number => NumObj(v),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.Array => ToLink(v),
        _ => null,
    };

    // 注意：不能写成三元表达式 `l ? long : double`，那会把整型统一提升成 double，
    // 步数/种子就全丢了（踩过，实测 1,200 张图 steps 为 null）。
    private static object NumObj(System.Text.Json.JsonElement v)
    {
        if (v.TryGetInt64(out var l)) return l;
        return v.GetDouble();
    }

    /// <summary>[nodeId, slot] 形式的连线。</summary>
    private static object? ToLink(System.Text.Json.JsonElement v)
    {
        if (v.GetArrayLength() != 2) return null;
        var a = v[0];
        if (a.ValueKind != System.Text.Json.JsonValueKind.String) return null;
        return new Link(a.GetString()!, 0);
    }

    private sealed record Link(string NodeId, int Slot);

    // ---------- 文本求值 ----------

    private static string ResolveValue(Dictionary<string, Node> g, object? v, int depth, HashSet<string> seen)
    {
        if (v is string s) return s;
        if (v is not Link l || depth > 8) return "";
        if (!seen.Add(l.NodeId)) return "";
        if (!g.TryGetValue(l.NodeId, out var node)) return "";
        return ResolveNodeText(g, node, depth + 1, seen);
    }

    private static string ResolveNodeText(Dictionary<string, Node> g, Node node, int depth, HashSet<string> seen)
    {
        var ins = node.Inputs;
        bool isConcat = ConcatRe().IsMatch(node.ClassType)
                        || ins.ContainsKey("string_a") || ins.ContainsKey("string_b");

        if (isConcat)
        {
            var parts = new List<string>(3);
            foreach (var k in ConcatStrings)
            {
                if (!ins.TryGetValue(k, out var raw)) continue;
                var t = ResolveValue(g, raw, depth, [.. seen]);
                if (!string.IsNullOrWhiteSpace(t)) parts.Add(t.Trim());
            }
            string delim = "";
            if (ins.TryGetValue("delimiter", out var d1) && d1 is string ds) delim = ds;
            else if (ins.TryGetValue("delim", out var d2) && d2 is string ds2) delim = ds2;
            return string.Join(delim, parts);
        }

        foreach (var k in TextKeys)
        {
            if (ins.TryGetValue(k, out var v) && v is string s && !string.IsNullOrWhiteSpace(s))
                return s;
        }
        return "";
    }

    private static void WalkUp(Dictionary<string, Node> g, string nodeId, string role,
        List<(string Id, string Role, string Text, string Cls)> found, HashSet<string> seen, int depth)
    {
        if (depth > 25 || !seen.Add(nodeId)) return;
        if (!g.TryGetValue(nodeId, out var node)) return;

        var txt = ResolveNodeText(g, node, 0, []);
        if (!string.IsNullOrWhiteSpace(txt))
        {
            found.Add((nodeId, role, txt, node.ClassType));
            return;
        }

        foreach (var (k, v) in node.Inputs)
        {
            if (v is not Link l) continue;
            var kl = k.ToLowerInvariant();
            if (NoFollow.Contains(kl)) continue;
            if (PosKeys.Contains(kl) && role != "positive") continue;
            if (NegKeys.Contains(kl) && role != "negative") continue;
            WalkUp(g, l.NodeId, role, found, [.. seen], depth + 1);
        }
    }

    public static ParseResult ParseWorkflowApiJson(string promptJson)
    {
        var res = new ParseResult();
        if (string.IsNullOrWhiteSpace(promptJson)) return res;

        System.Text.Json.JsonDocument? doc = TryParse(promptJson);
        // ComfyUI 里个别节点会把 is_changed 写成 JS 的 NaN / Infinity。
        // 这不是合法 JSON，Python 的 json.loads 能忍，System.Text.Json 不能。
        // 实测 1,192 张图因为这一个字面量整份解析失败。
        if (doc is null)
        {
            var fixedJson = NaNRe().Replace(promptJson, "null");
            if (!ReferenceEquals(fixedJson, promptJson)) doc = TryParse(fixedJson);
        }
        if (doc is null) return res;

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return res;
            var g = BuildGraph(root);
            if (g.Count == 0) return res;
            Fill(g, res);
        }
        return res;
    }

    private static System.Text.Json.JsonDocument? TryParse(string json)
    {
        try { return System.Text.Json.JsonDocument.Parse(json); }
        catch { return null; }
    }

    [GeneratedRegex(@"(?<=[:\[,\s])-?(?:Infinity|NaN)(?=\s*[,\}\]])")]
    private static partial Regex NaNRe();

    private static void Fill(Dictionary<string, Node> g, ParseResult res)
    {
        var found = new List<(string Id, string Role, string Text, string Cls)>(8);
        var seedsPos = new List<string>();
        var seedsNeg = new List<string>();

        foreach (var node in g.Values)
        {
            foreach (var (k, v) in node.Inputs)
            {
                var kl = k.ToLowerInvariant();
                bool isPos = PosKeys.Contains(kl);
                bool isNeg = NegKeys.Contains(kl);
                bool isCond = (kl is "conditioning" or "cond") && node.ClassType.Contains("uider", StringComparison.OrdinalIgnoreCase);

                if (isPos)
                {
                    if (v is string s && !string.IsNullOrWhiteSpace(s)) found.Add((node.Id, "positive", s, node.ClassType));
                    else if (v is Link l) seedsPos.Add(l.NodeId);
                }
                else if (isNeg)
                {
                    if (v is string s && !string.IsNullOrWhiteSpace(s)) found.Add((node.Id, "negative", s, node.ClassType));
                    else if (v is Link l) seedsNeg.Add(l.NodeId);
                }
                else if (isCond)
                {
                    if (v is Link l) seedsPos.Add(l.NodeId);
                }
            }
        }

        foreach (var s in seedsPos) WalkUp(g, s, "positive", found, [], 0);
        foreach (var s in seedsNeg) WalkUp(g, s, "negative", found, [], 0);

        res.Method = found.Any(x => x.Role == "positive") ? "conditioning-trace" : "fallback";

        if (res.Method == "fallback")
        {
            var cands = new List<(string Id, string Cls, string Text)>();
            foreach (var node in g.Values)
            {
                var t = ResolveNodeText(g, node, 0, []);
                if (!string.IsNullOrWhiteSpace(t)) cands.Add((node.Id, node.ClassType, t));
            }
            if (cands.Count == 1)
                found.Add((cands[0].Id, "positive", cands[0].Text, cands[0].Cls));
            else
                foreach (var c in cands)
                    found.Add((c.Id, NegRe().IsMatch(c.Cls) ? "negative" : "positive", c.Text, c.Cls));
        }

        var pos = new List<string>();
        var neg = new List<string>();
        foreach (var f in found)
        {
            var t = f.Text.Trim();
            if (t.Length == 0) continue;
            var bucket = f.Role == "positive" ? pos : neg;
            if (!bucket.Contains(t)) bucket.Add(t);
        }

        res.Positive = string.Join("\n", pos).Trim();
        res.Negative = string.Join("\n", neg).Trim();

        if (res.Negative.Length == 0 && res.Positive.Length > 0)
        {
            var m = NegMarkerRe().Match(res.Positive);
            if (m.Success && m.Index > 0)
            {
                var tail = res.Positive[(m.Index + m.Length)..].Trim();
                if (tail.Length > 0)
                {
                    res.Positive = res.Positive[..m.Index].Trim();
                    res.Negative = tail;
                    res.Split = true;
                }
            }
        }

        res.PosClasses = found.Where(x => x.Role == "positive").Select(x => x.Cls)
            .Where(x => x.Length > 0).Distinct().OrderBy(x => x).ToArray();
        res.NegClasses = found.Where(x => x.Role == "negative").Select(x => x.Cls)
            .Where(x => x.Length > 0).Distinct().OrderBy(x => x).ToArray();

        // 技术参数
        foreach (var node in g.Values)
        {
            var ins = node.Inputs;
            if (res.Model is null)
            {
                foreach (var k in (string[])["ckpt_name", "unet_name", "model_name", "checkpoint"])
                {
                    if (ins.TryGetValue(k, out var v) && v is string s && s.Trim().Length > 0
                        && !s.Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        res.Model = s;
                        break;
                    }
                }
            }
            if (res.Sampler is null && ins.TryGetValue("sampler_name", out var sa) && sa is string sas && sas.Trim().Length > 0)
                res.Sampler = sas;
            if (res.Scheduler is null && ins.TryGetValue("scheduler", out var sh) && sh is string shs && shs.Trim().Length > 0)
                res.Scheduler = shs;

            if (res.Steps is null && ins.TryGetValue("steps", out var st))
                res.Steps = st switch { long l => (int)l, double d => (int)d, _ => res.Steps };
            if (res.Cfg is null && ins.TryGetValue("cfg", out var cf))
                res.Cfg = cf switch { long l => l, double d => d, _ => res.Cfg };
            if (res.Seed is null)
            {
                if (ins.TryGetValue("seed", out var sd)) res.Seed = sd switch { long l => l, double d => (long)d, _ => null };
                if (res.Seed is null && ins.TryGetValue("noise_seed", out var ns))
                    res.Seed = ns switch { long l => l, double d => (long)d, _ => null };
            }
            if (res.Denoise is null && ins.TryGetValue("denoise", out var de))
                res.Denoise = de switch { long l => l, double d => d, _ => res.Denoise };
            if (res.Width is null && ins.TryGetValue("width", out var w) && w is long wl) res.Width = (int)wl;
            if (res.Height is null && ins.TryGetValue("height", out var h) && h is long hl) res.Height = (int)hl;
        }
    }

    [GeneratedRegex("neg", RegexOptions.IgnoreCase)]
    private static partial Regex NegRe();

    // ---------- 文本归一化 ----------

    [GeneratedRegex(@"\(([^()]*(?:\([^()]*\)[^()]*)*)\s*:\s*[-+]?[\d.]+\s*\)")]
    private static partial Regex WeightRe();

    [GeneratedRegex(@"^(?:@+|artist[:_\s]+)+", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistPrefixRe();

    [GeneratedRegex(@"(?<=[a-z0-9)\]}])\s*\(\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ParenSpaceRe();

    [GeneratedRegex(@"_\([^()]*\)$")]
    private static partial Regex VariantRe();

    public static string StripEmphasis(string t)
    {
        while (true)
        {
            var s = t.Trim();
            if (s.StartsWith('(') && s.Count(c => c == '(') > s.Count(c => c == ')'))
            {
                t = s[1..];
                continue;
            }
            if (s.Length > 1 && s.StartsWith('(') && s.EndsWith(')'))
            {
                t = s[1..^1];
                continue;
            }
            return s;
        }
    }

    /// <summary>归一成 danbooru tag 形式；同时抽出画师前缀。</summary>
    public static (string Tag, string? Artist) NormalizeTag(string raw)
    {
        var t = LoraRe().Replace(raw, "");
        t = WeightRe().Replace(t, "$1");
        t = t.Replace("\\", "");
        t = t.Trim().Trim('[', ']').Trim();
        t = t.Trim().Trim('[', ']').Trim();
        t = StripEmphasis(t);

        string? artist = null;
        var m = ArtistPrefixRe().Match(t);
        if (m.Success && t.Length > m.Length)
        {
            artist = StripEmphasis(t[m.Length..].Trim().Trim('[', ']').Trim());
            t = artist;
        }
        t = StripEmphasis(t);

        t = ParenSpaceRe().Replace(t, "_(");
        t = Regex.Replace(t, @"\s*\)", ")");
        while (t.Count(c => c == '(') > t.Count(c => c == ')'))
        {
            var i = t.IndexOf('(');
            if (i < 0) break;
            t = t.Remove(i, 1);
        }
        while (t.Count(c => c == ')') > t.Count(c => c == '('))
        {
            var i = t.LastIndexOf(')');
            if (i < 0) break;
            t = t.Remove(i, 1);
        }
        t = Regex.Replace(t, @"\s*/\s*", "/");
        t = Regex.Replace(t, @"\s+", " ").Trim().Trim(',').Trim('/');
        t = t.Replace(' ', '_').ToLowerInvariant();

        if (artist is not null)
        {
            artist = Regex.Replace(artist, @"\s+", "_").Trim('_', '/').ToLowerInvariant();
            artist = Regex.Replace(artist, @"[()]", "");
            if (artist.Length is 0 or > 90) artist = null;
        }
        return (t, artist);
    }

    [GeneratedRegex(@"\bBREAK\b", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRe();

    [GeneratedRegex(@"[,\n\r]+")]
    private static partial Regex SplitRe();

    /// <summary>切 token。返回 (tags, artists)。</summary>
    public static (List<string> Tags, List<string> Artists) Tokenize(string? text)
    {
        var tags = new List<string>(64);
        var artists = new List<string>(4);
        if (string.IsNullOrWhiteSpace(text)) return (tags, artists);
        var t = BreakRe().Replace(text, ",");
        foreach (var raw in SplitRe().Split(t))
        {
            var (tag, art) = NormalizeTag(raw);
            if (tag.Length is > 0 and <= 90) tags.Add(tag);
            if (!string.IsNullOrEmpty(art) && art.Length is > 1 and <= 90) artists.Add(art);
        }
        return (tags, artists);
    }

    /// <summary>归一化后的检索文本（用空格分隔 token）。</summary>
    public static string BuildSearchText(string? prompt, string? negative)
    {
        var sb = new StringBuilder(256);
        var (tags, _) = Tokenize(prompt);
        foreach (var t in tags) { if (sb.Length > 0) sb.Append(' '); sb.Append(t); }
        if (!string.IsNullOrWhiteSpace(negative))
        {
            var (n, _) = Tokenize(negative);
            foreach (var t in n) { if (sb.Length > 0) sb.Append(' '); sb.Append(t); }
        }
        // 中文提示词不切词，整段保留原文
        if (!string.IsNullOrEmpty(prompt) && HasCjk().IsMatch(prompt)) { sb.Append(' '); sb.Append(prompt); }
        return sb.ToString().ToLowerInvariant();
    }

    [GeneratedRegex(@"[\u4e00-\u9fff]")]
    private static partial Regex HasCjk();
}
