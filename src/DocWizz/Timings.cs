using System.Diagnostics;

// Wall-clock time per pipeline stage, printed to stderr at exit with --timings (bench/run.py reads it). Stages that
// run more than once (both sides of a diff) add up. Off by default: Measure is then a plain call.
static class Timings
{
    static readonly List<(string Stage, TimeSpan Time)> stages = [];

    // `startup`: from process start to here (runtime start, JIT of Main), so what the stages leave out shows.
    static bool enabled;
    public static bool Enabled
    {
        get => enabled;
        set { enabled = value; if (value) Add("startup", DateTime.Now - Process.GetCurrentProcess().StartTime); }
    }

    static void Add(string stage, TimeSpan time) { lock (stages) stages.Add((stage, time)); }

    public static T Measure<T>(string stage, Func<T> f)
    {
        if (!Enabled) return f();
        var sw = Stopwatch.StartNew();
        try { return f(); }
        finally { Add(stage, sw.Elapsed); }
    }

    // For a stretch of statements: `var t = Timings.Stage("link"); …; t.Dispose();`.
    public static IDisposable Stage(string stage) => new Scope(stage);

    sealed class Scope(string stage) : IDisposable
    {
        readonly Stopwatch sw = Stopwatch.StartNew();
        public void Dispose() { if (Enabled) Add(stage, sw.Elapsed); }
    }

    // One `timing <stage> <ms>` line per stage in first-run order, the whole process so far, then its peak memory.
    public static void Report(TextWriter o)
    {
        lock (stages)
            foreach (var g in stages.GroupBy(s => s.Stage))
                o.WriteLine($"timing {g.Key} {g.Sum(s => s.Time.TotalMilliseconds):0}");
        o.WriteLine($"timing total {(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds:0}");
        o.WriteLine($"timing peak-memory-mb {Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024)}");
    }
}
