using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace RelentlessMonitor.Ui;

public partial class App : Application
{
    /// <summary>Unexpected errors go here rather than silently killing the monitor.</summary>
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelentlessMonitor", "monitor.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, a) =>
        {
            // A widget that trips over an odd value shouldn't take every other panel down with it.
            Log("ui", a.Exception);
            a.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log("fatal", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log("task", a.Exception); a.SetObserved(); };
        base.OnStartup(e);
        // Opened here rather than by StartupUri: only Run() gets here, so code that merely creates an App
        // (or loads its resources) can never put a window on screen.
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    internal static void Log(string what, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > 1_000_000) info.Delete();
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:u} [{what}] {ex}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
