using System.IO;
using LatentFolio.Core.Db;
using LatentFolio.Core.Imaging;
using LatentFolio.Core.Scan;
using LatentFolio.Core.Text;

namespace LatentFolio.App.Services;

/// <summary>全局服务：库、缓存、扫描器。整个进程一套。</summary>
public sealed class AppServices : IDisposable
{
    public static AppServices I { get; private set; } = null!;

    public string DataDir { get; }
    public string DbPath { get; }
    public LatentFolio.Core.Db.Db Db { get; }
    public ImageStore Store { get; }
    public ThumbCache Thumbs { get; }
    public LibraryScanner Scanner { get; }
    public ThumbService ThumbService { get; }
    public VsrService Vsr { get; }
    public string AssetsDir { get; }

    public string LibraryRoot
    {
        get => Store.GetSetting("library_root") ?? DefaultLibraryRoot();
        set => Store.SetSetting("library_root", value);
    }

    public const string ThumbPresetKey = "library_root";

    public AppServices()
    {
        I = this;
        DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LatentFolio");
        Directory.CreateDirectory(DataDir);
        DbPath = Path.Combine(DataDir, "library.db");
        Thumbs = new ThumbCache(Path.Combine(DataDir, "thumbs"));
        Db = new Core.Db.Db(DbPath);
        Store = new ImageStore(Db);
        Scanner = new LibraryScanner(Db, Store, Thumbs);
        ThumbService = new ThumbService(Thumbs);
        Vsr = new VsrService();
        AssetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        Vocabulary.Instance.Warm(AssetsDir);
    }

    public static string DefaultLibraryRoot()
    {
        var candidates = new[]
        {
            @"D:\Recreation\ComfyUI-aki-v3\ComfyUI\output",
            @"C:\ComfyUI\output",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ComfyUI", "output"),
        };
        foreach (var c in candidates) if (Directory.Exists(c)) return c;
        return candidates[0];
    }

    public void Dispose()
    {
        Vsr.Dispose();
        ThumbService.Dispose();
        Db.Dispose();
    }
}

