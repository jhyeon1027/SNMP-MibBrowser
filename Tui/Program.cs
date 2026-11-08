using SnmpMibBrowser;
using SnmpMibBrowser.Tui;
using Terminal.Gui.App;

var version = typeof(MainView).Assembly.GetName().Version;
string? host = null;
var noMouse = false;
string? driver = null;
for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];
    switch (arg)
    {
        case "-h" or "--help":
            Console.WriteLine($"""
                SNMP MibBrowser (terminal) {version}

                Usage: snmpmb [options] [host]

                  host          Target to put in the Host field.
                  --no-mouse    Leave the mouse to the terminal, so text can be selected and
                                copied with the terminal's own selection (keyboard only in the app).
                  --driver NAME Terminal driver: ansi, dotnet or windows (default: automatic).
                                Try another one if keys or drawing misbehave in a terminal.
                  --version     Print the version.
                  -h, --help    Print this help.

                Settings: {new SettingsStore().FilePath}
                Keys: F1 in the app.
                """);
            return 0;
        case "--version":
            Console.WriteLine(version);
            return 0;
        case "--no-mouse":
            noMouse = true;
            break;
        case "--driver" when i + 1 < args.Length:
            driver = args[++i];
            break;
        default:
            if (arg.StartsWith('-')) { Console.Error.WriteLine($"Unknown option: {arg} (see --help)"); return 2; }
            host = arg;
            break;
    }
}

var store = new SettingsStore();
AppDomain.CurrentDomain.UnhandledException += (_, e) => store.WriteErrorLog("Fatal", e.ExceptionObject as Exception, version);
TaskScheduler.UnobservedTaskException += (_, e) => { store.WriteErrorLog("Background task", e.Exception, version); e.SetObserved(); };

// Ctrl+Z would suspend the program and leave the terminal in raw mode; it is not used.
Application.RemoveDefaultKeyBinding(Terminal.Gui.Input.Command.Suspend);

IApplication app;
try { app = Application.Create().Init(driver); }
catch (ArgumentException ex) when (driver != null)
{
    Console.Error.WriteLine($"{ex.Message} Use ansi, dotnet or windows.");
    return 2;
}
using (app)
{
    if (noMouse) app.Mouse.IsMouseDisabled = true;
    using var main = new MainView(store, host);
    // A closed SSH session (SIGHUP) or kill (SIGTERM) ends the program normally, so the terminal is restored.
    using var hup = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGHUP, Stop);
    using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, Stop);
    void Stop(System.Runtime.InteropServices.PosixSignalContext context)
    {
        context.Cancel = true;
        app.Invoke(main.RequestQuit);
    }
    // Errors inside event handlers are logged and shown; the app keeps running.
    app.Run(main, ex =>
    {
        store.WriteErrorLog("UI", ex, version);
        main.ShowUnexpectedError(ex);
        return true;
    });
}
return 0;
