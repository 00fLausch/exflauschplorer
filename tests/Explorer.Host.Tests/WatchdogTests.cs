using System.Diagnostics;
using Explorer.Host;

namespace Explorer.Host.Tests;

public class WatchdogTests
{
    [Fact]
    public void GracefulExitDoesNotRestart()
    {
        var starts = 0;
        Program.MonitorWatchdog(() => { starts++; return ExitWith(0); }, null, () => { });
        Assert.Equal(1, starts);
    }

    [Fact]
    public void CrashRestartsAndThenStopsOnGracefulExit()
    {
        var starts = 0;
        Program.MonitorWatchdog(() => ExitWith(++starts == 1 ? 1 : 0), null, () => { });
        Assert.Equal(2, starts);
    }

    [Fact]
    public void RepeatedCrashesStopAfterFiveRestarts()
    {
        var starts = 0;
        Program.MonitorWatchdog(() => { starts++; return ExitWith(1); }, null, () => { });
        Assert.Equal(6, starts);
    }

    private static Process ExitWith(int code) => Process.Start(new ProcessStartInfo("cmd.exe", $"/c exit {code}")
    {
        UseShellExecute = false,
        CreateNoWindow = true
    })!;
}
