using System.Windows;
using Azure.PIM.Tray.Models;
using Azure.PIM.Tray.Services;

namespace Azure.PIM.Tray.Windows;

public partial class ApprovalWindow : Window
{
    private readonly UnifiedPendingRequest _request;
    private readonly ITenantContext        _tenant;

    public ApprovalWindow(UnifiedPendingRequest request, ITenantContext tenant)
    {
        InitializeComponent();
        WindowIconHelper.ApplyApprovalIcon(this);
        _request = request;
        _tenant  = tenant;

        TxtPrincipal.Text = request.PrincipalName;
        TxtRole.Text      = request.RoleName;
        TxtType.Text      = request.Source == PimSource.EntraId ? "Entra ID" : "Azure RBAC";
        TxtScope.Text     = request.ScopeDisplayName;
        TxtCreated.Text   = request.CreatedOn.ToLocalTime().ToString("g");
        TxtReason.Text    = string.IsNullOrWhiteSpace(request.Reason)
                            ? "(no reason provided)" : request.Reason;

        TxtJustification.Text = "Approved via PIM Request Manager";
        TxtJustification.SelectAll();
        TxtJustification.Focus();
    }

    private async void Approve_Click(object sender, RoutedEventArgs e)
    {
        var justification = TxtJustification.Text.Trim();
        if (string.IsNullOrWhiteSpace(justification))
        {
            ShowError("Please enter a justification.");
            return;
        }

        BtnApprove.IsEnabled = false;
        BtnApprove.Content   = "Approving\u2026";
        TxtError.Visibility  = Visibility.Collapsed;

        var (ok, msg) = await _tenant.ApproveAsync(_request, justification);

        if (ok)
        {
            DialogResult = true;
            Close();
        }
        else
        {
            ShowError(msg);
            BtnApprove.IsEnabled = true;
            BtnApprove.Content   = "Approve";
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
