namespace DocWizz.Tests.Journey;

// `docwizz load import` then `generate` (#185): hand-written powerwizz.report/v1 files, as powerwizz exports them, show
// up in api.md under the endpoints they exercised, labelled measured, with their evidence. Files only, no network.
public class LoadJourneyTests
{
    static readonly string Root = FindRoot();

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "fixture")) && File.Exists(Path.Combine(dir.FullName, "CLI.md"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found above the test binary");
    }

    static Docwizz.Result Run(params string[] args) => Docwizz.Run(args);

    static string Step(string name, string method, string path, int requests, double errorRate, double p50) => $$"""
        { "name": "{{name}}", "method": "{{method}}", "path": "{{path}}", "requests": {{requests}}, "failures": 0, "error_rate": {{errorRate}},
          "latency_ms": { "mean": {{p50}}, "p50": {{p50}}, "p95": {{p50 * 2}}, "p99": {{p50 * 3}} } }
        """;

    static string Report(string startedAt, string baseUrl, bool local, params string[] steps) => $$"""
        {
          "schema": "powerwizz.report/v1",
          "generator": "powerwizz 0.1.0",
          "started_at": "{{startedAt}}",
          "runtime_s": 60.0,
          "target": { "base_url": "{{baseUrl}}", "local": {{(local ? "true" : "false")}} },
          "config": { "users": 50, "ramp_up_s": 10, "duration_s": 60, "request_limit": null, "timeout_s": 10, "auth": "PerUser", "scenarios": ["browse", "order"] },
          "totals": { "users_started": 50, "iterations": 10, "requests": 100, "failures": 0, "error_rate": 0.0, "rps": 1.6,
                      "latency_ms": { "mean": 1, "p50": 1, "p95": 1, "p99": 1 }, "status_counts": { "200": 100 }, "error_kinds": {} },
          "steps": [{{string.Join(",", steps)}}],
          "series": []
        }
        """;

    // The fixture's backend, out of the repository: generate writes docs next to it.
    static string Backend()
    {
        var dir = Path.Combine(Directory.CreateTempSubdirectory("docwizz-load-").FullName, "backend");
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Root, "fixture", "backend"), "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Path.Combine(Root, "fixture", "backend"), f);
            if (rel.Split(Path.DirectorySeparatorChar).Any(d => d is "bin" or "obj" or "docs")) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, rel))!);
            File.Copy(f, Path.Combine(dir, rel));
        }
        return dir;
    }

    static string Section(string api)
    {
        var start = api.IndexOf("\n## Load\n", StringComparison.Ordinal);
        Assert.True(start >= 0, api);
        var end = api.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return end < 0 ? api[start..] : api[start..end];
    }

    [Fact]
    public void Imported_runs_appear_under_the_endpoints_they_measured_the_newest_per_endpoint()
    {
        var dir = Backend();
        try
        {
            var reports = Path.GetDirectoryName(dir)!;
            File.WriteAllText(Path.Combine(reports, "old-bericht.json"), Report("2026-10-01T08:00:00Z", "http://127.0.0.1:18080/", true,
                Step("browse/detail", "GET", "/api/materials/{{vu_id}}", 111, 0.0, 10),
                Step("browse/stock", "GET", "/api/stock/A-1", 300, 0.0, 7)));
            File.WriteAllText(Path.Combine(reports, "new-bericht.json"), Report("2026-10-13T18:39:16Z", "https://staging.example.test/app/", false,
                Step("browse/detail", "GET", "/app/api/materials/{{vu_id}}", 898, 0.0021287919105907396, 24.198),
                Step("login", "POST", "/app/api/login", 50, 0.0, 30)));

            var import = Run("load", "import", dir, Path.Combine(reports, "new-bericht.json"), Path.Combine(reports, "old-bericht.json"));
            Assert.True(import.Exit == 0, import.ToString());
            Assert.Contains("2 reports (2 new)", import.Out);
            Assert.Contains("new-bericht.json: target https://staging.example.test/app/ is not local", import.Err);
            Assert.Contains("unchanged", Run("load", "import", dir, Path.Combine(reports, "old-bericht.json")).Out);

            var generate = Run("generate", dir);
            Assert.True(generate.Exit == 0, generate.ToString());
            var load = Section(File.ReadAllText(Path.Combine(dir, "docs", "api.md")));

            // Matched endpoints only, each with its newest run and the evidence it comes from.
            Assert.Contains("_measured_ by load tests", load);
            Assert.Equal(["GET /api/materials/{id}", "GET /api/stock/{sku}"],
                load.Split('\n').Where(l => l.StartsWith("### `")).Select(l => l[5..^1]));
            Assert.Contains("_measured_ · `new-bericht.json`, started 2026-10-13T18:39:16Z, `https://staging.example.test/app/` (not local), 50 users / 60 s", load);
            Assert.Contains("| browse/detail `GET /app/api/materials/{{vu_id}}` | 898 | 0.21 % | 24.2 | 48.4 | 72.6 |", load);
            Assert.DoesNotContain("| 111 |", load);   // the older run of the same endpoint is not shown
            Assert.Contains("_measured_ · `old-bericht.json`, started 2026-10-01T08:00:00Z, `http://127.0.0.1:18080/`, 50 users / 60 s", load);
            Assert.Contains("| browse/stock `GET /api/stock/A-1` | 300 | 0 % | 7 | 14 | 21 |", load);

            // The configuration once per run; a non-local target says so.
            Assert.Contains("| `old-bericht.json` | 2026-10-01T08:00:00Z | `http://127.0.0.1:18080/` | 50 | 10 s | 60 s | PerUser |", load);
            Assert.Contains("| `new-bericht.json` | 2026-10-13T18:39:16Z | `https://staging.example.test/app/` ⚠ not local: not comparable to a local run | 50 | 10 s | 60 s | PerUser |", load);

            // A step without an endpoint is listed, not dropped.
            Assert.Contains("### Steps without a matching endpoint\n\n- `POST /app/api/login` (new-bericht.json)", load);
            Assert.DoesNotContain("stale", load.Replace("_stale_", ""));   // no history: nothing is called stale
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true); }
    }

    [Fact]
    public void A_report_that_is_not_powerwizz_report_v1_is_rejected_and_nothing_is_written()
    {
        var dir = Backend();
        try
        {
            var reports = Path.GetDirectoryName(dir)!;
            File.WriteAllText(Path.Combine(reports, "good.json"), Report("2026-10-13T18:39:16Z", "http://127.0.0.1:18080/", true));
            File.WriteAllText(Path.Combine(reports, "wrong.json"), Report("2026-10-13T18:39:16Z", "http://127.0.0.1:18080/", true).Replace("powerwizz.report/v1", "powerwizz.report/v2"));
            File.WriteAllText(Path.Combine(reports, "none.json"), """{ "steps": [] }""");

            var wrong = Run("load", "import", dir, Path.Combine(reports, "good.json"), Path.Combine(reports, "wrong.json"));
            Assert.Equal(1, wrong.Exit);
            Assert.Contains("wrong.json: not powerwizz.report/v1 (schema: powerwizz.report/v2)", wrong.Err);
            var none = Run("load", "import", dir, Path.Combine(reports, "none.json"));
            Assert.Equal(1, none.Exit);
            Assert.Contains("none.json: not powerwizz.report/v1 (schema: missing)", none.Err);
            Assert.False(File.Exists(Path.Combine(dir, LoadReports.DefaultFile)));

            Assert.Equal(1, Run("load", "import", dir).Exit);
            Assert.Equal(1, Run("load", "export", dir).Exit);
            Docwizz.NoStackTrace(wrong);
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true); }
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
            File.WriteAllText(Path.Combine(reports, "before.json"), Report("2025-12-01T00:00:00Z", "http://localhost:5000/", true,
                Step("order", "GET", "/api/orders/{{vu_id}}", 10, 0.0, 5)));
            File.WriteAllText(Path.Combine(reports, "after.json"), Report("2026-02-01T00:00:00Z", "http://localhost:5000/", true,
                Step("health", "GET", "/api/health", 10, 0.0, 1)));
            Assert.Equal(0, Run("load", "import", repo.Root, Path.Combine(reports, "before.json"), Path.Combine(reports, "after.json")).Exit);
            var generate = Run("generate", repo.Root);
            Assert.True(generate.Exit == 0, generate.ToString());
            var load = Section(File.ReadAllText(Path.Combine(repo.Root, "docs", "api.md")));

            var lines = load.Split('\n').Where(l => l.StartsWith("_measured_ · ")).ToList();
            Assert.Equal(2, lines.Count);
            Assert.EndsWith("· **stale**: `Program.cs` changed 2026-01-01T00:00:00Z, after this run", Assert.Single(lines, l => l.Contains("before.json")));
            Assert.DoesNotContain("stale", Assert.Single(lines, l => l.Contains("after.json")));
        }
        finally { Directory.Delete(reports, recursive: true); }
    }
}
