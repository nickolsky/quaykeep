using Quaykeep.Core.Models;
using Quaykeep.Core.Ssh;

namespace Quaykeep.Core.Terminal;

/// <summary>What a built-in terminal tab talks to: an SSH shell (<see cref="TerminalSession"/>) or a local program in a
/// pseudo console (<see cref="LocalTerminalSession"/>, the plain AWS SSM shell).</summary>
public interface ITerminalSession : IDisposable
{
    ServerEntry Server { get; }

    /// <summary>Decoded output, on a background thread (may end in the middle of an escape sequence).</summary>
    event Action<string>? Output;

    /// <summary>The shell ended: null when it exited normally, otherwise why.</summary>
    event Action<string?>? Closed;

    /// <summary>Starts the shell; slow, call off the UI thread.</summary>
    /// <param name="command">Typed into the shell once it starts.</param>
    void Open(int cols, int rows, string? command = null);

    /// <summary>Runs a helper command for suggestions; throws <see cref="InvalidOperationException"/> when there is no way to.</summary>
    ShellResult Run(string script, TimeSpan timeout);

    void Send(string text);

    void Resize(int cols, int rows);
}
