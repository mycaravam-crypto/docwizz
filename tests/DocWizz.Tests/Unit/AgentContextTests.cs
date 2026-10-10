using static DocWizz.Tests.Models;

namespace DocWizz.Tests.Unit;

// docwizz context: how --for resolves, what a package holds and where each line comes from, and the token budget.
public class AgentContextTests
{
    static readonly CodeModel Shop = Model(
    [
        N("cs:Shop.Api.OrderController", "src/Api/OrderController.cs", tags: ["controller"]),
        N("cs:Shop.Api.OrderController.Place(int)", "src/Api/OrderController.cs", kind: "method", tags: ["endpoint", "POST"], route: "api/orders", parameters: ["id: int"]),
        N("cs:Shop.Application.OrderService", "src/Application/OrderService.cs", doc: Doc("Places orders."), tags: ["service"]),
        N("cs:Shop.Application.OrderService.Place(int)", "src/Application/OrderService.cs", kind: "method", hash: "h1"),
        N("cs:Shop.Application.OrderService.Place(string)", "src/Application/OrderService.cs", kind: "method"),
        N("cs:Shop.Domain.Order", "src/Domain/Order.cs", doc: Doc("An order.")),
        N("cs:Shop.Domain.Order.Save()", "src/Domain/Order.cs", kind: "method"),
        N("cs:Shop.Infrastructure.Db.Write()", "src/Infrastructure/Db.cs", kind: "method"),
    ],
    [
        E("cs:Shop.Api.OrderController", "cs:Shop.Api.OrderController.Place(int)", "contains"),
        E("cs:Shop.Application.OrderService", "cs:Shop.Application.OrderService.Place(int)", "contains"),
        E("cs:Shop.Application.OrderService", "cs:Shop.Application.OrderService.Place(string)", "contains"),
        E("cs:Shop.Domain.Order", "cs:Shop.Domain.Order.Save()", "contains"),
        E("cs:Shop.Api.OrderController", "cs:Shop.Application.OrderService", "injects"),
        E("cs:Shop.Api.OrderController.Place(int)", "cs:Shop.Application.OrderService.Place(int)"),
        E("cs:Shop.Application.OrderService.Place(int)", "cs:Shop.Domain.Order.Save()"),
        E("cs:Shop.Domain.Order.Save()", "cs:Shop.Infrastructure.Db.Write()"),
        E("cs:Tests.OrderServiceTests.Places()", "cs:Shop.Application.OrderService.Place(int)", "tests"),
    ]);

    static AgentContext.Target Resolve(string query) =>
        AgentContext.Resolve(Shop, query).Target ?? throw new Xunit.Sdk.XunitException($"{query} did not resolve");

    static string Package(string query, int budget = AgentContext.DefaultBudget, bool json = false, bool includeAi = false,
        Dictionary<string, AiProse.Draft>? drafts = null, string? head = null)
    {
        var config = Config();
        var target = Resolve(query);
        drafts ??= [];
        var blocks = AgentContext.Blocks(new ContextBuilder("", Shop), config, target, AgentContext.DefaultHops, includeAi,
            Analyzer.Analyze(Shop, config), Architecture.Check(Shop, config), drafts);
        return AgentContext.Render(new("shop", "abc1234", new("a fresh scan", head, 0), target, budget, includeAi,
            includeAi ? 0 : target.Scope.Count(n => drafts.ContainsKey(n.Id)), blocks, []), json);
    }

    [Fact]
    public void For_resolves_id_then_name_then_path_then_endpoint()
    {
        Assert.Equal(("symbol", "Shop.Domain.Order"), (Resolve("cs:Shop.Domain.Order").Kind, Resolve("cs:Shop.Domain.Order").Label));
        Assert.Equal("cs:Shop.Application.OrderService.Place(int)", Resolve("Shop.Application.OrderService.Place(int)").Primary!.Id);
        Assert.Equal("cs:Shop.Domain.Order.Save()", Resolve("Shop.Domain.Order.Save").Primary!.Id);   // without the parameter list
        var file = Resolve("./src/Application/OrderService.cs");
        Assert.Equal(("file", 3), (file.Kind, file.Scope.Count));
        var module = Resolve("src/");
        Assert.Equal(("module", "src", 8), (module.Kind, module.Label, module.Scope.Count));
        var endpoint = Resolve("post api/orders");
        Assert.Equal(("endpoint", "POST /api/orders", "cs:Shop.Api.OrderController.Place(int)"), (endpoint.Kind, endpoint.Label, endpoint.Primary!.Id));
        // A type's scope is the type and its members.
        Assert.Equal(3, Resolve("Shop.Application.OrderService").Scope.Count);
    }

