using System.Diagnostics;
using System.Text;

// Progress goes to stderr only; stdout stays usable for scripts and machine-readable output. Two renderings:
// - live (an interactive terminal): one bar redrawn in place ten times a second with a spinner, the elapsed time and
//   what the running stage is doing (files scanned, drafts written), so a long stage never looks frozen. Finished
//   stages can leave a ✓ line above it. Anything else written to the console meanwhile (scanner warnings, setup's
//   stage log) clears the bar first and the bar is redrawn below it, so the two never mix on one line.
// - lines (redirected, or --progress in CI logs): one line per finished stage, no control codes. Each stage ends with a
//   newline so warnings from scanners never interleave with a half-rendered bar.
// Stage weights are their expected share of the wall time, so the bar moves at an even pace rather than by stage count.
sealed class CliProgress : IDisposable
{
    public record Stage(string Name, double Weight = 1, string? Label = null);

    const int BarWidth = 24;
    static readonly string[] Spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    static CliProgress? active;   // the live bar on the console; a nested progress reports into it

    readonly TextWriter writer;
    readonly Stage[] stages;
    readonly bool enabled, live, color, checklist;
    readonly CliProgress? parent;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Stopwatch stageClock = Stopwatch.StartNew();
    readonly Lock gate = new();
    readonly Timer? timer;
    readonly (TextWriter Out, TextWriter Error)? previous;
    int current, completed, frame, shownWidth, suspended;
    double fraction;
    string? detail;
    bool shown, atLineStart = true, disposed;

    // Per finished stage, also when disabled: the result summary reports where the time went.
    public List<(string Stage, TimeSpan Time)> Durations { get; } = [];
    public TimeSpan Elapsed => clock.Elapsed;

    // `total` equal stages named by Advance (setup, tests).
    public CliProgress(TextWriter writer, int total, bool enabled = true)
        : this(writer, Enumerable.Range(1, Math.Max(total, 1)).Select(i => new Stage($"{i}")), enabled) { }

    public CliProgress(TextWriter writer, IEnumerable<Stage> stages, bool enabled = true, bool live = false, bool color = false,
        bool checklist = true, CliProgress? parent = null)
    {
        this.writer = writer;
        this.stages = [.. stages];
        this.parent = parent;
        this.enabled = enabled && parent is null;
        this.live = this.enabled && live;
        this.color = color;
        this.checklist = checklist;
        if (!this.live) return;
        // Every console write goes through the bar, which steps aside for it.
        previous = (Console.Out, Console.Error);
        if (!Console.IsOutputRedirected) Console.SetOut(new Interceptor(this, Console.Out));
        Console.SetError(new Interceptor(this, Console.Error));
        active = this;
        timer = new Timer(_ => Tick(), null, 0, 100);
    }

    public static CliProgress ForConsole(int total, bool force, bool checklist = true) =>
        ForConsole(Enumerable.Range(1, Math.Max(total, 1)).Select(i => new Stage($"{i}")), force, checklist);

    // Live on an interactive terminal; lines when forced into a redirected stream or CI log; inside another live bar,
    // a nested one that shows as that bar's detail.
    public static CliProgress ForConsole(IEnumerable<Stage> stages, bool force, bool checklist = true)
    {
        if (active is { } outer) return new(Console.Error, stages, parent: outer);
        var interactive = !Console.IsErrorRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
            && Environment.GetEnvironmentVariable("TERM") != "dumb";
        var color = interactive && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
            && (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("WT_SESSION") is not null);
        return new(Console.Error, stages, force || interactive, live: interactive, color: color, checklist: checklist);
    }

    // The named stage starts now (stages before it that never ran count as done); detail says what it is doing.
    public void Begin(string stage, string? detail = null)
    {
        lock (gate)
        {
            var i = Index(stage);
            if (i > current) { current = i; completed = Math.Max(completed, i); }
            stageClock.Restart();
            fraction = 0;
            this.detail = detail;
            // A log gets a line when a long stage starts too, so it doesn't sit silent until the stage is over.
            var total = stages.Sum(s => s.Weight);
            if (enabled && !live && total > 0 && stages[current].Weight / total >= 0.25)
            {
                writer.WriteLine($"docwizz {stages[current].Label ?? stage}…");
                writer.Flush();
            }
        }
        Forward();
    }

    // How far the running stage is: `done` of `total` units, and what it is working on.
    public void Report(int done, int total, string? detail = null)
    {
        lock (gate)
        {
            fraction = total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0;
            if (detail is not null) this.detail = detail;
        }
        Forward();
    }

    public void Detail(string detail)
    {
        lock (gate) this.detail = detail;
        Forward();
    }

    // The named stage (or the running one, when no stage has that name) is done; summary says what it found.
    public void Advance(string stage, string? summary = null)
    {
        lock (gate)
        {
            var i = Index(stage);
            if (i > current) current = i;
            Durations.Add((stage, stageClock.Elapsed));
            completed = Math.Min(Math.Max(completed, current) + 1, stages.Length);
            var label = stages.ElementAtOrDefault(current)?.Label ?? stage;
            var time = stageClock.Elapsed;
            current = Math.Min(current + 1, stages.Length - 1);
            fraction = 0;
            detail = null;
            stageClock.Restart();
            if (live)
            {
                Erase();
                if (!atLineStart) { writer.WriteLine(); atLineStart = true; }
                if (checklist)
                {
                    writer.WriteLine($"  {Paint("32", "✓")} {label,-16}{Paint("2", $"{Duration(time),7}")}{(summary is null ? "" : "  " + Paint("2", summary))}");
                    writer.Flush();
                }
                Draw();
            }
            else if (enabled)
            {
                var filled = (int)Math.Round(BarWidth * Done());
                writer.WriteLine($"docwizz [{new string('=', filled)}{new string('-', BarWidth - filled)}] {completed}/{stages.Length} {stage}" +
                    $" ({Duration(time)}{(summary is null ? "" : $", {summary}")})");
                writer.Flush();
            }
        }
        Forward();
    }

