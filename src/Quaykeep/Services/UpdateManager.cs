using System.Diagnostics;
using Avalonia.Threading;
using Quaykeep.Core;
using Quaykeep.Core.Updates;
using Quaykeep.Views;
using Velopack;
using Velopack.Sources;

namespace Quaykeep.Services;

/// <summary>
/// Update state for the UI: checks GitHub Releases once a day (when enabled), downloads and restarts into the new version.
/// <list type="bullet">
/// <item>Installed copies (Setup.exe, the macOS .pkg, the AppImage, Velopack's portable zip): Velopack, from the
/// release's releases.{win,linux,osx}.json; delta packages when the previous version is there.</item>
/// <item>A Windows copy unpacked from Quaykeep-X.Y.Z-win-x64.zip (every copy before 1.1): that zip replaces the
/// program files in place (<see cref="UpdateService"/>), as before.</item>
/// </list>
/// The side-by-side test copy and a build that is neither never update themselves.
/// </summary>
public sealed class UpdateManager
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    private readonly AppHost _host;
    private readonly UpdateService _service = new();
    private readonly Velopack.UpdateManager? _velopack;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromHours(1) };
    private ReleaseInfo? _zipRelease;
    private UpdateInfo? _velopackRelease;
    private bool _busy;

    public UpdateManager(AppHost host)
    {
        _host = host;
        _timer.Tick += (_, _) => AutoCheck();
        try
        {
            var manager = new Velopack.UpdateManager(new GithubSource($"https://github.com/{UpdateService.Repository}", null, false));
            if (manager.IsInstalled) _velopack = manager;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // not a Velopack install (a dev build, an unpacked zip)
        }
    }

    public Version Current => UpdateService.Current;
    /// <summary>A newer version that can be installed, null when none (or not checked yet).</summary>
    public string? AvailableVersion => _velopackRelease?.TargetFullRelease.Version.ToString() ?? _zipRelease?.Version.ToString(3);
    public string Status { get; private set; } = "";
    public bool Busy => _busy;
    public double Progress { get; private set; }

    /// <summary>This copy can update itself: an installed one, or a Windows zip copy.</summary>
    public bool CanInstall => !AppPaths.IsSideBySide && (_velopack != null || OperatingSystem.IsWindows() && !AppPaths.IsInstalled);

    public string ReleasesPage => _service.ReleasesPage;

    /// <summary>Status, progress or the available version changed (UI thread).</summary>
    public event Action? Changed;

    public void Start()
    {
        if (_velopack == null && OperatingSystem.IsWindows()) _ = Task.Run(() => UpdateService.CleanupOld(AppPaths.ExeDir));
        _timer.Start();
        AutoCheck();
    }

    private void AutoCheck()
    {
        var s = _host.SettingsStore.Settings;
        if (!s.CheckUpdates || AppPaths.IsSideBySide) return;
        if (s.LastUpdateCheck is { } last && DateTime.UtcNow - last < CheckEvery) return;
        _ = CheckAsync(manual: false);
    }

    public async Task CheckAsync(bool manual)
    {
        if (_busy) return;
        _busy = true;
        Set(L.Get("Update.Checking"));
        try
        {
            string? version;
            if (_velopack != null)
            {
                _velopackRelease = await _velopack.CheckForUpdatesAsync();
                version = _velopackRelease?.TargetFullRelease.Version.ToString();
            }
            else
            {
                _zipRelease = await _service.CheckAsync();
                version = _zipRelease?.Version.ToString(3);
            }
            var s = _host.SettingsStore.Settings;
            s.LastUpdateCheck = DateTime.UtcNow;
            _host.SettingsStore.Save();
            if (version == null) Set(L.F("Update.UpToDate", Current.ToString(3)));
            else
            {
                Set(L.F("Update.Available", version));
                // tell once per version, the settings page shows it anyway
                if (!manual && s.NotifiedUpdate != version)
                {
                    s.NotifiedUpdate = version;
                    _host.SettingsStore.Save();
                    _host.Notify(L.Get("Update.Title"), L.F("Update.AvailableBalloon", version));
                }
            }
        }
        catch (Exception ex)
        {
            Set(L.F("Update.CheckFailed", ex.Message));
        }
        finally
        {
            _busy = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Downloads, replaces the program files and restarts. False when it failed (see <see cref="Status"/>).</summary>
    public async Task<bool> InstallAsync()
    {
        if (AvailableVersion is not { } version || _busy || !CanInstall) return false;
        _busy = true;
        Progress = 0;
        Set(L.F("Update.Downloading", version));
        try
        {
            if (_velopack != null && _velopackRelease != null)
            {
                await _velopack.DownloadUpdatesAsync(_velopackRelease, p => Dispatcher.UIThread.Post(() =>
                {
                    Progress = p / 100.0;
                    Changed?.Invoke();
                }));
                Set(L.Get("Update.Installing"));
                // the updater waits for this process to exit, swaps the files and starts the new version
                _velopack.WaitExitThenApplyUpdates(_velopackRelease.TargetFullRelease, silent: false, restart: true);
                _host.Exit();
                return true;
            }
            if (_zipRelease is not { } release) return false;
            var progress = new Progress<double>(p =>
            {
                Progress = p;
                Changed?.Invoke();
            });
            var package = await _service.DownloadAsync(release, progress);
            Set(L.Get("Update.Installing"));
            await Task.Run(() => UpdateService.Apply(package, AppPaths.ExeDir));
            // the new process waits for this one to exit before it takes the single-instance lock
            Process.Start(new ProcessStartInfo(AppPaths.MainExe, $"--after-update {Environment.ProcessId}") { UseShellExecute = false })?.Dispose();
            _host.Exit();
            return true;
        }
        catch (Exception ex)
        {
            Set(L.F("Update.InstallFailed", ex.Message));
            return false;
        }
        finally
        {
            _busy = false;
            Changed?.Invoke();
        }
    }

    public void OpenReleasePage() =>
        UiExtensions.OpenWithShell(_zipRelease?.PageUrl is { Length: > 0 } url ? url
            : AvailableVersion is { } v ? $"{ReleasesPage}/tag/v{v}" : ReleasesPage);

    private void Set(string status)
    {
        Status = status;
        Changed?.Invoke();
    }
}
