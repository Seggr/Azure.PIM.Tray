using Azure.PIM.Tray.Models;
using Azure.PIM.Tray.Windows;

namespace Azure.PIM.Tray.Services;

public sealed class ContextMenuBuilder
{
    private readonly Func<List<ITenantContext>>     _getTenants;
    private readonly RefreshOrchestrator            _refreshOrchestrator;
    private readonly ActivationWatcher              _activationWatcher;
    private readonly UpdateService                  _updateService;
    private readonly TrayIconManager                _trayIconManager;
    private readonly Action<UnifiedPendingRequest>  _openApprovalWindow;
    private readonly Action<UnifiedEligibleRole>    _openActivateWindow;
    private readonly Action                         _openManageWindow;
    private readonly Action                         _openLogViewerWindow;
    private readonly Action                         _shutdown;

    private TrayMenuWindow? _current;

    public ContextMenuBuilder(
        Func<List<ITenantContext>>     getTenants,
        RefreshOrchestrator            refreshOrchestrator,
        ActivationWatcher              activationWatcher,
        UpdateService                  updateService,
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
        _updateService       = updateService;
        _trayIconManager     = trayIconManager;
        _openApprovalWindow  = openApprovalWindow;
        _openActivateWindow  = openActivateWindow;
        _openManageWindow    = openManageWindow;
        _openLogViewerWindow = openLogViewerWindow;
        _shutdown            = shutdown;
    }

    public void ShowContextMenu()
    {
        _current?.CloseAll();

        var menu = new TrayMenuWindow();
        _current = menu;

        var tenants = _getTenants();
        var isRefreshing = _refreshOrchestrator.IsRefreshing;

        // Pending Approvals
        var allPending   = tenants.SelectMany(t => t.PendingRequests).ToList();
        var pendingCount = allPending.Count;
        var pendingLabel = isRefreshing
            ? $"\u23f3  Pending Approvals  (refreshing\u2026)"
            : pendingCount > 0
                ? $"\u23f3  Pending Approvals  ({pendingCount})"
                : "\u23f3  Pending Approvals";
        menu.AddItem(pendingLabel, hasSubmenu: true,
            buildSubmenu: sub => BuildPendingItems(sub, tenants, isRefreshing));

        // Eligible Roles
        var allEligible   = tenants.SelectMany(t => t.EligibleRoles).ToList();
        var eligibleCount = allEligible.Count;
        var eligibleLabel = eligibleCount > 0
            ? $"\ud83d\udd11  Open Request  ({eligibleCount} role(s))"
            : "\ud83d\udd11  Open Request";
        if (isRefreshing) eligibleLabel += "  (refreshing\u2026)";
        menu.AddItem(eligibleLabel, hasSubmenu: true,
            buildSubmenu: sub => BuildEligibleItems(sub, tenants, isRefreshing));

        menu.AddSeparator();

        menu.AddItem("\u2699\ufe0f  Settings\u2026", onClick: _openManageWindow);

        menu.AddSeparator();

        // Log Viewer
        var errCount  = AppLog.ErrorCount;
        var warnCount = AppLog.WarningCount;
        string logLabel;
        string? logColor = null;
        if (errCount > 0)
        {
            logLabel = warnCount > 0
                ? $"\ud83d\udcdd  Log Viewer  (\u26a0 {warnCount} / \u274c {errCount})"
                : $"\ud83d\udcdd  Log Viewer  (\u274c {errCount} error{(errCount == 1 ? "" : "s")})";
            logColor = "#CC2200";
        }
        else if (warnCount > 0)
        {
            logLabel = $"\ud83d\udcdd  Log Viewer  (\u26a0 {warnCount} warning{(warnCount == 1 ? "" : "s")})";
            logColor = "#B86B00";
        }
        else
        {
            logLabel = "\ud83d\udcdd  Log Viewer";
        }
        menu.AddItem(logLabel, onClick: _openLogViewerWindow, foreground: logColor);

        menu.AddSeparator();

        menu.AddItem("Quit", onClick: _shutdown);

        menu.PositionNearTray();
    }

    private void BuildPendingItems(TrayMenuWindow sub, List<ITenantContext> tenants, bool isRefreshing)
    {
        foreach (var tenant in tenants)
        {
            sub.AddItem($"\u21ba  {tenant.TenantDisplayName}", isBold: true,
                onClick: () => _ = _refreshOrchestrator.RefreshPendingTenantAsync(tenant));

            if (isRefreshing)
            {
                var statusItem = _refreshOrchestrator.RefreshItems
                    .FirstOrDefault(x => x.TenantId == tenant.TenantId);
                sub.AddItem($"   {statusItem?.Status ?? "Refreshing\u2026"}", isDisabled: true);
            }
            else
            {
                var reqs = tenant.PendingRequests.ToList();
                if (reqs.Count == 0)
                {
                    sub.AddItem("   (none)", isDisabled: true);
                }
                else
                {
                    foreach (var req in reqs)
                    {
                        var cap = req;
                        sub.AddItem($"   {req.PrincipalName} \u2014 {req.RoleName}",
                            onClick: () => _openApprovalWindow(cap));
                    }
                }
            }
        }
    }

    private void BuildEligibleItems(TrayMenuWindow sub, List<ITenantContext> tenants, bool isRefreshing)
    {
        foreach (var tenant in tenants)
        {
            sub.AddItem($"\u21ba  {tenant.TenantDisplayName}", isBold: true,
                onClick: () => _ = _refreshOrchestrator.RefreshTenantAsync(tenant));

            var entraRoles = tenant.EligibleRoles
                .Where(r => r.Source == PimSource.EntraId)
                .OrderBy(r => r.RoleName).ToList();
            var rbacRoles = tenant.EligibleRoles
                .Where(r => r.Source == PimSource.AzureRbac)
                .OrderBy(r => r.RoleName).ToList();

            if (entraRoles.Count == 0 && rbacRoles.Count == 0)
            {
                sub.AddItem(isRefreshing ? "   (refreshing\u2026)" : "   (none)", isDisabled: true);
            }
            else
            {
                AddSourceGroup(sub, "Entra ID", entraRoles);
                AddSourceGroup(sub, "Azure RBAC", rbacRoles);
            }
        }
    }

    private void AddSourceGroup(TrayMenuWindow sub, string sourceLabel, List<UnifiedEligibleRole> roles)
    {
        if (roles.Count == 0) return;
        sub.AddItem($"   \u2014 {sourceLabel} \u2014", isDisabled: true, foreground: "#999999");

        foreach (var role in roles)
        {
            var cap = role;
            var watched = _activationWatcher.IsRoleWatched(role);
            var label = watched
                ? $"   {role.RoleName}  ({role.ScopeDisplayName})  \u23f3"
                : $"   {role.RoleName}  ({role.ScopeDisplayName})";
            sub.AddItem(label, onClick: () => _openActivateWindow(cap));
        }
    }
}
