using Avalonia;
using Avalonia.Media;
using Quaykeep.Core;
using Quaykeep.Core.Pipes;
using Quaykeep.Core.Storage;
using Quaykeep.Services;
using Velopack;

namespace Quaykeep;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack install / update / uninstall hooks run and exit here. A downloaded update is applied only when the
        // user restarts from Settings or exits: this program also runs as the qk helper (AppImage) and as SSH_ASKPASS.
        var velopack = VelopackApp.Build().SetAutoApplyOnStartup(false);
        if (OperatingSystem.IsWindows())
            velopack = velopack.OnBeforeUninstallFastCallback(_ => TryDisableAutostart());
        velopack.Run();

        // "Quaykeep qk …" (the helper inside an AppImage) and ssh asking for a password through SSH_ASKPASS
        if (args is ["qk", ..])
            return Cli.Program.RunAsync(args[1..]).GetAwaiter().GetResult();
        if (Cli.Program.IsAskPass)
            return Cli.Program.RunAsync(args).GetAwaiter().GetResult();

        // Release step: the icons in every size and format the installers need.
        if (args is ["--write-icons", var dir])
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            IconFactory.WriteAll(dir);
            return 0;
        }

        var tray = args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        WaitForUpdatedProcess(args);
        using var instance = new Mutex(true, AppPaths.SingleInstanceLock, out var isFirst);
        if (!isFirst)
        {
            // Already running: bring it to front (unless this is just the autostart entry).
            if (!tray)
            {
                try
                {
                    ControlClient.SendAsync(new ControlRequest { Op = "activate" }).Wait(3000);
                }
                catch (AggregateException)
                {
                }
            }
            return 0;
        }
        App.StartInTray = tray;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new X11PlatformOptions { WmClass = "Quaykeep" })
            .LogToTrace();
        if (OperatingSystem.IsLinux())
            builder = builder.With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" });
        return builder;
    }

    /// <summary>"--after-update PID": started by the old version (a zip copy updating itself), which is still exiting.</summary>
    private static void WaitForUpdatedProcess(string[] args)
    {
        var i = Array.FindIndex(args, a => a.Equals("--after-update", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid)) return;
        try
        {
            using var old = System.Diagnostics.Process.GetProcessById(pid);
            old.WaitForExit(30_000);
        }
        catch (ArgumentException)
        {
            // already gone
        }
    }

    /// <summary>The uninstaller removes the program: an autostart entry would point at nothing.</summary>
    private static void TryDisableAutostart()
    {
        try
        {
            Autostart.Apply(false);
        }
        catch
        {
            // best effort
        }
    }
}
