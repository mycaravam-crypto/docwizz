using System.Diagnostics;

// A scanner failure must not indefinitely block a CLI, MCP session or CI worker.
// Drain both streams while supplying stdin to avoid OS pipe deadlocks.
static class ScannerProcess
{
    const string TimeoutVariable = "DOCWIZZ_SCANNER_TIMEOUT_SECONDS";

    public static string Run(ProcessStartInfo start, string input)
    {
        var seconds = 600;
        if (Environment.GetEnvironmentVariable(TimeoutVariable) is { Length: > 0 } value)
        {
            if (!int.TryParse(value, out seconds) || seconds < 1)
                throw new ArgumentException($"{TimeoutVariable} must be a positive integer");
        }
        return RunAsync(start, input, TimeSpan.FromSeconds(seconds)).GetAwaiter().GetResult();
    }

    internal static async Task<string> RunAsync(ProcessStartInfo start, string input, TimeSpan timeout)
    {
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("scanner could not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var write = Task.Run(async () =>
            {
                await process.StandardInput.WriteAsync(input);
                process.StandardInput.Close();
            });
            await Task.WhenAll(write, process.WaitForExitAsync(cts.Token)).WaitAsync(cts.Token);
            var output = await stdout;
            var errors = await stderr;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"exit code {process.ExitCode}{Tail(errors)}");
            return output;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            throw new TimeoutException($"scanner timed out after {timeout.TotalSeconds:0} seconds");
        }
    }

    static string Tail(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(5);
        var tail = string.Join(" | ", lines);
        return tail.Length == 0 ? "" : $": {tail}";
    }
}
