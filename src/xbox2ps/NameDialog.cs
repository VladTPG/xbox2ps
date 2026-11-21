using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Xbox2Ps;

/// <summary>Asks for a profile name. <c>apply</c> does the work and returns an error to show, or null to close.</summary>
internal sealed class NameDialog : Window
{
    private readonly TextBox _box;
    private readonly TextBlock _error;

    public NameDialog(Window owner, string title, string initial, string okLabel, Func<string, string?> apply)
    {
        Owner = owner;
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Profile name", Margin = new Thickness(0, 0, 0, 6) });
        _box = new TextBox { Text = initial };
        panel.Children.Add(_box);
        _error = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = okLabel, IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) =>
        {
            string? problem;
            try { problem = apply(_box.Text); }
            catch (Exception ex) { problem = ex.Message; }
            if (problem is null) { DialogResult = true; return; }
            _error.Text = problem;
            _error.Visibility = Visibility.Visible;
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }
}
