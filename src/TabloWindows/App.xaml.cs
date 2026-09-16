using System.IO;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace TabloWindows;

public partial class App : Application
{
    private static readonly string LogDir = ResolveLogDir();

    private static string ResolveLogDir()
    {
        // Prefer %AppData%, but fall back to an always-writable absolute path so the crash
        // log is never lost when the process is launched in an odd environment.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(appData)
            ? @"C:\Users\Public\TabloWindows"
            : Path.Combine(appData, "TabloWindows");
    }

    public App()
    {
        // Subscribe BEFORE InitializeComponent (App.xaml parsing) so we capture any
        // resource/XAML load failure that happens before OnStartup.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("EarlyAppDomain", args.ExceptionObject as Exception);
        Trace("App ctor");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Log any failure that would otherwise crash the process before the UI is up.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("AppDomain", args.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("UnobservedTask", args.Exception);
            args.SetObserved();
        };
        DispatcherUnhandledException += OnUnhandledException;

        try
        {
            Trace("OnStartup begin");
            // Point libvlc at the bundled native runtime explicitly (self-contained layout).
            var libDir = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
            if (Directory.Exists(libDir))
                Core.Initialize(libDir);
            else
                Core.Initialize();
            Trace("Core.Initialize done");

            base.OnStartup(e);

            var window = new MainWindow();
            Trace("MainWindow constructed");
            MainWindow = window;
            window.Show();
            Trace("MainWindow shown");
        }
        catch (Exception ex)
        {
            Log("OnStartup", ex);
            MessageBox.Show(
                "TabloWindows failed to start:\n\n" + ex.Message +
                "\n\nA log was written to:\n" + Path.Combine(LogDir, "crash.log"),
                "TabloWindows — startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Dispatcher", e.Exception);
        MessageBox.Show(e.Exception.Message, "TabloWindows — Unexpected error",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void Log(string where, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(Path.Combine(LogDir, "crash.log"),
                $"[{DateTime.Now:u}] {where}: {ex}\n\n");
        }
        catch { /* nothing we can do */ }
    }

    /// <summary>Lightweight lifecycle trace (same crash.log file).</summary>
    public static void Trace(string msg)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(Path.Combine(LogDir, "crash.log"),
                $"[{DateTime.Now:u}] TRACE {msg}\n");
        }
        catch { /* ignore */ }
    }
}
