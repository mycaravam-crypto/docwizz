using static DocWizz.Tests.Models;

namespace DocWizz.Tests.Unit;

public class LoadReportsTests
{
    static Node Endpoint(string verb, string route, string file = "src/Api.cs") =>
        N($"cs:Api.{verb}_{route.Trim('/').Replace('/', '_')}", file, kind: "method", tags: ["endpoint", verb], route: route);

    static LoadReports.Step Step(string method, string path, long requests = 10) =>
        new("s", method, path, requests, 0, 0, new(1, 2, 3));

    static LoadReports.Report Report(string startedAt, string baseUrl, params LoadReports.Step[] steps) =>
        new(LoadReports.ReportSchema, DateTimeOffset.Parse(startedAt), new(baseUrl, true), new(5, 1, 10, "None"), [.. steps], "r.json");

    static LoadReports.Attached Attach(CodeModel model, params LoadReports.Report[] reports) =>
        LoadReports.Attach(model, LoadReports.Merge(null, reports), _ => null);

    [Fact]
    public void Steps_match_endpoints_by_method_and_segment()
    {
        var model = Model([Endpoint("GET", "api/items/{id}"), Endpoint("GET", "api/items/special"), Endpoint("PUT", "api/stock/:sku"),
            Endpoint("GET", "/health")]);
        var got = Attach(model, Report("2026-10-13T18:00:00Z", "http://shop:8080/app/",
            Step("GET", "/api/items/{{vu_id}}"),   // placeholder vs route parameter
            Step("get", "/api/items/special"),     // the literal route beats the parameter
            Step("PUT", "/app/api/stock/42"),      // base path prefix stripped, concrete id
            Step("POST", "/api/items/1"),          // method mismatch
            Step("GET", "/api/nothing")));

        Assert.Equal(["cs:Api.GET_api_items_special", "cs:Api.GET_api_items_{id}", "cs:Api.PUT_api_stock_:sku"], got.Endpoints.Select(m => m.Endpoint));
        Assert.Equal("/api/items/special", got.Endpoints[0].Step.Path);
        Assert.Equal(["GET /api/nothing (r.json)", "POST /api/items/1 (r.json)"], got.Unmatched);
        Assert.Empty(got.Ambiguous);
    }

    [Fact]
    public void A_tie_is_ambiguous_and_not_attached()
    {
        var got = Attach(Model([Endpoint("GET", "api/items/{id}"), Endpoint("GET", "api/items/[name]", "src/Other.cs")]),
            Report("2026-10-13T18:00:00Z", "http://shop/", Step("GET", "/api/items/7")));
        Assert.Empty(got.Endpoints);
        Assert.Single(got.Ambiguous);
        Assert.StartsWith("GET /api/items/7 (r.json): ", got.Ambiguous[0]);
    }

    [Fact]
    public void The_newest_run_wins_and_a_run_before_the_last_code_change_is_stale()
    {
        var model = Model([Endpoint("GET", "api/items/{id}")]);
        var index = LoadReports.Merge(null, [Report("2026-10-14T08:00:00Z", "http://shop/", Step("GET", "/api/items/1", 200)),
            Report("2026-10-13T08:00:00Z", "http://shop/", Step("GET", "/api/items/1", 100))]);
        var fresh = LoadReports.Attach(model, index, _ => DateTimeOffset.Parse("2026-10-14T07:00:00Z")).Endpoints.Single();
        Assert.Equal(200, fresh.Step.Requests);
        Assert.False(fresh.Stale);
        Assert.True(LoadReports.Attach(model, index, _ => DateTimeOffset.Parse("2026-10-14T09:00:00Z")).Endpoints.Single().Stale);
        // The same run imported again replaces itself.
        Assert.Equal(2, LoadReports.Merge(index, [Report("2026-10-14T08:00:00Z", "http://shop/")]).Reports.Count);
    }

    [Fact]
    public void Read_rejects_a_wrong_or_missing_schema_and_ignores_unknown_fields()
    {
        var dir = Directory.CreateTempSubdirectory("docwizz-load-").FullName;
        try
        {
            string Write(string name, string json) { var f = Path.Combine(dir, name); File.WriteAllText(f, json); return f; }
            var wrong = Assert.Throws<ArgumentException>(() => LoadReports.Read(Write("a.json", """{"schema":"powerwizz.report/v2"}""")));
            Assert.Contains("not powerwizz.report/v1 (schema: powerwizz.report/v2)", wrong.Message);
            var missing = Assert.Throws<ArgumentException>(() => LoadReports.Read(Write("b.json", """{"steps":[]}""")));
            Assert.Contains("(schema: missing)", missing.Message);
            var report = LoadReports.Read(Write("c.json", """
                {"schema":"powerwizz.report/v1","started_at":"2026-10-13T18:39:16Z","future":{"x":1},
                 "target":{"base_url":"http://127.0.0.1:18080/","local":true},
                 "config":{"users":50,"ramp_up_s":10,"duration_s":60,"auth":"PerUser"},
                 "steps":[{"name":"browse/detail","method":"GET","path":"/api/items/{{vu_id}}","requests":898,"failures":0,
                           "error_rate":0.0,"latency_ms":{"mean":24.2,"p50":24.198,"p95":39.417,"p99":41.388}}]}
                """));
            Assert.Equal("c.json", report.File);
            Assert.Equal(50, report.Config!.Users);
            Assert.Equal(39.417, report.Steps.Single().LatencyMs.P95);
        }
        finally { Directory.Delete(dir, true); }
    }
}
