using System.Diagnostics;

namespace DocWizz.Tests.Unit;

public class ScannerProcessTests
{
    static ProcessStartInfo Shell(string script) => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd.exe", ["/c", script])
        : new ProcessStartInfo("/bin/sh", ["-c", script]);

    [Fact]
    public async Task Kills_a_hung_scanner_with_a_named_timeout()
    {
        var command = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 > nul" : "sleep 30";
        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            ScannerProcess.RunAsync(Shell(command), "", TimeSpan.FromMilliseconds(200)));
        Assert.Contains("timed out", ex.Message);
        Assert.Contains("0.2", ex.Message);
    }

    [Fact]
    public async Task A_failing_scanner_includes_stderr_in_its_warning_cause()
    {
        var command = OperatingSystem.IsWindows() ? "echo scanner failure 1>&2 & exit /b 7" : "echo scanner failure >&2; exit 7";
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ScannerProcess.RunAsync(Shell(command), "", TimeSpan.FromSeconds(5)));
        Assert.Contains("exit code 7", ex.Message);
        Assert.Contains("scanner failure", ex.Message);
    }

    [Fact]
    public async Task Scanner_output_is_preserved_on_success()
    {
        var command = OperatingSystem.IsWindows() ? "echo expected" : "printf expected";
        var output = await ScannerProcess.RunAsync(Shell(command), "", TimeSpan.FromSeconds(5));
        Assert.Equal(OperatingSystem.IsWindows() ? "expected\\r\\n" : "expected", output);
    }
}
