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
        Assert.Contains("| `GET /api/materials/{id}` | 898 | 0.00 % | 24.2 | 39.4 | 41.4 | new.json 2026-10-13 18:39Z |", load);
        Assert.DoesNotContain("old.json", load);   // the newest run per endpoint
        Assert.DoesNotContain("`PUT /api/materials/{id}`", load);
        Assert.Contains("| new.json 2026-10-13 18:39Z | `http://127.0.0.1:18080/app/` | 50 | 10 s | 60 s | PerUser |", load);
        Assert.Contains("- `POST /api/checkout (new.json)`", load);
    }
}
