using System.Collections.Specialized;
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
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var verStr = ver != null
            ? (ver.Build > 0 ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : $"{ver.Major}.{ver.Minor}")
            : "0.21";
        Title += $" v{verStr}";
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
}
