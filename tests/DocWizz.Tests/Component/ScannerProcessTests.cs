using System.Diagnostics;

namespace DocWizz.Tests.Component;

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

    // Regression (#205 review): a successful scanner's stderr (scanner-vue's "skipped <file>" lines) reaches the user.
    [Fact]
    public async Task Scanner_output_is_preserved_on_success_and_its_stderr_forwarded()
    {
        var command = OperatingSystem.IsWindows() ? "echo skipped a.vue 1>&2 & echo expected" : "echo skipped a.vue >&2; printf expected";
        var diagnostics = new StringWriter();
        var output = await ScannerProcess.RunAsync(Shell(command), "", TimeSpan.FromSeconds(5), diagnostics);
        Assert.Equal(OperatingSystem.IsWindows() ? "expected" + Environment.NewLine : "expected", output);
        Assert.Contains("skipped a.vue", diagnostics.ToString());
    }

    // Regression (#205 review): a scanner that dies before reading its input reports its exit code and stderr, not
    // the broken pipe the unread input causes.
    [Fact]
    public async Task A_scanner_exiting_before_reading_input_reports_its_stderr()
    {
        var command = OperatingSystem.IsWindows() ? "echo boom 1>&2 & exit /b 3" : "exec 0<&-; echo boom >&2; exit 3";
        var input = new string('x', 4 * 1024 * 1024);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ScannerProcess.RunAsync(Shell(command), input, TimeSpan.FromSeconds(10)));
        Assert.Contains("exit code 3", ex.Message);
        Assert.Contains("boom", ex.Message);
    }
}
