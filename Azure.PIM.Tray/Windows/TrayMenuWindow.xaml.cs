using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Azure.PIM.Tray.Windows;

public partial class TrayMenuWindow : Window
{
    private TrayMenuWindow? _submenu;
    private TrayMenuWindow? _parentMenu;
    private Border? _activeSubmenuItem;
    private DispatcherTimer? _scrollTimer;
    private DispatcherTimer? _submenuDelay;
    private static readonly TimeSpan SubmenuOpenDelay = TimeSpan.FromMilliseconds(100);

    // Submenu pool to avoid recreating WPF windows
    private static readonly List<TrayMenuWindow> _pool = [];

    internal static TrayMenuWindow GetOrCreate()
    {
        for (int i = 0; i < _pool.Count; i++)
        {
            var w = _pool[i];
            if (!w.IsVisible)
            {
                w.Reset();
                return w;
            }
        }
        var fresh = new TrayMenuWindow();
        _pool.Add(fresh);
        return fresh;
    }

    /// <summary>Clears all items and resets state for reuse.</summary>
    public void Reset()
    {
        CancelSubmenuDelay();
        StopAutoScroll();
        HideSubmenu();
        MenuPanel.Children.Clear();
        MenuScroll.ScrollToVerticalOffset(0);
        MenuScroll.MaxHeight = double.PositiveInfinity;
        _activeSubmenuItem = null;
        _parentMenu = null;
    }

    public TrayMenuWindow()
    {
        InitializeComponent();
        InitScrollArrows();
    }

    private void InitScrollArrows()
    {
        MenuScroll.ScrollChanged += (_, _) => UpdateScrollArrows();

        ScrollUpArrow.MouseEnter += (_, _) => StartAutoScroll(-40);
        ScrollUpArrow.MouseLeave += (_, _) => StopAutoScroll();
        ScrollDownArrow.MouseEnter += (_, _) => StartAutoScroll(40);
        ScrollDownArrow.MouseLeave += (_, _) => StopAutoScroll();
    }

