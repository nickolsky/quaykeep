using System.Runtime.InteropServices;
using Tmds.DBus.Protocol;

namespace Quaykeep.Services.Platform;

/// <summary>
/// Time since the last keyboard/mouse input anywhere in the session: GetLastInputInfo (Windows), CoreGraphics (macOS),
/// the X screen saver extension (Linux X11) or GNOME Mutter's idle monitor over D-Bus (Linux Wayland under GNOME). Where
/// none answers (e.g. KDE or wlroots on Wayland) it is the time since the last input in Quaykeep's own windows, which locks
/// sooner rather than later.
/// </summary>
public static class IdleTime
{
    private static long _lastAppInputTicks = Environment.TickCount64;
    private static bool _nativeFailed;
    private static LinuxIdle? _linux;

    public static void NoteAppInput() => Interlocked.Exchange(ref _lastAppInputTicks, Environment.TickCount64);

    private static TimeSpan SinceAppInput => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastAppInputTicks));

    public static TimeSpan Get()
    {
        if (!_nativeFailed)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return Windows();
                if (OperatingSystem.IsMacOS())
                    return Mac();
                if (OperatingSystem.IsLinux())
                {
                    _linux ??= new LinuxIdle();
                    if (_linux.Get() is { } idle)
                        return idle;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                _nativeFailed = true;
            }
        }
        return SinceAppInput;
    }

    // ---------------- Windows ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    private static TimeSpan Windows()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    // ---------------- macOS ----------------

    private const int CombinedSessionState = 0; // kCGEventSourceStateCombinedSessionState
    private const uint AnyInputEvent = uint.MaxValue; // kCGAnyInputEventType

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern double CGEventSourceSecondsSinceLastEventType(int stateId, uint eventType);

    private static TimeSpan Mac() => TimeSpan.FromSeconds(Math.Max(0, CGEventSourceSecondsSinceLastEventType(CombinedSessionState, AnyInputEvent)));

    // ---------------- Linux ----------------

    /// <summary>X11: XScreenSaverQueryInfo, read on each call. Wayland: Mutter's GetIdletime, refreshed in the background.</summary>
    private sealed class LinuxIdle
    {
        private readonly bool _x11 = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") is "x11"
                                     || (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is not { Length: > 0 }
                                         && Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 });
        private IntPtr _display;
        private IntPtr _info;
        private bool _x11Failed;
        private long _mutterIdleMs = -1;
        private long _mutterReadTicks;
        private bool _mutterStarted;

        public TimeSpan? Get()
        {
            if (_x11 && !_x11Failed)
            {
                try
                {
                    if (X11() is { } idle)
                        return idle;
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    // no libXss: try D-Bus
                }
                _x11Failed = true;
            }
            if (!_mutterStarted)
            {
                _mutterStarted = true;
                _ = Task.Run(PollMutterAsync);
            }
            var ms = Interlocked.Read(ref _mutterIdleMs);
            if (ms < 0)
                return null;
            // The value is a few seconds old: add the time since it was read.
            return TimeSpan.FromMilliseconds(ms + (Environment.TickCount64 - Interlocked.Read(ref _mutterReadTicks)));
        }

        private TimeSpan? X11()
        {
            if (_display == IntPtr.Zero)
            {
                _display = XOpenDisplay(IntPtr.Zero);
                if (_display == IntPtr.Zero)
                    return null;
                _info = XScreenSaverAllocInfo();
            }
            if (_info == IntPtr.Zero || XScreenSaverQueryInfo(_display, XDefaultRootWindow(_display), _info) == 0)
                return null;
            // XScreenSaverInfo { Window window; int state; int kind; unsigned long til_or_since; unsigned long idle; ... }
            var idleOffset = IntPtr.Size + 8 + IntPtr.Size;
            return TimeSpan.FromMilliseconds((ulong)Marshal.ReadIntPtr(_info, idleOffset));
        }

        private async Task PollMutterAsync()
        {
            try
            {
                if (DBusAddress.Session is not { } address)
                    return;
                using var connection = new DBusConnection(address);
                await connection.ConnectAsync();
                while (true)
                {
                    MessageBuffer message;
                    using (var writer = connection.GetMessageWriter())
                    {
                        writer.WriteMethodCallHeader(destination: "org.gnome.Mutter.IdleMonitor", path: "/org/gnome/Mutter/IdleMonitor/Core",
                            @interface: "org.gnome.Mutter.IdleMonitor", member: "GetIdletime");
                        message = writer.CreateMessage();
                    }
                    var ms = await connection.CallMethodAsync(message, static (Message m, object? _) => m.GetBodyReader().ReadUInt64(), null);
                    Interlocked.Exchange(ref _mutterReadTicks, Environment.TickCount64);
                    Interlocked.Exchange(ref _mutterIdleMs, (long)Math.Min(ms, long.MaxValue / 2));
                    await Task.Delay(TimeSpan.FromSeconds(4));
                }
            }
            catch
            {
                // Not GNOME (or no session bus): fall back to the app's own input.
                Interlocked.Exchange(ref _mutterIdleMs, -1);
            }
        }

        [DllImport("libX11.so.6")]
        private static extern IntPtr XOpenDisplay(IntPtr name);

        [DllImport("libX11.so.6")]
        private static extern IntPtr XDefaultRootWindow(IntPtr display);

        [DllImport("libXss.so.1")]
        private static extern IntPtr XScreenSaverAllocInfo();

        [DllImport("libXss.so.1")]
        private static extern int XScreenSaverQueryInfo(IntPtr display, IntPtr drawable, IntPtr info);
    }
}
