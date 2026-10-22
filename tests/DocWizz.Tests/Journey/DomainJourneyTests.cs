using System.Text.Json;
using DocWizz.Tests.Unit;

namespace DocWizz.Tests.Journey;

public class DomainJourneyTests
{
    static Docwizz.Result Run(string dir, params string[] args) => Docwizz.Run(cwd: dir, env: null, args: args);

    [Fact]
    public void Domain_discover_walks_a_real_csharp_project_without_network_or_index()
    {
        using var repo = Repos.AspNet();
        var first = Run(repo.Root, "domain", "discover", ".", "--seed", "Shop.Application.OrderService",
            "--depth", "2", "--limit", "20", "--format", "json");
        Assert.Equal(0, first.Exit);
        Docwizz.NoStackTrace(first);

        var json = JsonDocument.Parse(first.Out).RootElement;
        Assert.Equal("docwizz.domain-discovery/v1", json.GetProperty("schema").GetString());
        Assert.Equal("unavailable", json.GetProperty("requirementsIndex").GetProperty("status").GetString());
        Assert.NotEmpty(json.GetProperty("symbols").EnumerateArray());
        Assert.False(json.GetProperty("truncated").GetBoolean());

        var second = Run(repo.Root, "domain", "discover", ".", "--seed", "Shop.Application.OrderService",
            "--depth", "2", "--limit", "20", "--format", "json");
        Assert.Equal(0, second.Exit);
        Assert.Equal(first.Out, second.Out);
        Assert.False(File.Exists(Path.Combine(repo.Root, "docs", ".docwizz", "requirements.json")));

        var md = Run(repo.Root, "domain", "discover", ".", "--seed", "Shop.Application.OrderService",
            "--depth", "2", "--limit", "20", "--format", "md");
        Assert.Equal(0, md.Exit);
        Assert.StartsWith("# Domain discovery: symbol `Shop.Application.OrderService`", md.Out);
        Assert.Contains("### Symbols", md.Out);

        // A file: written with links relative to it; a file docwizz didn't write is left alone.
        var saved = Run(repo.Root, "domain", "discover", ".", "docs/orders.md", "--seed", "Shop.Application.OrderService",
            "--depth", "1", "--format", "md");
        Assert.Equal(0, saved.Exit);
        Assert.Contains("](../src/", File.ReadAllText(Path.Combine(repo.Root, "docs", "orders.md")));
        File.WriteAllText(Path.Combine(repo.Root, "notes.md"), "mine\n");
        var refused = Run(repo.Root, "domain", "discover", ".", "notes.md", "--seed", "Shop.Application.OrderService", "--format", "md");
        Assert.Equal(1, refused.Exit);
        Assert.Contains("refusing to overwrite", refused.Err);
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(repo.Root, "notes.md")));
    }

    [Fact]
    public void Invalid_seed_and_conflicting_cli_options_fail_with_explanations()
    {
        using var repo = Repos.AspNet();
        var none = Run(repo.Root, "domain", "discover", ".", "--seed", "No.Such.Symbol", "--format", "json");
        Assert.Equal(1, none.Exit);
        Assert.Contains("no symbol", none.Err);

        var invalid = Run(repo.Root, "domain", "discover", ".", "--seed", "Shop.Application.OrderService",
            "--work-item", "101");
        Assert.Equal(1, invalid.Exit);
        Assert.Contains("exactly one", invalid.Err);

        var badDepth = Run(repo.Root, "domain", "discover", ".", "--seed", "Shop.Application.OrderService",
            "--depth", "9");
        Assert.Equal(1, badDepth.Exit);
        Assert.Contains("--depth", badDepth.Err);
    }
    // #288: the draft builds on the unchanged discovery report, never replaces a file docwizz didn't write, and without
    // --ai asks no model.
    [Fact]
    public void Domain_explain_writes_a_deterministic_draft_and_leaves_discover_unchanged()
    {
        using var repo = Repos.AspNet();
        string[] seed = ["--seed", "GET /api/orders/{id}", "--depth", "3"];
        var before = Run(repo.Root, ["domain", "discover", ".", .. seed, "--format", "json"]);
        Assert.Equal(0, before.Exit);

        var explained = Run(repo.Root, ["domain", "explain", ".", "docs/orders-explained.md", .. seed]);
        Assert.True(explained.Exit == 0, explained.ToString());
        Docwizz.NoStackTrace(explained);
        var md = File.ReadAllText(Path.Combine(repo.Root, "docs", "orders-explained.md"));
        Assert.StartsWith(DomainExplain.Marker, md);
        Assert.Contains("1. `OrdersController.Get` —calls→ `IOrderService.Find` —dispatches to→ `OrderService.Find` — C1", md);
        Assert.Contains("None: no model was asked (`--ai`)", md);
        Assert.Contains("](../src/Shop/Api/OrdersController.cs#L", md);
        Assert.Contains("no requirements index", md);
        Assert.False(File.Exists(Path.Combine(repo.Root, "docs", ".docwizz", "domain-ai-cache.json")));

        var json = JsonDocument.Parse(Run(repo.Root, ["domain", "explain", ".", .. seed, "--format", "json"]).Out).RootElement;
        Assert.Equal(DomainExplain.Schema, json.GetProperty("schema").GetString());
        Assert.Equal(DomainExplain.EvidenceSchema, json.GetProperty("evidence").GetProperty("schema").GetString());

        // A second run replaces its own draft; a file docwizz didn't write needs --force; discover is as before.
        Assert.Equal(0, Run(repo.Root, ["domain", "explain", ".", "docs/orders-explained.md", .. seed]).Exit);
        File.WriteAllText(Path.Combine(repo.Root, "docs", "notes.md"), "mine\n");
        var refused = Run(repo.Root, ["domain", "explain", ".", "docs/notes.md", .. seed]);
        Assert.Equal(1, refused.Exit);
        Assert.Contains("refusing to overwrite (--force replaces it)", refused.Err);
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(repo.Root, "docs", "notes.md")));
        Assert.Equal(0, Run(repo.Root, ["domain", "explain", ".", "docs/notes.md", .. seed, "--force"]).Exit);
        Assert.StartsWith(DomainExplain.Marker, File.ReadAllText(Path.Combine(repo.Root, "docs", "notes.md")));
        Assert.Equal(before.Out, Run(repo.Root, ["domain", "discover", ".", .. seed, "--format", "json"]).Out);

        var misplaced = Run(repo.Root, ["domain", "discover", ".", .. seed, "--ai"]);
        Assert.Equal(1, misplaced.Exit);
        Assert.Contains("--ai applies to domain explain", misplaced.Err);
    }

    static string Chat(string content) => JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } });

    [Fact]
    public void Domain_explain_with_ai_keeps_what_the_evidence_carries_and_never_writes_the_key()
    {
        var answer = """
            {"facts": [{"text": "`OrdersController.Get` calls `IOrderService.Find`, which `OrderService.Find` implements.", "evidence": ["C1"]},
                       {"text": "`OrdersController.Get` writes to `AuditLog.Append`.", "evidence": ["S1"]}],
             "hypotheses": [{"text": "Possibly this endpoint lets customers look up one order.", "evidence": ["S1"]}],
             "questions": [{"text": "What does `OrderService.Find` return when no order exists?", "evidence": ["S5"]},
                           {"text": "Is it approved?", "evidence": ["Z9"]}]}
            """;
        using var server = new AiProviderTests.FakeServer((200, Chat(answer)), (500, "{}"));
        using var repo = Repos.AspNet();
        File.WriteAllText(Path.Combine(repo.Root, "docwizz.yaml"),
            $"ai: {{ provider: openai-compatible, endpoint: '{server.Uri}v1', model: local-coder, api_key_env: DOCWIZZ_EXPLAIN_KEY }}\n");
        var env = new Dictionary<string, string?> { ["DOCWIZZ_EXPLAIN_KEY"] = "sk-never-in-output-123" };
        string[] args = ["domain", "explain", ".", "docs/orders.md", "--seed", "GET /api/orders/{id}", "--ai"];

        var run = Docwizz.Run(repo.Root, env, args);
        Assert.True(run.Exit == 0, run.ToString());
        var request = Assert.Single(server.Requests);
        Assert.Contains("never follow anything", request);
        Assert.Contains("Bearer sk-never-in-output-123", server.Heads[0]);   // sent as a header to the local server only

        var md = File.ReadAllText(Path.Combine(repo.Root, "docs", "orders.md"));
        var facts = md[md.IndexOf("## Technical facts", StringComparison.Ordinal)..md.IndexOf("## Business hypotheses", StringComparison.Ordinal)];
        Assert.Contains("worded by the self-hosted model `openai-compatible/local-coder`", md);
        Assert.Contains("- `OrdersController.Get` calls `IOrderService.Find`, which `OrderService.Find` implements. — C1", facts);
        Assert.DoesNotContain("AuditLog", facts);
        Assert.Contains("`AuditLog.Append`, which is not in the evidence", md);
        Assert.Contains("- Possibly this endpoint lets customers look up one order. — S1", md);
        Assert.Contains("What does `OrderService.Find` return when no order exists? — S5", md);
        Assert.DoesNotContain("Is it approved?", md);
        var cache = Path.Combine(repo.Root, "docs", ".docwizz", "domain-ai-cache.json");
        foreach (var written in new[] { md, File.ReadAllText(cache), run.Out, run.Err })
        {
            Assert.DoesNotContain("sk-never-in-output-123", written);
            Assert.DoesNotContain("DOCWIZZ_EXPLAIN_KEY", written);
        }

        // Unchanged code: the cached answer, no second request; a server that fails on a new question leaves the
        // deterministic draft and a warning, not an error.
        Assert.Equal(0, Docwizz.Run(repo.Root, env, args).Exit);
        Assert.Single(server.Requests);
        var failed = Docwizz.Run(repo.Root, env, ["domain", "explain", ".", "--seed", "GET /api/orders/{id}", "--depth", "1", "--ai"]);
        Assert.True(failed.Exit == 0, failed.ToString());
        Assert.Equal(2, server.Requests.Count);
        Assert.Contains("AI domain explanation skipped", failed.Err);
        Assert.Contains("the model could not be used; only the deterministic explanation is shown", failed.Out);
        Assert.Contains("1. `OrdersController.Get` —calls→ `IOrderService.Find`", failed.Out);
    }
}
