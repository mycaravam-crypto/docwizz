using System.Text.Json;

// Measured load-test results from powerwizz (`powerwizz.report/v1`), read natively like gitwizz.history/v2: offline,
// deterministic, no adapter. `load import` keeps the reports in the index; generate attaches each step to an endpoint.
static class LoadReports
{
    public const string ReportSchema = "powerwizz.report/v1";
    public const string Schema = "docwizz.load-index/v1";
    public const string DefaultFile = "docs/.docwizz/load.json";

    public record Latency(double P50, double P95, double P99);
    public record Step(string Name, string Method, string Path, long Requests, long Failures, double ErrorRate, Latency LatencyMs);
    public record Target(string BaseUrl, bool Local);
    public record RunConfig(int Users, double RampUpS, double DurationS, string? Auth);
    // File: the report's file name, kept as evidence.
    public record Report(string Schema, DateTimeOffset StartedAt, Target Target, RunConfig? Config, List<Step> Steps, string? File = null);
    public record Index(string Schema, List<Report> Reports);

    // Stale: the endpoint's code changed after the run, so the numbers may no longer hold.
    public record Measured(string Endpoint, Step Step, Report Report, bool Stale);
    public record Attached(List<Measured> Endpoints, List<string> Unmatched, List<string> Ambiguous);

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    /// <summary>Reads one powerwizz report. Throws ArgumentException naming the file when it isn't powerwizz.report/v1.</summary>
    public static Report Read(string file)
    {
        Report? report;
        try
        {
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
            var schema = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("schema", out var s) ? s.ToString() : null;
            if (schema != ReportSchema)
                throw new ArgumentException($"{file}: not {ReportSchema} (schema: {schema ?? "missing"})");
            report = doc.RootElement.Deserialize<Report>(Json);
        }
        catch (JsonException e) { throw new ArgumentException($"{file}: not valid JSON ({e.Message})"); }
        if (report is not { Target.BaseUrl: not null, Steps: not null } || report.Steps.Any(x => x is not { Method: not null, Path: not null, LatencyMs: not null }))
            throw new ArgumentException($"{file}: {ReportSchema} without target.base_url, steps or their method, path and latency_ms");
        return report with { File = Path.GetFileName(file) };
    }

    public static Index Load(string file) => JsonSerializer.Deserialize<Index>(System.IO.File.ReadAllText(file), Json) is { Schema: Schema } i ? i
        : throw new ArgumentException($"{file}: not a {Schema} index; run docwizz load import again");

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
        var newest = new Dictionary<string, (Step Step, Report Report)>(StringComparer.Ordinal);
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
                else newest[scored[0].Endpoint.Id] = (step, report);
            }
        }
        var byId = endpoints.ToDictionary(e => e.Id, StringComparer.Ordinal);
        return new([.. newest.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new Measured(x.Key, x.Value.Step, x.Value.Report, changed(byId[x.Key]) is { } at && at > x.Value.Report.StartedAt))],
            [.. unmatched.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value)],
            [.. ambiguous.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value)]);
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