    [Fact]
    public void An_ambiguous_or_unknown_target_is_an_error_with_candidates_never_a_guess()
    {
        var overloads = AgentContext.Resolve(Shop, "Shop.Application.OrderService.Place");
        Assert.Null(overloads.Target);
        Assert.Contains("ambiguous", overloads.Error);
        Assert.Equal(["cs:Shop.Application.OrderService.Place(int)", "cs:Shop.Application.OrderService.Place(string)"], overloads.Candidates);

        var unknown = AgentContext.Resolve(Shop, "Ordr");
        Assert.Null(unknown.Target);
        Assert.Contains("no symbol, file, folder or endpoint matches Ordr", unknown.Error);
        Assert.Empty(unknown.Candidates);
        Assert.Contains("cs:Shop.Domain.Order", AgentContext.Resolve(Shop, "order").Candidates);
    }

    [Fact]
    public void A_package_names_the_commit_neighbours_flows_tests_and_files_to_read_with_provenance_per_line()
    {
        var text = Package("Shop.Application.OrderService.Place(int)");
        Assert.Contains("model at commit `abc1234`", text);
        Assert.Contains("- calls `Shop.Domain.Order.Save()` — src/Domain/Order.cs:1 [detected]", text);
        Assert.Contains("- called by `Shop.Api.OrderController.Place(int)`", text);
        Assert.Contains("- `Shop.Infrastructure.Db.Write()` (hop 2, via `Shop.Domain.Order.Save()`) — src/Infrastructure/Db.cs:1 [detected]", text);
        Assert.Contains("- flow `POST /api/orders → OrderService → Order", text);
        Assert.Contains("- test `Tests.OrderServiceTests.Places`", text);
        Assert.Contains("## Read before you change it\n\n- `Place` — src/Application/OrderService.cs:1 [detected]\n- `OrderService`", text);
        Assert.All(text.Split('\n').Where(l => l.StartsWith("- ") && text.IndexOf(l) > text.IndexOf("## Target")),
            l => Assert.Matches(@"\[(detected|inferred|human|ai-drafted)\]$", l));
        Assert.Equal(text, Package("Shop.Application.OrderService.Place(int)"));   // deterministic
        Assert.Contains("- summary: Places orders. [human]", Package("Shop.Application.OrderService"));
    }

    [Fact]
    public void AI_drafts_are_left_out_unless_asked_for()
    {
        var drafts = new Dictionary<string, AiProse.Draft> { ["cs:Shop.Application.OrderService.Place(int)"] = new("Places one order.", []) };
        var without = Package("Shop.Application.OrderService.Place(int)", drafts: drafts);
        Assert.DoesNotContain("Places one order.", without);
        Assert.Contains("AI drafts: 1 left out (--include-ai to see them)", without);
        var with = Package("Shop.Application.OrderService.Place(int)", drafts: drafts, includeAi: true);
        Assert.Contains("- summary 🤖: Places one order. [ai-drafted]", with);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_budget_holds_and_what_was_cut_is_said(bool json)
    {
        Assert.Contains(json ? "\"truncated\":[]" : "", Package("src", json: json));
        Assert.DoesNotContain("truncated to fit", Package("src", json: json));
        foreach (var budget in new[] { 300, 250, 200 })
        {
            var cut = Package("src", budget, json);
            Assert.True(cut.Length <= budget * AgentContext.CharsPerToken, $"{cut.Length} characters for a budget of {budget} tokens");
            Assert.Contains(json ? "\"truncated\":[{" : "- truncated to fit the budget: ", cut);
        }
        // The header always stays, even when nothing else fits.
        Assert.Contains("src", Package("src", 100, json));
    }

    [Fact]
    public void A_model_from_another_commit_is_reported_stale()
    {
        Assert.Contains("⚠ stale: HEAD is `def5678`, the model was scanned at `abc1234`", Package("Shop.Domain.Order", head: "def5678"));
        Assert.DoesNotContain("stale", Package("Shop.Domain.Order", head: "abc1234ff"));   // the same commit, longer abbreviation
        Assert.Contains("\"stale\":true", Package("Shop.Domain.Order", json: true, head: "def5678"));
    }

    [Fact]
    public void Context_parses_its_options_and_rejects_md_elsewhere()
    {
        var r = Cli.Parse(["context", ".", "--for", "POST /api/orders", "--hops", "1", "--budget", "2000", "--format", "md", "--include-ai"]);
        Assert.Null(r.Error);
        Assert.Equal(("POST /api/orders", "1", "2000"), (r.Options["for"], r.Options["hops"], r.Options["budget"]));
        Assert.Contains("unknown format md", Cli.Parse(["analyze", ".", "--format", "md"]).Error);
        Assert.Contains("--include-ai doesn't apply to generate", Cli.Parse(["generate", ".", "--include-ai"]).Error);
    }
}
