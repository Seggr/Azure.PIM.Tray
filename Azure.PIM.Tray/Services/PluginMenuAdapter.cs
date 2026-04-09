using Azure.PIM.Tray.Extensibility;
using Azure.PIM.Tray.Windows;

namespace Azure.PIM.Tray.Services;

/// <summary>
/// Bridges <see cref="IPluginMenuBuilder"/> to <see cref="TrayMenuWindow"/>
/// so plugins can add menu items without depending on WPF.
/// </summary>
internal sealed class PluginMenuAdapter : IPluginMenuBuilder
{
    private readonly TrayMenuWindow _menu;
    private readonly TrayIconManager _trayIcon;

    public PluginMenuAdapter(TrayMenuWindow menu, TrayIconManager trayIcon)
    {
        _menu = menu;
        _trayIcon = trayIcon;
    }

    public void AddItem(string text, Action? onClick = null, bool isDisabled = false,
        string? foreground = null, bool isBold = false)
    {
        _menu.AddItem(text, onClick: onClick is null ? null : () =>
        {
            _menu.CloseAll();
            onClick();
        }, isDisabled: isDisabled, foreground: foreground, isBold: isBold);
    }

    public void AddSeparator() => _menu.AddSeparator();

    public void AddSearchBox(string placeholder, Action<string> onTextChanged)
        => _menu.AddSearchBox(placeholder, onTextChanged);

    public void AddSubmenu(string text, Action<IPluginMenuBuilder> buildSubmenu)
    {
        _menu.AddItem(text, hasSubmenu: true,
            buildSubmenu: sub =>
            {
                var adapter = new PluginMenuAdapter(sub, _trayIcon);
                buildSubmenu(adapter);
            });
    }

    public void CopyAndNotify(string text, string balloonMessage)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            System.Windows.Clipboard.SetText(text);
            _trayIcon.ShowActivationBalloon(balloonMessage, System.Windows.Forms.ToolTipIcon.Info);
        });
    }

    public void CloseMenu() => _menu.CloseAll();

    public int ItemCount => _menu.Items.Children.Count;

    public void RemoveItemsAfter(int index)
    {
        var panel = _menu.Items;
        while (panel.Children.Count > index)
            panel.Children.RemoveAt(panel.Children.Count - 1);
    }
}
