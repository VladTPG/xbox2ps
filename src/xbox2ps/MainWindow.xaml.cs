using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Xbox2Ps.Core;
using Xbox2Ps.ViewModels;

namespace Xbox2Ps;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly App _app;
    private bool _allowClose;

    public MainWindow(MainViewModel vm, App app)
    {
        InitializeComponent();
        _vm = vm;
        _app = app;
        DataContext = vm;

        // Keep the newest log line in view.
        vm.LogLines.CollectionChanged += (_, _) =>
        {
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        };
    }

    public void AllowClose() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose) _app.OnWindowClosing(e);
        base.OnClosing(e);
    }

    private void StartStop_Click(object sender, RoutedEventArgs e) => _vm.ToggleMapping();

    // ---- Keyboard & mouse: picking a key ----

    private void KbmKey_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is KbmRowViewModel row) _vm.StartListening(row);
    }

    private void KbmClear_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is KbmRowViewModel row) _vm.ClearKey(row);
    }

    private void Hotkey_Click(object sender, RoutedEventArgs e) => _vm.StartListeningHotkey();

    private void ResetKeys_Click(object sender, RoutedEventArgs e) => _vm.ResetKeys();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_vm.IsListening)
        {
            var key = e.Key switch
            {
                Key.System => e.SystemKey,
                Key.ImeProcessed => e.ImeProcessedKey,
                Key.DeadCharProcessed => e.DeadCharProcessedKey,
                _ => e.Key,
            };
            _vm.HandleKey(KeyInterop.VirtualKeyFromKey(key));
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (_vm.IsListening)
        {
            // A left click elsewhere just stops waiting (and clicks what it was on); on the box
            // that is waiting, it binds the left mouse button.
            if (e.ChangedButton == MouseButton.Left && !OnListeningBox(e.OriginalSource as DependencyObject))
            {
                bool onHotkey = IsWithin(e.OriginalSource as DependencyObject, HotkeyButton);
                _vm.CancelListening();
                if (onHotkey) e.Handled = true; // a second click on the hotkey box just stops waiting
                return;
            }
            int code = e.ChangedButton switch
            {
                MouseButton.Left => 0x01,
                MouseButton.Right => 0x02,
                MouseButton.Middle => 0x04,
                MouseButton.XButton1 => 0x05,
                MouseButton.XButton2 => 0x06,
                _ => -1,
            };
            _vm.HandleKey(code);
            e.Handled = true;
            return;
        }
        base.OnPreviewMouseDown(e);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (_vm.IsListening && e.Delta != 0)
        {
            _vm.HandleKey(e.Delta > 0 ? KeyNames.WheelUp : KeyNames.WheelDown);
            e.Handled = true;
            return;
        }
        base.OnPreviewMouseWheel(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        _vm.CancelListening();
        base.OnDeactivated(e);
    }

    /// <summary>Whether the click landed on the key box of the row that's waiting for a key.</summary>
    private bool OnListeningBox(DependencyObject? hit)
    {
        var row = _vm.ListeningRow;
        if (row is null) return false;
        for (var d = hit; d is not null; d = ParentOf(d))
            if (d is Button { Tag: "KbmKey" } b && ReferenceEquals(b.DataContext, row)) return true;
        return false;
    }

    private static bool IsWithin(DependencyObject? hit, DependencyObject target)
    {
        for (var d = hit; d is not null; d = ParentOf(d))
            if (ReferenceEquals(d, target)) return true;
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private void Retry_Click(object sender, RoutedEventArgs e) => _vm.Retry();

    private void OpenViiper_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(Viiper.ReleasesUrl);

    private void Donate_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(AppInfo.DonateUrl);

    private void ProjectPage_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(AppInfo.ProjectUrl);

    private void AddCombo_Click(object sender, RoutedEventArgs e) => _vm.AddCombo();

    private void RemoveCombo_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ComboViewModel combo) _vm.RemoveCombo(combo);
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_vm.LogText); }
        catch (Exception) { /* clipboard busy */ }
    }

    // ---- Profiles ----

    private void AskName(string title, string initial, string ok, Func<string, string?> apply) =>
        new NameDialog(this, title, initial, ok, apply).ShowDialog();

    private void NewProfile_Click(object sender, RoutedEventArgs e) =>
        AskName("New profile", "", "Create", _vm.NewProfile);

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e) =>
        AskName("Duplicate profile", ProfileStore.UniqueName(_vm.CurrentProfileName + " copy"), "Duplicate", _vm.DuplicateProfile);

    private void RenameProfile_Click(object sender, RoutedEventArgs e) =>
        AskName("Rename profile", _vm.CurrentProfileName, "Rename", _vm.RenameProfile);

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, $"Delete the profile \"{_vm.CurrentProfileName}\"? This can't be undone.",
            "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        if (_vm.DeleteProfile() is string error) Problem(error);
    }

    private void ResetProfile_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, $"Reset every setting in \"{_vm.CurrentProfileName}\" to the defaults?",
            "Reset profile", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) _vm.ResetProfile();
    }

    private const string ProfileFilter = "xbox2ps profile (*.json)|*.json|All files (*.*)|*.*";

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import profile", Filter = ProfileFilter };
        if (dialog.ShowDialog(this) != true) return;
        if (_vm.ImportProfile(dialog.FileName) is string error) Problem(error);
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export profile",
            Filter = ProfileFilter,
            FileName = _vm.CurrentProfileName + ".json",
        };
        if (dialog.ShowDialog(this) != true) return;
        if (_vm.ExportProfile(dialog.FileName) is string error) Problem(error);
    }

    private void Problem(string message) =>
        MessageBox.Show(this, message, "xbox2ps", MessageBoxButton.OK, MessageBoxImage.Warning);
}
