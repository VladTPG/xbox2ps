using System.ComponentModel;
using Xbox2Ps.ViewModels;
using Forms = System.Windows.Forms;

namespace Xbox2Ps;

/// <summary>The notification-area icon, so mapping can keep running with the window closed.</summary>
internal sealed class Tray : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _toggle;
    private readonly MainViewModel _vm;

    public Tray(MainViewModel vm, Action show, Action quit)
    {
        _vm = vm;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open xbox2ps", null, (_, _) => show());
        _toggle = new Forms.ToolStripMenuItem(vm.StartStopLabel, null, (_, _) => vm.ToggleMapping());
        menu.Items.Add(_toggle);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => quit());

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = Tooltip(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };
        vm.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.StartStopLabel)) _toggle.Text = _vm.StartStopLabel;
        if (e.PropertyName == nameof(MainViewModel.StatusSummary)) _icon.Text = Tooltip();
    }

    // NotifyIcon.Text is limited to 127 characters.
    private string Tooltip()
    {
        string text = "xbox2ps: " + _vm.StatusSummary;
        return text.Length > 127 ? text[..127] : text;
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/xbox2ps.ico"));
        using var stream = info!.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void ShowHint(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _vm.PropertyChanged -= OnViewModelChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
