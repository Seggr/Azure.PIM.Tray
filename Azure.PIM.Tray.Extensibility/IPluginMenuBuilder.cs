namespace Azure.PIM.Tray.Extensibility;

/// <summary>
/// Abstraction over the tray menu that plugins use to add items.
/// The main app provides the concrete implementation.
/// </summary>
public interface IPluginMenuBuilder
{
    /// <summary>Add a clickable menu item.</summary>
    void AddItem(string text, Action? onClick = null, bool isDisabled = false,
        string? foreground = null, bool isBold = false);

    /// <summary>Add a horizontal separator line.</summary>
    void AddSeparator();

    /// <summary>
    /// Add a search box pinned to the top of a submenu.
    /// <paramref name="onTextChanged"/> fires on each keystroke with the current text.
    /// </summary>
    void AddSearchBox(string placeholder, Action<string> onTextChanged);

    /// <summary>Add a submenu item that expands into a child menu.</summary>
    void AddSubmenu(string text, Action<IPluginMenuBuilder> buildSubmenu);

    /// <summary>Copy text to the clipboard and show a tray balloon notification.</summary>
    void CopyAndNotify(string text, string balloonMessage);

    /// <summary>
    /// Removes all items added after the given index (0-based).
    /// Used to rebuild a filtered list below a pinned search box.
    /// </summary>
    void RemoveItemsAfter(int index);

    /// <summary>Returns the current number of items in the menu.</summary>
    int ItemCount { get; }

    /// <summary>Close the entire menu chain.</summary>
    void CloseMenu();
}
