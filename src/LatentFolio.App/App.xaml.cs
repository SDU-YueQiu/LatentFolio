using System.Windows;
using System.Windows.Threading;
using LatentFolio.App.Services;
using LatentFolio.App.ViewModels;

namespace LatentFolio.App;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;
    public static AppVm Root { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                var p = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LatentFolio", "crash.log");
                System.IO.File.AppendAllText(p, $"[{DateTime.Now:O}] {args.ExceptionObject}\n\n");
            }
            catch { }
        };

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

        var win = new MainWindow();
        MainWindow = win;
        win.Show();
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var p = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LatentFolio", "crash.log");
            System.IO.File.AppendAllText(p, $"[{DateTime.Now:O}] {e.Exception}\n\n");
        }
        catch { }

        e.Handled = true;
        MessageBox.Show($"出错了：{e.Exception.Message}", "LatentFolio", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