    // No redraws while an interactive prompt owns the terminal.
    public IDisposable Suspend()
    {
        lock (gate)
        {
            suspended++;
            Erase();
        }
        return new Resume(this);
    }

    sealed class Resume(CliProgress bar) : IDisposable
    {
        int done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) == 0) lock (bar.gate) bar.suspended--;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer?.Dispose();
        lock (gate)
        {
            if (!live) return;
            Erase();
            writer.Flush();
            if (previous is { } p)
            {
                Console.SetOut(p.Out);
                Console.SetError(p.Error);
            }
            if (active == this) active = null;
        }
    }

    // A nested progress shows as its parent's detail, and moves the parent's running stage along with it.
    void Forward()
    {
        if (parent is null) return;
        string? text;
        double done;
        lock (gate)
        {
            var label = stages.ElementAtOrDefault(current) is { } s ? s.Label ?? s.Name : "";
            text = completed >= stages.Length ? null : detail is null ? label : $"{label} · {detail}";
            done = Done();
        }
        lock (parent.gate)
        {
            parent.fraction = done;
            parent.detail = text;
        }
    }

    int Index(string stage)
    {
        for (var i = current; i < stages.Length; i++)
            if (stages[i].Name == stage) return i;
        return current;
    }

    // Weighted share of the work that is done, the running stage counted by its fraction.
    double Done()
    {
        var total = stages.Sum(s => s.Weight);
        if (total <= 0) return (double)completed / stages.Length;
        var done = stages.Take(completed).Sum(s => s.Weight);
        if (completed < stages.Length && current >= completed) done += stages[current].Weight * fraction;
        return Math.Clamp(done / total, 0, 1);
    }

    void Tick()
    {
        lock (gate)
        {
            if (disposed || !live || suspended > 0 || !atLineStart) return;
            frame++;
            Erase();
            Draw();
        }
    }

    // ⠹ Scanning code   ███████████░░░░░░░░░░░░░  46%  C# 312/950 files · 4.2s
    void Draw()
    {
        if (suspended > 0 || !atLineStart || disposed) return;
        var width = TerminalWidth() - 1;   // the last column wraps on some terminals, and a wrapped bar can't be redrawn
        var stage = stages.ElementAtOrDefault(current) is { } s ? s.Label ?? s.Name : "";
        var done = Done();
        var filled = (int)Math.Round(BarWidth * done);
        var pct = $"{done * 100,3:0}%";
        var time = Duration(clock.Elapsed);
        var head = $"{Spinner[frame % Spinner.Length]} {Fit(stage, 16),-16} ";
        var tail = $"  {pct}  ";
        var room = width - head.Length - BarWidth - tail.Length - time.Length - 3;
        var info = detail is { Length: > 0 } d && room > 4 ? Fit(d, room) + " · " : "";
        if (head.Length + BarWidth + tail.Length + info.Length + time.Length > width) { info = ""; head = ""; }
        var visible = head.Length + BarWidth + tail.Length + info.Length + time.Length;
        writer.Write(Paint("36", head) + Paint("36", new string('█', filled)) + Paint("2", new string('░', BarWidth - filled)) +
            Paint("1", tail) + Paint("2", info + time));
        writer.Flush();
        shown = true;
        shownWidth = visible;
    }

    void Erase()
    {
        if (!shown) return;
        writer.Write("\r" + new string(' ', shownWidth) + "\r");
        shown = false;
    }

    // Someone else writes to the console: the bar steps aside, and comes back once their line is complete.
    void Passthrough(TextWriter inner, string text)
    {
        if (text.Length == 0) return;
        lock (gate)
        {
            Erase();
            inner.Write(text);
            inner.Flush();
            atLineStart = text[^1] == '\n';
        }
    }

    string Paint(string code, string text) => color && text.Length > 0 ? $"\u001b[{code}m{text}\u001b[0m" : text;

    static string Fit(string text, int max) => text.Length <= max ? text : max <= 1 ? "" : text[..(max - 1)] + "…";

    static int TerminalWidth()
    {
        try { return Console.WindowWidth is > 20 and var w ? w : 80; }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException or InvalidOperationException) { return 80; }
    }

    public static string Duration(TimeSpan t) => t.TotalSeconds < 10 ? $"{t.TotalSeconds:0.0}s"
        : t.TotalSeconds < 60 ? $"{t.TotalSeconds:0}s" : $"{(int)t.TotalMinutes}m {t.Seconds:00}s";

    sealed class Interceptor(CliProgress bar, TextWriter inner) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;
        public override void Write(char value) => bar.Passthrough(inner, value.ToString());
        public override void Write(string? value) => bar.Passthrough(inner, value ?? "");
        public override void Write(char[] buffer, int index, int count) => bar.Passthrough(inner, new string(buffer, index, count));
        public override void WriteLine(string? value) => bar.Passthrough(inner, (value ?? "") + new string(CoreNewLine));
        public override void WriteLine() => bar.Passthrough(inner, new string(CoreNewLine));
        public override void Flush() => inner.Flush();
    }
}
