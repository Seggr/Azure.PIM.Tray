using Velopack;
using Velopack.Sources;

namespace Azure.PIM.Tray.Services;

public sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly UpdateManager _mgr;
    private readonly System.Threading.Timer _timer;

    private UpdateInfo? _pendingUpdate;
    private bool _downloading;
    private bool _lastCheckFailed;

    public bool UpdateAvailable => _pendingUpdate is not null && !_downloading;
    public bool IsDownloading   => _downloading;
    public bool LastCheckFailed => _lastCheckFailed;
    public string? AvailableVersion => _pendingUpdate?.TargetFullRelease?.Version?.ToString();
    public string  CurrentVersion   => _mgr.CurrentVersion?.ToString() ?? "dev";

    /// <summary>Raised (on a background thread) when a non-patch update is downloaded and ready.</summary>
    public event Action<string>? UpdateReady;

    public UpdateService()
    {
        _mgr = new UpdateManager(
            new GithubSource("https://github.com/Seggr/Azure.PIM.Tray", null, false));

        // First check after 30 seconds, then every 6 hours.
        _timer = new System.Threading.Timer(
            state => { _ = CheckForUpdatesAsync(); }, null,
            TimeSpan.FromSeconds(30), CheckInterval);
    }

    public async Task CheckForUpdatesAsync()
    {
        if (!_mgr.IsInstalled)
        {
            AppLog.Debug("Update", "Skipping update check \u2014 not installed via Velopack");
            return;
        }

        if (_pendingUpdate is not null && !_downloading)
        {
            AppLog.Debug("Update", "Update already downloaded \u2014 skipping check");
            return;
        }

        _lastCheckFailed = false;

        try
        {
            AppLog.Info("Update", "Checking for updates...");
            var update = await _mgr.CheckForUpdatesAsync();

            if (update is null)
            {
                AppLog.Info("Update", "No updates available");
                return;
            }

            AppLog.Info("Update",
                $"Update available: {CurrentVersion} \u2192 {update.TargetFullRelease.Version}");
            _pendingUpdate = update;
            _downloading = true;

            await _mgr.DownloadUpdatesAsync(update);

            _downloading = false;
            AppLog.Info("Update",
                $"Update v{update.TargetFullRelease.Version} downloaded and ready to install");

            // Auto-apply patch updates (same major + minor) silently on exit
            var cur = _mgr.CurrentVersion;
            var tgt = update.TargetFullRelease.Version;
            if (cur is not null && tgt.Major == cur.Major && tgt.Minor == cur.Minor)
            {
                AppLog.Info("Update",
                    $"Patch update ({cur} \u2192 {tgt}) \u2014 will apply on next exit");
                // _pendingUpdate is set; ApplyUpdateOnExit() in App.OnExit handles the rest
            }
            else
            {
                AppLog.Info("Update",
                    $"New version {tgt} available \u2014 user action required");
                UpdateReady?.Invoke(tgt.ToString());
            }
        }
        catch (Exception ex)
        {
            _downloading = false;
            _lastCheckFailed = true;
            AppLog.Warning("Update", $"Update check failed: {ex.Message}");
        }
    }

    public void ApplyUpdateAndRestart()
    {
        if (_pendingUpdate is null)
        {
            AppLog.Warning("Update", "ApplyUpdateAndRestart called but no pending update");
            return;
        }

        AppLog.Info("Update", $"Applying update v{_pendingUpdate.TargetFullRelease.Version} and restarting...");
        _mgr.ApplyUpdatesAndRestart(_pendingUpdate);
    }

    public void ApplyUpdateOnExit()
    {
        if (_pendingUpdate is null) return;

        AppLog.Info("Update", $"Scheduling update v{_pendingUpdate.TargetFullRelease.Version} for after exit");
        _mgr.WaitExitThenApplyUpdates(_pendingUpdate);
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
