using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace InputAutomationTool.App;

public partial class App : Application
{
    private static readonly string CrashLogPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "InputAutomationTool", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        // Catch everything we possibly can so a failure is logged instead of
        // silently killing the process.
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true; // keep the app alive for UI-thread exceptions
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    private static void LogCrash(string source, Exception? ex)
    {
        var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\r\n{ex}\r\n\r\n";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath, text);
        }
        catch { /* nothing else we can do */ }

        try
        {
            MessageBox.Show($"{source}:\n\n{ex?.Message}\n\nDetails written to:\n{CrashLogPath}",
                "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* process may be tearing down */ }
    }
}
