using System.Text.Json;
using System.Text.Json.Nodes;
using DocWizz.Tests.Unit;

namespace DocWizz.Tests.Journey;

public class GithubRequirementsJourneyTests
{
    static Docwizz.Result Run(FakeHistory repo, params string[] args) => Docwizz.Run(repo.Root, null, args);

    [Fact]
    public void Github_import_show_reverse_domain_query_and_ai_drafts_work_offline()
    {
        using var repo = new FakeHistory();
        using var exports = new Sources();
        repo.Write("Shop.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        repo.Write("docwizz.yaml", "patterns: { all: { level: medium, sections: [summary] } }\n");
        repo.Write("Orders.cs", "namespace Shop;\npublic class Orders\n{\n    public int Place(int amount)\n    {\n        return amount;\n    }\n}\n");
        repo.Commit("implement limit", FakeHistory.Status("unknown"), FakeHistory.Pr(7));
        var history = JsonNode.Parse(repo.History())!;
        history["provider"] = "github";
        history["repository"] = "https://github.com/acme/shop.git";
        history["pullRequests"] = JsonNode.Parse("""
            [{"id":"#7","title":"limit","url":"https://github.com/acme/shop/pull/7","target":"main",
              "status":"confirmed","strategy":"squash","mergeCommit":null,"commits":[],
              "workItemsStatus":"verified","problem":null,"workItems":[
                {"workItem":"#42","source":"provider","kind":"pr-link","evidence":"closingIssuesReferences",
                 "uri":"https://github.com/acme/shop/issues/42"}]}]
            """);
        var historyFile = Path.Combine(exports.Root, "history.json");
        File.WriteAllText(historyFile, history.ToJsonString());
        var issueFile = Path.Combine(exports.Root, "issue.json");
        void Issue(string body) => File.WriteAllText(issueFile, JsonSerializer.Serialize(new
        {
            number = 42, url = "https://github.com/acme/shop/issues/42", title = "Credit limit", body,
            state = "OPEN", updatedAt = "2026-10-15T12:00:00Z", labels = new[] { new { name = "business" } },
        }));
        Issue("Preserve the customer's credit limit.\n## Acceptance criteria\n- Reject amounts above 1000.");

        var imported = Run(repo, "requirements", "import", ".", historyFile, issueFile);
        Assert.True(imported.Exit == 0, imported.ToString());
        var indexFile = Path.Combine(repo.Root, RequirementIndex.DefaultFile);
        var first = File.ReadAllText(indexFile);
        var rerun = Run(repo, "requirements", "import", ".", historyFile, issueFile);
        Assert.True(rerun.Exit == 0, rerun.ToString());
        Assert.Contains("unchanged", rerun.Out);
        Assert.Equal(first, File.ReadAllText(indexFile));

        var shown = Run(repo, "requirements", "show", ".", "--for", "Shop.Orders.Place", "--format", "json");
        Assert.True(shown.Exit == 0, shown.ToString());
        var context = JsonDocument.Parse(shown.Out).RootElement;
        Assert.Equal("docwizz.requirement-context/v2", context.GetProperty("schema").GetString());
        var link = Assert.Single(context.GetProperty("requirements").EnumerateArray());
        Assert.Equal("github:acme/shop#42", link.GetProperty("id").GetString());
        Assert.Equal("candidate", link.GetProperty("status").GetString());
        Assert.Contains("Preserve the customer's", link.GetProperty("workItem").GetProperty("description").GetString());
        var reverse = Run(repo, "requirements", "show", ".", "--work-item", "github#42", "--format", "json");
        Assert.True(reverse.Exit == 0, reverse.ToString());
        Assert.Contains("cs:Shop.Orders.Place(int)", reverse.Out);
        var domain = Run(repo, "domain", "discover", ".", "--work-item", "github#42", "--format", "json");
        Assert.True(domain.Exit == 0, domain.ToString());
        Assert.Contains("github:acme/shop#42", domain.Out);
        Assert.Contains("\"candidate\"", domain.Out);

        var queryFile = Path.Combine(exports.Root, "query.json");
        File.WriteAllText(queryFile, """{"schema":"gitwizz.requirement-query/v1","symbols":[{"id":"cs:Shop.Orders.Place(int)"}]}""");
        var query = Run(repo, "requirements", "query", ".", queryFile);
        Assert.True(query.Exit == 0, query.ToString());
        var queryLink = Assert.Single(JsonDocument.Parse(query.Out).RootElement.GetProperty("links").EnumerateArray());
        Assert.Equal("github#42", queryLink.GetProperty("requirement").GetString());
        Assert.Equal("acme/shop", queryLink.GetProperty("repository").GetString());

        const string reply = """
            summary: Returns the amount. [source]
            intent: Orders may require a credit limit. [github#42]
            intent: This code satisfies github#42. [github#42]
            side_effects: Writes the credit ledger. [github#42]
            """;
        using var server = new AiProviderTests.FakeServer(200, JsonSerializer.Serialize(new { message = new { content = reply } }));
        var drafted = Docwizz.Run(repo.Root, new Dictionary<string, string?>
        {
            ["OLLAMA_HOST"] = server.Uri.ToString(), ["DOCWIZZ_MODEL"] = "local:7b",
        }, "generate", ".", "--ai", "--requirements");
        Assert.True(drafted.Exit == 0, drafted.ToString());
        var cache = File.ReadAllText(Path.Combine(repo.Root, "docs", ".docwizz", "ai-cache.json"));
        Assert.Contains("github#42 candidate", cache);
        Assert.Contains("workitem:github:acme/shop#42", cache);
        Assert.DoesNotContain("satisfies github", cache);
        Assert.DoesNotContain("Writes the credit ledger", cache);
        Assert.Contains(server.Requests, r => JsonDocument.Parse(r).RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!.Contains("Preserve the customer's", StringComparison.Ordinal));

        Issue("Updated business request.\n## Acceptance criteria\n- Reject amounts above 2000.");
        var changed = Run(repo, "requirements", "import", ".", historyFile, issueFile);
        Assert.True(changed.Exit == 0, changed.ToString());
        Assert.NotEqual(first, File.ReadAllText(indexFile));
    }
}
