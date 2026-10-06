using System.Diagnostics;

// Wall-clock time per pipeline stage, printed to stderr at exit with --timings (bench/run.py reads it). Stages that
// run more than once (both sides of a diff) add up. Off by default: Measure is then a plain call.
static class Timings
{
    public static bool Enabled { get; set; }
    static readonly List<(string Stage, TimeSpan Time)> stages = [];

    public static T Measure<T>(string stage, Func<T> f)
    {
        if (!Enabled) return f();
        var sw = Stopwatch.StartNew();
        try { return f(); }
        finally { stages.Add((stage, sw.Elapsed)); }
    }

    // For a stretch of statements: `var t = Timings.Stage("link"); …; t.Dispose();`.
    public static IDisposable Stage(string stage) => new Scope(stage);

    sealed class Scope(string stage) : IDisposable
    {
        readonly Stopwatch sw = Stopwatch.StartNew();
        public void Dispose() { if (Enabled) stages.Add((stage, sw.Elapsed)); }
    }

    // One `timing <stage> <ms>` line per stage in first-run order, then the process's peak memory.
    public static void Report(TextWriter o)
    {
        foreach (var g in stages.GroupBy(s => s.Stage))
            o.WriteLine($"timing {g.Key} {g.Sum(s => s.Time.TotalMilliseconds):0}");
        o.WriteLine($"timing peak-memory-mb {Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024)}");
    }
}
