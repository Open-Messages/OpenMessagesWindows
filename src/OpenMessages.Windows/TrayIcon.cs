using System.Drawing;
using System.Windows.Forms;
using OpenMessages.Core;

namespace OpenMessages.Windows;

/// <summary>Notification-area icon: recent codes to copy, open, quit, and new-code toasts.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly MainViewModel _model;
    private readonly Action _showWindow;
    private readonly Action _quit;

    public TrayIcon(MainViewModel model, Action showWindow, Action quit)
    {
        _model = model;
        _showWindow = showWindow;
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "OpenMessages",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) showWindow(); };
        _icon.BalloonTipClicked += (_, _) => showWindow();
        _quit = quit;
        model.CodesChanged += Rebuild;
        // Rebuild on open too, so the "min ago" labels are current.
        _icon.ContextMenuStrip.Opening += (_, _) => Rebuild();
        model.Announce += (item, copied) => _icon.ShowBalloonTip(
            5000,
            $"OTP received from {item.Sender}",
            copied ? $"{item.Code} copied to clipboard" : $"{item.Code}\n{item.Body}",
            ToolTipIcon.None);
        Rebuild();
    }

    private void Rebuild()
    {
        var menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();
        if (_model.Phase == SyncPhase.Ready && _model.Codes.Count > 0)
        {
            menu.Items.Add(new ToolStripLabel("Click a code to copy it") { ForeColor = SystemColors.GrayText });
            foreach (var item in _model.Codes.Take(5))
            {
                var entry = new ToolStripMenuItem($"{item.Code}    {item.Sender} · {Relative(item.ReceivedAt)}");
                entry.Click += (_, _) => _model.Copy(item);
                menu.Items.Add(entry);
            }
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add("Open OpenMessages", null, (_, _) => _showWindow());
        menu.Items.Add("Quit", null, (_, _) => _quit());
    }

    private static string Relative(DateTimeOffset time)
    {
        var age = DateTimeOffset.Now - time;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : $"{(int)age.TotalHours} h ago";
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
