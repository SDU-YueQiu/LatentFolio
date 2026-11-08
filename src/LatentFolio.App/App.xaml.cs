using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using LatentFolio.App.Services;
using LatentFolio.App.ViewModels;
using LatentFolio.Core.Comfy;

namespace LatentFolio.App;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;
    public static AppVm Root { get; private set; } = null!;

    private Mutex? _instanceMutex;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? cls, string title);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var cli0 = Environment.GetCommandLineArgs();

        // 单实例：两个进程同时开同一个 SQLite 库、两个目录监视器都会打架。
        // 开发用的诊断开关允许并存。
        bool diagnostic = cli0.Any(a => a is "--self-test" or "--parse-one" or "--test-vsr"
                                          or "--bench" or "--show-stats" or "--show-editor"
                                          or "--show-people" or "--show-tags" or "--show-candidates"
                                          or "--show-filter" or "--show-settings" or "--mem-report" or "--retag"
                                          or "--bench-decode" or "--sort-rating");
        if (!diagnostic)
        {
            _instanceMutex = new Mutex(true, @"Local\LatentFolio.SingleInstance", out bool isNew);
            if (!isNew)
            {
                var hwnd = FindWindow(null, "LatentFolio");
                if (hwnd != IntPtr.Zero) { ShowWindow(hwnd, 9); SetForegroundWindow(hwnd); }
                Shutdown(0);
                return;
            }
        }

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject?.ToString() ?? "?");

        var cli = cli0;
        int pi = Array.IndexOf(cli, "--parse-one");
        if (pi >= 0 && pi + 1 < cli.Length)
        {
            RunParseOne(cli[pi + 1]);
            Shutdown(0);
            return;
        }

        try
        {
            Services = new AppServices();
            Root = new AppVm(Services, Services.ThumbService);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"初始化失败：\n{ex}", "LatentFolio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        int vi = Array.IndexOf(cli, "--test-vsr");
        if (vi >= 0)
        {
            RunVsrTest(vi + 1 < cli.Length ? cli[vi + 1] : null);
            Shutdown(0);
            return;
        }

        int bi = Array.IndexOf(cli, "--bench");
        if (bi >= 0)
        {
            RunBench(bi + 1 < cli.Length && int.TryParse(cli[bi + 1], out var n) ? n : 50000);
            Shutdown(0);
            return;
        }

        int dbi = Array.IndexOf(cli, "--bench-decode");
        if (dbi >= 0)
        {
            RunDecodeBench(dbi + 1 < cli.Length && int.TryParse(cli[dbi + 1], out var n2) ? n2 : 300);
            Shutdown(0);
            return;
        }

        var win = new MainWindow();
        MainWindow = win;
        win.Show();
    }

    private static void Log(string text)
    {
        try
        {
            var p = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LatentFolio", "crash.log");
            System.IO.File.AppendAllText(p, $"[{DateTime.Now:O}] {text}\n\n");
        }
        catch { }
    }

    /// <summary>开发诊断：--test-vsr [png] 跑一遍超分，把耗时写进 %TEMP%\lf-vsr.txt。</summary>
    private static void RunVsrTest(string? path)
    {
        var sb = new StringBuilder();
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ok = Services.Vsr.StartAsync().GetAwaiter().GetResult();
            sw.Stop();
            sb.AppendLine($"engine available = {ok}  status = {Services.Vsr.Status}");
            sb.AppendLine($"libdir = {Services.Vsr.Engine.LibDir}");
            sb.AppendLine($"start took {sw.ElapsedMilliseconds} ms");

            if (path is null)
            {
                var first = Services.Store.LoadAll().FirstOrDefault(x => x.Width >= 512);
                path = first?.Path;
            }
            if (path is null) { sb.AppendLine("no image"); }
            else
            {
                sb.AppendLine($"input = {path}");
                var item = new Core.Models.ImageItem { Id = 1, Path = path };

                var t0 = System.Diagnostics.Stopwatch.StartNew();
                var bmp = Services.Vsr.ProcessAsync(item).GetAwaiter().GetResult();
                t0.Stop();
                sb.AppendLine(bmp is null
                    ? "PROCESS FAILED"
                    : $"output {bmp.PixelWidth}x{bmp.PixelHeight} in {t0.ElapsedMilliseconds} ms "
                      + $"(engine {Services.Vsr.Engine.LastRunMs:F0} ms)");

                var t1 = System.Diagnostics.Stopwatch.StartNew();
                var bmp2 = Services.Vsr.ProcessAsync(new Core.Models.ImageItem { Id = 2, Path = path }).GetAwaiter().GetResult();
                t1.Stop();
                sb.AppendLine(bmp2 is null ? "2nd FAILED" : $"2nd run {t1.ElapsedMilliseconds} ms");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION " + ex);
        }

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lf-vsr.txt"), sb.ToString());
    }

    /// <summary>
    /// 开发诊断：--bench [N] 用临时库造 N 张图，量 Core 侧的读取 / 筛选 / 排序 / 相似分组。
    /// 不碰用户的真库。
    /// </summary>
    private static void RunBench(int count)
    {
        var sb = new StringBuilder();
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lf-bench");
        try
        {
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
            System.IO.Directory.CreateDirectory(dir);

            var db = new LatentFolio.Core.Db.Db(System.IO.Path.Combine(dir, "bench.db"));
            var store = new LatentFolio.Core.Db.ImageStore(db);

            var models = new[] { "z_image_turbo", "krea2-turbo", "noobai_v15", "illustrious_xl", "animagine_xl" };
            var samplers = new[] { "er_sde", "euler_ancestral", "euler", "dpmpp_2m_sde" };
            var words = new[] { "1girl", "solo", "looking_at_viewer", "long_hair", "smile", "blue_eyes",
                                "school_uniform", "outdoors", "night", "starry_sky", "silver_hair", "dress" };
            var rnd = new Random(1234);

            var swInsert = System.Diagnostics.Stopwatch.StartNew();
            using (var tx = db.Connection.BeginTransaction())
            {
                for (int i = 0; i < count; i++)
                {
                    var sbPrompt = new StringBuilder();
                    int nw = 20 + rnd.Next(60);
                    for (int k = 0; k < nw; k++) sbPrompt.Append(' ').Append(words[rnd.Next(words.Length)]);
                    var prompt = sbPrompt.ToString();

                    var item = new LatentFolio.Core.Models.ImageItem
                    {
                        Path = $@"{dir}\img\{i % 400}\{i}.png",
                        Folder = $"2025-{1 + i % 12:00}-{1 + i % 28:00}" + (i % 7 == 0 ? "/sub" : ""),
                        FileName = $"img_{i:000000}.png",
                        Ext = "png",
                        Size = 1_000_000 + i * 37L,
                        MTime = DateTime.UtcNow.Ticks,
                        AddedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - i * 1000L,
                        Width = 1024, Height = rnd.Next(2) == 0 ? 1024 : 1536,
                        Model = models[rnd.Next(models.Length)],
                        Sampler = samplers[rnd.Next(samplers.Length)],
                        Scheduler = "simple",
                        Steps = 8 + rnd.Next(30),
                        Cfg = 1 + rnd.Next(6),
                        Seed = rnd.NextInt64(),
                        Prompt = prompt,
                        Negative = "worst quality, bad anatomy",
                        PromptSource = "conditioning-trace",
                        Rating = rnd.Next(6),
                        SearchText = prompt.ToLowerInvariant() + " worst quality bad anatomy",
                        DHash = rnd.NextInt64(),
                    };
                    item.Aspect = LatentFolio.Core.Models.ImageItem.BucketOf(item.AspectRatio);
                    store.Insert(item);
                }
                tx.Commit();
            }
            swInsert.Stop();

            var swLoad = System.Diagnostics.Stopwatch.StartNew();
            var all = store.LoadAll();
            swLoad.Stop();

            var cond = new LatentFolio.Core.Models.FilterGroup
            {
                Op = LatentFolio.Core.Models.BoolOp.And,
                Children =
                {
                    new LatentFolio.Core.Models.FilterCond
                    {
                        Field = LatentFolio.Core.Models.FieldType.Rating,
                        Op = LatentFolio.Core.Models.CondOp.Gte, Value = "4",
                    },
                    new LatentFolio.Core.Models.FilterCond
                    {
                        Field = LatentFolio.Core.Models.FieldType.Prompt,
                        Op = LatentFolio.Core.Models.CondOp.Contains, Value = "starry_sky",
                    },
                    new LatentFolio.Core.Models.FilterGroup
                    {
                        Op = LatentFolio.Core.Models.BoolOp.Or,
                        Children =
                        {
                            new LatentFolio.Core.Models.FilterCond
                            {
                                Field = LatentFolio.Core.Models.FieldType.Model,
                                Op = LatentFolio.Core.Models.CondOp.Contains, Value = "turbo",
                            },
                            new LatentFolio.Core.Models.FilterCond
                            {
                                Field = LatentFolio.Core.Models.FieldType.Steps,
                                Op = LatentFolio.Core.Models.CondOp.Gte, Value = "20",
                            },
                        },
                    },
                },
            };

            var swFilter = System.Diagnostics.Stopwatch.StartNew();
            var hits = LatentFolio.Core.Filtering.ColumnEngine.Filter(all,
                cond, new LatentFolio.Core.Models.FilterContext());
            swFilter.Stop();

            var swSort = System.Diagnostics.Stopwatch.StartNew();
            LatentFolio.Core.Filtering.ColumnEngine.Sort(hits,
                new LatentFolio.Core.Filtering.SortSpec(LatentFolio.Core.Filtering.SortKey.Rating, true));
            swSort.Stop();

            var swSim = System.Diagnostics.Stopwatch.StartNew();
            var sim = LatentFolio.Core.Filtering.SimilarityIndex.Build(all);
            swSim.Stop();

            sb.AppendLine($"合成 {count:N0} 张");
            sb.AppendLine($"落库        {swInsert.ElapsedMilliseconds,6} ms");
            sb.AppendLine($"全量读入    {swLoad.ElapsedMilliseconds,6} ms");
            sb.AppendLine($"三条件筛选  {swFilter.ElapsedMilliseconds,6} ms  -> {hits.Count:N0} 张");
            sb.AppendLine($"排序        {swSort.ElapsedMilliseconds,6} ms");
            sb.AppendLine($"相似分组    {swSim.ElapsedMilliseconds,6} ms  -> {sim.GroupCount:N0} 组 / {sim.InGroup.Count:N0} 张");

            db.Dispose();
            System.IO.Directory.Delete(dir, true);
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION " + ex);
        }

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lf-bench.txt"), sb.ToString());
    }

    /// <summary>
    /// 开发诊断：--bench-decode [n] 量「从磁盘缓存解码到某个长边」要多久。
    /// 这个数字决定了解码缓存值不值得占几百 MB —— 照片应用是不缓存的。
    /// </summary>
    private static void RunDecodeBench(int count)
    {
        var sb = new StringBuilder();
        try
        {
            var root = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LatentFolio", "thumbs", "grid");
            var files = System.IO.Directory.Exists(root)
                ? System.IO.Directory.GetFiles(root, "*.webp", System.IO.SearchOption.AllDirectories)
                : [];
            sb.AppendLine($"cache files: {files.Length}   采样: {Math.Min(count, files.Length)}");
            if (files.Length == 0) { Write(sb); return; }

            var rnd = new Random(7);
            var pick = files.OrderBy(_ => rnd.Next()).Take(Math.Min(count, files.Length)).ToArray();
            var blobs = new byte[pick.Length][];
            for (int i = 0; i < pick.Length; i++) blobs[i] = System.IO.File.ReadAllBytes(pick[i]);
            long diskBytes = blobs.Sum(b => (long)b.Length);
            sb.AppendLine($"读入 {diskBytes / 1024 / 1024} MB，平均 {diskBytes / blobs.Length / 1024} KB/张");

            foreach (var side in new[] { 0, 416, 608, 704 })
            {
                // 预热
                for (int i = 0; i < Math.Min(8, blobs.Length); i++)
                    _ = LatentFolio.App.Services.ThumbService.DecodeScaled(blobs[i], side == 0 ? 100000 : side);

                var ms = new double[blobs.Length];
                long px = 0;
                var sw = new System.Diagnostics.Stopwatch();
                for (int i = 0; i < blobs.Length; i++)
                {
                    sw.Restart();
                    var bmp = LatentFolio.App.Services.ThumbService.DecodeScaled(blobs[i], side == 0 ? 100000 : side);
                    sw.Stop();
                    ms[i] = sw.Elapsed.TotalMilliseconds;
                    if (bmp is not null) px += (long)bmp.PixelWidth * bmp.PixelHeight;
                }
                Array.Sort(ms);
                double Avg() => ms.Average();
                double P(double q) => ms[Math.Min(ms.Length - 1, (int)(ms.Length * q))];
                sb.AppendLine(side == 0
                    ? $"全长解码       avg={Avg(),6:F2} ms  p50={P(0.5),6:F2}  p95={P(0.95),6:F2}  max={ms[^1],7:F2}   平均 {px / blobs.Length / 1000} kpx"
                    : $"长边 {side,3} 解码  avg={Avg(),6:F2} ms  p50={P(0.5),6:F2}  p95={P(0.95),6:F2}  max={ms[^1],7:F2}   平均 {px / blobs.Length / 1000} kpx");
            }

            // ---- 只量「缩放」本身：同一批位图，不同采样器 ----
            int sample = Math.Min(60, blobs.Length);
            var srcs = new SkiaSharp.SKBitmap[sample];
            for (int i = 0; i < sample; i++) srcs[i] = SkiaSharp.SKBitmap.Decode(blobs[i])!;
            try
            {
                sb.AppendLine();
                sb.AppendLine("缩放-only（不含量解码）  样本 " + sample);
                var samplers = new (string Name, SkiaSharp.SKSamplingOptions Opt)[]
                {
                    ("CatmullRom", new(new SkiaSharp.SKCubicResampler(0f, 0.5f))),
                    ("Mitchell  ", new(new SkiaSharp.SKCubicResampler(1f / 3f, 1f / 3f))),
                    ("Linear    ", new(SkiaSharp.SKFilterMode.Linear)),
                    ("Nearest   ", new(SkiaSharp.SKFilterMode.Nearest)),
                };
                foreach (var side in new[] { 608, 416 })
                {
                    foreach (var (name, opt) in samplers)
                    {
                        var ms = new double[sample];
                        var sw2 = new System.Diagnostics.Stopwatch();
                        for (int i = 0; i < sample; i++)
                        {
                            var s = srcs[i];
                            int ls = Math.Max(s.Width, s.Height);
                            double k = (double)side / ls;
                            int dw = Math.Max(1, (int)Math.Round(s.Width * k));
                            int dh = Math.Max(1, (int)Math.Round(s.Height * k));
                            var info = new SkiaSharp.SKImageInfo(dw, dh, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                            sw2.Restart();
                            var r = s.Resize(info, opt);
                            sw2.Stop();
                            ms[i] = sw2.Elapsed.TotalMilliseconds;
                            r?.Dispose();
                        }
                        Array.Sort(ms);
                        sb.AppendLine($"  长边 {side}  {name}  avg={ms.Average(),6:F2} ms  p50={ms[ms.Length / 2],6:F2}  max={ms[^1],7:F2}");
                    }
                }
            }
            finally
            {
                foreach (var s in srcs) s?.Dispose();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION " + ex);
        }
        Write(sb);

        static void Write(StringBuilder sb) => System.IO.File.WriteAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lf-decode.txt"), sb.ToString());
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception.ToString());
        e.Handled = true;
        MessageBox.Show($"出错了：{e.Exception.Message}", "LatentFolio", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>开发诊断：--parse-one &lt;png&gt; 把解析过程写进 %TEMP%\lf-parse.txt。</summary>
    private static void RunParseOne(string path)
    {
        var sb = new StringBuilder();
        try
        {
            var meta = PngReader.Read(path);
            sb.AppendLine($"file: {path}");
            sb.AppendLine($"png: {meta.Width}x{meta.Height}  prompt={(meta.Prompt?.Length ?? -1)}  workflow={(meta.Workflow?.Length ?? -1)}");

            if (meta.Prompt is { } pj)
            {
                sb.AppendLine("prompt head: " + pj[..Math.Min(200, pj.Length)].Replace('\n', ' '));
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(pj);
                    var n = 0;
                    foreach (var _ in doc.RootElement.EnumerateObject()) n++;
                    sb.AppendLine($"json ok, nodes={n}");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("JSON PARSE FAIL: " + ex.Message);
                }

                var r = GraphParser.ParseWorkflowApiJson(pj);
                sb.AppendLine($"method={r.Method} split={r.Split}");
                sb.AppendLine($"posClasses={string.Join(",", r.PosClasses)}");
                sb.AppendLine($"negClasses={string.Join(",", r.NegClasses)}");
                sb.AppendLine($"pos[{r.Positive.Length}]={r.Positive[..Math.Min(300, r.Positive.Length)].Replace('\n', ' ')}");
                sb.AppendLine($"neg[{r.Negative.Length}]={r.Negative[..Math.Min(160, r.Negative.Length)].Replace('\n', ' ')}");
                sb.AppendLine($"model={r.Model} sampler={r.Sampler} steps={r.Steps} cfg={r.Cfg} seed={r.Seed}");
            }
            else
            {
                sb.AppendLine("NO prompt chunk");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }

        var outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lf-parse.txt");
        System.IO.File.WriteAllText(outPath, sb.ToString());
    }
}
