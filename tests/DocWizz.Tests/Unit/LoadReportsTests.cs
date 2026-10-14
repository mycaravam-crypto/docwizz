using System.Text;

namespace DocWizz.Tests.Unit;

// `docwizz load import` (#185): powerwizz.report/v1 read as it is, and each step matched to a detected endpoint by
// method and path, segment by segment. No scan, no files: endpoints are built in memory.
public class LoadReportsTests
{
    static Node Endpoint(string verb, string route, string id) => new(id, "endpoint", $"{verb} {route}", "Api/C.cs", 1, Tags: ["endpoint", verb], Route: route);

    static readonly List<Node> Endpoints =
    [
        Endpoint("GET", "api/items", "items"),
        Endpoint("GET", "api/items/{id}", "item"),
        Endpoint("GET", "api/items/special", "special"),
        Endpoint("PUT", "/api/items/{id:int}", "put"),
        Endpoint("GET", "/users/:userId/orders/[orderId]", "orders"),
        Endpoint("DELETE", "api/{area}/cache", "area-cache"),
        Endpoint("DELETE", "api/{tenant}/cache", "tenant-cache"),
    ];

    static string Report(string steps = """[{ "name": "s", "method": "GET", "path": "/api/items", "requests": 10, "failures": 1, "error_rate": 0.1, "latency_ms": { "mean": 2, "p50": 1.5, "p95": 3.25, "p99": 4 } }]""",
        string startedAt = "2026-10-13T18:39:16Z", string baseUrl = "http://127.0.0.1:18080/", string schema = "\"schema\": \"powerwizz.report/v1\",") => $$"""
        {
          {{schema}}
          "generator": "powerwizz 0.1.0",
          "started_at": "{{startedAt}}",
          "runtime_s": 60.0,
          "target": { "base_url": "{{baseUrl}}", "local": true },
          "config": { "users": 50, "ramp_up_s": 10, "duration_s": 60, "request_limit": null, "timeout_s": 10, "auth": "PerUser", "scenarios": ["browse"] },
          "totals": { "requests": 10 },
          "steps": {{steps}},
          "series": [ { "t": 1, "requests": 9, "failures": 0, "p50_ms": 2.0, "p95_ms": 4.0, "p99_ms": 4.0 } ]
        }
        """;

    static LoadReports.Report Parse(string json, string file = "run.json") => LoadReports.Parse(Encoding.UTF8.GetBytes(json), file);

    static LoadReports.Match Match(string method, string path, string baseUrl = "http://127.0.0.1:18080/")
    {
        var report = Parse(Report(baseUrl: baseUrl));
        return LoadReports.MatchStep(report, report.Steps[0] with { Method = method, Path = path }, Endpoints);
    }

    static string? Matched(string method, string path, string baseUrl = "http://127.0.0.1:18080/") =>
        Match(method, path, baseUrl) is { Outcome: "matched" } m ? m.Endpoints[0].Id : null;

    [Fact]
    public void Reads_the_report_and_ignores_fields_v1_may_add()
    {
        var r = Parse(Report().Replace("\"generator\"", "\"added_in_v1_1\": { \"x\": 1 }, \"generator\""));
        Assert.Equal("2026-10-13T18:39:16Z", r.StartedAt);
        Assert.Equal(new LoadReports.Target("http://127.0.0.1:18080/", true), r.Target);
        Assert.Equal(new LoadReports.RunConfig(50, 10, 60, "PerUser"), r.Config);
        var s = Assert.Single(r.Steps);
        Assert.Equal(("GET", "/api/items", 10L, 1L, 0.1), (s.Method, s.Path, s.Requests, s.Failures, s.ErrorRate));
        Assert.Equal(new LoadReports.Latency(1.5, 3.25, 4), s.LatencyMs);
    }

