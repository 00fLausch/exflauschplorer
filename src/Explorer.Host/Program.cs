using Explorer.Host.Tray;
using Explorer.Host.Configuration;
using System.Diagnostics;

namespace Explorer.Host;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--watchdog")
        {
            RunWatchdog(args);
            return;
        }
        // The browser E2E harness uses an isolated profile, temp share and test recycle bin;
        // it must not create a tray icon or collide with the user's normal single-instance mutex.
        if (Environment.GetEnvironmentVariable("WINDOWS_MOBILE_EXPLORER_E2E") == "1")
        {
            ExplorerWebApp.Build(SettingsStore.Load(), args).Run();
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, "Local\\WindowsMobileExplorer.Tray", out var createdNew);
        if (!createdNew) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new ExplorerTrayApplicationContext(args, mutex));
    }

    private static void RunWatchdog(string[] args)
    {
        using var mutex = new Mutex(true, "Local\\WindowsMobileExplorer.Watchdog", out var createdNew);
        if (!createdNew) return;
        var executable = Environment.ProcessPath;
        if (executable is null) return;
        Process? child = null;
        if (args.Length > 1 && int.TryParse(args[1], out var pid))
        {
            try { child = Process.GetProcessById(pid); }
            catch (ArgumentException) { }
        }
        MonitorWatchdog(() => Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true }), child,
            () => Thread.Sleep(TimeSpan.FromSeconds(3)));
    }

    internal static void MonitorWatchdog(Func<Process?> start, Process? child, Action waitBeforeRestart)
    {
        var restarts = new Queue<DateTime>();
        while (true)
        {
            try
            {
                child ??= start();
                if (child is null) return;
                child.WaitForExit();
                var exitCode = child.ExitCode;
                child.Dispose();
                child = null;
                // A normal exit, including the tray's Beenden command, is intentional.
                if (exitCode == 0) return;
                var now = DateTime.UtcNow;
                while (restarts.Count > 0 && now - restarts.Peek() > TimeSpan.FromMinutes(5)) restarts.Dequeue();
                if (restarts.Count >= 5) return;
                restarts.Enqueue(now);
                waitBeforeRestart();
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return;
            }
        }
    }
}
