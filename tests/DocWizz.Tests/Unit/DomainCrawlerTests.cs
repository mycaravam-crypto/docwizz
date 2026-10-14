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
        Assert.Equal([Endpoint, Service, Interface, Entity, "cs:Shop.Infrastructure.Db.Write()"],
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
    public void Node_budget_reaches_behaviour_before_data_records()
    {
        const string Dto = "cs:Shop.Api.OrderDto";
        var model = Model(
            [.. Graph().Nodes, N(Dto, "src/Api/OrderDto.cs", kind: "record"), N("cs:Shop.Api.Mapper", "src/Api/Mapper.cs")],
            [.. Graph().Edges, E(Endpoint, Dto, "creates"), E(Endpoint, "cs:Shop.Api.Mapper", "creates")]);

        // Plain BFS would spend both remaining slots on depth-1 constructions; behaviour wins instead.
        var tight = DomainCrawler.Discover(model, Endpoint, null, 3, 3);
        Assert.Equal([Endpoint, Service, Interface], tight.Symbols.Select(s => s.Id).ToArray());
        Assert.True(tight.Truncated);

        // With budget to spare, deferred nodes are still reached at their real hop distance.
        var all = DomainCrawler.Discover(model, Endpoint, null, 3, 100);
        Assert.Contains(all.Symbols, s => s.Id == Dto && s.Depth == 1);
        Assert.Contains(all.Relations, r => r.From == Endpoint && r.To == Dto && r.Kind == "creates");
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
    public void A_class_reached_through_a_field_does_not_pull_in_its_other_members()
    {
        const string Repo = "cs:Shop.Data.Repo", Save = "cs:Shop.Data.Repo.Save()", Purge = "cs:Shop.Data.Repo.Purge()";
        var model = Model(
            [N(Service, "src/Application/OrderService.cs", kind: "method"), N(Repo, "src/Data/Repo.cs"),
             N(Save, "src/Data/Repo.cs", kind: "method"), N(Purge, "src/Data/Repo.cs", kind: "method")],
            [E(Service, Repo, "accesses"), E(Service, Save), E(Repo, Save, "contains"), E(Repo, Purge, "contains")]);

        var walked = DomainCrawler.Discover(model, Service, null, 3, 100).Symbols.Select(s => s.Id).ToArray();
        Assert.Equal([Service, Repo, Save], walked);

        var seeded = DomainCrawler.Discover(model, Repo, null, 1, 100).Symbols.Select(s => s.Id);
        Assert.Contains(Purge, seeded);
    }

    [Fact]
    public void Markdown_separates_as_is_as_intended_and_unclear_and_escapes_untrusted_titles()
    {
        var index = Index();
        index = index with { WorkItems = [index.WorkItems[0] with { Title = "Limit | <script>\"x\"" }, index.WorkItems[1]] };
        var md = DomainCrawler.Markdown(DomainCrawler.Discover(Graph(), Endpoint, null, 2, 100, index, "abc123"));

        Assert.Contains("```mermaid\ngraph LR", md);
        Assert.Contains("-->|implements|", md);
        Assert.Contains("[\"OrderService.Place\"]", md);
        Assert.Contains("| 1 | `cs:Shop.Application.OrderService.Place(int)` | method |", md);
        var (asIs, intended, unclear) = (At(md, "## As-is"), At(md, "## As-intended"), At(md, "## Unclear"));
        Assert.True(asIs < intended && intended < unclear);
        // WI-101: direct commit link, code unchanged -> intent, with its criteria. WI-102: PR only -> unclear, with the reason.
        Assert.InRange(At(md, "| WI-101 rev 3 |"), intended, unclear);
        Assert.Contains("Orders above the threshold are rejected", md[intended..unclear]);
        Assert.Contains("| WI-102 rev 2 | Improve approval flow | `cs:Shop.Application.OrderService.Place(int)` | linked only through a pull request", md[unclear..]);
        Assert.Contains("Methods without a linked work item (2): `OrderController.Place`, `Order.Save`", md);
        Assert.Contains("Limit \\| \\<script>", md);
        Assert.DoesNotContain("<script>", md.Replace("\\<script>", ""));

        // The code changed after the linked commit: the same confirmed link is no longer plain intent.
        var changed = Model([.. Graph().Nodes.Select(n => n.Id == Service ? n with { Hash = "h999" } : n)], Graph().Edges);
        var drifted = DomainCrawler.Markdown(DomainCrawler.Discover(changed, Endpoint, null, 2, 100, index, "abc123"));
        Assert.Contains("None.", drifted[At(drifted, "## As-intended")..At(drifted, "## Unclear")]);
        Assert.Contains("the code changed after the linked commit", drifted[At(drifted, "## Unclear")..]);

        // Saved next to docs/: locations link back into the repository with line anchors; on stdout they stay code.
        var linked = DomainCrawler.Markdown(DomainCrawler.Discover(Graph(), Endpoint, null, 1, 100), "..");
        Assert.Contains("[`src/Api/OrderController.cs:1`](../src/Api/OrderController.cs#L1)", linked);
        Assert.DoesNotContain("](", md);

        var bare = DomainCrawler.Markdown(DomainCrawler.Discover(Graph(), Endpoint, null, 0, 10));
        Assert.Contains("No relations within the bounds.", bare);
        Assert.Contains("Unavailable: run `docwizz requirements import`", bare);
    }

    static int At(string text, string value) => text.IndexOf(value, StringComparison.Ordinal);

    [Fact]
    public void Cross_checks_move_links_the_code_disagrees_with_out_of_as_intended()
    {
        string[] source =
        [
            "public class Orders {",
            "public decimal Refund(decimal a) { return a; }",
            "public decimal Total(int id) { return 10m; }",
            "public bool Overdue(int days) { return days > 30; }",
            "public bool Remind(int id) { return true; }",
            "public string Track(string c) { return c.StartsWith(\"TRK-\") ? \"in transit\" : \"unknown\"; }",
            "public int Available(string sku) { return 0; }",
            "}",
        ];
        var root = Directory.CreateTempSubdirectory("docwizz-checks-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllLines(Path.Combine(root, "src/Orders.cs"), source);
        string Id(string m) => $"cs:Shop.Orders.{m}()";
        string[] methods = ["Refund", "Total", "Overdue", "Remind", "Track", "Available"];
        var model = Model(
            [N("cs:Shop.Orders", "src/Orders.cs"), .. methods.Select((m, i) => N(Id(m), "src/Orders.cs", kind: "method", hash: m) with { Line = i + 2, EndLine = i + 2 })],
            methods.Select(m => E("cs:Shop.Orders", Id(m), "contains")));
        RequirementIndex.Commit Commit(char sha, int workItem, params string[] changed) => new(new string(sha, 40), "verified", [workItem], [], [], "mapped",
            [.. changed.Select(m => new RequirementIndex.Touch(Id(m), "method", m, "src/Orders.cs", "1", "1"))], []);
        RequirementIndex.WorkItem Item(int id, string title, params string[] criteria) => new(id, 1, 1, true, "User Story", "Closed",
            title, null, null, null, null, null, [.. criteria], $"d{id}", []);
        var index = Index() with
        {
            Commits = [Commit('a', 1, "Refund"), Commit('b', 2, "Overdue", "Remind"), Commit('c', 3, "Track", "Available")],
            PullRequests = [],
            WorkItems =
            [
                Item(1, "Partial refunds", "A refund may not exceed the order total."),
                Item(2, "Overdue reminders", "An invoice is overdue 14 days after its due date.", "The reminder names the invoice."),
                Item(3, "Tracking codes", "Codes starting with TRK- are in transit."),
            ],
        };
        try
        {
            var checks = methods.SelectMany(m => DomainCrawler.Discover(model, Id(m), null, 0, 10, index, "abc123", root).Requirements)
                .ToDictionary(r => r.Symbol[(r.Symbol.LastIndexOf('.') + 1)..^2], r => string.Join("; ", r.Checks));
            Assert.Equal("the work item mentions `Total`, which this method doesn't call", checks["Refund"]);
            Assert.Equal("the work item states 14, the code uses 30", checks["Overdue"]);
            Assert.Equal("`Orders.Track`, also linked to the work item, has its values; this method has none", checks["Available"]);
            // Overdue shares the work item with Remind, and Track holds its TRK-: neither is questioned.
            Assert.Equal("", checks["Remind"]);
            Assert.Equal("", checks["Track"]);
            // Both methods in one walk: each is compared with the other, not with itself.
            var both = DomainCrawler.Discover(model, null, 2, 0, 10, index, "abc123", root).Requirements;
            Assert.Equal(["the work item states 14, the code uses 30"], both.Single(r => r.Symbol == Id("Overdue")).Checks);
            Assert.Empty(both.Single(r => r.Symbol == Id("Remind")).Checks);

            var md = DomainCrawler.Markdown(DomainCrawler.Discover(model, Id("Overdue"), null, 0, 10, index, "abc123", root));
            Assert.Contains("None.", md[At(md, "## As-intended")..At(md, "## Unclear")]);
            Assert.Contains("the work item states 14, the code uses 30", md[At(md, "## Unclear")..]);
            // Without the sources there is nothing to check: the link stays As-intended.
            Assert.Empty(DomainCrawler.Discover(model, Id("Overdue"), null, 0, 10, index, "abc123").Requirements.Single().Checks);
        }
        finally { Directory.Delete(root, true); }
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
