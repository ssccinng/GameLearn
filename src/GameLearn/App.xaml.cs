using System.Windows;

namespace GameLearn;

public partial class App : System.Windows.Application
{
    private Mutex? mutex;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--diagnose"))
        {
            try { await Diagnostics.RunAsync(e.Args); Shutdown(0); }
            catch (Exception error) { Directory.CreateDirectory(AppSettings.DataDirectory); File.WriteAllText(Path.Combine(AppSettings.DataDirectory, "diagnostic-error.txt"), error.ToString()); Shutdown(1); }
            return;
        }
        mutex = new Mutex(true, "Local\\GameLearn.Desktop", out var first);
        if (!first) { MessageBox.Show("GameLearn 已在运行，请从任务栏或系统托盘打开。", "GameLearn"); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            File.AppendAllText(Path.Combine(AppSettings.DataDirectory, "errors.log"), $"{DateTimeOffset.Now:O} {args.Exception.GetType().Name}: {args.Exception.Message}\n");
            MessageBox.Show(args.Exception.Message, "GameLearn · 操作未完成"); args.Handled = true;
        };
        var window = new MainWindow(); MainWindow = window;
        if (e.Args.Contains("--floating")) window.Vm.Settings.PreferFloatingMode = true;
        await window.StartUserInterfaceAsync();
    }
    protected override void OnExit(ExitEventArgs e) { mutex?.Dispose(); base.OnExit(e); }
}
