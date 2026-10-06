using static DocWizz.Tests.Models;

namespace DocWizz.Tests.Unit;

public class DiffTests
{
    const string Svc = "cs:Shop.Application.OrderService", Order = "cs:Shop.Domain.Order", Repo = "cs:Shop.Infrastructure.OrderRepository";
    static readonly Node Service = N(Svc, "src/Application/OrderService.cs", doc: Doc("Places orders."), hash: "1");
    static readonly Node Entity = N(Order, "src/Domain/Order.cs", doc: Doc("An order."), hash: "1");
    static readonly Node Repository = N(Repo, "src/Infrastructure/OrderRepository.cs", doc: Doc("Stores orders."), hash: "1");

    static DiffResult Compare(CodeModel before, CodeModel after) => Diff.Compare(before, after, Config());

    [Fact]
    public void Added_removed_and_changed_come_from_ids_and_hashes()
    {
        var d = Compare(Model([Service, Entity]), Model([Service with { Hash = "2" }, Repository]));
        Assert.Equal([Repo], d.Added.Select(n => n.Id));
        Assert.Equal([Order], d.Removed.Select(n => n.Id));
        Assert.Equal([Svc], d.Changed.Select(n => n.Id));
        Assert.Contains("modules/src-Application.md", d.Pages);
    }

    [Fact]
    public void Unchanged_model_reports_nothing()
    {
        var m = Model([Service, Entity]);
        var d = Compare(m, m);
        Assert.Empty(d.Added.Concat(d.Removed).Concat(d.Changed));
        Assert.Empty(d.Pages);
    }

    [Fact]
    public void Only_violations_introduced_by_the_change_are_new()
    {
        var bad = E(Order, Repo, "injects");
        var before = Model([Service, Entity, Repository], [bad]);
        Assert.Empty(Compare(before, before).NewViolations);
        var v = Assert.Single(Compare(Model([Service, Entity, Repository]), before).NewViolations);
        Assert.Equal("ARCH-001", v.Rule);
    }

    [Fact]
    public void A_contract_change_under_an_unchanged_doc_is_possibly_stale()
    {
        var m = N("cs:Shop.Application.OrderService.Place(int)", "src/Application/OrderService.cs", kind: "method",
            doc: Doc("Places an order."), hash: "1", parameters: ["id: int"]);
        var renamed = m with { Id = "cs:Shop.Application.OrderService.Place(int, bool)", Parameters = ["id: int", "express: bool"], Hash = "2" };
        var stale = Assert.Single(Compare(Model([m]), Model([renamed])).Stale);
        Assert.Equal(["parameters"], stale.Changes);
    }

    [Fact]
    public void A_new_external_system_is_an_ADR_candidate()
    {
        var redis = new Node("ext:redis", "external", "Redis", "src/Shop.csproj", 1, Tags: ["cache", "detected"]);
        var d = Compare(Model([Service]), Model([Service, redis], [E(Svc, redis.Id, "connects")]));
        Assert.Contains(d.Decisions, x => x.StartsWith("Adopt Redis (cache, detected)") && x.Contains("Shop.Application.OrderService"));
    }
}
