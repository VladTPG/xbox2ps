using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
