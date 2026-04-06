using System.Windows;
using System.Windows.Controls;
using Azure.PIM.Tray.Models;
using Azure.PIM.Tray.Services;

namespace Azure.PIM.Tray.Windows;

public partial class ActivateWindow : Window
{
    private readonly UnifiedEligibleRole _role;
    private readonly ITenantContext      _tenant;

    public string? ActivationPollUrl { get; private set; }

    public ActivateWindow(UnifiedEligibleRole role, ITenantContext tenant)
    {
        InitializeComponent();
        WindowIconHelper.ApplyActivateIcon(this);
        _role   = role;
        _tenant = tenant;

        TxtRole.Text  = role.RoleName;
        TxtType.Text  = role.Source == PimSource.EntraId ? "Entra ID" : "Azure RBAC";
        TxtScope.Text = role.ScopeDisplayName;

        TxtJustification.Focus();
    }

    private void CboDuration_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (CboDuration.SelectedItem is not ComboBoxItem item) return;
        var isCustom = item.Tag?.ToString() == "0";
        TxtCustomMinutes.Visibility = isCustom ? Visibility.Visible  : Visibility.Collapsed;
        LblCustomUnit.Visibility    = isCustom ? Visibility.Visible  : Visibility.Collapsed;
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        int minutes;
        if (CboDuration.SelectedItem is ComboBoxItem selected && selected.Tag?.ToString() == "0")
        {
            if (!int.TryParse(TxtCustomMinutes.Text.Trim(), out minutes) || minutes < 1)
            {
                ShowError("Enter a valid number of minutes (minimum 1).");
                return;
            }
        }
        else
        {
            var tag = (CboDuration.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "60";
            minutes = int.TryParse(tag, out var m) ? m : 60;
        }

        var justification = TxtJustification.Text.Trim();

        BtnActivate.IsEnabled = false;
        BtnActivate.Content   = "Activating\u2026";
        TxtError.Visibility   = Visibility.Collapsed;

        var (ok, msg, pollUrl) = await _tenant.ActivateRoleAsync(
            _role, TimeSpan.FromMinutes(minutes), justification);

        if (ok)
        {
            ActivationPollUrl = pollUrl;
            DialogResult = true;
            Close();
        }
        else
        {
            ShowError(msg);
            BtnActivate.IsEnabled = true;
            BtnActivate.Content   = "Activate";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowError(string msg)
    {
        TxtError.Text       = msg;
        TxtError.Visibility = Visibility.Visible;
    }
}
