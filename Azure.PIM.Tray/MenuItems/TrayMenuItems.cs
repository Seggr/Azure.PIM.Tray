using System.Drawing;
using System.Windows.Forms;
using Azure.PIM.Tray.Models;
using Azure.PIM.Tray.Services;

namespace Azure.PIM.Tray.MenuItems;

public interface ILiveMenuItem : IDisposable
{
    string Text     { get; }
    bool   Enabled  { get; }
    Color  ForeColor { get; }
    Font?  Font     { get; }
    void   Execute();
    event Action? StateChanged;
}

public static class LiveMenuItemBinder
{
    public static ToolStripMenuItem Bind(ILiveMenuItem vm)
    {
        var uiCtx = SynchronizationContext.Current
                 ?? throw new InvalidOperationException("Bind() must be called from the UI thread.");

        var item = new ToolStripMenuItem(vm.Text)
        {
            Enabled   = vm.Enabled,
            ForeColor = vm.ForeColor,
        };
        if (vm.Font is { } f) item.Font = f;

        vm.StateChanged += Refresh;
        item.Disposed   += (_, _) =>
        {
            vm.StateChanged -= Refresh;
            vm.Dispose();
        };
        item.Click += (_, _) => vm.Execute();
        return item;

        void Refresh()
        {
            uiCtx.Post(_ =>
            {
                if (item.IsDisposed) return;
                item.Text      = vm.Text;
                item.Enabled   = vm.Enabled;
                item.ForeColor = vm.ForeColor;
                if (vm.Font is { } f2) item.Font = f2;
                item.GetCurrentParent()?.Refresh();
            }, null);
        }
    }
}

public sealed class TenantHeaderMenuItem : ILiveMenuItem
{
    private static readonly Font HeaderFont = new("Segoe UI", 8.5f, FontStyle.Bold);

    private bool _isRefreshing;
    private readonly ITenantContext _tenant;
    private readonly Func<Task>     _refresh;

    public string Text     => _isRefreshing
        ? $"\u21bb  {_tenant.TenantDisplayName}  (refreshing\u2026)"
        : $"\u21ba  {_tenant.TenantDisplayName}";
    public bool   Enabled   => !_isRefreshing;
    public Color  ForeColor => Color.FromArgb(0x00, 0x44, 0x99);
    public Font?  Font      => HeaderFont;

    public event Action? StateChanged;

    public TenantHeaderMenuItem(ITenantContext tenant, Func<Task> refresh)
    {
        _tenant  = tenant;
        _refresh = refresh;
    }

    public async void Execute()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        StateChanged?.Invoke();
        try   { await _refresh(); }
        catch { }
        finally
        {
            _isRefreshing = false;
            StateChanged?.Invoke();
        }
    }

    public void Dispose() { }
}

public sealed class EligibleRoleMenuItem : ILiveMenuItem
{
    private readonly UnifiedEligibleRole             _role;
    private readonly Func<UnifiedEligibleRole, bool> _isWatched;
    private readonly Action                          _execute;
    private readonly Action                          _cleanup;

    public string Text     => _isWatched(_role)
        ? $"   {_role.RoleName}  ({_role.ScopeDisplayName})  \u23f3"
        : $"   {_role.RoleName}  ({_role.ScopeDisplayName})";
    public bool   Enabled   => true;
    public Color  ForeColor => SystemColors.ControlText;
    public Font?  Font      => null;

    public event Action? StateChanged;

    public EligibleRoleMenuItem(
        UnifiedEligibleRole             role,
        Func<UnifiedEligibleRole, bool> isWatched,
        Action                          execute,
        Action<Action>                  subscribe,
        Action<Action>                  unsubscribe)
    {
        _role      = role;
        _isWatched = isWatched;
        _execute   = execute;

        void OnWatchingChanged() => StateChanged?.Invoke();
        subscribe(OnWatchingChanged);
        _cleanup = () => unsubscribe(OnWatchingChanged);
    }

    public void Execute() => _execute();
    public void Dispose() => _cleanup();
}

public sealed class PendingRequestMenuItem : ILiveMenuItem
{
    private bool _completed;
    private readonly UnifiedPendingRequest _request;
    private readonly ITenantContext        _tenant;
    private readonly Action                _execute;

    private string Id =>
        _request.EntraApprovalId ?? _request.ArmRequest?.Name ?? string.Empty;

    private string Label
    {
        get
        {
            var src = _request.Source == PimSource.EntraId ? "Entra" : "RBAC";
            return $"{_request.PrincipalName}  \u2014  {_request.RoleName}  [{src}]";
        }
    }

    public string Text     => _completed ? $"   \u2713 {Label}" : $"   {Label}";
    public bool   Enabled  => !_completed;
    public Color  ForeColor => _completed ? Color.SeaGreen : SystemColors.ControlText;
    public Font?  Font      => null;

    public event Action? StateChanged;

    public PendingRequestMenuItem(
        UnifiedPendingRequest request,
        ITenantContext        tenant,
        Action                execute)
    {
        _request = request;
        _tenant  = tenant;
        _execute = execute;
        _tenant.DataChanged += OnDataChanged;
    }

    private void OnDataChanged()
    {
        if (_completed) return;
        bool stillExists = _tenant.PendingRequests.Any(r =>
            string.Equals(r.EntraApprovalId ?? r.ArmRequest?.Name, Id,
                StringComparison.OrdinalIgnoreCase));
        if (!stillExists)
        {
            _completed = true;
            StateChanged?.Invoke();
        }
    }

    public void Execute() => _execute();
    public void Dispose() => _tenant.DataChanged -= OnDataChanged;
}
