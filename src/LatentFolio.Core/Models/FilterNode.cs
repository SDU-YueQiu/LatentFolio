using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LatentFolio.Core.Models;

public enum BoolOp { And, Or, Not }

public enum FieldKind { Text, Number, Enum, Tags, Bool }

public enum FieldType
{
    Model, Sampler, Scheduler, Steps, Cfg, Seed, Resolution, Aspect,
    Prompt, Negative, Character, Artist, Tag,
    Folder, FileName, Date,
    Rating, Note, MyTag,
    HasPrompt, Deleted, SimilarGroup, FileState,

    /// <summary>
    /// 只能往最后追加。栏的条件树在库里存的是这个枚举的数字，
    /// 插在中间会让所有老栏的字段整体串位（Rating 会变成 Date）。
    /// 界面上的排法看 FieldNames.Groups，跟这里的顺序无关。
    /// </summary>
    Lora,
}

public enum CondOp
{
    Eq, Ne, Contains, NotContains, StartsWith, EndsWith,
    Gt, Gte, Lt, Lte,
    In, NotIn,
    IsTrue, IsFalse,
    Between,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(FilterGroup), "g")]
[JsonDerivedType(typeof(FilterCond), "c")]
public abstract class FilterNode
{
    public abstract bool Evaluate(ImageItem img, FilterContext ctx);

    public static string OpText(BoolOp op) => op switch
    {
        BoolOp.And => "AND",
        BoolOp.Or => "OR",
        _ => "NOT",
    };
}

public sealed class FilterGroup : FilterNode
{
    public BoolOp Op { get; set; } = BoolOp.And;
    public List<FilterNode> Children { get; set; } = [];

    public override bool Evaluate(ImageItem img, FilterContext ctx)
    {
        switch (Op)
        {
            case BoolOp.And:
                foreach (var c in Children) if (!c.Evaluate(img, ctx)) return false;
                return true;
            case BoolOp.Or:
                if (Children.Count == 0) return true;
                foreach (var c in Children) if (c.Evaluate(img, ctx)) return true;
                return false;
            default:
                return Children.Count == 0 || !Children[0].Evaluate(img, ctx);
        }
    }
}

public sealed class FilterCond : FilterNode
{
    public FieldType Field { get; set; } = FieldType.Prompt;
    public CondOp Op { get; set; } = CondOp.Contains;
    public string Value { get; set; } = "";
    public string Value2 { get; set; } = "";

    public override bool Evaluate(ImageItem img, FilterContext ctx)
    {
        switch (Field)
        {
            case FieldType.Model:
                return TextMatch(img.Model);
            case FieldType.Sampler:
                return TextMatch(img.Sampler);
            case FieldType.Scheduler:
                return TextMatch(img.Scheduler);
            case FieldType.Prompt:
                return TextMatch(img.SearchText);
            case FieldType.Negative:
                return TextMatch(img.Negative);
            case FieldType.Note:
                return TextMatch(img.Note);
            case FieldType.Folder:
                return TextMatch(img.Folder);
            case FieldType.FileName:
                return TextMatch(img.FileName);
            case FieldType.Artist:
                return TagMatch(img.Artists);
            case FieldType.Lora:
                return TagMatch(img.Loras);
            case FieldType.Character:
                return TagMatch(img.Chars);
            case FieldType.Tag:
                return TagMatch(img.Chars) || TagMatch(img.Artists);
            case FieldType.MyTag:
                return TagMatch(img.AppTags);
            case FieldType.Steps:
                return NumMatch(img.Steps);
            case FieldType.Cfg:
                return NumMatch(img.Cfg);
            case FieldType.Seed:
                return NumMatch(img.Seed);
            case FieldType.Rating:
                return NumMatch(img.Rating);
            case FieldType.Resolution:
                return ResMatch(img);
            case FieldType.Aspect:
                return TextMatch(ImageItem.AspectRate(img.AspectRatio));
            case FieldType.Date:
                {
                    var d = img.Added;
                    return Op switch
                    {
                        CondOp.Between => d >= ParseDate(Value, DateTime.MinValue) && d <= ParseDate(Value2, DateTime.MaxValue),
                        CondOp.Gt => d > ParseDate(Value, DateTime.MinValue),
                        CondOp.Gte => d >= ParseDate(Value, DateTime.MinValue),
                        CondOp.Lt => d < ParseDate(Value, DateTime.MaxValue),
                        CondOp.Lte => d <= ParseDate(Value, DateTime.MaxValue),
                        _ => d.ToString("yyyy-MM-dd") == Value.Trim(),
                    };
                }
            case FieldType.HasPrompt:
                return (img.Prompt.Length > 0) == (Op != CondOp.IsFalse);
            case FieldType.Deleted:
                return (img.State == FileState.Deleted) == (Op != CondOp.IsFalse);
            case FieldType.FileState:
                {
                    var want = Value.Trim().ToLowerInvariant() switch
                    {
                        "missing" or "丢失" => FileState.Missing,
                        "deleted" or "已删除" => FileState.Deleted,
                        _ => FileState.Present,
                    };
                    return (img.State == want) == (Op != CondOp.Ne);
                }
            case FieldType.SimilarGroup:
                {
                    // 同组张数 = 这一组里有几张（自己算 1）。不在任何组里就是 1。
                    int size = 1;
                    if (ctx.GroupSize is not null && ctx.GroupSize.TryGetValue(img.Id, out var gs)) size = gs;
                    else if (ctx.SimilarGroupIds?.Contains(img.Id) == true) size = 2;
                    int n = int.TryParse(Value.Trim(), out var parsed) ? parsed : 2;
                    return Op switch
                    {
                        CondOp.IsFalse => size < 2,
                        CondOp.IsTrue => size >= 2,
                        CondOp.Gte => size >= n,
                        CondOp.Gt => size > n,
                        CondOp.Lt => size < n,
                        CondOp.Lte => size <= n,
                        CondOp.Eq => size == n,
                        _ => true,
                    };
                }
        }
        return true;
    }

