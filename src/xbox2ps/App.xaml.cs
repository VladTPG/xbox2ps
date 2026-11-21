using System.Windows;
using System.Windows.Threading;
using Xbox2Ps.Core;
using Xbox2Ps.ViewModels;

namespace Xbox2Ps;

public partial class App : Application
{
    private const string MutexName = @"Local\xbox2ps-single-instance";
    private const string ShowEventName = @"Local\xbox2ps-show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private bool _isPrimary;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private Tray? _tray;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One instance only: a second one would create a second virtual DualSense.
        _mutex = new Mutex(initiallyOwned: true, MutexName, out _isPrimary);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!_isPrimary)
        {
            _showEvent.Set(); // ask the running instance to show its window
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        var settings = AppSettings.Load();
        _vm = new MainViewModel(settings);
        _window = new MainWindow(_vm, this);
        _tray = new Tray(_vm, ShowMainWindow, Quit);
        _vm.Notify += (title, text) => _tray?.ShowHint(title, text);

        new Thread(WaitForShowRequests) { IsBackground = true, Name = "xbox2ps show-request" }.Start();

        bool minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase) || settings.StartMinimized;
        if (!minimized) _window.Show();
        else if (!settings.CloseToTray) { _window.WindowState = WindowState.Minimized; _window.Show(); }
        // otherwise it starts in the tray only

        if (settings.StartMappingOnLaunch) _vm.StartMapping();
    }

    private void WaitForShowRequests()
    {
        var handle = _showEvent;
        while (handle is not null)
        {
            try { handle.WaitOne(); }
            catch (ObjectDisposedException) { return; }
            if (_exiting) return;
            Dispatcher.BeginInvoke(ShowMainWindow);
        }
    }

    public void ShowMainWindow()
    {
        if (_window is null || _exiting) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;  // bring to front even when another app has focus
        _window.Topmost = false;
        _window.Focus();
    }

    /// <summary>Close for real: stop mapping, unplug the virtual pad, leave.</summary>
    public void Quit()
    {
        if (_exiting) return;
        _exiting = true;
        _window?.AllowClose();
        _window?.Close();
        _tray?.Dispose();
        _vm?.Shutdown();
        Shutdown();
    }

    /// <summary>The window's close button: hide to the tray, or quit, per the options.</summary>
    public void OnWindowClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || _vm is null) return;
        if (_vm.CloseToTray)
        {
            e.Cancel = true;
            _window?.Hide();
            if (_vm.TakeTrayHint())
                _tray?.ShowHint("xbox2ps is still running", "It keeps mapping in the background. Right-click the tray icon to quit.");
        }
        else
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(Quit);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Quit();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_isPrimary)
        {
            if (!_exiting && _vm is not null)
            {
                _exiting = true;
                _tray?.Dispose();
                _vm.Shutdown();
            }
            _exiting = true;
            _showEvent?.Set(); // wake the waiting thread so it can end
        }
        _showEvent?.Dispose();
        _showEvent = null;
        if (_mutex is not null)
        {
            if (_isPrimary) _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _vm?.AddLog($"Unexpected error: {e.Exception.Message}");
        MessageBox.Show(e.Exception.ToString(), "xbox2ps: unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
