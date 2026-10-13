using System.Text.Json;
using static DocWizz.Tests.Models;

namespace DocWizz.Tests.Unit;

public class DomainCrawlerTests
{
    const string Endpoint = "cs:Shop.Api.OrderController.Place(int)";
    const string Service = "cs:Shop.Application.OrderService.Place(int)";
    const string Entity = "cs:Shop.Domain.Order.Save()";
    const string Interface = "cs:Shop.Application.IOrderService";
    const string Controller = "cs:Shop.Api.OrderController";

    static CodeModel Graph() => Model(
    [
        N(Endpoint, "src/Api/OrderController.cs", kind: "method", tags: ["endpoint", "POST"], route: "api/orders"),
        N(Service, "src/Application/OrderService.cs", kind: "method", hash: "h101"),
        N(Entity, "src/Domain/Order.cs", kind: "method"),
        N(Interface, "src/Application/IOrderService.cs", kind: "interface"),
        N(Controller, "src/Api/OrderController.cs"),
        N("cs:Shop.Infrastructure.Db.Write()", "src/Infrastructure/Db.cs", kind: "method"),
        N("cs:Shop.Unrelated.Reporting.Run()", "src/Reporting/Report.cs", kind: "method"),
    ],
    [
        E(Controller, Endpoint, "contains"),
        E(Endpoint, Service),
        E(Service, Entity),
        E(Entity, "cs:Shop.Infrastructure.Db.Write()"),
        E(Entity, Service), // cycle, must not cause repeated walks
        E(Service, Interface, "implements"),
        E("cs:Shop.Unrelated.Reporting.Run()", Service), // caller is not followed backwards
    ]);

    static RequirementIndex.Index Index() => new(
        RequirementIndex.Schema, "fingerprint-123",
        new RequirementIndex.Inputs(RequirementIndex.HistorySchema, "digest", "azure-devops", "shop",
            "v1", null, "abc123", false, false, null, []),
        [
            new RequirementIndex.Commit(new string('a', 40), "verified", [101], [], [], "mapped",
                [new RequirementIndex.Touch(Service, "method", "h101", "src/Application/OrderService.cs", "12-14", "10-20")], []),
            new RequirementIndex.Commit(new string('b', 40), "verified", [], [],
                [new RequirementIndex.PullRequestRef(7, "exact")], "mapped",
                [new RequirementIndex.Touch(Service, "method", "h101", "src/Application/OrderService.cs", "15", "10-20")], []),
        ],
        [new RequirementIndex.PullRequest(7, "Order rules", "squash", "confirmed", "verified", [102])],
        [
            new RequirementIndex.WorkItem(101, 3, 3, true, "User Story", "Closed",
                "Check order credit limit", null, null, "System.Description", null,
                "Microsoft.VSTS.Common.AcceptanceCriteria", ["Orders above the threshold are rejected"], "digest-101", []),
            new RequirementIndex.WorkItem(102, 2, 2, true, "User Story", "Closed",
                "Improve approval flow", null, null, "System.Description", null,
                "Microsoft.VSTS.Common.AcceptanceCriteria", [], "digest-102", []),
        ], []);

    [Fact]
    public void Bounded_walk_is_directed_cycle_safe_and_deterministic()
    {
        var model = Graph();
        var report = DomainCrawler.Discover(model, Endpoint, null, 3, 100);
        Assert.Equal("docwizz.domain-discovery/v1", report.Schema);
        Assert.Equal([Endpoint, Service, Entity, Interface, "cs:Shop.Infrastructure.Db.Write()"],
            report.Symbols.Select(s => s.Id).ToArray());
        Assert.Equal([0, 1, 2, 2, 3], report.Symbols.Select(s => s.Depth).ToArray());
        Assert.DoesNotContain(report.Symbols, s => s.Id.Contains("Reporting"));
        Assert.Contains(report.Relations, r => r.From == Entity && r.To == Service && r.Kind == "calls");
        Assert.False(report.Truncated);
        Assert.Equal("unavailable", report.RequirementsIndex.Status);
        Assert.Equal(DomainCrawler.Json(report), DomainCrawler.Json(DomainCrawler.Discover(model, Endpoint, null, 3, 100)));
    }

