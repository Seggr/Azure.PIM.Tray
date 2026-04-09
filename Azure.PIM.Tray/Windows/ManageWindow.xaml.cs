using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Azure.PIM.Tray.Models;
using Azure.PIM.Tray.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Azure.PIM.Tray.Windows;

[ValueConversion(typeof(string), typeof(string))]
public class NullToBoolTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is string s && !string.IsNullOrEmpty(s) ? "Yes" : "No";
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}

[ValueConversion(typeof(string), typeof(Brush))]
public class NullToColorConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is string s && !string.IsNullOrEmpty(s)
            ? new SolidColorBrush(Color.FromRgb(0, 128, 0))
            : new SolidColorBrush(Color.FromRgb(180, 0, 0));
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}

// -- Tree node models --

public sealed class TenantTreeNode : INotifyPropertyChanged
{
    private string _permissionsStatus = "Checking...";
    private Brush _permissionsColor = Brushes.Gray;
    private bool _subsLoaded;

    public TrayConnection Connection { get; set; }
    public string DisplayName => Connection.TenantDisplayName ?? Connection.TenantId;
    public string TenantId    => Connection.TenantId;
    public string Email       => Connection.Email;
    public string? ClientId   => Connection.ClientId;

    public string PermissionsStatus
    {
        get => _permissionsStatus;
        set { _permissionsStatus = value; PropertyChanged?.Invoke(this, new(nameof(PermissionsStatus))); }
    }
    public Brush PermissionsColor
    {
        get => _permissionsColor;
        set { _permissionsColor = value; PropertyChanged?.Invoke(this, new(nameof(PermissionsColor))); }
    }

    public ObservableCollection<object> Children { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public TenantTreeNode(TrayConnection conn)
    {
        Connection = conn;
        if (string.IsNullOrWhiteSpace(conn.ClientId))
        {
            _permissionsStatus = "No app registered";
            _permissionsColor  = Brushes.DarkRed;
        }
        // Add a placeholder so the expand arrow shows
        Children.Add(new LoadingTreeNode("Loading subscriptions..."));
    }

    public async Task LoadSubscriptionsAsync()
    {
        if (_subsLoaded) return;
        _subsLoaded = true;
        Children.Clear();

        if (string.IsNullOrWhiteSpace(ClientId))
        {
            Children.Add(new LoadingTreeNode("No app registered — connect first"));
            return;
        }

        try
        {
            var subs = await ConnectionService.ListSubscriptionsAsync(Connection);
            var excluded = Connection.ExcludedSubscriptions
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var s in subs)
                Children.Add(new SubscriptionTreeNode(s.Id, s.DisplayName, !excluded.Contains(s.Id)));

            if (Children.Count == 0)
                Children.Add(new LoadingTreeNode("No subscriptions found"));
        }
        catch (Exception ex)
        {
            Children.Add(new LoadingTreeNode($"Failed: {ex.Message}"));
        }
    }
}

public sealed class SubscriptionTreeNode : INotifyPropertyChanged
{
    private bool _isIncluded;

    public string Id          { get; }
    public string DisplayName { get; }
    public string IdDisplay   => $"({Id})";

