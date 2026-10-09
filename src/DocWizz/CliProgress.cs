// Progress goes to stderr only; stdout stays usable for scripts and machine-readable output.
// Each stage ends with a newline so warnings from scanners never interleave with a half-rendered bar.
sealed class CliProgress(TextWriter writer, int total, bool interactive, bool enabled = true) : IDisposable
{
    int completed;

    public static CliProgress ForConsole(int total, bool force) =>
        new(Console.Error, total, !Console.IsErrorRedirected,
            force || (!Console.IsErrorRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))));

    public void Advance(string stage)
    {
        if (!enabled) return;
        completed = Math.Min(completed + 1, total);
        const int width = 24;
        var filled = width * completed / total;
        var line = $"docwizz [{new string('=', filled)}{new string('-', width - filled)}] {completed}/{total} {stage}";
        writer.WriteLine(line);
        writer.Flush();
    }

    public void Dispose() { }
}
