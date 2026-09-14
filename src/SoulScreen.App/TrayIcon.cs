using System.IO;
using SoulScreen.Core.Logging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace SoulScreen.App;

/// <summary>
/// The notification-area icon. Lets the receiver keep running with the window out of the
/// way, and says when a phone connects while it is.
/// <para>
/// WinForms' NotifyIcon is used because it is the one Windows API for this; the menu is the
/// system's own, which is what a tray menu is expected to look like.
/// </para>
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private static readonly ILogger Log_ = Log.For("tray");

    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _showItem;
    private readonly Forms.ToolStripMenuItem _screenshotItem;
    private readonly Forms.ToolStripMenuItem _recordItem;
    private readonly Forms.ToolStripMenuItem _miniPlayerItem;
    private readonly Forms.ToolStripMenuItem _receiverItem;
    private readonly Forms.ToolStripMenuItem _disconnectItem;
    private bool _disposed;

    public event Action? ShowRequested;
    public event Action? ScreenshotRequested;
    public event Action? RecordRequested;
    public event Action? MiniPlayerRequested;
    public event Action? CapturesRequested;
    public event Action? ToggleReceiverRequested;
    public event Action? DisconnectRequested;
    public event Action? SettingsRequested;
    public event Action? QuitRequested;

    public TrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        _showItem = new Forms.ToolStripMenuItem("Show SoulScreen", null, (_, _) => ShowRequested?.Invoke())
        {
            Font = new Drawing.Font(Forms.Control.DefaultFont, Drawing.FontStyle.Bold),
        };
        // What can be done to the mirror without bringing the window back: disabled, not
        // hidden, while nothing is mirroring, so the menu keeps its shape.
        _screenshotItem = new Forms.ToolStripMenuItem("Take a screenshot", null, (_, _) => ScreenshotRequested?.Invoke()) { Enabled = false };
        _recordItem = new Forms.ToolStripMenuItem("Start recording", null, (_, _) => RecordRequested?.Invoke()) { Enabled = false };
        _miniPlayerItem = new Forms.ToolStripMenuItem("Mini player", null, (_, _) => MiniPlayerRequested?.Invoke()) { Enabled = false };
        _receiverItem = new Forms.ToolStripMenuItem("Stop receiver", null, (_, _) => ToggleReceiverRequested?.Invoke());
        _disconnectItem = new Forms.ToolStripMenuItem("Disconnect iPhone", null, (_, _) => DisconnectRequested?.Invoke())
        {
            Enabled = false,
        };
        menu.Items.Add(_showItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_screenshotItem);
        menu.Items.Add(_recordItem);
        menu.Items.Add(_miniPlayerItem);
        menu.Items.Add(new Forms.ToolStripMenuItem("Captures", null, (_, _) => CapturesRequested?.Invoke()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_receiverItem);
        menu.Items.Add(_disconnectItem);
        menu.Items.Add(new Forms.ToolStripMenuItem("Settings...", null, (_, _) => SettingsRequested?.Invoke()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Quit SoulScreen", null, (_, _) => QuitRequested?.Invoke()));

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "SoulScreen",
            ContextMenuStrip = menu,
            Visible = false,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ShowRequested?.Invoke();
        };
        _icon.BalloonTipClicked += (_, _) => ShowRequested?.Invoke();
    }

    /// <summary>Whether the icon is in the notification area at all.</summary>
    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    /// <summary>Updates the hover text and the menu to match the receiver.</summary>
    /// <param name="streaming">A mirrored picture is on screen.</param>
    public void Update(string status, bool receiverRunning, bool streaming, bool recording)
    {
        // NotifyIcon.Text is limited to 127 characters; longer throws.
        var text = $"SoulScreen - {status}";
        text = text.Length > 120 ? text[..120] : text;

        // Called twice a second; the menu is only touched when something about it changed.
        if (_icon.Text != text) _icon.Text = text;
        SetText(_receiverItem, receiverRunning ? "Stop receiver" : "Start receiver");
        SetText(_recordItem, recording ? "Stop recording" : "Start recording");
        SetEnabled(_disconnectItem, streaming);
        SetEnabled(_screenshotItem, streaming);
        SetEnabled(_recordItem, streaming);
        SetEnabled(_miniPlayerItem, streaming);
    }

    private static void SetText(Forms.ToolStripMenuItem item, string text)
    {
        if (item.Text != text) item.Text = text;
    }

    private static void SetEnabled(Forms.ToolStripMenuItem item, bool enabled)
    {
        if (item.Enabled != enabled) item.Enabled = enabled;
    }

    /// <summary>Shows a toast from the notification area.</summary>
    public void Notify(string title, string message)
    {
        if (!_icon.Visible) return;
        try
        {
            _icon.ShowBalloonTip(4000, title, message, Forms.ToolTipIcon.None);
        }
        catch (Exception ex)
        {
            Log_.Debug($"notification failed: {ex.Message}");
        }
    }

    private static Drawing.Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/SoulScreen.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new Drawing.Icon(stream, new Drawing.Size(16, 16));
            }
        }
        catch (Exception ex)
        {
            Log_.Debug($"could not load the tray icon from resources: {ex.Message}");
        }

        try
        {
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(executable) && File.Exists(executable))
                return Drawing.Icon.ExtractAssociatedIcon(executable) ?? Drawing.SystemIcons.Application;
        }
        catch (Exception) { /* fall through */ }

        return Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Hidden first: a disposed-but-visible icon lingers in the tray until the pointer
        // sweeps over it.
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
