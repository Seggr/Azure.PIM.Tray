using System.Drawing;
using System.Windows.Forms;
using Azure.PIM.Tray.MenuItems;
using Azure.PIM.Tray.Models;

namespace Azure.PIM.Tray.Services;

public sealed class ContextMenuBuilder
{
    private readonly Func<List<ITenantContext>>     _getTenants;
    private readonly RefreshOrchestrator            _refreshOrchestrator;
    private readonly ActivationWatcher              _activationWatcher;
    private readonly TrayIconManager                _trayIconManager;
    private readonly Action<UnifiedPendingRequest>  _openApprovalWindow;
    private readonly Action<UnifiedEligibleRole>    _openActivateWindow;
    private readonly Action                         _openManageWindow;
    private readonly Action                         _openLogViewerWindow;
    private readonly Action                         _shutdown;

    public ContextMenuBuilder(
        Func<List<ITenantContext>>     getTenants,
        RefreshOrchestrator            refreshOrchestrator,
        ActivationWatcher              activationWatcher,
        TrayIconManager                trayIconManager,
        Action<UnifiedPendingRequest>  openApprovalWindow,
        Action<UnifiedEligibleRole>    openActivateWindow,
        Action                         openManageWindow,
        Action                         openLogViewerWindow,
        Action                         shutdown)
    {
        _getTenants          = getTenants;
        _refreshOrchestrator = refreshOrchestrator;
        _activationWatcher   = activationWatcher;
        _trayIconManager     = trayIconManager;
        _openApprovalWindow  = openApprovalWindow;
        _openActivateWindow  = openActivateWindow;
        _openManageWindow    = openManageWindow;
        _openLogViewerWindow = openLogViewerWindow;
        _shutdown            = shutdown;
    }

    public void ShowContextMenu()
    {
        var ownerForm = _trayIconManager.OwnerForm;
        if (ownerForm is null) return;

        var menu = new ContextMenuStrip();
        menu.Font = new Font("Segoe UI", 9f);

        _trayIconManager.BringOwnerToForeground();

        var tenants = _getTenants();
        var isRefreshing = _refreshOrchestrator.IsRefreshing;

        // -- Pending Approvals submenu --
        BuildPendingMenu(menu, tenants, isRefreshing);

        // -- Eligible Roles submenu --
        BuildEligibleMenu(menu, tenants, isRefreshing);

        menu.Items.Add(new ToolStripSeparator());

        var manageItem = new ToolStripMenuItem("\u2699\ufe0f  Manage Tenants\u2026");
        manageItem.Click += (_, _) => _openManageWindow();
        menu.Items.Add(manageItem);

        menu.Items.Add(new ToolStripSeparator());

        // -- Log Viewer --
        BuildLogViewerItem(menu);

        menu.Items.Add(new ToolStripSeparator());

        var quitItem = new ToolStripMenuItem("Quit");
        quitItem.Font  = new Font(menu.Font, System.Drawing.FontStyle.Regular);
        quitItem.Click += (_, _) => _shutdown();
        menu.Items.Add(quitItem);

        menu.Show(ownerForm, ownerForm.PointToClient(Cursor.Position));
    }

    private void BuildPendingMenu(ContextMenuStrip menu, List<ITenantContext> tenants, bool isRefreshing)
    {
        var allPending   = tenants.SelectMany(t => t.PendingRequests).ToList();
        var pendingCount = allPending.Count;
        var pendingLabel = isRefreshing
            ? "\u23f3  Pending Approvals  (refreshing\u2026)"
            : pendingCount > 0
                ? $"\u23f3  Pending Approvals  ({pendingCount})"
                : "\u23f3  Pending Approvals";

        var pendingMenu = new ToolStripMenuItem(pendingLabel);
        pendingMenu.DropDown.ItemClicked += (_, e) =>
        {
            if (e.ClickedItem?.Tag is Action action)
            {
                menu.Close();
                action();
            }
        };

        foreach (var tenant in tenants)
        {
            pendingMenu.DropDownItems.Add(LiveMenuItemBinder.Bind(
                new TenantHeaderMenuItem(tenant, () => _refreshOrchestrator.RefreshPendingTenantAsync(tenant))));

            if (isRefreshing)
            {
                var statusItem = _refreshOrchestrator.RefreshItems.FirstOrDefault(x => x.TenantId == tenant.TenantId);
                pendingMenu.DropDownItems.Add(
                    new ToolStripMenuItem($"   {statusItem?.Status ?? "Refreshing\u2026"}")
                    { Enabled = false, ForeColor = Color.Gray });
            }
            else
            {
                var tenantRequests = tenant.PendingRequests.ToList();
                if (tenantRequests.Count == 0)
                {
                    pendingMenu.DropDownItems.Add(
                        new ToolStripMenuItem("   (none)") { Enabled = false });
                }
                else
                {
                    foreach (var req in tenantRequests)
                    {
                        var cap = req;
                        var item = LiveMenuItemBinder.Bind(
                            new PendingRequestMenuItem(req, tenant, () => { }));
                        item.Tag = (Action)(() => _openApprovalWindow(cap));
                        pendingMenu.DropDownItems.Add(item);
                    }
                }
            }
        }

        menu.Items.Add(pendingMenu);
    }

