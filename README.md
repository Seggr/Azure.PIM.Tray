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
- .NET 8.0 runtime (or SDK to build from source)

### Build & Publish

```bash
dotnet publish Azure.PIM.Tray --configuration Release --self-contained false
```

This produces `Azure.PIM.Tray-{version}.zip` in `bin/Release/` ready to extract to Program Files.

### App Registration Setup

The app requires an Entra ID (Azure AD) app registration named **"PIM Request Manager"** in each tenant you want to manage. You can create this manually or let the app's **Fix Permissions** button configure it for you.

#### Option A: Automatic (recommended)

1. Create a new app registration in the [Azure Portal](https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade):
   - Name: **PIM Request Manager**
   - Supported account types: **Single tenant**
   - Redirect URI: **Public client/native** — `http://localhost`
2. Launch the app, open **Manage Tenants**, enter your Tenant ID and email, click **Connect & Discover**
3. Select the tenant in the list and click **Fix Permissions** (requires **Global Administrator**)
4. The app will automatically add all required API permissions and grant admin consent

#### Option B: Manual

1. Create the app registration as above
2. Under **API permissions**, add the following **Delegated** permissions:

   **Microsoft Graph:**
   | Permission | Purpose |
   |------------|---------|
   | `User.Read` | Identify the signed-in user |
   | `RoleAssignmentSchedule.ReadWrite.Directory` | Read/write Entra ID PIM role assignments |
   | `RoleManagement.Read.Directory` | Read Entra ID role definitions and eligibility |
   | `PrivilegedAccess.ReadWrite.AzureAD` | Approve Entra ID PIM requests |
   | `PrivilegedAccess.ReadWrite.AzureResources` | Approve Azure resource PIM requests |

   **Azure Service Management:**
   | Permission | Purpose |
   |------------|---------|
   | `user_impersonation` | Access ARM APIs for Azure RBAC PIM |

3. Click **Grant admin consent** for all permissions
4. Under **Authentication**, ensure **Allow public client flows** is set to **Yes**

#### PIM Role Requirements

The app registration permissions allow the app to *call* the PIM APIs, but the signed-in user must also be assigned as an **approver** in the relevant PIM policies to approve requests. If you can see pending requests but approval fails with "not assigned to you", check the PIM role settings in the Azure Portal to confirm your account is listed as an approver. Note that Azure PIM does not allow users to approve their own requests.

#### Verifying Permissions

After setup, select the tenant in **Manage Tenants** and check the **Permissions** column. A green checkmark indicates all permissions are correctly configured. If any are missing, the app will show what's needed.

### First-Time Setup

1. Launch the app — it starts in the system tray and opens **Manage Tenants**
2. Enter your Tenant ID and email, then click **Connect & Discover**
3. Sign in via browser when prompted
4. If permissions are missing, click **Fix Permissions** (requires Global Administrator) or configure them manually as described above
5. Click **Sign In** to authenticate with the app's own credentials
6. Expand a tenant node to see subscriptions; uncheck any you don't need to monitor
7. Close the window — the app begins monitoring in the background

Repeat for each tenant you want to manage.

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
