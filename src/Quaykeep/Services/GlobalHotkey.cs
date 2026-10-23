using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Threading;

namespace Quaykeep.Services;

/// <summary>
/// A system-wide shortcut that brings up the main window. Windows only: RegisterHotKey on a message-only window that
/// runs its own message loop on a background thread (macOS and Linux desktops have no API for it that works without
/// accessibility permissions or a specific desktop). Written as "Win+Alt+X", "Ctrl+Shift+F12"…; empty = off.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    public const string Default = "Win+Alt+X";
    private const int WM_HOTKEY = 0x0312, WM_APP_SET = 0x8001, WM_QUIT = 0x0012;
    private const int Id = 0x5353; // "SS"
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    public static bool IsSupported => OperatingSystem.IsWindows();

    private readonly Action _pressed;
    private readonly Thread? _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private IntPtr _hwnd;
    private (uint Mods, uint Vk)? _request;
    private bool _result;
    private readonly AutoResetEvent _done = new(false);
    private WndProc? _proc; // kept alive: the window calls it

    public GlobalHotkey(Action pressed)
    {
        _pressed = pressed;
        if (!OperatingSystem.IsWindows()) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Quaykeep.Hotkey" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(5000);
    }

    /// <summary>The registered shortcut, null when off or taken.</summary>
    public string? Current { get; private set; }

    /// <summary>Registers <paramref name="text"/> (replacing the previous one). False: invalid, used by another program or not Windows.</summary>
    public bool Set(string? text)
    {
        Current = null;
        if (!IsSupported || _hwnd == IntPtr.Zero) return string.IsNullOrWhiteSpace(text);
        (uint, uint)? request = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (Parse(text) is not var (mods, key) || VirtualKey(key) is not { } vk)
            {
                Send(null);
                return false;
            }
            request = (mods, vk);
        }
        var ok = Send(request);
        if (ok && request != null) Current = Format(request.Value.Item1, Parse(text!)!.Value.Key);
        return ok;
    }

    private bool Send((uint, uint)? request)
    {
        _request = request;
        PostMessageW(_hwnd, WM_APP_SET, IntPtr.Zero, IntPtr.Zero);
        return _done.WaitOne(3000) && (_result || request == null);
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        _proc = (hwnd, msg, w, l) =>
        {
            if (msg == WM_HOTKEY && w.ToInt32() == Id)
            {
                Dispatcher.UIThread.Post(_pressed);
                return IntPtr.Zero;
            }
            if (msg == WM_APP_SET)
            {
                UnregisterHotKey(hwnd, Id);
                _result = _request is var (mods, vk) && RegisterHotKey(hwnd, Id, mods | MOD_NOREPEAT, vk);
                _done.Set();
                return IntPtr.Zero;
            }
            return DefWindowProcW(hwnd, msg, w, l);
        };
        var cls = new WndClass { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), lpszClassName = "Quaykeep.Hotkey" };
        RegisterClassW(ref cls);
        // HWND_MESSAGE parent: a window that only receives messages
        _hwnd = CreateWindowExW(0, "Quaykeep.Hotkey", "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _ready.Set();
        while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref m);
            DispatchMessageW(ref m);
        }
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, Id);
            DestroyWindow(_hwnd);
        }
    }

    /// <summary>"Win+Alt+S" → modifiers and key; null when it has no modifier or no key.</summary>
    public static (uint Modifiers, Key Key)? Parse(string text)
    {
        uint mods = 0;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win" or "windows" or "meta": mods |= MOD_WIN; break;
                default:
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (!Enum.TryParse<Key>(name, true, out var k) || IsModifier(k)) return null;
                    key = k;
                    break;
            }
        }
        return mods != 0 && key is { } kk ? (mods, kk) : null;
    }

    /// <summary>From a key press in the settings box: "Ctrl+Alt+K", null for a lone modifier.</summary>
    public static string? FromKeyPress(KeyModifiers modifiers, Key key)
    {
        if (IsModifier(key)) return null;
        uint mods = 0;
        if (modifiers.HasFlag(KeyModifiers.Control)) mods |= MOD_CONTROL;
        if (modifiers.HasFlag(KeyModifiers.Alt)) mods |= MOD_ALT;
        if (modifiers.HasFlag(KeyModifiers.Shift)) mods |= MOD_SHIFT;
        if (modifiers.HasFlag(KeyModifiers.Meta)) mods |= MOD_WIN;
        return mods == 0 ? null : Format(mods, key);
    }

    public static string Format(uint mods, Key key)
    {
        var parts = new List<string>();
        if ((mods & MOD_WIN) != 0) parts.Add("Win");
        if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
        var name = key.ToString();
        parts.Add(name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]) ? name[1..] : name);
        return string.Join("+", parts);
    }

    private static bool IsModifier(Key k) =>
        k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System or Key.None;

    /// <summary>Windows virtual-key code of a key: letters, digits, F-keys and the common named keys.</summary>
    private static uint? VirtualKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => (uint)('A' + (key - Key.A)),
        >= Key.D0 and <= Key.D9 => (uint)('0' + (key - Key.D0)),
        >= Key.NumPad0 and <= Key.NumPad9 => (uint)(0x60 + (key - Key.NumPad0)),
        >= Key.F1 and <= Key.F24 => (uint)(0x70 + (key - Key.F1)),
        Key.Space => 0x20, Key.Enter => 0x0D, Key.Tab => 0x09, Key.Escape => 0x1B, Key.Back => 0x08,
        Key.PageUp => 0x21, Key.PageDown => 0x22, Key.End => 0x23, Key.Home => 0x24,
        Key.Left => 0x25, Key.Up => 0x26, Key.Right => 0x27, Key.Down => 0x28,
        Key.Insert => 0x2D, Key.Delete => 0x2E, Key.Pause => 0x13, Key.Scroll => 0x91,
        Key.OemTilde => 0xC0, Key.OemMinus => 0xBD, Key.OemPlus => 0xBB, Key.OemOpenBrackets => 0xDB, Key.OemCloseBrackets => 0xDD,
        Key.OemSemicolon => 0xBA, Key.OemQuotes => 0xDE, Key.OemComma => 0xBC, Key.OemPeriod => 0xBE, Key.OemQuestion => 0xBF,
        Key.OemPipe => 0xDC,
        _ => null,
    };

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int x, y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WndClass cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Msg msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint thread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
