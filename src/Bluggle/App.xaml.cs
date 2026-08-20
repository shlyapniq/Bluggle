using System.IO;
using System.Windows;
using System.Windows.Threading;
using Bluggle.Services;
using Bluggle.Views;

namespace Bluggle;

public partial class App : Application
{
    // "Local\" scopes the mutex to the login session, which is what we want: two users logged
    // in at once should each get their own widget.
    private const string SingleInstanceMutexName = @"Local\Bluggle.SingleInstance";

    private Mutex? _singleInstance;
    private ConfigStore? _store;
    private WidgetController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirst);
        if (!isFirst)
        {
            // A second copy launched from the Run key or a stray double-click just goes away.
            // No dialog: this happens invisibly at login and a nag box would be worse.
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogFatal(args.ExceptionObject as Exception, "AppDomain");

        _store = new ConfigStore();
        _controller = new WidgetController(_store);

        var window = new WidgetWindow(_controller);
        MainWindow = window;
        window.Show();

        // Fire and forget: the first radio enumeration and device scan should not hold up the
        // window appearing.
        _ = _controller.StartAsync();
    }

    /// <summary>
    /// Last line of defence. A widget that vanishes because a background poll threw is worse
    /// than one showing a red error flash, so anything reaching here is logged, surfaced in the
    /// tooltip, and swallowed.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogFatal(e.Exception, "Dispatcher");

        try
        {
            _controller?.ShowError($"Internal error: {e.Exception.Message}");
            e.Handled = true;
        }
        catch
        {
            // If even that fails, let the runtime take the process down.
        }
    }

    private void LogFatal(Exception? exception, string source)
    {
        if (exception is null) return;

        try
        {
            string directory = _store?.Directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ConfigStore.AppName);
            Directory.CreateDirectory(directory);

            File.AppendAllText(
                Path.Combine(directory, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never be the thing that crashes us.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Order matters: the controller may re-enable Bluetooth services on the way out, and
        // the store flushes any debounced position save.
        _controller?.Dispose();
        _store?.Dispose();

        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();

        base.OnExit(e);
    }
}
