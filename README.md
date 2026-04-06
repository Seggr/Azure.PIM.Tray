# Azure PIM Tray

A Windows system tray application for managing Azure Privileged Identity Management (PIM) requests across multiple tenants.

## Features

- **Monitor pending approvals** from Entra ID and Azure RBAC in one place
- **Approve requests** directly from desktop notifications or the tray menu
- **Self-activate eligible roles** with custom durations and justifications
- **Multi-tenant support** with per-tenant configuration and subscription filtering
- **Desktop notifications** when new requests arrive or activations complete
- **Built-in log viewer** for troubleshooting

## Tech Stack

- .NET 8.0 (Windows), WPF + WinForms interop for system tray
- Azure.Identity with MSAL persistent token caching
- Microsoft Graph API (v1.0 + beta) for Entra ID PIM
- Azure Resource Manager API for Azure RBAC PIM

## Getting Started

### Prerequisites

- Windows 10/11
- .NET 8.0 SDK
- An Azure AD app registration with the following permissions:
  - `User.Read`, `RoleAssignmentSchedule.ReadWrite.Directory`, `RoleManagement.Read.Directory`
  - `PrivilegedAccess.ReadWrite.AzureAD`, `PrivilegedAccess.ReadWrite.AzureResources`
  - Azure Service Management `user_impersonation`

### Build & Run

```bash
dotnet build
dotnet run --project Azure.PIM.Tray
```

### First-Time Setup

1. The app launches to the system tray and opens the **Manage Tenants** window
2. Enter your Tenant ID and email, then click **Connect & Discover**
3. Sign in via browser when prompted — the app registers and configures itself
4. Expand a tenant node to see subscriptions; uncheck any you don't need to monitor
5. Close the window — the app begins monitoring in the background

## Configuration

Config is stored at `%APPDATA%\PimRequestManager\tray-config.json`. Subscription caches are stored alongside as `subs-{tenantId}.json`.

You can exclude subscriptions from scanning via the Manage Tenants UI to reduce API calls and avoid rate limiting.

## Architecture

```
App.xaml.cs              Thin shell: startup, shutdown, window launchers
TrayIconManager          NotifyIcon, balloons, icon generation
RefreshOrchestrator      Background polling, fan-out refresh, new/completed detection
ContextMenuBuilder       Tray context menu construction
ActivationWatcher        Activation polling and status notifications
TenantContext            Per-tenant state: pending requests, eligible roles
PimDataService           Routes to Graph or ARM data services
ArmPimService            Azure Resource Manager PIM API client
PimService               Microsoft Graph PIM API client
```

## Usage

| Action | How |
|--------|-----|
| View pending approvals | Left/right-click tray icon |
| Approve a request | Click a pending request in the menu, or click the balloon notification |
| Activate a role | Expand "Open Request" in the menu, click a role |
| View logs | Click "Log Viewer" in the menu |
| Manage tenants | Click "Manage Tenants" in the menu |

## AI Acknowledgment

This application was developed with assistance from [Claude](https://claude.ai), Anthropic's AI assistant. Claude contributed to architecture design, code implementation, debugging, and documentation throughout the development process.

## License

Private — all rights reserved.
