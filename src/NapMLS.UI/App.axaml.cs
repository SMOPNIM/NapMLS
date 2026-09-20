using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NapMLS.UI.ViewModels;
using NapMLS.UI.Views;

namespace NapMLS.UI;

public partial class App : Application
{
    private static string LogDir = null!;
    private static string LogFile = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Per-instance log directory: --data-dir takes priority, else AppData
        LogDir = GetDataDirFromArgs();
        Directory.CreateDirectory(LogDir);
        LogFile = Path.Combine(LogDir, "crash.log");

        // Redirect Console output to per-instance log file
        try
        {
            var logStream = new StreamWriter(File.Open(Path.Combine(LogDir, "napmls.log"),
                FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            { AutoFlush = true };
            Console.SetOut(logStream);
            Console.SetError(logStream);
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] NapMLS starting... (data-dir={LogDir})");
        }
        catch { /* best effort */ }

        // Global unhandled exception handlers
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                File.AppendAllText(LogFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UNHANDLED: {e.ExceptionObject}\n");
            }
            catch { }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                File.AppendAllText(LogFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TASK ERROR: {e.Exception}\n");
            }
            catch { }
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string GetDataDirFromArgs()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--data-dir")
                return args[i + 1];
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NapMLS");
    }
}