    private void BuildEligibleMenu(ContextMenuStrip menu, List<ITenantContext> tenants, bool isRefreshing)
    {
        var allEligible   = tenants.SelectMany(t => t.EligibleRoles).ToList();
        var eligibleCount = allEligible.Count;
        var eligibleLabel = eligibleCount > 0
            ? $"\ud83d\udd11  Open Request  ({eligibleCount} role(s))"
            : "\ud83d\udd11  Open Request";
        if (isRefreshing) eligibleLabel += "  (refreshing\u2026)";

        var eligibleMenu = new ToolStripMenuItem(eligibleLabel);
        eligibleMenu.DropDown.ItemClicked += (_, e) =>
        {
            if (e.ClickedItem?.Tag is Action action)
            {
                menu.Close();
                action();
            }
        };

        foreach (var tenant in tenants)
        {
            eligibleMenu.DropDownItems.Add(LiveMenuItemBinder.Bind(
                new TenantHeaderMenuItem(tenant, () => _refreshOrchestrator.RefreshTenantAsync(tenant))));

            var entraRoles = tenant.EligibleRoles
                .Where(r => r.Source == PimSource.EntraId)
                .OrderBy(r => r.RoleName).ToList();
            var rbacRoles = tenant.EligibleRoles
                .Where(r => r.Source == PimSource.AzureRbac)
                .OrderBy(r => r.RoleName).ToList();

            if (entraRoles.Count == 0 && rbacRoles.Count == 0)
            {
                var noRolesLabel = isRefreshing ? "   (refreshing\u2026)" : "   (none)";
                eligibleMenu.DropDownItems.Add(
                    new ToolStripMenuItem(noRolesLabel) { Enabled = false });
            }
            else
            {
                void AddSourceGroup(string sourceLabel, List<UnifiedEligibleRole> roles)
                {
                    if (roles.Count == 0) return;
                    eligibleMenu.DropDownItems.Add(new ToolStripMenuItem($"   \u2014 {sourceLabel} \u2014")
                    {
                        Enabled   = false,
                        ForeColor = Color.DimGray,
                        Font      = new Font("Segoe UI", 8f, System.Drawing.FontStyle.Italic)
                    });
                    foreach (var role in roles)
                    {
                        var cap = role;
                        var item = LiveMenuItemBinder.Bind(
                            new EligibleRoleMenuItem(
                                role,
                                _activationWatcher.IsRoleWatched,
                                () => { },
                                _activationWatcher.Subscribe,
                                _activationWatcher.Unsubscribe));
                        item.Tag = (Action)(() => _openActivateWindow(cap));
                        eligibleMenu.DropDownItems.Add(item);
                    }
                }
                AddSourceGroup("Entra ID",   entraRoles);
                AddSourceGroup("Azure RBAC", rbacRoles);
            }
        }

        menu.Items.Add(eligibleMenu);
    }

    private void BuildLogViewerItem(ContextMenuStrip menu)
    {
        var errCount  = AppLog.ErrorCount;
        var warnCount = AppLog.WarningCount;
        string logLabel;
        Color? logColor = null;

        if (errCount > 0)
        {
            logLabel = warnCount > 0
                ? $"\ud83d\udcdd  Log Viewer  (\u26a0 {warnCount} / \u274c {errCount})"
                : $"\ud83d\udcdd  Log Viewer  (\u274c {errCount} error{(errCount == 1 ? "" : "s")})";
            logColor = Color.FromArgb(0xCC, 0x22, 0x00);
        }
        else if (warnCount > 0)
        {
            logLabel = $"\ud83d\udcdd  Log Viewer  (\u26a0 {warnCount} warning{(warnCount == 1 ? "" : "s")})";
            logColor = Color.FromArgb(0xB8, 0x6B, 0x00);
        }
        else
        {
            logLabel = "\ud83d\udcdd  Log Viewer";
        }

        var errorLogItem = new ToolStripMenuItem(logLabel);
        if (logColor.HasValue)
            errorLogItem.ForeColor = logColor.Value;
        errorLogItem.Click += (_, _) => _openLogViewerWindow();
        menu.Items.Add(errorLogItem);
    }
}
