using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Tmds.DBus.Protocol;

namespace Quaykeep.Services.Platform;

/// <summary>
/// Raises <see cref="Locked"/> when the session locks (raised on a background thread). Windows: SystemEvents.SessionSwitch.
/// macOS: the session dictionary's CGSSessionScreenIsLocked, polled every 2 s. Linux: the screen saver's ActiveChanged
/// signal (freedesktop / GNOME) on the session bus and logind's Lock signal on the system bus.
/// </summary>
public sealed class SessionLockMonitor : IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<DBusConnection> _connections = [];
    private Timer? _macTimer;
    private bool _macWasLocked;

    public event Action? Locked;

    public void Start()
    {
        if (OperatingSystem.IsWindows())
            StartWindows();
        else if (OperatingSystem.IsMacOS())
            _macTimer = new Timer(_ => PollMac(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        else if (OperatingSystem.IsLinux())
            _ = Task.Run(StartLinuxAsync);
    }

    // ---------------- Windows ----------------

    [SupportedOSPlatform("windows")]
    private void StartWindows()
    {
        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }
        catch
        {
            // No session events (e.g. service session).
        }
    }

    [SupportedOSPlatform("windows")]
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
            Locked?.Invoke();
    }

    // ---------------- macOS ----------------

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGSessionCopyCurrentDictionary();

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

    [DllImport(CoreFoundation)]
    private static extern nuint CFGetTypeID(IntPtr value);

    [DllImport(CoreFoundation)]
    private static extern nuint CFBooleanGetTypeID();

    [DllImport(CoreFoundation)]
    private static extern byte CFBooleanGetValue(IntPtr boolean);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);

    private void PollMac()
    {
        bool locked;
        try
        {
            locked = MacScreenIsLocked();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _macTimer?.Dispose();
            return;
        }
        if (locked && !_macWasLocked)
            Locked?.Invoke();
        _macWasLocked = locked;
    }

    private static bool MacScreenIsLocked()
    {
        var session = CGSessionCopyCurrentDictionary();
        if (session == IntPtr.Zero)
            return false;
        var key = CFStringCreateWithCString(IntPtr.Zero, "CGSSessionScreenIsLocked", Utf8);
        try
        {
            var value = key == IntPtr.Zero ? IntPtr.Zero : CFDictionaryGetValue(session, key);
            return value != IntPtr.Zero && CFGetTypeID(value) == CFBooleanGetTypeID() && CFBooleanGetValue(value) != 0;
        }
        finally
        {
            if (key != IntPtr.Zero)
                CFRelease(key);
            CFRelease(session);
        }
    }

    // ---------------- Linux ----------------

    private async Task StartLinuxAsync()
    {
        if (DBusAddress.Session is { } session && await ConnectAsync(session) is { } sessionBus)
        {
            // KDE, Xfce, Cinnamon, MATE and others implement org.freedesktop.ScreenSaver; GNOME has its own name.
            await WatchAsync(sessionBus, null, "/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver", "ActiveChanged", ReadBool);
            await WatchAsync(sessionBus, null, "/ScreenSaver", "org.freedesktop.ScreenSaver", "ActiveChanged", ReadBool);
            await WatchAsync(sessionBus, null, "/org/gnome/ScreenSaver", "org.gnome.ScreenSaver", "ActiveChanged", ReadBool);
        }
        // `loginctl lock-session` and most lock screens go through logind. Any session's Lock counts: locking too often is safe.
        if (DBusAddress.System is { } system && await ConnectAsync(system) is { } systemBus)
            await WatchAsync(systemBus, "org.freedesktop.login1", null, "org.freedesktop.login1.Session", "Lock", static (_, _) => true);
    }

    private static bool ReadBool(Message message, object? state) => message.GetBodyReader().ReadBool();

    private async Task<DBusConnection?> ConnectAsync(string address)
    {
        try
        {
            var connection = new DBusConnection(address);
            await connection.ConnectAsync();
            lock (_connections)
                _connections.Add(connection);
            return connection;
        }
        catch
        {
            return null;
        }
    }

    private async Task WatchAsync(DBusConnection connection, string? sender, string? path, string @interface, string signal, MessageValueReader<bool> reader)
    {
        try
        {
            var subscription = await connection.WatchSignalAsync(sender, path, @interface, signal, reader,
                (Notification<bool> n) =>
                {
                    if (n.HasValue && n.Value)
                        Locked?.Invoke();
                }, ObserverFlags.None, emitOnCapturedContext: false);
            lock (_subscriptions)
                _subscriptions.Add(subscription);
        }
        catch
        {
            // That service is not there.
        }
    }

    public void Dispose()
    {
        _macTimer?.Dispose();
        lock (_subscriptions)
        {
            foreach (var s in _subscriptions)
                s.Dispose();
            _subscriptions.Clear();
        }
        lock (_connections)
        {
            foreach (var c in _connections)
                c.Dispose();
            _connections.Clear();
        }
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        }
        catch
        {
            // ignore
        }
    }
}
