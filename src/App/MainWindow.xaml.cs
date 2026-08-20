using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SmartActiveTools.Core;

namespace SmartActiveTools.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var ver = asm.GetName().Version;
        var verStr = ver != null
            ? (ver.Build > 0 ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : $"{ver.Major}.{ver.Minor}")
            : "0.22";

        var buildDate = GetBuildDate();
        Title += string.IsNullOrEmpty(buildDate)
            ? $" v{verStr}"
            : $" v{verStr} ({buildDate})";

        DataContext = _vm;

        // Auto-scroll the log to the newest entry.
        _vm.Log.CollectionChanged += OnLogChanged;

        Closing += (_, _) => _vm.SaveConfig();
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
            return;

        // Defer so the TextBox has applied the updated bound text before we scroll.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (FindName("LogBox") is TextBox box)
                box.ScrollToEnd();
        });
    }

    private static string GetBuildDate()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath) && File.Exists(processPath))
            {
                return File.GetLastWriteTime(processPath).ToString("dd/MM/yyyy");
            }
        }
        catch
        {
            // fallback if file metadata is unavailable
        }
        return "";
    }
}

