using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Quaykeep.Core.Models;
using Quaykeep.Core.Ssh;

namespace Quaykeep.Core.Terminal;

/// <summary>
/// A local program in a Windows pseudo console (ConPTY) for a built-in terminal tab: the plain "aws ssm start-session"
/// shell, which needs a real console and is not SSH. Its children (session-manager-plugin) share the console and end
/// with it. macOS and Linux open that shell in the system terminal instead.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LocalTerminalSession(ServerEntry server, ProcessStartInfo program) : ITerminalSession
{
    private readonly object _sync = new();
    private IntPtr _console;
    private FileStream? _input;
    private FileStream? _output;
    private int _pid;
    private bool _disposed;

    public ServerEntry Server { get; } = server;
    public event Action<string>? Output;
    public event Action<string?>? Closed;

    /// <summary>Whether this Windows has pseudo consoles (10 1809 and later).</summary>
    public static bool Available => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    public void Open(int cols, int rows, string? command = null)
    {
        // a command typed before the remote shell is up would get lost; commands need SSH anyway
        if (command != null) throw new InvalidOperationException(L.Get("Ssm.ShellOnly"));
        if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0) ||
            !CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0))
            throw new Win32Exception();
        var hr = CreatePseudoConsole(Size(cols, rows), inRead, outWrite, 0, out var console);
        // the console holds its own copies of its ends
        inRead.Dispose();
        outWrite.Dispose();
        if (hr != 0)
        {
            inWrite.Dispose();
            outRead.Dispose();
            throw new Win32Exception(hr);
        }

        var attrs = IntPtr.Zero;
        var env = IntPtr.Zero;
        try
        {
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrs = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref size) ||
                !UpdateProcThreadAttribute(attrs, 0, (IntPtr)ProcThreadAttributePseudoConsole, console, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();
            var si = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfoEx>(),
                    // empty std handles: otherwise the child may pick up this process's instead of the console
                    dwFlags = StartfUseStdHandles,
                },
                lpAttributeList = attrs,
            };
            env = Marshal.StringToHGlobalUni(EnvironmentBlock(program));
            var line = new StringBuilder(CommandLine(program.FileName, program.ArgumentList));
            if (!CreateProcessW(null, line, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    env, null, ref si, out var pi))
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, program.FileName + ": " + new Win32Exception(err).Message);
            }
            CloseHandle(pi.hThread);
            var output = new FileStream(outRead, FileAccess.Read, 1);
            var process = new SafeWaitHandle(pi.hProcess, ownsHandle: true);
            lock (_sync)
            {
                // from here the session owns the console, the pipes and the process
                _console = console;
                console = IntPtr.Zero;
                _input = new FileStream(inWrite, FileAccess.Write, 1);
                _output = output;
                _pid = pi.dwProcessId;
            }
            var reader = new Thread(() => Pump(output)) { IsBackground = true, Name = "qk-console " + Server.Name };
            reader.Start();
            new Thread(() => Watch(process, reader)) { IsBackground = true, Name = "qk-console-exit " + Server.Name }.Start();
            lock (_sync)
                if (_disposed) Shutdown();
        }
        catch
        {
            if (console != IntPtr.Zero)
            {
                inWrite.Dispose();
                outRead.Dispose();
                ClosePseudoConsole(console);
            }
            throw;
        }
        finally
        {
            if (attrs != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrs);
                Marshal.FreeHGlobal(attrs);
            }
            if (env != IntPtr.Zero) Marshal.FreeHGlobal(env);
        }
    }

    public ShellResult Run(string script, TimeSpan timeout) => throw new InvalidOperationException(L.Get("Ssm.ShellOnly"));

    private void Pump(Stream output)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[16384];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        try
        {
            int n;
            while ((n = output.Read(bytes, 0, bytes.Length)) > 0)
            {
                var c = decoder.GetChars(bytes, 0, n, chars, 0);
                if (c > 0) Output?.Invoke(new string(chars, 0, c));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>When the program exits: close the console (which flushes its last output), let the reader drain, report.</summary>
    /// <remarks>This thread owns the process handle: nothing else closes it while it is waited on.</remarks>
    private void Watch(SafeWaitHandle process, Thread reader)
    {
        string? error = null;
        using (process)
        {
            using (var wait = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), false) })
                wait.WaitOne();
            if (GetExitCodeProcess(process, out var code) && code != 0) error = L.F("Term.ExitCode", code);
        }
        bool wasDisposed;
        lock (_sync)
        {
            wasDisposed = _disposed;
            _pid = 0;
            if (_console != IntPtr.Zero) ClosePseudoConsole(_console);
            _console = IntPtr.Zero;
        }
        reader.Join(TimeSpan.FromSeconds(2));
        lock (_sync) Shutdown();
        if (!wasDisposed) Closed?.Invoke(error);
    }

    public void Send(string text)
    {
        var input = _input;
        if (input == null || text.Length == 0) return;
        try
        {
            var data = Encoding.UTF8.GetBytes(text);
            input.Write(data, 0, data.Length);
            input.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    public void Resize(int cols, int rows)
    {
        lock (_sync)
            if (_console != IntPtr.Zero) ResizePseudoConsole(_console, Size(cols, rows));
    }

    /// <summary>Ends the program with its children (aws starts session-manager-plugin) and frees the console.</summary>
    private void Shutdown()
    {
        if (_pid != 0)
        {
            try
            {
                using var p = Process.GetProcessById(_pid);
                p.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
            }
            _pid = 0;
        }
        if (_console != IntPtr.Zero)
        {
            ClosePseudoConsole(_console);
            _console = IntPtr.Zero;
        }
        _input?.Dispose();
        _input = null;
        _output?.Dispose();
        _output = null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // closing the console waits for its output to be read: not on the UI thread
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_sync) Shutdown();
        });
    }

    private static Coord Size(int cols, int rows) => new() { X = (short)Math.Clamp(cols, 10, 1000), Y = (short)Math.Clamp(rows, 2, 1000) };

    /// <summary>A CreateProcess command line, quoted the way the C runtime splits it (as .NET does for ArgumentList).</summary>
    internal static string CommandLine(string file, IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        foreach (var a in args.Prepend(file))
        {
            if (sb.Length > 0) sb.Append(' ');
            if (a.Length > 0 && !a.Any(c => c is ' ' or '\t' or '"'))
            {
                sb.Append(a);
                continue;
            }
            sb.Append('"');
            var slashes = 0;
            foreach (var c in a)
            {
                if (c == '\\')
                {
                    slashes++;
                    continue;
                }
                sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                slashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', slashes * 2).Append('"');
        }
        return sb.ToString();
    }

    /// <summary>The program's environment (this process's plus its changes) as a Unicode block, sorted as Windows expects.</summary>
    private static string EnvironmentBlock(ProcessStartInfo psi)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in psi.Environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            if (v != null) sb.Append(k).Append('=').Append(v).Append('\0');
        return sb.Append('\0').ToString();
    }

    // ---------- Win32 ----------

    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int StartfUseStdHandles = 0x00000100;

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr console, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size,
        IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeWaitHandle process, out int exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