    private void UpdateScrollArrows()
    {
        ScrollUpArrow.Visibility = MenuScroll.VerticalOffset > 0
            ? Visibility.Visible : Visibility.Collapsed;
        ScrollDownArrow.Visibility =
            MenuScroll.VerticalOffset < MenuScroll.ScrollableHeight - 1
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartAutoScroll(double delta)
    {
        StopAutoScroll();
        _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _scrollTimer.Tick += (_, _) => MenuScroll.ScrollToVerticalOffset(MenuScroll.VerticalOffset + delta);
        _scrollTimer.Start();
        // Scroll immediately on enter
        MenuScroll.ScrollToVerticalOffset(MenuScroll.VerticalOffset + delta);
    }

    private void StopAutoScroll()
    {
        _scrollTimer?.Stop();
        _scrollTimer = null;
    }

    public StackPanel Items => MenuPanel;

    /// <summary>
    /// Adds a search text box pinned at the current position in the menu.
    /// <paramref name="onTextChanged"/> fires on each keystroke with the current text.
    /// </summary>
    public void AddSearchBox(string placeholder, Action<string> onTextChanged)
    {
        var tb = new System.Windows.Controls.TextBox
        {
            Margin = new Thickness(8, 4, 8, 4),
            Padding = new Thickness(4, 3, 4, 3),
            FontSize = 12,
            FontFamily = new FontFamily("Segoe UI"),
        };

        // Placeholder text via a visual hint
        var placeholderBlock = new TextBlock
        {
            Text = placeholder,
            Foreground = Brushes.Gray,
            FontSize = 12,
            FontFamily = new FontFamily("Segoe UI"),
            IsHitTestVisible = false,
            Margin = new Thickness(13, 7, 0, 0),
            Visibility = Visibility.Visible
        };

        var container = new System.Windows.Controls.Grid();
        container.Children.Add(tb);
        container.Children.Add(placeholderBlock);

        tb.TextChanged += (_, _) =>
        {
            placeholderBlock.Visibility = string.IsNullOrEmpty(tb.Text)
                ? Visibility.Visible : Visibility.Collapsed;
            onTextChanged(tb.Text);
        };

        tb.GotFocus += (_, _) =>
        {
            if (string.IsNullOrEmpty(tb.Text))
                placeholderBlock.Visibility = Visibility.Collapsed;
        };

        tb.LostFocus += (_, _) =>
        {
            if (string.IsNullOrEmpty(tb.Text))
                placeholderBlock.Visibility = Visibility.Visible;
        };

        MenuPanel.Children.Add(container);

        // Focus the search box once the window is visible
        Dispatcher.InvokeAsync(() => tb.Focus(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    public void AddItem(string text, Action? onClick = null, bool isHeader = false,
        bool isDisabled = false, string? foreground = null, bool isBold = false,
        bool hasSubmenu = false, Action<TrayMenuWindow>? buildSubmenu = null)
    {
        var item = new Border
        {
            Padding = new Thickness(12, 6, 24, 6),
            Background = Brushes.Transparent,
            Cursor = isDisabled ? Cursors.Arrow : Cursors.Hand
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = isBold || isHeader ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = foreground is not null
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(foreground))
                : isDisabled ? Brushes.Gray : Brushes.Black,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(label);

        if (hasSubmenu)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "  \u25b6",
                FontSize = 10,
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
        }

        item.Child = panel;

        if (!isDisabled)
        {
            item.MouseEnter += (_, _) =>
            {
                item.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFF));
                CancelSubmenuDelay();

                if (hasSubmenu && buildSubmenu is not null)
                {
                    // Delay opening so diagonal mouse movement toward an
                    // already-open submenu doesn't accidentally swap it.
                    _submenuDelay = new DispatcherTimer { Interval = SubmenuOpenDelay };
                    _submenuDelay.Tick += (_, _) =>
                    {
                        _submenuDelay?.Stop();
                        OpenSubmenuFor(item, buildSubmenu);
                    };
                    _submenuDelay.Start();
                }
                else
                {
                    HideSubmenu();
                }
            };

            item.MouseLeave += (_, _) =>
            {
                item.Background = Brushes.Transparent;
                CancelSubmenuDelay();
            };

            if (!hasSubmenu && onClick is not null)
            {
                item.MouseLeftButtonUp += (_, _) =>
                {
                    CloseAll();
                    onClick();
                };
            }
            else if (hasSubmenu && onClick is null && buildSubmenu is not null)
            {
                // Submenu items: click also opens (for accessibility)
                item.MouseLeftButtonUp += (_, _) => OpenSubmenuFor(item, buildSubmenu);
            }
        }

        MenuPanel.Children.Add(item);
    }

    private void OpenSubmenuFor(Border item, Action<TrayMenuWindow> buildSubmenu)
    {
        // Already showing for this item
        if (_activeSubmenuItem == item && _submenu is { IsVisible: true })
            return;

        HideSubmenu();

        var sub = GetOrCreate();
        sub._parentMenu = this;
        buildSubmenu(sub);
        _submenu = sub;
        _activeSubmenuItem = item;

        sub.WindowStartupLocation = WindowStartupLocation.Manual;

        // Keep submenu on screen
        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)Left, (int)Top));
        var dpi = VisualTreeHelper.GetDpi(this);
        var workRight  = screen.WorkingArea.Right  / dpi.DpiScaleX;
        var workBottom = screen.WorkingArea.Bottom / dpi.DpiScaleY;
        var workLeft   = screen.WorkingArea.Left   / dpi.DpiScaleX;
        var workTop    = screen.WorkingArea.Top    / dpi.DpiScaleY;
        var workHeight = workBottom - workTop;

        // Cap submenu height at 90% of the working area
        sub.MenuScroll.MaxHeight = workHeight * 0.9 - 20;

        // Measure without showing
        sub.MenuPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var subDesired = sub.MenuPanel.DesiredSize;
        var subW = subDesired.Width + 20;
        var subH = Math.Min(subDesired.Height + 20, workHeight * 0.9);

        var targetLeft = Left + ActualWidth - 10; // overlap shadow region
        var targetTop  = Top + item.TranslatePoint(new Point(0, 0), this).Y;

        if (targetLeft + subW > workRight)
            targetLeft = Left - subW + 10; // overlap shadow region
        if (targetTop + subH > workBottom)
            targetTop = workBottom - subH;
        targetLeft = Math.Max(workLeft, targetLeft);
        targetTop  = Math.Max(workTop, targetTop);

        sub.Left = targetLeft;
        sub.Top = targetTop;
        sub.Show();
    }

    public void AddSeparator()
    {
        MenuPanel.Children.Add(new Separator
        {
            Margin = new Thickness(8, 2, 8, 2),
            Background = Brushes.LightGray
        });
    }

    public void PositionNearTray()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;

        // Measure without showing
        MenuPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = MenuPanel.DesiredSize;
        // Add padding for border + shadow
        var estWidth  = desired.Width + 20;
        var estHeight = desired.Height + 20;

        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var dpi = VisualTreeHelper.GetDpi(this);

        var workLeft   = screen.WorkingArea.Left   / dpi.DpiScaleX;
        var workTop    = screen.WorkingArea.Top    / dpi.DpiScaleY;
        var workRight  = screen.WorkingArea.Right  / dpi.DpiScaleX;
        var workBottom = screen.WorkingArea.Bottom / dpi.DpiScaleY;
        var workHeight = workBottom - workTop;
        var cursorX    = cursor.X / dpi.DpiScaleX;

        // Cap menu height at 90% of the working area (border+shadow padding excluded)
        MenuScroll.MaxHeight = workHeight * 0.9 - 20;

        // Re-measure with the constrained height
        MenuPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        desired = MenuPanel.DesiredSize;
        estWidth  = desired.Width + 20;
        estHeight = Math.Min(desired.Height + 20, workHeight * 0.9);

        Left = Math.Min(cursorX, workRight - estWidth);
        Top  = workBottom - estHeight;
        Left = Math.Max(workLeft, Left);
        Top  = Math.Max(workTop, Top);

        Show();
        Activate();
    }

    private void CancelSubmenuDelay()
    {
        _submenuDelay?.Stop();
        _submenuDelay = null;
    }

    private void HideSubmenu()
    {
        if (_submenu is not null)
        {
            _submenu.HideSubmenu();
            _submenu.Hide();
            _submenu = null;
        }
        _activeSubmenuItem = null;
    }

    public void CloseAll()
    {
        HideSubmenu();
        Hide();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // Check after focus has settled — submenu may be receiving focus
        Dispatcher.InvokeAsync(() =>
        {
            if (_submenu is { IsActive: true } or { IsMouseOver: true }) return;
            if (IsActive) return; // Re-activated before dispatch ran
            if (_parentMenu is { IsActive: true } or { IsMouseOver: true }) return;

            // Walk up to the root menu and close the entire chain
            var root = this;
            while (root._parentMenu is not null)
                root = root._parentMenu;
            root.CloseAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }
}
