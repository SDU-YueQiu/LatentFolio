using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LatentFolio.Core.Comfy;
using LatentFolio.Core.Db;
using LatentFolio.Core.Models;
using LatentFolio.Core.Text;

namespace LatentFolio.App.ViewModels;

/// <summary>网格里一个格子 / 查看器里的当前图。同一个 ImageItem 只有一个 VM。</summary>
public sealed partial class ImageVm : ObservableObject, Controls.IAspectItem
{
    public override string ToString() => FileName;
    public ImageItem Item { get; }
    public long Id => Item.Id;

    /// <summary>改了要落库。由 AppVm 在创建时挂上；分开是因为改评分要重算栏命中，
    /// 改批注不用（批注框每敲一个字都会触发）。</summary>
    public Action<ImageVm>? RatingPersist;
    public Action<ImageVm>? NotePersist;

    public ImageVm(ImageItem item)
    {
        Item = item;
        _rating = item.Rating;
        _note = item.Note;
    }

    public string FileName => Item.FileName;
    public string Folder => string.IsNullOrEmpty(Item.Folder) ? "（根目录）" : Item.Folder;
    public string FullPath => Item.Path;
    public string Resolution => Item.Resolution;
    public string AspectText => Item.AspectText;

    public int Width => Item.Width;
    public int Height => Item.Height;

    /// <summary>等高网格靠它算这一张该占多宽。</summary>
    public double PixelAspect => Height > 0 ? (double)Width / Height : 1;

    public string SizeText => Item.Size switch
    {
        < 1024 => $"{Item.Size} B",
        < 1024 * 1024 => $"{Item.Size / 1024.0:F0} KB",
        _ => $"{Item.Size / 1024.0 / 1024.0:F2} MB",
    };

    public string ExtText => Item.Ext.ToUpperInvariant();
    public string DateText => Item.Added.ToString("yyyy-MM-dd HH:mm");

    [ObservableProperty]
    private int _rating;

    partial void OnRatingChanged(int value)
    {
        Item.Rating = value;
        OnPropertyChanged(nameof(StarText));
        RatingPersist?.Invoke(this);
    }

    public string StarText => Rating > 0 ? new string('★', Rating) : "";

    [ObservableProperty]
    private string _note = "";

    partial void OnNoteChanged(string value)
    {
        Item.Note = value;
        NotePersist?.Invoke(this);
    }

    public string Prompt => Item.Prompt;
    public string Negative => Item.Negative;
    public bool HasPrompt => Item.Prompt.Length > 0;
    public bool HasNegative => Item.Negative.Length > 0;
    public bool NegIsDup => Item.NegIsDup;

    public string NegHint => Item.NegIsDup
        ? "与正面提示词完全相同 —— 工作流把同一条 CLIPTextEncode 接到了正负两个口，不是真的负面词。"
        : "";

    public string ModelText => Item.Model ?? "—";
    public string SamplerText => Item.Sampler ?? "—";
    public string SchedulerText => Item.Scheduler ?? "—";
    public string StepsText => Item.Steps?.ToString() ?? "—";
    public string CfgText => Item.Cfg?.ToString("0.##") ?? "—";
    public string SeedText => Item.Seed?.ToString() ?? "—";
    public string DenoiseText => Item.Denoise?.ToString("0.##") ?? "—";

    public bool HasSamplerParams => Item.Steps is not null || Item.Cfg is not null || Item.Seed is not null;

    public IReadOnlyList<PersonChip> People { get; private set; } = [];
    public IReadOnlyList<string> Artists { get; private set; } = [];

    public bool HasPeople => People.Count > 0;
    public bool HasArtists => Artists.Count > 0;

    public void RefreshTags(Func<string, long>? avatarOf = null)
    {
        var v = Vocabulary.Instance;
        var chars = new List<PersonChip>(4);
        foreach (var c in Item.Chars)
        {
            var zh = v.Display(c);
            chars.Add(new PersonChip(c, zh, zh != c, avatarOf?.Invoke(c) ?? 0));
        }
        People = chars;
        Artists = Item.Artists;
        OnPropertyChanged(nameof(People));
        OnPropertyChanged(nameof(Artists));
        OnPropertyChanged(nameof(HasPeople));
        OnPropertyChanged(nameof(HasArtists));
    }

    [ObservableProperty]
    private ImageSource? _thumb;

    [ObservableProperty]
    private bool _thumbTried;

    /// <summary>底部胶片条里高亮这一张。</summary>
    [ObservableProperty]
    private bool _isCurrent;

    public string LegacyTag => "";

    public void ApplyFromItem()
    {
        Rating = Item.Rating;
        Note = Item.Note;
        RefreshTags();
        OnPropertyChanged(nameof(Prompt));
        OnPropertyChanged(nameof(Negative));
        OnPropertyChanged(nameof(HasPrompt));
        OnPropertyChanged(nameof(HasNegative));
        OnPropertyChanged(nameof(NegIsDup));
        OnPropertyChanged(nameof(ModelText));
        OnPropertyChanged(nameof(SamplerText));
        OnPropertyChanged(nameof(SchedulerText));
        OnPropertyChanged(nameof(StepsText));
        OnPropertyChanged(nameof(CfgText));
        OnPropertyChanged(nameof(SeedText));
        OnPropertyChanged(nameof(Resolution));
        OnPropertyChanged(nameof(AspectText));
    }
}

public sealed record PersonChip(string Tag, string Display, bool IsLocalized, long AvatarImageId);


