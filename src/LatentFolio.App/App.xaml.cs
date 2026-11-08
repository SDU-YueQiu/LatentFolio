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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject?.ToString() ?? "?");

        var cli = Environment.GetCommandLineArgs();
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
