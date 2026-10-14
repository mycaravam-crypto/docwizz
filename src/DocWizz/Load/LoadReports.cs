using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

// `docwizz load import`: load-test results as measured evidence for the endpoints in api.md. The input is what powerwizz
// exports (`powerwizz.report/v1`, the producer's contract is the powerwizz README, "JSON report for docwizz"), read
// natively like `gitwizz.history/v2`: no adapter, no network. The index keeps every imported run (one per report file);
// matching steps to endpoints happens at generate time against the current model, so a renamed route stops matching
// instead of keeping stale numbers. Only the aggregated report is read: no per-request CSVs, no series.
static class LoadReports
{
    public const string Schema = "docwizz.load-index/v1";
    public const string ReportSchema = "powerwizz.report/v1";
    public const string DefaultFile = "docs/.docwizz/load.json";
    // powerwizz's latencies come from an HDR histogram with 2.5 % relative precision.
    public const string Precision = "±2.5 %";

    public record Index(string Schema, List<Report> Reports);

    // One run. File is the report's file name (never its path, so the index is the same anywhere); Digest tells two runs
    // exported under the same name apart and makes a re-import a no-op.
    public record Report(string File, string Digest, string StartedAt, string? Generator, double? RuntimeS, Target Target,
        RunConfig Config, List<Step> Steps);
    public record Target(string BaseUrl, bool Local);
    public record RunConfig(int? Users, double? RampUpS, double? DurationS, string? Auth);
    // Path is the step's template as powerwizz reports it: absolute, no query, placeholders literal (`/users/{{vu_id}}`).
    public record Step(string? Name, string Method, string Path, long Requests, long? Failures, double ErrorRate, Latency LatencyMs);
    public record Latency(double P50, double P95, double P99);

    static readonly JsonSerializerOptions Read = new(JsonSerializerDefaults.Web);
    static readonly JsonSerializerOptions Write = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---- input ----

