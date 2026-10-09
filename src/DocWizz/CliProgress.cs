// Progress goes to stderr only; stdout stays usable for scripts and machine-readable output.
// A terminal gets one updating bar; redirected streams stay quiet unless --progress is requested.
sealed class CliProgress(TextWriter writer, int total, bool interactive, bool enabled = true) : IDisposable
{
    int completed;
    bool displayed;

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
        if (interactive) writer.Write("\r\u001b[2K" + line);
        else writer.WriteLine(line);
        writer.Flush();
        displayed = true;
    }

    public void Dispose()
    {
        if (interactive && displayed) writer.WriteLine();
    }
}
