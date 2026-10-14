using System.Text.Json;

// Measured load-test results from powerwizz (`powerwizz.report/v1`), read natively like gitwizz.history/v2: offline,
// deterministic, no adapter. `load import` keeps the reports in the index; generate attaches each step to an endpoint.
static class LoadReports
{
    public const string ReportSchema = "powerwizz.report/v1";
    public const string Schema = "docwizz.load-index/v1";
    public const string DefaultFile = "docs/.docwizz/load.json";

    public record Latency(double P50, double P95, double P99);
    public record Step(string? Name, string Method, string Path, long Requests, long Failures, double ErrorRate, Latency LatencyMs);
    public record Target(string BaseUrl, bool Local);
    public record RunConfig(int Users, double RampUpS, double DurationS, string? Auth);
    // File: the report's file name, kept as evidence.
    // One second of the run (powerwizz's `series`, totals over all steps): T is the end of the second.
    public record Point(double T, long Requests, double P95Ms);
    public record Report(string Schema, DateTimeOffset StartedAt, Target Target, RunConfig? Config, List<Step> Steps, string? File = null,
        List<Point>? Series = null);
    // One run's steps on one endpoint.
    public record Run(Report Report, List<Step> Steps);
    public record Index(string Schema, List<Report> Reports);

    // Stale: the endpoint's code changed after the run, so the numbers may no longer hold.
    // Steps: every step of that run that matched the endpoint (two scenarios may call the same endpoint).
    // Runs: every run on the endpoint against the newest run's target (others aren't comparable), oldest first, newest included.
    public record Measured(string Endpoint, List<Step> Steps, Report Report, bool Stale, List<Run> Runs);
    public record Attached(List<Measured> Endpoints, List<string> Unmatched, List<string> Ambiguous);

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    /// <summary>
    /// Reads one powerwizz report. Throws ArgumentException naming the file when it isn't powerwizz.report/v1, and naming
    /// the field when one v1 has is missing or of another type: a missing error rate or latency must not read as 0.
    /// </summary>
    public static Report Read(string file)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
            root = doc.RootElement.Clone();
        }
        catch (JsonException e) { throw new ArgumentException($"{file}: not valid JSON ({e.Message})"); }
        var schema = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("schema", out var s) ? s.ToString() : null;
        if (schema != ReportSchema)
            throw new ArgumentException($"{file}: not {ReportSchema} (schema: {schema ?? "missing"})");
        Validate(root, file);
        var report = root.Deserialize<Report>(Json)!;
        return report with { File = Path.GetFileName(file) };
    }

    // The fields Read relies on, with their JSON kind; unknown fields are not looked at (v1 may add some).
    static void Validate(JsonElement root, string file)
    {
        void Need(JsonElement e, string at, string name, string what, params JsonValueKind[] kinds)
        {
            if (!e.TryGetProperty(name, out var v) || !kinds.Contains(v.ValueKind))
                throw new ArgumentException($"{file}: {at}{name} missing or not {what}");
        }
        const JsonValueKind Str = JsonValueKind.String, Num = JsonValueKind.Number, Obj = JsonValueKind.Object, Arr = JsonValueKind.Array;
        Need(root, "", "started_at", "a timestamp", Str);
        if (!DateTimeOffset.TryParse(root.GetProperty("started_at").GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out _))
            throw new ArgumentException($"{file}: started_at missing or not a timestamp");
        Need(root, "", "target", "an object", Obj);
        Need(root.GetProperty("target"), "target.", "base_url", "a URL", Str);
        Need(root.GetProperty("target"), "target.", "local", "true or false", JsonValueKind.True, JsonValueKind.False);
        if (root.TryGetProperty("config", out var config) && config.ValueKind != JsonValueKind.Null)
        {
            Need(root, "", "config", "an object", Obj);
            foreach (var n in new[] { "users", "ramp_up_s", "duration_s" }) Need(config, "config.", n, "a number", Num);
        }
        Need(root, "", "steps", "an array", Arr);
        var i = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var at = $"steps[{i++}].";
            if (step.ValueKind != Obj) throw new ArgumentException($"{file}: {at[..^1]} not an object");
            Need(step, at, "method", "an HTTP method", Str);
            Need(step, at, "path", "a path", Str);
            foreach (var n in new[] { "requests", "error_rate" }) Need(step, at, n, "a number", Num);
            Need(step, at, "latency_ms", "an object", Obj);
            foreach (var n in new[] { "p50", "p95", "p99" }) Need(step.GetProperty("latency_ms"), $"{at}latency_ms.", n, "a number", Num);
        }
        if (root.TryGetProperty("series", out var series) && series.ValueKind != JsonValueKind.Null)
        {
            Need(root, "", "series", "an array", Arr);
            var j = 0;
            foreach (var point in series.EnumerateArray())
            {
                var at = $"series[{j++}].";
                if (point.ValueKind != Obj) throw new ArgumentException($"{file}: {at[..^1]} not an object");
                foreach (var n in new[] { "t", "requests", "p95_ms" }) Need(point, at, n, "a number", Num);
            }
        }
    }

    public static Index Load(string file)
    {
        Index? index;
        try { index = JsonSerializer.Deserialize<Index>(System.IO.File.ReadAllText(file), Json); }
        catch (JsonException e) { throw new ArgumentException($"{file}: not valid JSON ({e.Message}); delete it and run docwizz load import again"); }
        return index is { Schema: Schema } i ? i
            : throw new ArgumentException($"{file}: not a {Schema} index; delete it and run docwizz load import again");
    }

    /// <summary>Adds reports to an index; a report of the same run (start time and target) replaces the earlier copy.</summary>
    public static Index Merge(Index? index, IEnumerable<Report> reports)
    {
        var all = (index?.Reports ?? []).Concat(reports).GroupBy(r => (r.StartedAt, r.Target.BaseUrl)).Select(g => g.Last());
        return new(Schema, [.. all.OrderBy(r => r.StartedAt).ThenBy(r => r.Target.BaseUrl, StringComparer.Ordinal)]);
    }

    public static string Serialize(Index index) => JsonSerializer.Serialize(index, Json) + "\n";

    /// <summary>
    /// Matches every step to a detected endpoint: same method, then segment by segment. A route parameter (`{id}`, `:id`,
    /// `[id]`) matches any one segment, a literal must be equal (case-insensitive). The base URL's path prefix may precede the
    /// route. The most literal segments win; a tie is ambiguous and not attached. Per endpoint the newest run wins.
    /// </summary>
    /// <param name="changed">When an endpoint's code last changed, if known; a run before that is stale.</param>
    public static Attached Attach(CodeModel model, Index index, Func<Node, DateTimeOffset?> changed)
    {
        var endpoints = model.Nodes.Where(n => n.Tags is ["endpoint", _, ..]).ToList();
        var newest = new Dictionary<string, (List<Step> Steps, Report Report)>(StringComparer.Ordinal);
        var runs = new Dictionary<string, List<Run>>(StringComparer.Ordinal);
        // Per step (method and path) only the newest run's verdict counts, like per endpoint.
        var unmatched = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var report in index.Reports.OrderBy(r => r.StartedAt))
        {
            var prefix = Uri.TryCreate(report.Target.BaseUrl, UriKind.Absolute, out var u) ? Segments(u.AbsolutePath) : [];
            foreach (var step in report.Steps)
            {
                var path = Segments(step.Path);
                List<string[]> tries = [path];
                if (prefix.Length > 0 && path.Length > prefix.Length && path.Take(prefix.Length).SequenceEqual(prefix, StringComparer.OrdinalIgnoreCase))
                    tries.Add(path[prefix.Length..]);
                var scored = endpoints.Where(e => string.Equals(e.Tags![1], step.Method, StringComparison.OrdinalIgnoreCase))
                    .Select(e => (Endpoint: e, Score: tries.Select(t => Score(Segments(e.Route ?? ""), t)).Max()))
                    .Where(x => x.Score >= 0).OrderByDescending(x => x.Score).ToList();
                var key = $"{step.Method.ToUpperInvariant()} {step.Path}";
                var label = $"{key} ({report.File})";
                unmatched.Remove(key);
                ambiguous.Remove(key);
                if (scored.Count == 0) unmatched[key] = label;
                else if (scored.Count > 1 && scored[1].Score == scored[0].Score)
                    ambiguous[key] = $"{label}: {string.Join(", ", scored.TakeWhile(x => x.Score == scored[0].Score).Select(x => Naming.EndpointLabel(x.Endpoint)).Order(StringComparer.Ordinal))}";
                // Reports come oldest first: a newer run replaces the steps, another step of the same run joins them.
                else if (newest.TryGetValue(scored[0].Endpoint.Id, out var seen) && ReferenceEquals(seen.Report, report)) seen.Steps.Add(step);
                else
                {
                    newest[scored[0].Endpoint.Id] = ([step], report);
                    // The same list as the newest entry's, so the run's later steps join both.
                    (runs.TryGetValue(scored[0].Endpoint.Id, out var list) ? list : runs[scored[0].Endpoint.Id] = []).Add(new(report, newest[scored[0].Endpoint.Id].Steps));
                }
            }
        }
        var byId = endpoints.ToDictionary(e => e.Id, StringComparer.Ordinal);
        return new([.. newest.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new Measured(x.Key, x.Value.Steps, x.Value.Report, changed(byId[x.Key]) is { } at && at > x.Value.Report.StartedAt,
                    [.. runs[x.Key].Where(r => r.Report.Target == x.Value.Report.Target)]))],
            [.. unmatched.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value)],
            [.. ambiguous.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value)]);
    }

    /// <summary>
    /// A run's series for a chart: at most <paramref name="max"/> buckets of equal length, each with its end second, the
    /// requests per second over it and its highest p95 (the worst second, not an average that would hide a spike).
    /// </summary>
    public static List<(double T, double Rps, double P95)> Buckets(List<Point> series, int max = 60)
    {
        var points = series.OrderBy(p => p.T).ToList();
        if (points.Count == 0) return [];
        var size = (int)Math.Ceiling(points.Count / (double)max);
        return [.. points.Chunk(size).Select(c => (c[^1].T, c.Sum(p => p.Requests) / Math.Max(c[^1].T - c[0].T + 1, 1), c.Max(p => p.P95Ms)))];
    }

    static string[] Segments(string path) => path.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);

    static bool Parameter(string segment) => segment is ['{', .., '}'] or [':', ..] or ['[', .., ']'];

    // Literal segments matched, or -1 when the route doesn't fit the path.
    static int Score(string[] route, string[] path)
    {
        if (route.Length != path.Length) return -1;
        var literal = 0;
        for (var i = 0; i < route.Length; i++)
        {
            if (Parameter(route[i])) continue;
            if (!string.Equals(route[i], path[i], StringComparison.OrdinalIgnoreCase)) return -1;
            literal++;
        }
        return literal;
    }
}
