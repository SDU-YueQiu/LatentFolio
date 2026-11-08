namespace LatentFolio.Core.Models;

public enum FileState
{
    Present = 0,
    Missing = 1,
    Deleted = 2,
}

[Flags]
public enum AspectBucket
{
    None = 0,
    Square = 1,
    Landscape = 2,
    Portrait = 4,
    Wide = 8,
    Tall = 16,
}

/// <summary>一张图片的完整内存表示。工作流 JSON 不在这里（按需从库里取）。</summary>
public sealed class ImageItem
{
    public long Id;
    public string Path = "";
    public string Folder = "";      // 相对图库根，'' 表示根
    public string FileName = "";
    public string Ext = "png";
    public long Size;
    public long MTime;
    public long AddedAt;
    public int Width;
    public int Height;
    public FileState State;

    // 内容
    public string? Model;
    public string? Sampler;
    public string? Scheduler;
    public int? Steps;
    public double? Cfg;
    public long? Seed;
    public double? Denoise;

    // 提示词
    public string Prompt = "";
    public string Negative = "";
    public string PromptSource = "";   // conditioning-trace / fallback / none
    public bool NegIsDup;

    // 我的
    public int Rating;
    public string Note = "";
    public string[] AppTags = [];

    // 派生
    public string[] Chars = [];
    public string[] Artists = [];
    public long Hash;
    public long DHash;

    public AspectBucket Aspect;
    public DateTime Added => DateTimeOffset.FromUnixTimeMilliseconds(AddedAt).LocalDateTime;

    public string DirPath => System.IO.Path.GetDirectoryName(Path) ?? "";
    public double AspectRatio => Height <= 0 ? 1 : (double)Width / Height;
    public string Resolution => $"{Width}×{Height}";
    public string AspectText => AspectRate(AspectRatio);

    /// <summary>归一化后的检索文本：小写、下划线转空格、去权重括号。用于「包含」匹配。</summary>
    public string SearchText = "";

    public static string AspectRate(double r) => r switch
    {
        < 0.62 => "9:16 竖",
        < 0.8 => "3:4 竖",
        < 0.95 => "4:5 竖",
        < 1.05 => "1:1 方",
        < 1.25 => "5:4 横",
        < 1.45 => "4:3 横",
        < 1.7 => "3:2 横",
        _ => "16:9 横",
    };

    public static AspectBucket BucketOf(double r) => r switch
    {
        < 0.62 => AspectBucket.Tall,
        < 0.95 => AspectBucket.Portrait,
        < 1.05 => AspectBucket.Square,
        < 1.45 => AspectBucket.Landscape,
        _ => AspectBucket.Wide,
    };
}