    public bool IsIncluded
    {
        get => _isIncluded;
        set { _isIncluded = value; PropertyChanged?.Invoke(this, new(nameof(IsIncluded))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SubscriptionTreeNode(string id, string displayName, bool isIncluded)
    {
        Id          = id;
        DisplayName = displayName;
        _isIncluded = isIncluded;
    }
}

public sealed class LoadingTreeNode
{
    public string Text { get; }
    public LoadingTreeNode(string text) => Text = text;
}

// -- Window --

public partial class ManageWindow : Window
{
    private TrayAppConfig _config;
    private readonly ObservableCollection<TenantTreeNode> _tenantNodes;
    private readonly UpdateService? _updateService;
    private readonly ExtensionFeedService? _feedService;
    private readonly PluginLoader? _pluginLoader;

    public event EventHandler? ConfigChanged;

    public ManageWindow(TrayAppConfig config, UpdateService? updateService = null,
        ExtensionFeedService? feedService = null, PluginLoader? pluginLoader = null)
    {
        InitializeComponent();
        WindowIconHelper.ApplyManageIcon(this);
        WindowIconHelper.CenterOnActiveScreen(this);
        _config = config;
        _updateService = updateService;
        _feedService = feedService;
        _pluginLoader = pluginLoader;
        _tenantNodes = new ObservableCollection<TenantTreeNode>(
            config.Connections.Select(c => new TenantTreeNode(c)));
        TenantTree.ItemsSource = _tenantNodes;

        if (updateService is { UpdateAvailable: true })
        {
            TxtVersion.Text = $"v{updateService.AvailableVersion} available";
            BtnUpdateRestart.Visibility = Visibility.Visible;
        }
        else
        {
            TxtVersion.Text = $"v{updateService?.CurrentVersion ?? "dev"}";
        }

        BuildInstalledExtensionsList();
        _ = CheckAllPermissionsAsync();
    }

    private void BuildInstalledExtensionsList()
    {
        InstalledExtensionsList.Items.Clear();

        if (_pluginLoader is null || _pluginLoader.Plugins.Count == 0)
        {
            TxtNoInstalled.Visibility = Visibility.Visible;
            return;
        }

        TxtNoInstalled.Visibility = Visibility.Collapsed;

        foreach (var plugin in _pluginLoader.Plugins)
        {
            var pluginId = plugin.Id;
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

            var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            header.Children.Add(new TextBlock
            {
                Text = plugin.Name,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            if (plugin.RequiredRoles.Count > 0)
            {
                header.Children.Add(new TextBlock
                {
                    Text = $"  (requires: {string.Join(", ", plugin.RequiredRoles)})",
                    Foreground = Brushes.Gray,
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            panel.Children.Add(header);

            // Per-tenant enable/disable toggles
            foreach (var node in _tenantNodes)
            {
                var tenantId = node.TenantId;
                var isDisabled = node.Connection.DisabledExtensions
                    .Contains(pluginId, StringComparer.OrdinalIgnoreCase);

                var cb = new System.Windows.Controls.CheckBox
                {
                    Content = node.DisplayName,
                    IsChecked = !isDisabled,
                    Margin = new Thickness(12, 2, 0, 0),
                    FontSize = 11
                };

                var capturedNode = node;
                var capturedPluginId = pluginId;
                cb.Checked += (_, _) => ToggleExtension(capturedNode, capturedPluginId, enabled: true);
                cb.Unchecked += (_, _) => ToggleExtension(capturedNode, capturedPluginId, enabled: false);

                panel.Children.Add(cb);
            }

            InstalledExtensionsList.Items.Add(panel);
        }
    }

    private void ToggleExtension(TenantTreeNode node, string pluginId, bool enabled)
    {
        var disabled = node.Connection.DisabledExtensions.ToList();
        if (enabled)
            disabled.RemoveAll(id => string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase));
        else if (!disabled.Contains(pluginId, StringComparer.OrdinalIgnoreCase))
            disabled.Add(pluginId);

        node.Connection = node.Connection with { DisabledExtensions = disabled };
        SaveConfigOnly();
        AppLog.Info("Extensions",
            $"{pluginId} {(enabled ? "enabled" : "disabled")} for {node.DisplayName}");
    }

    private async Task CheckAllPermissionsAsync()
    {
        foreach (var node in _tenantNodes.ToList())
        {
            if (string.IsNullOrWhiteSpace(node.ClientId)) continue;
            await CheckPermissionsForNodeAsync(node);
        }
    }

    private async Task CheckPermissionsForNodeAsync(TenantTreeNode node)
    {
        if (string.IsNullOrWhiteSpace(node.ClientId)) return;
        try
        {
            var (pluginScopes, pluginScopeIds) = GetEnabledPluginScopes(node.Connection);
            var status = await ConnectionService.CheckPermissionsAsync(
                node.Connection, pluginScopes, pluginScopeIds);
            await Dispatcher.InvokeAsync(() =>
            {
                node.PermissionsStatus = status;
                node.PermissionsColor  = status.StartsWith("\u2713")
                    ? new SolidColorBrush(Color.FromRgb(0, 128, 0))
                    : new SolidColorBrush(Color.FromRgb(180, 0, 0));
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                node.PermissionsStatus = $"Check failed: {ex.Message}";
                node.PermissionsColor  = Brushes.DarkRed;
            });
        }
    }

    private void TenantTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // Selection also triggers load in case user clicks directly
        if (e.NewValue is TenantTreeNode node)
            _ = node.LoadSubscriptionsAsync();
    }

    private void TenantTree_ItemExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: TenantTreeNode node })
            _ = node.LoadSubscriptionsAsync();
    }

    private TenantTreeNode? GetSelectedTenant()
    {
        var sel = TenantTree.SelectedItem;
        if (sel is TenantTreeNode t) return t;
        // If a subscription is selected, find its parent tenant
        foreach (var node in _tenantNodes)
            if (node.Children.Contains(sel))
                return node;
        return null;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var tenantId = TxtTenantId.Text.Trim();
        var email    = TxtEmail.Text.Trim();

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(email))
        {
            ShowAddStatus("Please enter both Tenant ID and Email.", isError: true);
            return;
        }

        BtnConnect.IsEnabled     = false;
        TxtConnecting.Visibility = Visibility.Visible;
        TxtAddStatus.Visibility  = Visibility.Collapsed;

        try
        {
            var conn = await ConnectionService.DiscoverAsync(tenantId, email);

            var existing = _tenantNodes.FirstOrDefault(n =>
                string.Equals(n.TenantId, conn.TenantId, StringComparison.OrdinalIgnoreCase));

            TenantTreeNode newNode;
            if (existing is not null)
            {
                newNode = new TenantTreeNode(conn);
                _tenantNodes[_tenantNodes.IndexOf(existing)] = newNode;
            }
            else
            {
                newNode = new TenantTreeNode(conn);
                _tenantNodes.Add(newNode);
            }

            SaveAndNotify();
            ShowAddStatus(
                $"Connected: {conn.TenantDisplayName ?? conn.TenantId} (app: {conn.ClientId})",
                isError: false);

            TxtTenantId.Clear();
            TxtEmail.Clear();
            ExpanderAdd.IsExpanded = false;

            _ = CheckPermissionsForNodeAsync(newNode);
        }
        catch (Exception ex)
        {
            ShowAddStatus(ex.Message, isError: true);
        }
        finally
        {
            BtnConnect.IsEnabled     = true;
            TxtConnecting.Visibility = Visibility.Collapsed;
        }
    }

    private async void FixPermissions_Click(object sender, RoutedEventArgs e)
    {
        var sel = GetSelectedTenant();
        if (sel is null)
        {
            System.Windows.MessageBox.Show(
                "Select a tenant first.", "Fix Permissions",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(sel.ClientId))
        {
            ShowAddStatus("No app registered for this tenant \u2014 run Connect & Discover first.", isError: true);
            return;
        }

        BtnFixPermissions.IsEnabled = false;
        sel.PermissionsStatus = "Applying fixes\u2026";
        sel.PermissionsColor  = Brushes.Gray;

        try
        {
            var (enabledPluginScopes, pluginScopeIds) = GetEnabledPluginScopes(sel.Connection);
            var result = await ConnectionService.FixPermissionsAsync(
                sel.Connection, enabledPluginScopes, pluginScopeIds);
            ShowAddStatus(result, isError: !result.StartsWith("\u2713"));
            _ = CheckPermissionsForNodeAsync(sel);
        }
        catch (Exception ex)
        {
            ShowAddStatus($"Fix failed: {ex.Message}", isError: true);
            sel.PermissionsStatus = "Fix failed";
            sel.PermissionsColor  = Brushes.DarkRed;
        }
        finally
        {
            BtnFixPermissions.IsEnabled = true;
        }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        var sel = GetSelectedTenant();
        if (sel is null) return;
        if (string.IsNullOrWhiteSpace(sel.ClientId))
        {
            ShowAddStatus("No app registered for this tenant \u2014 run the provisioner first.", isError: true);
            return;
        }

        BtnSignIn.IsEnabled    = false;
        sel.PermissionsStatus  = "Signing in\u2026";
        sel.PermissionsColor   = Brushes.Gray;

        try
        {
            await ConnectionService.SignInAsync(sel.Connection);
            ShowAddStatus($"Signed in successfully for {sel.DisplayName}.", isError: false);
            _ = CheckPermissionsForNodeAsync(sel);
        }
        catch (Exception ex)
        {
            ShowAddStatus($"Sign in failed: {ex.Message}", isError: true);
            sel.PermissionsStatus = "Sign in failed";
            sel.PermissionsColor  = Brushes.DarkRed;
        }
        finally
        {
            BtnSignIn.IsEnabled = true;
        }
    }

    private async void ReloadToken_Click(object sender, RoutedEventArgs e)
    {
        var sel = GetSelectedTenant();
        if (sel is null)
        {
            ShowAddStatus("Select a tenant first.", isError: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(sel.ClientId))
        {
            ShowAddStatus("No app registered for this tenant.", isError: true);
            return;
        }

        BtnSignIn.IsEnabled    = false;
        sel.PermissionsStatus  = "Reloading tokens\u2026";
        sel.PermissionsColor   = Brushes.Gray;

        try
        {
            await ConnectionService.ReloadTokensAsync(sel.Connection);
            ShowAddStatus($"Tokens reloaded for {sel.DisplayName}. Restart for plugins to pick up new permissions.", isError: false);
            _ = CheckPermissionsForNodeAsync(sel);
        }
        catch (Exception ex)
        {
            ShowAddStatus($"Token reload failed: {ex.Message}", isError: true);
            sel.PermissionsStatus = "Token reload failed";
            sel.PermissionsColor  = Brushes.DarkRed;
        }
        finally
        {
            BtnSignIn.IsEnabled = true;
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var sel = GetSelectedTenant();
        if (sel is null) return;

        var result = System.Windows.MessageBox.Show(
            $"Remove connection for \"{sel.DisplayName}\"?",
            "Confirm removal", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _tenantNodes.Remove(sel);
        SaveAndNotify();
        ShowAddStatus($"Removed tenant: {sel.DisplayName}", isError: false);
    }

    private void Subscription_Toggled(object sender, RoutedEventArgs e)
    {
        // Find which tenant this subscription belongs to
        if (sender is not System.Windows.Controls.CheckBox { DataContext: SubscriptionTreeNode subNode }) return;

        var tenantNode = _tenantNodes.FirstOrDefault(t =>
            t.Children.OfType<SubscriptionTreeNode>().Contains(subNode));
        if (tenantNode is null) return;

        var excluded = tenantNode.Children
            .OfType<SubscriptionTreeNode>()
            .Where(s => !s.IsIncluded)
            .Select(s => s.Id)
            .ToList();

        tenantNode.Connection = tenantNode.Connection with { ExcludedSubscriptions = excluded };

        // Only save config — don't rebuild services (avoids disposing in-flight HTTP requests).
        // Changes take effect on next app restart or when user clicks Close.
        SaveConfigOnly();

        var action = subNode.IsIncluded ? "Included" : "Excluded";
        ShowAddStatus($"{action}: {subNode.DisplayName}", isError: false);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // Notify app to rebuild services with updated config on close
        ConfigChanged?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void SaveAndNotify()
    {
        SaveConfigOnly();
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SaveConfigOnly()
    {
        _config = _config with { Connections = [.. _tenantNodes.Select(n => n.Connection)] };
        ConnectionService.SaveConfig(_config);
    }

    private (List<string> Scopes, Dictionary<string, string> ScopeIds) GetEnabledPluginScopes(
        TrayConnection connection)
    {
        var enabledPlugins = _pluginLoader?.Plugins
            .Where(p => !connection.DisabledExtensions.Contains(p.Id, StringComparer.OrdinalIgnoreCase))
            .ToList() ?? [];
        var scopes = enabledPlugins
            .SelectMany(p => p.RequiredGraphPermissions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var scopeIds = enabledPlugins
            .SelectMany(p => p.RequiredGraphPermissionIds)
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        return (scopes, scopeIds);
    }

    private async void Version_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_updateService is null) return;

        TxtVersion.Text = "Checking for updates\u2026";
        BtnUpdateRestart.Visibility = Visibility.Collapsed;
        await _updateService.CheckForUpdatesAsync();

        if (_updateService.UpdateAvailable)
        {
            TxtVersion.Text = $"v{_updateService.AvailableVersion} available";
            BtnUpdateRestart.Visibility = Visibility.Visible;
        }
        else if (_updateService.LastCheckFailed)
        {
            TxtVersion.Text = $"v{_updateService.CurrentVersion} (check failed)";
        }
        else
        {
            TxtVersion.Text = $"v{_updateService.CurrentVersion} (up to date)";
        }
    }

    private void UpdateRestart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Close();
            _updateService?.ApplyUpdateAndRestart();
        }
        catch (Exception ex)
        {
            AppLog.Error("Update", $"Failed to apply update: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Extensions
    // ------------------------------------------------------------------

    private async void RefreshExtensions_Click(object sender, RoutedEventArgs e)
    {
        if (_feedService is null) return;

        TxtExtensionsStatus.Text = "Loading...";
        ExtensionsList.Items.Clear();

        var available = await _feedService.FetchAvailableAsync(
            _config.ExtensionFeeds?.Count > 0 ? _config.ExtensionFeeds : null);
        var installed = ExtensionFeedService.GetInstalledPlugins()
            .Select(f => System.IO.Path.GetFileNameWithoutExtension(f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (available.Count == 0)
        {
            TxtExtensionsStatus.Text = "No extensions available.";
            return;
        }

        TxtExtensionsStatus.Text = $"{available.Count} extension(s) found.";

        foreach (var ext in available)
        {
            var isInstalled = installed.Contains(ext.Id);
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

            var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            header.Children.Add(new TextBlock
            {
                Text = $"{ext.Name}  v{ext.Version}",
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            if (isInstalled)
            {
                header.Children.Add(new TextBlock
                {
                    Text = "  \u2713 Installed",
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 128, 0)),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11
                });
            }
            panel.Children.Add(header);

            panel.Children.Add(new TextBlock
            {
                Text = ext.Description,
                Foreground = Brushes.Gray,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });

            // Show required permissions from the loaded plugin (if installed)
            var loadedPlugin = _pluginLoader?.Plugins
                .FirstOrDefault(p => p.Id == ext.Id);
            var perms = loadedPlugin?.RequiredGraphPermissions;
            if (perms is { Count: > 0 })
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"Requires: {string.Join(", ", perms)}",
                    Foreground = Brushes.DarkOrange,
                    FontSize = 10,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            var btnPanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 4, 0, 0)
            };

            var btn = new System.Windows.Controls.Button
            {
                Content = isInstalled ? "Remove" : "Install",
                Padding = new Thickness(10, 3, 10, 3),
                Cursor = System.Windows.Input.Cursors.Hand,
            };

            var capturedExt = ext;
            var capturedInstalled = isInstalled;
            btn.Click += async (_, _) =>
            {
                btn.IsEnabled = false;
                if (capturedInstalled)
                {
                    ExtensionFeedService.Remove(capturedExt.Id);
                    btn.Content = "Removed \u2014 restart to apply";
                }
                else
                {
                    btn.Content = "Installing...";
                    var ok = await _feedService.InstallAsync(capturedExt);
                    if (ok && _pluginLoader is not null)
                    {
                        var dllPath = System.IO.Path.Combine(
                            PluginLoader.PluginsDir, $"{capturedExt.Id}.dll");
                        await _pluginLoader.LoadAndInitializeAsync(dllPath);
                        btn.Content = "\u2713 Installed";
                        BuildInstalledExtensionsList();
                    }
                    else
                    {
                        btn.Content = ok ? "Installed \u2014 restart to apply" : "Install failed";
                    }
                }
            };
            btnPanel.Children.Add(btn);

            if (perms is { Count: > 0 })
            {
                var fixBtn = new System.Windows.Controls.Button
                {
                    Content = "Grant Permissions",
                    Padding = new Thickness(10, 3, 10, 3),
                    Margin = new Thickness(8, 0, 0, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "Adds the required API permissions and grants admin consent for all tenants (requires Global Administrator)"
                };
                var capturedPerms = perms.ToList();
                var capturedPermIds = loadedPlugin?.RequiredGraphPermissionIds
                    ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();
                fixBtn.Click += async (_, _) =>
                {
                    fixBtn.IsEnabled = false;
                    fixBtn.Content = "Granting...";
                    var results = new List<string>();
                    foreach (var node in _tenantNodes)
                    {
                        if (string.IsNullOrWhiteSpace(node.ClientId)) continue;
                        var result = await ConnectionService.FixPermissionsAsync(
                            node.Connection, capturedPerms, capturedPermIds);
                        results.Add($"{node.DisplayName}: {result}");
                    }
                    fixBtn.Content = results.All(r => r.Contains("\u2713"))
                        ? "\u2713 Permissions granted"
                        : "Done (check log)";
                    foreach (var r in results)
                        AppLog.Info("Extensions", r);
                };
                btnPanel.Children.Add(fixBtn);
            }

            panel.Children.Add(btnPanel);
            ExtensionsList.Items.Add(panel);
        }
    }

    private void ShowAddStatus(string msg, bool isError)
    {
        TxtAddStatus.Text       = msg;
        TxtAddStatus.Foreground = isError ? Brushes.DarkRed : Brushes.DarkGreen;
        TxtAddStatus.Visibility = Visibility.Visible;
    }
}
