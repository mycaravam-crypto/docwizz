using System.Text.Json;

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
}