    private static DateTime ParseDate(string s, DateTime fallback)
        => DateTime.TryParse(s, out var d) ? d : fallback;

    private bool NumMatch(double? v)
    {
        if (v is null) return Op == CondOp.Ne;
        double a = v.Value;
        if (!double.TryParse(Value, out var b)) return false;
        return Op switch
        {
            CondOp.Eq => Math.Abs(a - b) < 1e-9,
            CondOp.Ne => Math.Abs(a - b) >= 1e-9,
            CondOp.Gt => a > b,
            CondOp.Gte => a >= b,
            CondOp.Lt => a < b,
            CondOp.Lte => a <= b,
            CondOp.Between => double.TryParse(Value2, out var c) && a >= Math.Min(b, c) && a <= Math.Max(b, c),
            _ => false,
        };
    }

    private bool ResMatch(ImageItem img)
    {
        // 「包含」按文本匹配（能写 4K / 1024x1024），其余按长边比数字
        var s = $"{img.Width}x{img.Height}";
        if (Op is CondOp.Contains or CondOp.NotContains)
        {
            var hit = s.Contains(Value.Trim(), StringComparison.OrdinalIgnoreCase)
                      || img.Resolution.Contains(Value.Trim(), StringComparison.OrdinalIgnoreCase);
            return Op == CondOp.NotContains ? !hit : hit;
        }
        if (!int.TryParse(Value.Trim(), out var n)) return true;
        int edge = Math.Max(img.Width, img.Height);
        return Op switch
        {
            CondOp.Eq => edge == n,
            CondOp.Ne => edge != n,
            CondOp.Gt => edge > n,
            CondOp.Gte => edge >= n,
            CondOp.Lt => edge < n,
            CondOp.Lte => edge <= n,
            _ => true,
        };
    }

