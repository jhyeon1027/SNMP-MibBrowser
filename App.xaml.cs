using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SnmpMibBrowser;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static readonly string ErrorLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SNMP-MibBrowser", "error.log");

    static App()
    {
        // Show the entered auth/privacy key as a tooltip while hovering any PasswordBox.
        EventManager.RegisterClassHandler(typeof(PasswordBox), UIElement.MouseEnterEvent, new System.Windows.Input.MouseEventHandler((s, _) =>
        {
            var box = (PasswordBox)s;
            box.ToolTip = string.IsNullOrEmpty(box.Password) ? null : box.Password;
        }));
    }

    public App()
    {
        // Unhandled errors are written to error.log; UI-thread errors are shown and the app keeps running.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteErrorLog("Fatal", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { WriteErrorLog("Background task", e.Exception); e.SetObserved(); };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteErrorLog("UI", e.Exception);
        e.Handled = true;
        MessageBox.Show($"{e.Exception.Message}\n\nDetails: {ErrorLogPath}", "SNMP MibBrowser - Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void WriteErrorLog(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogPath)!);
            var version = typeof(App).Assembly.GetName().Version;
            File.AppendAllText(ErrorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source} error (v{version}){Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}

