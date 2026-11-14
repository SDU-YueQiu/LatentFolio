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

    private static readonly string[] ConcatStrings = ["string_a", "string_b", "text_a", "text_b", "text_c", "text_d", "a", "b"];

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

    /// <summary>求值上下文：图 + ShowText 记下来的实际输出。</summary>
    private sealed class Eval(Dictionary<string, Node> g)
    {
        public Dictionary<string, Node> G { get; } = g;

        /// <summary>节点 id → ShowText 在那次运行里实际显示的文字。</summary>
        public Dictionary<string, string> Snapshot { get; } = new(StringComparer.Ordinal);

        /// <summary>节点 id → 这棵子树里有没有随机源（通配符 / 随机分支）。记忆化，免得重复走图。</summary>
        public Dictionary<string, bool> RandomMemo { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// ShowText 节点会把「这次运行实际显示的文本」写进 text_0，text 则是它连着的上游。
    /// 通配符（Wildcards）和随机分支（easy anythingIndexSwitch）真正选中哪一条只在运行时才知道，
    /// 从图里推不出来，但 ShowText 记着 —— 所以链路上遇到有快照的节点，直接用快照。
    /// </summary>
    private static void CollectSnapshots(Eval e)
    {
        foreach (var node in e.G.Values)
        {
            if (!node.ClassType.Contains("ShowText", StringComparison.OrdinalIgnoreCase)) continue;
            if (!node.Inputs.TryGetValue("text", out var v) || v is not Link l) continue;

            string? snap = null;
            foreach (var k in (string[])["text_0", "text_1"])
            {
                if (node.Inputs.TryGetValue(k, out var s) && s is string str && !string.IsNullOrWhiteSpace(str))
                {
                    snap = str;
                    break;
                }
            }
            if (snap is null) continue;

            // 同一个上游可能挂了多个 ShowText，取最长的那个（最完整的）
            if (!e.Snapshot.TryGetValue(l.NodeId, out var old) || snap.Length > old.Length)
                e.Snapshot[l.NodeId] = snap;
        }
    }

    /// <summary>
    /// prompt 段里的 text_0 是「排队那一刻控件上显示的文字」，也就是上一张的取值：
    /// 通配符/随机分支要到真正执行时才抽，抽完才写回控件，而 workflow 段正好是保存时控件的样子。
    /// 实测 Krea2-turbo_00047_：prompt 写着上一轮的 arched bangs/trilby/harness，
    /// 画出来的却是 workflow 里那身 silvery white/teal/behive hairdo/kimono skirt。
    /// 所以 workflow 段能对上节点 id 的，一律以它为准。
    /// </summary>
    private static void ApplyWorkflowTexts(Eval e, string? workflowJson)
    {
        if (string.IsNullOrWhiteSpace(workflowJson)) return;

        var doc = TryParse(workflowJson);
        if (doc is null)
        {
            var fixedJson = NaNRe().Replace(workflowJson, "null");
            if (!ReferenceEquals(fixedJson, workflowJson)) doc = TryParse(fixedJson);
        }
        if (doc is null) return;

        using (doc)
        {
            var texts = new Dictionary<string, string>(64, StringComparer.Ordinal);
            CollectWorkflowTexts(doc.RootElement, texts, 0);
            if (texts.Count == 0) return;

            foreach (var node in e.G.Values)
            {
                if (!node.ClassType.Contains("ShowText", StringComparison.OrdinalIgnoreCase)) continue;
                if (!node.Inputs.TryGetValue("text", out var v) || v is not Link l) continue;
                if (!texts.TryGetValue(node.Id, out var t) || t.Length == 0) continue;
                e.Snapshot[l.NodeId] = t;  // 覆盖 prompt 段里那份过期的
            }
        }
    }

    /// <summary>UI 图里每个节点都带 id + widgets_values，把有文字的那些收成 id → 文字。</summary>
    private static void CollectWorkflowTexts(System.Text.Json.JsonElement el, Dictionary<string, string> into, int depth)
    {
        if (depth > 8) return;

        if (el.ValueKind == System.Text.Json.JsonValueKind.Object
            && el.TryGetProperty("id", out var idEl)
            && el.TryGetProperty("widgets_values", out var wv)
            && idEl.ValueKind is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number)
        {
            var key = idEl.ValueKind == System.Text.Json.JsonValueKind.String ? idEl.GetString() : idEl.GetRawText();
            var txt = FirstString(wv);
            if (!string.IsNullOrEmpty(key) && txt is not null && txt.Trim().Length > 0 && !into.ContainsKey(key))
                into[key] = txt;
        }

        if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject()) CollectWorkflowTexts(p.Value, into, depth + 1);
        }
        else if (el.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var it in el.EnumerateArray()) CollectWorkflowTexts(it, into, depth + 1);
        }
    }

    private static string? FirstString(System.Text.Json.JsonElement v)
    {
        if (v.ValueKind == System.Text.Json.JsonValueKind.String) return v.GetString();
        if (v.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var it in v.EnumerateArray())
            {
                var s = FirstString(it);
                if (s is not null) return s;
            }
        }
        return null;
    }

    private static string ResolveValue(Eval e, object? v, int depth, HashSet<string> seen)
    {
        if (v is string s) return s;
        if (v is not Link l || depth > 8) return "";
        if (!seen.Add(l.NodeId)) return "";
        if (!e.G.TryGetValue(l.NodeId, out var node)) return "";
        return ResolveNodeText(e, node, depth + 1, seen);
    }

    [GeneratedRegex("wildcard|indexswitch|random|shuffle|choice|pick", RegexOptions.IgnoreCase)]
    private static partial Regex RandomNodeRe();

    /// <summary>这棵子树里有没有随机源（通配符、随机分支）。有的话图算不出确定结果，只能信 ShowText。</summary>
    private static bool HasRandom(Eval e, string nodeId, int depth = 0)
    {
        if (e.RandomMemo.TryGetValue(nodeId, out var memo)) return memo;
        if (depth > 12) return false;
        if (!e.G.TryGetValue(nodeId, out var node)) return false;

        if (RandomNodeRe().IsMatch(node.ClassType) && !node.ClassType.Contains("ShowText", StringComparison.OrdinalIgnoreCase))
        {
            e.RandomMemo[nodeId] = true;
            return true;
        }

        e.RandomMemo[nodeId] = false;   // 先按没有记，防环
        foreach (var (_, v) in node.Inputs)
        {
            if (v is not Link l) continue;
            if (HasRandom(e, l.NodeId, depth + 1))
            {
                e.RandomMemo[nodeId] = true;
                return true;
            }
        }
        return false;
    }

    private static string ResolveNodeText(Eval e, Node node, int depth, HashSet<string> seen)
    {
        var ins = node.Inputs;

        // ShowText 自己被连到编码器时，先算它连着的上游：text_0 只是控件上显示的字，可能过期
        if (node.ClassType.Contains("ShowText", StringComparison.OrdinalIgnoreCase)
            && ins.TryGetValue("text", out var tv) && tv is Link tl)
        {
            var upstream = ResolveValue(e, tl, depth, [.. seen]);
            if (!string.IsNullOrWhiteSpace(upstream)) return upstream;
        }

        bool isConcat = ConcatRe().IsMatch(node.ClassType)
                        || ins.ContainsKey("string_a") || ins.ContainsKey("string_b");

        string normal;
        if (isConcat)
        {
            var parts = new List<string>(4);
            foreach (var k in ConcatStrings)
            {
                if (!ins.TryGetValue(k, out var raw)) continue;
                var t = ResolveValue(e, raw, depth, [.. seen]);
                if (!string.IsNullOrWhiteSpace(t)) parts.Add(t.Trim());
            }
            string delim = "";
            if (ins.TryGetValue("delimiter", out var d1) && d1 is string ds) delim = ds;
            else if (ins.TryGetValue("delim", out var d2) && d2 is string ds2) delim = ds2;
            normal = string.Join(delim, parts);
        }
        else
        {
            normal = "";
            foreach (var k in TextKeys)
            {
                if (ins.TryGetValue(k, out var v) && v is string s && !string.IsNullOrWhiteSpace(s))
                {
                    normal = s;
                    break;
                }
            }
        }

        // ShowText 记下的「这次运行实际显示的文字」只在图算不出确定结果时才有用：
        // 通配符 / 随机分支选哪条只有运行时知道。图能算出来的（一段字面量拼接）就以图为准——
        // 那个控件值经常是上一次运行留下的，实测同一套工作流里 02052 和 02054 两张图
        // 的 ShowText 都写着同一段旧文字（ligne claire…），而 02054 真正喂给编码器的
        // 是 node 94 里那串 hanfu / silver wolf。
        if (e.Snapshot.TryGetValue(node.Id, out var snap) && snap.Length > 0)
        {
            if (HasRandom(e, node.Id)) return snap;
            if (normal.Length == 0) return snap;
        }

        return normal;
    }

    /// <summary>
    /// 这些节点产出的 conditioning 是「空的」。最典型的是 ConditioningZeroOut：
    /// 把正面的 conditioning 清零当负面用（就是「不要负面词」的标准接法）。
    /// 顺着它的输入往上走会把正面文本当成负面，于是一大批工作流会误报「正负提示词相同」。
    /// </summary>
    private static readonly HashSet<string> EmptyCond = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConditioningZeroOut",
        "ConditioningSetAreaEmpty",
    };

    private static void WalkUp(Eval e, string nodeId, string role,
        List<(string Id, string Role, string Text, string Cls)> found, HashSet<string> seen, int depth)
    {
        if (depth > 25 || !seen.Add(nodeId)) return;
        if (!e.G.TryGetValue(nodeId, out var node)) return;

        // 空 conditioning：到此为止，这一口就是没有文本
        if (EmptyCond.Contains(node.ClassType)) return;

        var txt = ResolveNodeText(e, node, 0, []);
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
            WalkUp(e, l.NodeId, role, found, [.. seen], depth + 1);
        }
    }

    /// <summary>
    /// imagePath 是这张 PNG 自己的路径。同一份图里可能并排放着好几条流水线
    /// （一次排队把 Krea2 / anima / MOSS 各存一张），只有文件名能说明哪一条生成了这一张。
    /// </summary>
    public static ParseResult ParseWorkflowApiJson(string promptJson, string? workflowJson = null, string? imagePath = null)
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
            var e = new Eval(g);
            CollectSnapshots(e);
            ApplyWorkflowTexts(e, workflowJson);
            Fill(e, res, imagePath);
            FillTechnical(e, res, imagePath);
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

    /// <summary>
    /// 提示词也只从这张图真正走过的那一支里取。
    /// 一次排队同时跑好几个模型时，图里会有好几套 CLIPTextEncode（有时还接不同的文本变体），
    /// 全图收一遍就会把别的分支的提示词也拼进来 —— 实测 anima-pre_00005_ 把同一段提示词显示两遍
    /// （其中一遍还多出 (flat color),(anime coloring )）。
    /// 认不出分支（文件改名、多个保存点冲突）时才退回全图。
    /// </summary>
    private static void Fill(Eval e, ParseResult res, string? imagePath)
    {
        var branch = BranchSet(e, imagePath);
        if (!FillFrom(e, res, branch) && branch is not null)
            FillFrom(e, res, null);
    }

    private static HashSet<string>? BranchSet(Eval e, string? imagePath)
    {
        var outs = PickOutputs(e, imagePath);
        if (outs.Count == 0) return null;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in outs) set.UnionWith(Ancestors(e, id));
        return set;
    }

    /// <summary>收一遍提示词；limit 非空时只看这一支里的节点。返回是否找到了正向文本。</summary>
    private static bool FillFrom(Eval e, ParseResult res, HashSet<string>? limit)
    {
        var found = new List<(string Id, string Role, string Text, string Cls)>(8);
        var seedsPos = new List<string>();
        var seedsNeg = new List<string>();

        foreach (var node in e.G.Values)
        {
            if (limit is not null && !limit.Contains(node.Id)) continue;
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

        foreach (var s in seedsPos) WalkUp(e, s, "positive", found, [], 0);
        foreach (var s in seedsNeg) WalkUp(e, s, "negative", found, [], 0);

        res.Method = found.Any(x => x.Role == "positive") ? "conditioning-trace" : "fallback";

        if (res.Method == "fallback")
        {
            var cands = new List<(string Id, string Cls, string Text)>();
            foreach (var node in e.G.Values)
            {
                if (limit is not null && !limit.Contains(node.Id)) continue;
                var t = ResolveNodeText(e, node, 0, []);
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

        return res.Positive.Length > 0;
    }

    // ---------- 技术参数：只认这张图真正走过的那一支 ----------

    private static readonly string[] ModelKeys = ["ckpt_name", "unet_name", "model_name", "checkpoint"];

    /// <summary>放大/超分/锐化模型不是「出图模型」。</summary>
    [GeneratedRegex("upscale|seedvr|esrgan|\\bvsr\\b|ultra", RegexOptions.IgnoreCase)]
    private static partial Regex NotGenModelRe();

    [GeneratedRegex("sampler", RegexOptions.IgnoreCase)]
    private static partial Regex SamplerRe();

    /// <summary>
    /// 一张图里并排几条流水线时，每条都有自己的 SaveImage 和自己的 ckpt。
    /// 以前是「JSON 里第一个出现的 ckpt_name 赢」，于是 MOSS/anima 的图全挂上了 Krea2 的模型名。
    /// 只有 filename_prefix 能说明这个文件是哪条流水线写出来的。
    /// 同一个前缀会挂在好几个保存点上（一次排队存好几张），它们一起返回。
    /// </summary>
    private static List<string> PickOutputs(Eval e, string? imagePath)
    {
        var hits = new List<string>(4);
        if (string.IsNullOrEmpty(imagePath)) return hits;
        var stem = Path.GetFileNameWithoutExtension(imagePath);
        var dir = Path.GetDirectoryName(imagePath) ?? "";

        int best = 0;
        foreach (var node in e.G.Values)
        {
            if (!node.Inputs.TryGetValue("images", out var iv) || iv is not Link) continue;
            if (!node.ClassType.Contains("save", StringComparison.OrdinalIgnoreCase)) continue;
            if (!node.Inputs.TryGetValue("filename_prefix", out var pv) || pv is not string pref) continue;
            pref = pref.Trim();
            if (pref.Length == 0) continue;

            var seg = LastSegment(pref);
            int score = 0;
            if (StemMatches(stem, seg)) score += 100 + seg.Length;
            var pdir = PrefixDir(pref);
            if (pdir.Length > 0 && DirEndsWith(dir, pdir)) score += 30;
            if (score == 0) continue;

            if (score > best) { best = score; hits.Clear(); hits.Add(node.Id); }
            else if (score == best) hits.Add(node.Id);
        }
        return hits;
    }

    /// <summary>
    /// ComfyUI 存图叫「前缀_00001_.png」。要求前缀后面紧跟数字，不能只是「以它开头」：
    /// 文件被改名过（比如 animaMIX_00001_ 只跟 anima 共用一个开头）时不该算到 anima 那条流水线头上。
    /// </summary>
    private static bool StemMatches(string stem, string seg)
    {
        if (seg.Length == 0 || stem.Length <= seg.Length) return false;
        if (!stem.StartsWith(seg, StringComparison.OrdinalIgnoreCase)) return false;

        int i = seg.Length;
        while (i < stem.Length && stem[i] == '_') i++;
        int start = i;
        while (i < stem.Length && char.IsAsciiDigit(stem[i])) i++;
        if (i == start) return false;
        return i == stem.Length || stem[i] == '_';
    }

    private static string LastSegment(string prefix)
    {
        var s = prefix.Replace('\\', '/');
        int i = s.LastIndexOf('/');
        return i >= 0 ? s[(i + 1)..] : s;
    }

    private static string PrefixDir(string prefix)
    {
        var s = prefix.Replace('\\', '/');
        int i = s.LastIndexOf('/');
        return i > 0 ? s[..i] : "";
    }

    private static bool DirEndsWith(string dir, string tail)
    {
        var a = dir.Replace('\\', '/').TrimEnd('/');
        var b = tail.Replace('\\', '/').Trim('/');
        if (b.Length == 0) return false;
        return a.Equals(b, StringComparison.OrdinalIgnoreCase)
            || a.EndsWith("/" + b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>上游可达的全部节点（含起点）：分支 = 这个输出的整棵子树。</summary>
    private static HashSet<string> Ancestors(Eval e, string startId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { startId };
        var stack = new Stack<string>();
        stack.Push(startId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!e.G.TryGetValue(id, out var node)) continue;
            foreach (var v in node.Inputs.Values)
                if (v is Link l && seen.Add(l.NodeId)) stack.Push(l.NodeId);
        }
        return seen;
    }

    /// <summary>
    /// 一条流水线的取值顺序：模型链最前，其次采样器，再次这条分支的其余节点。
    /// 故意不补全图 —— 这条分支没有模型时就得是空，不能顺手从别的分支抓一个。
    /// </summary>
    private static List<Node> BranchOrder(Eval e, string outputId)
    {
        var order = new List<Node>(64);
        var added = new HashSet<string>(StringComparer.Ordinal);
        var branch = Ancestors(e, outputId);

        var samplers = new List<Node>();
        foreach (var node in e.G.Values)
            if (branch.Contains(node.Id) && LooksLikeSampler(node)) samplers.Add(node);

        foreach (var s in samplers) AddModelChain(e, s, order, added);
        foreach (var s in samplers) if (added.Add(s.Id)) order.Add(s);
        foreach (var node in e.G.Values)
            if (branch.Contains(node.Id) && added.Add(node.Id)) order.Add(node);
        return order;
    }

    private static bool LooksLikeSampler(Node node) =>
        SamplerRe().IsMatch(node.ClassType) && !node.ClassType.Contains("samplers", StringComparison.OrdinalIgnoreCase);

    /// <summary>顺着 model / model_2 … 往回走，模型加载器就在这条链上。</summary>
    private static void AddModelChain(Eval e, Node from, List<Node> order, HashSet<string> added)
    {
        var stack = new Stack<(string Id, int Depth)>();
        foreach (var (k, v) in from.Inputs)
        {
            var kl = k.ToLowerInvariant();
            if (v is Link l && (kl == "model" || kl.StartsWith("model_"))) stack.Push((l.NodeId, 1));
        }

        while (stack.Count > 0)
        {
            var (id, depth) = stack.Pop();
            if (depth > 12 || !added.Add(id) || !e.G.TryGetValue(id, out var node)) continue;
            order.Add(node);
            foreach (var (k, v) in node.Inputs)
            {
                var kl = k.ToLowerInvariant();
                if (v is Link l && (kl == "model" || kl.StartsWith("model_"))) stack.Push((l.NodeId, depth + 1));
            }
        }
    }

    /// <summary>一条流水线的技术参数。宽高单独处理（空 latent 优先），不放进来。</summary>
    private sealed record BranchTech(
        string? Model, string? Sampler, string? Scheduler, int? Steps, double? Cfg, long? Seed, double? Denoise);

    private static BranchTech TechOf(Eval e, List<Node> order)
    {
        string? model = null, sampler = null, scheduler = null;
        int? steps = null;
        double? cfg = null, denoise = null;
        long? seed = null;

        foreach (var node in order)
        {
            var ins = node.Inputs;
            if (model is null && !NotGenModelRe().IsMatch(node.ClassType))
            {
                foreach (var k in ModelKeys)
                {
                    if (ins.TryGetValue(k, out var v) && v is string s && s.Trim().Length > 0
                        && !s.Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        model = s;
                        break;
                    }
                }
            }
            if (sampler is null && ins.TryGetValue("sampler_name", out var sa) && sa is string sas && sas.Trim().Length > 0)
                sampler = sas;
            if (scheduler is null && ins.TryGetValue("scheduler", out var sh) && sh is string shs && shs.Trim().Length > 0)
                scheduler = shs;

            if (steps is null && ins.TryGetValue("steps", out var st) && TryNum(e, st, 0, out var sv))
                steps = (int)Math.Round(sv);
            if (cfg is null && ins.TryGetValue("cfg", out var cf) && TryNum(e, cf, 0, out var cv)) cfg = cv;
            if (seed is null)
            {
                if (ins.TryGetValue("seed", out var sd) && TryNum(e, sd, 0, out var sdv)) seed = (long)sdv;
                else if (ins.TryGetValue("noise_seed", out var ns) && TryNum(e, ns, 0, out var nsv)) seed = (long)nsv;
            }
            if (denoise is null && ins.TryGetValue("denoise", out var de) && TryNum(e, de, 0, out var dv)) denoise = dv;
        }

        return new BranchTech(model, sampler, scheduler, steps, cfg, seed, denoise);
    }

    /// <summary>几张图都指向同一条流水线时才敢写；有一条对不上就空着，不猜。</summary>
    private static T? Agree<T>(List<T?> vals) where T : class
    {
        if (vals.Count == 0) return null;
        var first = vals[0];
        foreach (var v in vals) if (!Equals(v, first)) return null;
        return first;
    }

    private static T? AgreeNum<T>(List<T?> vals) where T : struct
    {
        if (vals.Count == 0) return null;
        var first = vals[0];
        foreach (var v in vals) if (!Equals(v, first)) return null;
        return first;
    }

    /// <summary>全图出现过的出图模型（不含放大模型）。</summary>
    private static HashSet<string> DistinctModels(Eval e)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in e.G.Values)
        {
            if (NotGenModelRe().IsMatch(node.ClassType)) continue;
            foreach (var k in ModelKeys)
            {
                if (node.Inputs.TryGetValue(k, out var v) && v is string s && s.Trim().Length > 0
                    && !s.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    set.Add(s);
                    break;
                }
            }
        }
        return set;
    }

    private static void FillTechnical(Eval e, ParseResult res, string? imagePath)
    {
        // 名字只用来找「这个文件是哪个 SaveImage 写的」，模型本身永远来自那条流水线里的加载器。
        // 认不出来（文件被改名、或同一个前缀挂了几条不同模型的流水线）就空着，绝不拿别的分支顶上。
        var outputs = PickOutputs(e, imagePath);
        if (outputs.Count > 0)
        {
            var techs = new List<BranchTech>(outputs.Count);
            foreach (var id in outputs)
                techs.Add(TechOf(e, BranchOrder(e, id)));

            res.Model = Agree(techs.Select(t => t.Model).ToList());
            res.Sampler = Agree(techs.Select(t => t.Sampler).ToList());
            res.Scheduler = Agree(techs.Select(t => t.Scheduler).ToList());
            res.Steps = AgreeNum(techs.Select(t => t.Steps).ToList());
            res.Cfg = AgreeNum(techs.Select(t => t.Cfg).ToList());
            res.Seed = AgreeNum(techs.Select(t => t.Seed).ToList());
            res.Denoise = AgreeNum(techs.Select(t => t.Denoise).ToList());
        }
        else
        {
            // 文件名对不上任何保存点：整张图只有一种模型才敢认，参数沿用老的全图扫描
            var models = DistinctModels(e);
            if (models.Count == 1)
            {
                var t = TechOf(e, [.. e.G.Values]);
                res.Model = t.Model;
                res.Sampler = t.Sampler;
                res.Scheduler = t.Scheduler;
                res.Steps = t.Steps;
                res.Cfg = t.Cfg;
                res.Seed = t.Seed;
                res.Denoise = t.Denoise;
            }
        }

        // 宽高沿用全图顺序：空 latent 一般最先出现，别让缩放节点上的 width/height 抢了
        foreach (var node in e.G.Values)
        {
            if (res.Width is null && node.Inputs.TryGetValue("width", out var w) && w is long wl) res.Width = (int)wl;
            if (res.Height is null && node.Inputs.TryGetValue("height", out var h) && h is long hl) res.Height = (int)hl;
        }
    }

    /// <summary>数值可能是个连线（种子常接在 Seed 节点上），顺着连线取。只认明确的数值键，别乱猜。</summary>
    private static bool TryNum(Eval e, object? v, int depth, out double d)
    {
        d = 0;
        switch (v)
        {
            case long l: d = l; return true;
            case int i: d = i; return true;
            case double x: d = x; return true;
        }
        if (v is not Link link || depth >= 4 || !e.G.TryGetValue(link.NodeId, out var node)) return false;
        foreach (var (k, val) in node.Inputs)
        {
            if (k.ToLowerInvariant() is not ("seed" or "noise_seed" or "steps" or "cfg" or "denoise" or "value" or "int" or "float"))
                continue;
            if (TryNum(e, val, depth + 1, out d)) return true;
        }
        return false;
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
            foreach (var piece in ExpandArtistWrapper(raw))
            {
                var (tag, art) = NormalizeTag(piece);
                if (tag.Length is > 0 and <= 90) tags.Add(tag);
                if (!string.IsNullOrEmpty(art) && art.Length is > 1 and <= 90) artists.Add(art);
            }
        }
        return (tags, artists);
    }

    /// <summary>
    /// 有的工作流把画师写成 `artist:[a|b|c]` 或 `[artist:name]`（NovelAI 那套括号语法）。
    /// 整段当一个 token 的话，方括号和竖线会把名字糊在一起，一个画师都认不出来。
    /// </summary>
    private static IEnumerable<string> ExpandArtistWrapper(string raw)
    {
        var s = raw.Trim();
        int colon = s.IndexOf(':');
        if (colon <= 0)
        {
            yield return raw;
            yield break;
        }

        var head = s[..colon].Trim().TrimStart('[').Trim();
        if (!head.Equals("artist", StringComparison.OrdinalIgnoreCase))
        {
            yield return raw;
            yield break;
        }

        var body = s[(colon + 1)..].Trim();
        body = body.TrimEnd(']').Trim();
        if (body.StartsWith('[')) body = body[1..].Trim();
        if (body.Length == 0)
        {
            yield return raw;
            yield break;
        }

        foreach (var part in body.Split('|'))
        {
            var p = part.Trim();
            if (p.Length > 0) yield return p;
        }
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