    // One exported report, checked. Fields v1 may add later are ignored; a missing or mistyped field v1 has is an error
    // naming it, never a guess (a missing error rate must not read as 0 %).
    public static Report Parse(byte[] bytes, string file)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(bytes).RootElement.Clone(); }
        catch (JsonException e) { throw new ArgumentException($"{file}: not JSON ({e.Message})"); }
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException($"{file}: not {ReportSchema} (not a JSON object)");
        var schema = root.TryGetProperty("schema", out var s) ? s.ValueKind == JsonValueKind.String ? s.GetString() : s.ToString() : null;
        if (schema != ReportSchema) throw new ArgumentException($"{file}: not {ReportSchema} (schema: {schema ?? "missing"})");

        string Where(string path) => $"{file}: {path}";
        var started = Str(root, "started_at", Where) ?? throw Missing(Where("started_at"), "a timestamp");
        if (!DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            throw new ArgumentException($"{Where("started_at")}: not a timestamp ({started})");
        var target = Obj(root, "target", Where) ?? throw Missing(Where("target"), "an object");
        var baseUrl = Str(target, "base_url", p => Where($"target.{p}")) ?? throw Missing(Where("target.base_url"), "a URL");
        var local = Bool(target, "local", p => Where($"target.{p}")) ?? throw Missing(Where("target.local"), "true or false");
        var config = Obj(root, "config", Where);
        RunConfig run = config is { } c
            ? new((int?)Num(c, "users", p => Where($"config.{p}")), Num(c, "ramp_up_s", p => Where($"config.{p}")),
                Num(c, "duration_s", p => Where($"config.{p}")), Str(c, "auth", p => Where($"config.{p}")))
            : new(null, null, null, null);
        if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            throw Missing(Where("steps"), "an array");
        var list = new List<Step>();
        var i = 0;
        foreach (var step in steps.EnumerateArray())
        {
            var at = $"steps[{i++}]";
            if (step.ValueKind != JsonValueKind.Object) throw Missing(Where(at), "an object");
            string W(string p) => Where($"{at}.{p}");
            var method = Str(step, "method", W) ?? throw Missing(W("method"), "an HTTP method");
            var path = Str(step, "path", W) ?? throw Missing(W("path"), "a path");
            var requests = Num(step, "requests", W) ?? throw Missing(W("requests"), "a number");
            var rate = Num(step, "error_rate", W) ?? throw Missing(W("error_rate"), "a number");
            var latency = Obj(step, "latency_ms", W) ?? throw Missing(W("latency_ms"), "an object");
            double P(string q) => Num(latency, q, x => W($"latency_ms.{x}")) ?? throw Missing(W($"latency_ms.{q}"), "a number");
            list.Add(new(Str(step, "name", W), method.ToUpperInvariant(), path, (long)requests, (long?)Num(step, "failures", W), rate,
                new(P("p50"), P("p95"), P("p99"))));
        }
        return new(file, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), started, Str(root, "generator", Where),
            Num(root, "runtime_s", Where), new(baseUrl, local), run, list);
    }

    static ArgumentException Missing(string where, string what) => new($"{where}: missing or not {what}");

    static JsonElement? Field(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    static string? Str(JsonElement e, string name, Func<string, string> where) => Field(e, name) is not { } v ? null
        : v.ValueKind == JsonValueKind.String ? v.GetString() : throw Missing(where(name), "a string");

    static double? Num(JsonElement e, string name, Func<string, string> where) => Field(e, name) is not { } v ? null
        : v.ValueKind == JsonValueKind.Number ? v.GetDouble() : throw Missing(where(name), "a number");

    static bool? Bool(JsonElement e, string name, Func<string, string> where) => Field(e, name) is not { } v ? null
        : v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw Missing(where(name), "true or false");

    static JsonElement? Obj(JsonElement e, string name, Func<string, string> where) => Field(e, name) is not { } v ? null
        : v.ValueKind == JsonValueKind.Object ? v : throw Missing(where(name), "an object");

    // ---- index ----

    // The previous runs plus the new ones, oldest first; a report already imported (same bytes) is not added twice.
    public static (Index Index, int Added) Merge(Index? previous, IEnumerable<Report> reports)
    {
        var all = new List<Report>(previous?.Reports ?? []);
        var added = 0;
        foreach (var r in reports)
            if (all.All(x => x.Digest != r.Digest)) { all.Add(r); added++; }
        return (new(Schema, [.. all.OrderBy(r => Started(r)).ThenBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Digest, StringComparer.Ordinal)]), added);
    }

    public static DateTimeOffset Started(Report r) =>
        DateTimeOffset.Parse(r.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public static string Serialize(Index index) => JsonSerializer.Serialize(index, Write).Replace("\r\n", "\n") + "\n";

    public static Index Load(string file)
    {
        JsonElement raw;
        try { raw = JsonDocument.Parse(File.ReadAllBytes(file)).RootElement.Clone(); }
        catch (JsonException e) { throw new ArgumentException($"{file}: not JSON ({e.Message})"); }
        var schema = raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("schema", out var s) ? s.ToString() : null;
        if (schema != Schema) throw new ArgumentException($"{file}: unknown schema {schema ?? "(missing)"}; expected {Schema} (delete it and run docwizz load import again)");
        return raw.Deserialize<Index>(Read) ?? throw new ArgumentException($"{file}: empty index");
    }

    // ---- matching ----

    // Matched: exactly one endpoint has the most equal literal segments. Ambiguous: several tie (none is attached).
    // Unmatched: no endpoint with that method has the step's shape.
    public record Match(Report Report, Step Step, string Outcome, List<Node> Endpoints);

    // A step against the detected endpoints, segment by segment: a route parameter (`{id}`, `{id:int}`, `:id`, `[id]`)
    // takes any one segment, a `{{placeholder}}` included; literal segments must be equal, ignoring case. The base URL's
    // path (`/app/` in http://host/app/) may precede the route: it is stripped when the whole path matches nothing.
    public static Match MatchStep(Report report, Step step, IReadOnlyList<Node> endpoints)
    {
        var path = Segments(step.Path.Split('?', '#')[0]);
        var candidates = endpoints.Where(e => e.Tags is [_, var verb, ..] && string.Equals(verb, step.Method, StringComparison.OrdinalIgnoreCase)).ToList();
        var best = Best(path, candidates);
        if (best.Count == 0 && Prefix(report.Target.BaseUrl) is { Length: > 0 } prefix && path.Length > prefix.Length
            && prefix.Select((p, i) => string.Equals(p, path[i], StringComparison.OrdinalIgnoreCase)).All(x => x))
            best = Best(path[prefix.Length..], candidates);
        return new(report, step, best.Count switch { 0 => "unmatched", 1 => "matched", _ => "ambiguous" }, best);
    }

    static List<Node> Best(string[] path, List<Node> candidates)
    {
        var scored = candidates.Select(e => (e, score: Score(path, Segments(e.Route ?? "")))).Where(x => x.score is not null).ToList();
        if (scored.Count == 0) return [];
        var top = scored.Max(x => x.score);
        return [.. scored.Where(x => x.score == top).Select(x => x.e).OrderBy(e => e.Id, StringComparer.Ordinal)];
    }

    // The number of literal segments that matched, or null when the shapes differ.
    static int? Score(string[] path, string[] route)
    {
        if (path.Length != route.Length) return null;
        var literal = 0;
        for (var i = 0; i < path.Length; i++)
        {
            if (IsParameter(route[i])) continue;
            if (!string.Equals(route[i], path[i], StringComparison.OrdinalIgnoreCase)) return null;
            literal++;
        }
        return literal;
    }

    static bool IsParameter(string segment) =>
        (segment.Length > 2 && segment[0] == '{' && segment[^1] == '}' && !segment.StartsWith("{{"))
        || (segment.Length > 1 && segment[0] == ':')
        || (segment.Length > 2 && segment[0] == '[' && segment[^1] == ']');

    static string[] Segments(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    static string[] Prefix(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? Segments(Uri.UnescapeDataString(uri.AbsolutePath)) : [];

    // What api.md shows: per endpoint the steps of its newest run (older runs are not shown), and the steps that
    // matched no endpoint or several, each (method, path) once, from the newest report that has it.
    public record Attachment(Dictionary<string, (Report Report, List<Step> Steps)> ByEndpoint, List<Match> Ambiguous, List<Match> Unmatched)
    {
        public IEnumerable<Report> Shown => ByEndpoint.Values.Select(v => v.Report).Distinct();
    }

    public static Attachment Attach(Index index, IReadOnlyList<Node> endpoints)
    {
        var matches = index.Reports.SelectMany(r => r.Steps.Select(s => MatchStep(r, s, endpoints))).ToList();
        var byEndpoint = new Dictionary<string, (Report, List<Step>)>();
        foreach (var g in matches.Where(m => m.Outcome == "matched").GroupBy(m => m.Endpoints[0].Id))
        {
            // Reports are oldest first; the last one with this endpoint is the newest.
            var newest = g.Last().Report;
            byEndpoint[g.Key] = (newest, [.. g.Where(m => m.Report == newest).Select(m => m.Step)]);
        }
        List<Match> Distinct(string outcome) => [.. matches.Where(m => m.Outcome == outcome)
            .GroupBy(m => (m.Step.Method, m.Step.Path)).Select(g => g.Last())
            .OrderBy(m => m.Step.Path, StringComparer.Ordinal).ThenBy(m => m.Step.Method, StringComparer.Ordinal)];
        return new(byEndpoint, Distinct("ambiguous"), Distinct("unmatched"));
    }
}
