namespace DocWizz.Tests.Journey;

// `docwizz load import` + generate on the bundled fixture, with hand-written powerwizz reports.
public class LoadJourneyTests
{
    static string Report(string startedAt, long requests, double p95) => $$$"""
        {"schema":"powerwizz.report/v1","generator":"powerwizz 0.1.0","started_at":"{{{startedAt}}}","runtime_s":60.0,"later_field":true,
         "target":{"base_url":"http://127.0.0.1:18080/app/","local":true},
         "config":{"users":50,"ramp_up_s":10,"duration_s":60,"request_limit":null,"timeout_s":10,"auth":"PerUser","scenarios":["browse"]},
         "steps":[{"name":"browse/detail","method":"GET","path":"/app/api/materials/{{vu_id}}","requests":{{{requests}}},"failures":0,"error_rate":0.0,
                   "latency_ms":{"mean":24.2,"p50":24.198,"p95":{{{p95}}},"p99":41.388}},
                  {"name":"order/missing","method":"POST","path":"/api/checkout","requests":10,"failures":10,"error_rate":1.0,
                   "latency_ms":{"mean":3,"p50":3,"p95":4,"p99":5}}],
         "series":[]}
        """;

    [Fact]
    public void Imported_reports_show_measured_numbers_for_matched_endpoints_only()
    {
        using var fixture = new ContextJourneyTests.FixtureCopy();
        var dir = Path.GetDirectoryName(fixture.Dir)!;
        File.WriteAllText(Path.Combine(dir, "old.json"), Report("2026-10-12T08:00:00Z", 111, 99.9));
        File.WriteAllText(Path.Combine(dir, "new.json"), Report("2026-10-13T18:39:16Z", 898, 39.417));
        File.WriteAllText(Path.Combine(dir, "other.json"), """{"schema":"gitwizz.history/v2"}""");

        var wrong = Docwizz.Run("load", "import", fixture.Dir, Path.Combine(dir, "other.json"));
        Assert.Equal(1, wrong.Exit);
        Assert.Contains("not powerwizz.report/v1 (schema: gitwizz.history/v2)", wrong.Err);
        Docwizz.NoStackTrace(wrong);

        var import = Docwizz.Run("load", "import", fixture.Dir, Path.Combine(dir, "old.json"), Path.Combine(dir, "new.json"));
        Assert.True(import.Exit == 0, import.ToString());
        Assert.Contains("2 reports, 1 endpoints measured", import.Out);
        Assert.Contains("no endpoint matches POST /api/checkout", import.Err);

        var generate = Docwizz.Run("generate", fixture.Dir);
        Assert.True(generate.Exit == 0, generate.ToString());
        var api = File.ReadAllText(Path.Combine(fixture.Dir, "docs", "api.md"));
        var load = api[api.IndexOf("## Load (measured)", StringComparison.Ordinal)..];
        load = load[..(load.IndexOf("\n## ", 3, StringComparison.Ordinal) is var end and > 0 ? end : load.Length)];
        Assert.Contains("| `GET /api/materials/{id}` | browse/detail | 898 | 0.00 % | 24.2 | 39.4 | 41.4 | new.json 2026-10-13 18:39Z |", load);
        Assert.DoesNotContain("old.json", load);   // the newest run per endpoint
        Assert.DoesNotContain("`PUT /api/materials/{id}`", load);
        Assert.Contains("| new.json 2026-10-13 18:39Z | `http://127.0.0.1:18080/app/` | 50 | 10 s | 60 s | PerUser |", load);
        Assert.Contains("- `POST /api/checkout (new.json)`", load);
    }

    [Fact]
    public void A_run_older_than_its_endpoints_last_commit_is_stale()
    {
        using var repo = new FakeHistory();   // commits are dated 2026-01-01T00:00:00Z
        repo.Write("Shop.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web" />""");
        repo.Write("Program.cs", """
            var app = WebApplication.CreateBuilder(args).Build();
            app.MapGet("/api/orders/{id}", (int id) => id);
            app.MapGet("/api/health", () => "ok");
            app.Run();
            """);
        repo.Commit("orders");
        var reports = Directory.CreateTempSubdirectory("docwizz-load-reports-").FullName;
        try
        {
            static string Run(string startedAt, string path) => $$$"""
                {"schema":"powerwizz.report/v1","started_at":"{{{startedAt}}}","target":{"base_url":"http://localhost:5000/","local":true},
                 "config":{"users":5,"ramp_up_s":1,"duration_s":10,"auth":"None"},
                 "steps":[{"name":"s","method":"GET","path":"{{{path}}}","requests":10,"failures":0,"error_rate":0.0,"latency_ms":{"p50":1,"p95":2,"p99":3}}]}
                """;
            File.WriteAllText(Path.Combine(reports, "before.json"), Run("2025-12-01T00:00:00Z", "/api/orders/{{vu_id}}"));
            File.WriteAllText(Path.Combine(reports, "after.json"), Run("2026-02-01T00:00:00Z", "/api/health"));
            var import = Docwizz.Run("load", "import", repo.Root, Path.Combine(reports, "before.json"), Path.Combine(reports, "after.json"));
            Assert.True(import.Exit == 0, import.ToString());
            var generate = Docwizz.Run("generate", repo.Root);
            Assert.True(generate.Exit == 0, generate.ToString());
            var rows = File.ReadAllLines(Path.Combine(repo.Root, "docs", "api.md")).Where(l => l.StartsWith("| `GET /api/")).ToList();
            Assert.EndsWith("before.json 2025-12-01 00:00Z _stale_ |", Assert.Single(rows, r => r.Contains("/api/orders/")));
            Assert.EndsWith("after.json 2026-02-01 00:00Z |", Assert.Single(rows, r => r.Contains("/api/health")));

            // A broken index is reported (exit 1), never a crash.
            File.WriteAllText(Path.Combine(repo.Root, LoadReports.DefaultFile), "{ not json");
            var broken = Docwizz.Run("load", "import", repo.Root, Path.Combine(reports, "after.json"));
            Assert.Equal(1, broken.Exit);
            Assert.Contains("delete it and run docwizz load import again", broken.Err);
            Docwizz.NoStackTrace(broken);
        }
        finally { Directory.Delete(reports, recursive: true); }
    }
}
