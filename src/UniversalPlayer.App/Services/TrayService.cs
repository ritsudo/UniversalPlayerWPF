using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace UniversalPlayer.App.Services;

/// <summary>Windows tray icon: the player keeps playing when minimised here.</summary>
public sealed class TrayService : IDisposable
{
    private readonly TaskbarIcon _icon = new();

    public event EventHandler? ShowRequested;

    public TrayService()
    {
        _icon.ToolTipText = "Universal Player";
        _icon.TrayLeftMouseUp += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        _icon.TrayMouseDoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Initialize(Icon icon, ContextMenu menu, object dataContext)
    {
        _icon.Icon = icon;
        menu.DataContext = dataContext;
        _icon.ContextMenu = menu;
        _icon.Visibility = Visibility.Visible;
    }

    public void SetTooltip(string text) => _icon.ToolTipText = text;

    public void ShowBalloon(string title, string message)
    {
        try { _icon.ShowBalloonTip(title, message, BalloonIcon.Info); } catch { /* ignore */ }
    }

    public void Dispose() => _icon.Dispose();
}
