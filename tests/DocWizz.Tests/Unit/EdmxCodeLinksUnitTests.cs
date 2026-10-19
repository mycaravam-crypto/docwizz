namespace DocWizz.Tests.Unit;

public class EdmxCodeLinksUnitTests
{
    static readonly EdmxParser.Evidence Source = new("m.edmx", 4);
    static EdmxParser.Report Facts() => new(EdmxParser.Schema, "m.edmx",
        [new EdmxParser.Entity("N.Customer", null, [], Source)], [], [], [], [], [], [], [], [], [], []);

    [Fact]
    public void Wrong_namespace_or_ambiguous_csharp_id_does_not_create_links()
    {
        var source = Facts();
        var wrong = new Node("cs:Other.Customer", "class", "Customer", "Wrong.cs", 3);
        var report = EdmxCodeLinks.Build(source, new CodeModel(null, [wrong], []));
        Assert.Empty(report.Links);
        Assert.Contains(report.Unknowns, x => x.Kind == "csharp-type");
        var duplicate = new Node("cs:N.Customer", "class", "Customer", "One.cs", 1);
        report = EdmxCodeLinks.Build(source, new CodeModel(null, [duplicate, duplicate with { File = "Two.cs" }], []));
        Assert.Empty(report.Links);
        Assert.Contains(report.Unknowns, x => x.Reason.Contains("Ambiguous", StringComparison.Ordinal));
    }

    [Fact]
    public void A_route_without_observed_dependency_is_not_a_persistence_path()
    {
        var facts = Facts();
        var entity = new Node("cs:N.Customer", "class", "Customer", "Customer.cs", 5);
        var db = new Node("cs:N.Db", "class", "Db", "Db.cs", 8, Tags: ["dbcontext"]);
        var controller = new Node("cs:N.HomeController", "class", "HomeController", "Home.cs", 1, Tags: ["controller"]);
        var action = new Node("cs:N.HomeController.Index()", "method", "Index", "Home.cs", 4, Route: "home");
        var model = new CodeModel(null, [entity, db, controller, action],
            [new Edge(db.Id, entity.Id, "persists"), new Edge(controller.Id, action.Id, "contains")]);
        var result = EdmxCodeLinks.Build(facts, model);
        Assert.Equal(2, result.Links.Count);
        Assert.Empty(result.Traces);
        Assert.Contains(result.Unknowns, x => x.Kind == "controller-path");
        Assert.Empty(result.DomainEvidence);
    }

    [Fact]
    public void Domain_hints_never_become_confirmed()
    {
        var facts = Facts();
        var entity = new Node("cs:N.Customer", "class", "Customer", "Customer.cs", 5);
        var db = new Node("cs:N.Db", "class", "Db", "Db.cs", 8, Tags: ["dbcontext"]);
        var controller = new Node("cs:N.HomeController", "class", "HomeController", "Home.cs", 1, Tags: ["controller"]);
        var action = new Node("cs:N.HomeController.Index()", "method", "Index", "Home.cs", 4);
        var code = new CodeModel(null, [entity, db, controller, action],
            [new Edge(db.Id, entity.Id, "persists"), new Edge(controller.Id, action.Id, "contains"),
                new Edge(action.Id, db.Id, "accesses")]);
        var graph = EdmxCodeLinks.Build(facts, code);
        var path = Assert.Single(graph.Traces);
        Assert.Null(path.Route);
        Assert.Equal("unknown", path.RouteStatus);
        Assert.Equal(["accesses", "persists"], path.Hops.Select(x => x.Kind));
        Assert.All(graph.DomainEvidence, x => Assert.Equal("inferred", x.Status));
        Assert.NotEmpty(graph.DomainEvidence);
    }
}