    private bool TagMatch(string[] arr)
    {
        if (arr.Length == 0) return Op is CondOp.NotContains or CondOp.Ne or CondOp.NotIn;
        foreach (var t in arr)
        {
            if (Op switch
            {
                CondOp.Eq => string.Equals(t, Value.Trim(), StringComparison.OrdinalIgnoreCase),
                CondOp.Ne => string.Equals(t, Value.Trim(), StringComparison.OrdinalIgnoreCase),
                CondOp.Contains => t.Contains(Value.Trim(), StringComparison.OrdinalIgnoreCase),
                CondOp.StartsWith => t.StartsWith(Value.Trim(), StringComparison.OrdinalIgnoreCase),
                CondOp.In => SplitValues(Value).Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)),
                _ => false,
            })
            {
                return Op != CondOp.Ne;
            }
        }
        return Op is CondOp.Ne or CondOp.NotContains or CondOp.NotIn;
    }

    private static string[] SplitValues(string s)
        => s.Split(['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private bool TextMatch(string? src)
    {
        src ??= "";
        var v = Value.Trim();
        switch (Op)
        {
            case CondOp.Contains:
                return v.Length == 0 || src.Contains(v, StringComparison.OrdinalIgnoreCase);
            case CondOp.NotContains:
                return v.Length > 0 && !src.Contains(v, StringComparison.OrdinalIgnoreCase);
            case CondOp.Eq:
                return string.Equals(src, v, StringComparison.OrdinalIgnoreCase);
            case CondOp.Ne:
                return !string.Equals(src, v, StringComparison.OrdinalIgnoreCase);
            case CondOp.StartsWith:
                return src.StartsWith(v, StringComparison.OrdinalIgnoreCase);
            case CondOp.EndsWith:
                return src.EndsWith(v, StringComparison.OrdinalIgnoreCase);
            case CondOp.In:
                return SplitValues(Value).Any(x => string.Equals(x, src, StringComparison.OrdinalIgnoreCase));
            case CondOp.NotIn:
                return !SplitValues(Value).Any(x => string.Equals(x, src, StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    // ---------- 表达式文本 ----------
    public string Describe() => Field == FieldType.SimilarGroup
        ? Op switch
        {
            CondOp.IsFalse => "无相似图",
            CondOp.IsTrue => "有相似图",
            _ => $"同组 ≥ {Value} 张",
        }
        : Op switch
        {
            CondOp.IsTrue => $"{FieldNames.Label(Field)} 是",
            CondOp.IsFalse => $"{FieldNames.Label(Field)} 否",
            CondOp.Between => $"{FieldNames.Label(Field)} 在 [{Value}, {Value2}]",
            _ => $"{FieldNames.Label(Field)} {FieldNames.OpLabel(Op)} {Value}",
        };

    public override string ToString() => Describe();
}

public static class FieldNames
{
    public static string Label(FieldType f) => f switch
    {
        FieldType.Model => "模型",
        FieldType.Lora => "LoRA",
        FieldType.Sampler => "采样器",
        FieldType.Scheduler => "调度器",
        FieldType.Steps => "步数",
        FieldType.Cfg => "CFG",
        FieldType.Seed => "种子",
        FieldType.Resolution => "分辨率",
        FieldType.Aspect => "宽高比",
        FieldType.Prompt => "提示词",
        FieldType.Negative => "负面提示词",
        FieldType.Character => "角色",
        FieldType.Artist => "画师",
        FieldType.Tag => "标签",
        FieldType.Folder => "文件夹",
        FieldType.FileName => "文件名",
        FieldType.Date => "日期",
        FieldType.Rating => "评分",
        FieldType.Note => "批注",
        FieldType.MyTag => "自建标签",
        FieldType.HasPrompt => "有提示词",
        FieldType.Deleted => "已删除",
        FieldType.SimilarGroup => "相似图组",
        FieldType.FileState => "文件状态",
        _ => f.ToString(),
    };

    public static string OpLabel(CondOp op) => op switch
    {
        CondOp.Eq => "=",
        CondOp.Ne => "≠",
        CondOp.Contains => "包含",
        CondOp.NotContains => "不包含",
        CondOp.StartsWith => "开头是",
        CondOp.EndsWith => "结尾是",
        CondOp.Gt => ">",
        CondOp.Gte => "≥",
        CondOp.Lt => "<",
        CondOp.Lte => "≤",
        CondOp.In => "属于",
        CondOp.NotIn => "不属于",
        CondOp.Between => "区间",
        CondOp.IsTrue => "是",
        CondOp.IsFalse => "否",
        _ => op.ToString(),
    };

    public static CondOp[] OpsFor(FieldType f) => f switch
    {
        FieldType.Steps or FieldType.Cfg or FieldType.Seed => [CondOp.Eq, CondOp.Ne, CondOp.Gt, CondOp.Gte, CondOp.Lt, CondOp.Lte, CondOp.Between],
        FieldType.Rating => [CondOp.Eq, CondOp.Gte, CondOp.Lte, CondOp.Between],
        FieldType.Resolution => [CondOp.Eq, CondOp.Contains, CondOp.Gt, CondOp.Lt],
        FieldType.Character or FieldType.Artist or FieldType.Tag or FieldType.MyTag or FieldType.Lora
            => [CondOp.Eq, CondOp.Contains, CondOp.Ne, CondOp.NotContains],
        FieldType.HasPrompt or FieldType.Deleted => [CondOp.IsTrue, CondOp.IsFalse],
        // 设计稿 §12.4：相似图组要能筛「无相似图」和「堆叠 ≥N 张」
        FieldType.SimilarGroup => [CondOp.IsFalse, CondOp.IsTrue, CondOp.Gte],
        FieldType.Date => [CondOp.Eq, CondOp.Gte, CondOp.Lte, CondOp.Between],
        _ => [CondOp.Contains, CondOp.NotContains, CondOp.Eq, CondOp.Ne, CondOp.StartsWith],
    };

    /// <summary>这个算子下要不要填值（相似图组的「≥N」要，其余不要）。</summary>
    public static bool NeedsValue(FieldType f, CondOp op)
        => f switch
        {
            FieldType.HasPrompt or FieldType.Deleted => false,
            FieldType.SimilarGroup => op is not (CondOp.IsTrue or CondOp.IsFalse),
            _ => true,
        };

    public static bool NeedsSecond(FieldType f, CondOp op) => op == CondOp.Between;

    /// <summary>编辑器右栏「可用字段」按这五组摆（设计稿 §12.4 的 22 个字段）。</summary>
    public static readonly (string Name, FieldType[] Fields)[] Groups =
    [
        ("内容", [FieldType.Model, FieldType.Lora, FieldType.Sampler, FieldType.Scheduler,
                  FieldType.Steps, FieldType.Cfg, FieldType.Resolution, FieldType.Aspect]),
        ("提示词", [FieldType.Prompt, FieldType.Negative, FieldType.Character, FieldType.Artist, FieldType.Tag]),
        ("文件", [FieldType.Folder, FieldType.FileName, FieldType.Date]),
        ("我的", [FieldType.Rating, FieldType.Note, FieldType.MyTag]),
        ("状态", [FieldType.HasPrompt, FieldType.SimilarGroup, FieldType.Deleted]),
    ];

    public static readonly FieldType[] All = Groups.SelectMany(g => g.Fields).ToArray();

    /// <summary>字段属于哪一组，给编辑器高亮用。</summary>
    public static string GroupOf(FieldType f)
    {
        foreach (var (name, fields) in Groups)
            foreach (var x in fields) if (x == f) return name;
        return "内容";
    }
}

/// <summary>求值上下文：给「相似图组」这类需要外部数据的字段用。</summary>
public sealed class FilterContext
{
    public HashSet<long>? SimilarGroupIds { get; set; }

    /// <summary>图片 id → 所在相似组的张数。给「堆叠 ≥N 张」用。</summary>
    public Dictionary<long, int>? GroupSize { get; set; }
}

public static class FilterNodeExtensions
{
    public static FilterNode Clone(this FilterNode n) => n switch
    {
        FilterGroup g => new FilterGroup { Op = g.Op, Children = g.Children.Select(Clone).ToList() },
        FilterCond c => new FilterCond { Field = c.Field, Op = c.Op, Value = c.Value, Value2 = c.Value2 },
        _ => throw new NotSupportedException(),
    };

    public static string ToJson(this FilterNode n)
        => JsonSerializer.Serialize(n, JsonOpts);

    public static FilterNode FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new FilterGroup();
        try
        {
            return JsonSerializer.Deserialize<FilterNode>(json, JsonOpts) ?? new FilterGroup();
        }
        catch
        {
            return new FilterGroup();
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        // 不能忽略默认值：Op 的默认枚举值恰好是 Eq，一旦被省略，
        // 反序列化时属性初始值 Contains 会顶上来，数字比较就变成永远真。
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>人读的等价表达式。</summary>
    public static string ToExpression(this FilterNode n)
    {
        var sb = new StringBuilder();
        Write(n, sb);
        return sb.ToString();
    }

    private static void Write(FilterNode n, StringBuilder sb)
    {
        switch (n)
        {
            case FilterCond c:
                sb.Append(c.Describe());
                break;
            case FilterGroup g when g.Op == BoolOp.Not:
                sb.Append("NOT (");
                if (g.Children.Count > 0) Write(g.Children[0], sb);
                sb.Append(')');
                break;
            case FilterGroup g:
                if (g.Children.Count == 0) { sb.Append("全部"); break; }
                sb.Append('(');
                for (int i = 0; i < g.Children.Count; i++)
                {
                    if (i > 0) sb.Append(g.Op == BoolOp.And ? " AND " : " OR ");
                    Write(g.Children[i], sb);
                }
                sb.Append(')');
                break;
        }
    }

    public static int CountLeaves(this FilterNode n) => n switch
    {
        FilterCond => 1,
        FilterGroup g => g.Children.Sum(CountLeaves),
        _ => 0,
    };
}
