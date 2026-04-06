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

    public event EventHandler? ConfigChanged;

    public ManageWindow(TrayAppConfig config)
    {
        InitializeComponent();
        WindowIconHelper.ApplyManageIcon(this);
        _config = config;
        _tenantNodes = new ObservableCollection<TenantTreeNode>(
            config.Connections.Select(c => new TenantTreeNode(c)));
        TenantTree.ItemsSource = _tenantNodes;

        _ = CheckAllPermissionsAsync();
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
            var status = await ConnectionService.CheckPermissionsAsync(node.Connection);
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
            var result = await ConnectionService.FixPermissionsAsync(sel.Connection);
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

    private void ShowAddStatus(string msg, bool isError)
    {
        TxtAddStatus.Text       = msg;
        TxtAddStatus.Foreground = isError ? Brushes.DarkRed : Brushes.DarkGreen;
        TxtAddStatus.Visibility = Visibility.Visible;
    }
}
