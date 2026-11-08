using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LatentFolio.Core.Comfy;
using LatentFolio.Core.Db;
using LatentFolio.Core.Models;
using LatentFolio.Core.Text;

namespace LatentFolio.App.ViewModels;

/// <summary>网格里一个格子 / 查看器里的当前图。同一个 ImageItem 只有一个 VM。</summary>
public sealed partial class ImageVm : ObservableObject
{
    public ImageItem Item { get; }
    public long Id => Item.Id;

    public ImageVm(ImageItem item) => Item = item;

    public string FileName => Item.FileName;
    public string Folder => string.IsNullOrEmpty(Item.Folder) ? "（根目录）" : Item.Folder;
    public string FullPath => Item.Path;
    public string Resolution => Item.Resolution;
    public string AspectText => Item.AspectText;

    public int Width => Item.Width;
    public int Height => Item.Height;

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
    }

    public string StarText => Rating > 0 ? new string('★', Rating) : "";

    [ObservableProperty]
    private string _note = "";

    partial void OnNoteChanged(string value) => Item.Note = value;

    public string Prompt => Item.Prompt;
    public string Negative => Item.Negative;
    public bool HasPrompt => Item.Prompt.Length > 0;
    public bool HasNegative => Item.Negative.Length > 0;
    public bool NegIsDup => Item.NegIsDup;

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

    public void RefreshTags()
    {
        var v = Vocabulary.Instance;
        var chars = new List<PersonChip>(4);
        foreach (var c in Item.Chars)
        {
            var zh = v.Display(c);
            chars.Add(new PersonChip(c, zh, zh != c));
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

public sealed record PersonChip(string Tag, string Display, bool IsLocalized);
