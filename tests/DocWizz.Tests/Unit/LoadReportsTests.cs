using static DocWizz.Tests.Models;

namespace DocWizz.Tests.Unit;

public class LoadReportsTests
{
    static Node Endpoint(string verb, string route, string file = "src/Api.cs") =>
        N($"cs:Api.{verb}_{route.Trim('/').Replace('/', '_')}", file, kind: "method", tags: ["endpoint", verb], route: route);

    static LoadReports.Step Step(string method, string path, long requests = 10, string name = "s") =>
        new(name, method, path, requests, 0, 0, new(1, 2, 3));

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
        Assert.Equal("/api/items/special", Assert.Single(got.Endpoints[0].Steps).Path);
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
        Assert.Equal(200, Assert.Single(fresh.Steps).Requests);
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
                {"schema":"powerwizz.report/v1","started_at":"2026-10-13T18:39:16Z","future":{"x":1},"label":"dev, Debug",
                 "target":{"base_url":"http://127.0.0.1:18080/","local":true},
                 "config":{"users":50,"ramp_up_s":10,"duration_s":60,"auth":"PerUser"},
                 "steps":[{"name":"browse/detail","method":"GET","path":"/api/items/{{vu_id}}","requests":898,"failures":0,
                           "error_rate":0.0,"latency_ms":{"mean":24.2,"p50":24.198,"p95":39.417,"p99":41.388}}]}
                """));
            Assert.Equal("c.json", report.File);
            Assert.Equal(50, report.Config!.Users);
            Assert.Equal("dev, Debug", report.Label);
            var odd = Assert.Throws<ArgumentException>(() => LoadReports.Read(Write("d.json", File.ReadAllText(Path.Combine(dir, "c.json")).Replace("\"dev, Debug\"", "42"))));
            Assert.Contains("label not text", odd.Message);
            Assert.Equal(39.417, report.Steps.Single().LatencyMs.P95);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("api/items/{id:int}", "/api/items/{{vu_id}}")]   // a constrained parameter
    [InlineData("api/items/{id}", "/API/Items/42")]               // literals ignore case
    [InlineData("api/items/{id}", "/api/items/42?verbose=1")]     // the query string is not part of the path
    [InlineData("users/:userId/orders/[orderId]", "/users/{{vu_id}}/orders/7")]
    public void More_route_shapes_match(string route, string path)
    {
        var got = Attach(Model([Endpoint("GET", route)]), Report("2026-10-13T18:00:00Z", "http://shop/", Step("GET", path)));
        Assert.Single(got.Endpoints);
        Assert.Empty(got.Unmatched);
    }

    [Theory]
    [InlineData("/api/{{vu_id}}")]          // a placeholder doesn't stand in for a literal
    [InlineData("/api/items/1/extra")]      // the shape has to match
    [InlineData("/other/api/items/1")]      // only the base URL's path is stripped
    public void A_step_of_another_shape_is_unmatched(string path)
    {
        var got = Attach(Model([Endpoint("GET", "api/items/{id}"), Endpoint("GET", "api/items")]),
            Report("2026-10-13T18:00:00Z", "http://shop/app/", Step("GET", path)));
        Assert.Empty(got.Endpoints);
        Assert.Single(got.Unmatched);
    }

    [Fact]
    public void Every_step_of_the_newest_run_on_an_endpoint_is_kept()
    {
        var model = Model([Endpoint("GET", "api/items/{id}")]);
        var got = Attach(model,
            Report("2026-10-13T08:00:00Z", "http://shop/", Step("GET", "/api/items/1", 1, "old")),
            Report("2026-10-14T08:00:00Z", "http://shop/", Step("GET", "/api/items/{{vu_id}}", 200, "browse/detail"), Step("GET", "/api/items/7", 30, "order/check")));
        Assert.Equal(["browse/detail", "order/check"], got.Endpoints.Single().Steps.Select(x => x.Name));
    }

    [Fact]
    public void A_missing_or_mistyped_measurement_is_an_error_naming_the_field_not_a_zero()
    {
        var dir = Directory.CreateTempSubdirectory("docwizz-load-").FullName;
        try
        {
            string Write(string json) { var f = Path.Combine(dir, "r.json"); File.WriteAllText(f, json); return f; }
            string Error(string steps, string started = "\"started_at\":\"2026-10-13T18:39:16Z\",") =>
                Assert.Throws<ArgumentException>(() => LoadReports.Read(Write($$"""
                    {"schema":"powerwizz.report/v1",{{started}}"target":{"base_url":"http://shop/","local":true},"steps":[{{steps}}]}
                    """))).Message;
            const string Latency = "\"latency_ms\":{\"p50\":1,\"p95\":2,\"p99\":3}";
            Assert.EndsWith("steps[0].error_rate missing or not a number",
                Error($$"""{"method":"GET","path":"/x","requests":1,{{Latency}}}"""));
            Assert.EndsWith("steps[0].latency_ms.p95 missing or not a number",
                Error("""{"method":"GET","path":"/x","requests":1,"error_rate":0,"latency_ms":{"p50":1,"p99":3}}"""));
            Assert.EndsWith("steps[0].requests missing or not a number",
                Error($$"""{"method":"GET","path":"/x","requests":"many","error_rate":0,{{Latency}}}"""));
            Assert.EndsWith("started_at missing or not a timestamp", Error("", started: ""));
            Assert.EndsWith("started_at missing or not a timestamp", Error("", started: "\"started_at\":\"yesterday\","));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_broken_index_is_an_error_not_a_crash()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "{ not json");
            Assert.Contains("delete it and run docwizz load import again", Assert.Throws<ArgumentException>(() => LoadReports.Load(file)).Message);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_trend_has_every_run_against_the_newest_runs_target_oldest_first()
    {
        var model = Model([Endpoint("GET", "api/items/{id}")]);
        var got = Attach(model,
            Report("2026-10-01T08:00:00Z", "http://shop/", Step("GET", "/api/items/1", 1)),
            Report("2026-10-05T08:00:00Z", "https://staging/", Step("GET", "/api/items/1", 2)),   // another target: not comparable
            Report("2026-10-09T08:00:00Z", "http://shop/", Step("GET", "/api/items/1", 3), Step("GET", "/api/items/2", 4, "s2")));
        var m = got.Endpoints.Single();
        Assert.Equal([1L, 3L], m.Runs.Select(r => r.Steps[0].Requests));
        Assert.Equal(2, m.Runs[^1].Steps.Count);   // the newest run's steps, all of them
        Assert.Same(m.Report, m.Runs[^1].Report);
    }

    [Fact]
    public void Buckets_keep_the_worst_p95_and_average_the_requests_per_second()
    {
        List<LoadReports.Point> series = [.. Enumerable.Range(1, 120).Select(t => new LoadReports.Point(t, 10, t == 50 ? 900 : 20))];
        var b = LoadReports.Buckets(series);
        Assert.Equal(60, b.Count);
        Assert.Equal((50.0, 10.0, 900.0), b[24]);           // seconds 49-50: the spike survives
        Assert.All(b, x => Assert.Equal(10, x.Rps));
        Assert.Equal(3, LoadReports.Buckets([.. series.Take(3)]).Count);   // short runs are not stretched
        Assert.Empty(LoadReports.Buckets([]));
    }

    [Fact]
    public void A_series_point_without_its_numbers_is_an_error_and_the_series_is_kept()
    {
        var dir = Directory.CreateTempSubdirectory("docwizz-load-").FullName;
        try
        {
            string Write(string series) { var f = Path.Combine(dir, "r.json"); File.WriteAllText(f, $$"""
                {"schema":"powerwizz.report/v1","started_at":"2026-10-13T18:39:16Z","target":{"base_url":"http://shop/","local":true},
                 "steps":[],"series":{{series}}}
                """); return f; }
            var read = LoadReports.Read(Write("""[{"t":1,"requests":9,"failures":0,"p50_ms":2,"p95_ms":43.5,"p99_ms":50}]"""));
            Assert.Equal(new LoadReports.Point(1, 9, 43.5), Assert.Single(read.Series!));
            Assert.EndsWith("series[0].p95_ms missing or not a number",
                Assert.Throws<ArgumentException>(() => LoadReports.Read(Write("""[{"t":1,"requests":9}]"""))).Message);
            Assert.Null(LoadReports.Read(Write("null")).Series);
        }
        finally { Directory.Delete(dir, true); }
    }
}