    [Theory]
    [InlineData("\"schema\": \"powerwizz.report/v2\",", "run.json: not powerwizz.report/v1 (schema: powerwizz.report/v2)")]
    [InlineData("\"schema\": \"gitwizz.history/v2\",", "run.json: not powerwizz.report/v1 (schema: gitwizz.history/v2)")]
    [InlineData("", "run.json: not powerwizz.report/v1 (schema: missing)")]
    public void A_wrong_or_missing_schema_is_rejected_with_the_reason(string schema, string message) =>
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => Parse(Report(schema: schema))).Message);

    [Fact]
    public void A_missing_measurement_is_an_error_not_a_zero()
    {
        var e = Assert.Throws<ArgumentException>(() => Parse(Report(steps: """[{ "method": "GET", "path": "/x", "requests": 1, "latency_ms": { "p50": 1, "p95": 1, "p99": 1 } }]""")));
        Assert.Equal("run.json: steps[0].error_rate: missing or not a number", e.Message);
        Assert.Contains("not JSON", Assert.Throws<ArgumentException>(() => Parse("{")).Message);
    }

    [Theory]
    [InlineData("GET", "/api/items/{{vu_id}}", "item")]       // a placeholder fills a route parameter
    [InlineData("GET", "/api/items/42", "item")]              // so does a concrete id
    [InlineData("GET", "/API/Items/42", "item")]              // literals ignore case
    [InlineData("GET", "/api/items/special", "special")]      // the longest literal match beats the parameter
    [InlineData("GET", "/api/items", "items")]
    [InlineData("PUT", "/api/items/{{vu_id}}", "put")]        // a constrained parameter
    [InlineData("GET", "/users/{{vu_id}}/orders/7", "orders")] // :param and [param]
    [InlineData("GET", "/api/items/42?verbose=1", "item")]    // a query string is not part of the path
    public void A_step_matches_its_endpoint(string method, string path, string endpoint) =>
        Assert.Equal(endpoint, Matched(method, path));

    [Theory]
    [InlineData("POST", "/api/items")]                        // the method has to match too
    [InlineData("DELETE", "/api/items/1")]
    [InlineData("GET", "/api/items/1/extra")]                 // the shape has to match
    [InlineData("GET", "/api/{{vu_id}}")]                     // a placeholder doesn't stand in for a literal
    [InlineData("GET", "/other")]
    public void A_step_without_its_endpoint_is_unmatched(string method, string path)
    {
        var m = Match(method, path);
        Assert.Equal("unmatched", m.Outcome);
        Assert.Empty(m.Endpoints);
    }

    [Fact]
    public void A_tie_is_ambiguous_and_names_the_candidates()
    {
        var m = Match("DELETE", "/api/eu/cache");
        Assert.Equal("ambiguous", m.Outcome);
        Assert.Equal(["area-cache", "tenant-cache"], m.Endpoints.Select(e => e.Id));
    }

    [Theory]
    [InlineData("http://shop.test/app/", "/app/api/items/3", "item")]   // the base path precedes the route
    [InlineData("http://shop.test/App", "/app/api/items", "items")]
    [InlineData("http://shop.test/app/", "/api/items/3", "item")]       // a step that doesn't carry it still matches
    [InlineData("http://shop.test/", "/app/api/items/3", null)]          // without a base path nothing is stripped
    [InlineData("http://shop.test/app/", "/other/api/items/3", null)]    // only the base path is stripped
    public void The_base_url_path_is_stripped_when_the_rest_matches(string baseUrl, string path, string? endpoint) =>
        Assert.Equal(endpoint, Matched("GET", path, baseUrl));

    [Fact]
    public void The_newest_run_per_endpoint_is_shown_and_unmatched_steps_are_listed()
    {
        const string Item = """{ "name": "detail", "method": "GET", "path": "/api/items/{{vu_id}}", "requests": REQ, "error_rate": 0.0, "latency_ms": { "p50": 1, "p95": 2, "p99": 3 } }""";
        const string List = """{ "name": "list", "method": "GET", "path": "/api/items", "requests": 5, "error_rate": 0.0, "latency_ms": { "p50": 1, "p95": 2, "p99": 3 } }""";
        const string Lost = """{ "name": "lost", "method": "GET", "path": "/nowhere", "requests": 1, "error_rate": 1.0, "latency_ms": { "p50": 1, "p95": 2, "p99": 3 } }""";
        var older = Parse(Report($"[{Item.Replace("REQ", "100")}, {List}, {Lost}]", startedAt: "2026-10-01T08:00:00Z"), "old.json");
        var newer = Parse(Report($"[{Item.Replace("REQ", "200")}, {Lost}]", startedAt: "2026-10-12T08:00:00+02:00"), "new.json");
        var (index, added) = LoadReports.Merge(null, [newer, older]);
        Assert.Equal(2, added);
        Assert.Equal(["old.json", "new.json"], index.Reports.Select(r => r.File));   // oldest first, whatever the import order

        var a = LoadReports.Attach(index, Endpoints);
        Assert.Equal("new.json", a.ByEndpoint["item"].Report.File);
        Assert.Equal(200, Assert.Single(a.ByEndpoint["item"].Steps).Requests);
        Assert.Equal("old.json", a.ByEndpoint["items"].Report.File);              // only the older run exercised it
        var lost = Assert.Single(a.Unmatched);                                     // listed once, from the newest report
        Assert.Equal(("/nowhere", "new.json"), (lost.Step.Path, lost.Report.File));
        Assert.Empty(a.Ambiguous);
    }

    [Fact]
    public void Importing_the_same_report_again_adds_nothing_and_the_index_round_trips()
    {
        var (index, _) = LoadReports.Merge(null, [Parse(Report())]);
        var (again, added) = LoadReports.Merge(index, [Parse(Report(), "copy.json")]);
        Assert.Equal(0, added);
        Assert.Single(again.Reports);

        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, LoadReports.Serialize(index));
            Assert.Equal(LoadReports.Serialize(index), LoadReports.Serialize(LoadReports.Load(file)));
            File.WriteAllText(file, "{\"schema\":\"other\"}");
            Assert.Contains("unknown schema other", Assert.Throws<ArgumentException>(() => LoadReports.Load(file)).Message);
        }
        finally { File.Delete(file); }
    }
}