    [Fact]
    public void Interface_dispatch_walks_to_implementation_but_preserves_original_edge_direction()
    {
        var report = DomainCrawler.Discover(Graph(), Interface, null, 1, 100);
        Assert.Contains(report.Symbols, s => s.Id == Service && s.Depth == 1);
        Assert.Contains(report.Relations, e => e.From == Service && e.To == Interface && e.Kind == "implements");
    }

    [Fact]
    public void Limits_and_zero_depth_cannot_turn_into_unbounded_scan()
    {
        var zero = DomainCrawler.Discover(Graph(), Endpoint, null, 0, 10);
        Assert.Single(zero.Symbols);
        Assert.Empty(zero.Relations);
        var limited = DomainCrawler.Discover(Graph(), Endpoint, null, 3, 2);
        Assert.Equal(2, limited.Symbols.Count);
        Assert.True(limited.Truncated);
        Assert.Contains(limited.Warnings, w => w.Contains("node limit"));

        Assert.Throws<ArgumentException>(() => DomainCrawler.Discover(Graph(), Endpoint, null, 9, 10));
        Assert.Throws<ArgumentException>(() => DomainCrawler.Discover(Graph(), Endpoint, null, 1, 0));
        Assert.Throws<ArgumentException>(() => DomainCrawler.Discover(Graph(), null, null, 1, 10));
    }

    [Fact]
    public void Confirmed_commit_link_stays_distinct_from_pr_candidate()
    {
        var report = DomainCrawler.Discover(Graph(), Endpoint, null, 2, 100, Index(), "abc123");
        var links = report.Requirements.Where(r => r.Symbol == Service).ToList();
        Assert.Equal([(101, "confirmed"), (102, "candidate")],
            links.Select(r => (r.Id, r.Status)).ToList());
        Assert.Equal("commit-workitem", links[0].Evidence[0].Relation);
        Assert.Equal("pr-workitem", links[1].Evidence[0].Relation);
        Assert.False(links[0].ChangedSince);
        Assert.Equal(3, links[0].Revision);
        Assert.All(report.CandidateTerms, t => Assert.Equal("historical-requirement-title", t.Classification));
        Assert.Contains(report.CandidateTerms, t => t.WorkItem == 101 && t.Symbols.SequenceEqual([Service]));
        var json = JsonDocument.Parse(DomainCrawler.Json(report)).RootElement;
        Assert.Equal("available", json.GetProperty("requirementsIndex").GetProperty("status").GetString());
        Assert.Equal("confirmed", json.GetProperty("requirements").EnumerateArray().First().GetProperty("status").GetString());
    }

    [Fact]
    public void Work_item_seed_starts_from_linked_current_symbols_only()
    {
        var report = DomainCrawler.Discover(Graph(), null, 101, 1, 100, Index(), "abc123");
        Assert.Equal("work-item", report.Seed.Kind);
        Assert.Equal([Service], report.Seed.Symbols);
        Assert.Contains(report.Symbols, s => s.Id == Entity);
        Assert.DoesNotContain(report.Symbols, s => s.Id == Endpoint);
        Assert.Throws<ArgumentException>(() => DomainCrawler.Discover(Graph(), null, 101, 1, 100));
        Assert.Throws<ArgumentException>(() => DomainCrawler.Discover(Graph(), null, 999, 1, 100, Index()));
    }

    [Fact]
    public void Historical_index_freshness_and_unresolvable_seeds_are_explicit()
    {
        var stale = DomainCrawler.Discover(Graph(), Service, null, 1, 100, Index(), "newhead");
        Assert.False(stale.RequirementsIndex.CoversHead);
        Assert.Contains(stale.Warnings, w => w.Contains("does not cover"));
        var unknown = Assert.Throws<ArgumentException>(() => DomainCrawler.Discover(Graph(), "Nothing.Found", null, 1, 100));
        Assert.Contains("no symbol", unknown.Message);
    }
}
