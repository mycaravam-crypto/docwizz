using System.Text.Json;

namespace DocWizz.Tests.Component;

public class EdmxCodeLinksTests
{
    const string Edmx = "fixture-legacy/Shop/Models/GraphGolden.edmx";
    const string CSharp = "tests/fixtures/edmx-code/LegacyMvc.cs";

    static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Join(d.FullName, Edmx)) && File.Exists(Path.Join(d.FullName, CSharp)))
                return d.FullName;
        throw new InvalidOperationException("EDMX code fixture was not found");
    }

    static (EdmxParser.Report Facts, CodeModel Model, EdmxCodeLinks.Report Links) Fixture()
    {
        var root = Root();
        var facts = EdmxParser.Parse(root, Edmx);
        var (nodes, edges) = CSharpScanner.Scan(root, [Path.Join(root, CSharp)]);
        var model = new CodeModel(null, nodes, edges);
        return (facts, model, EdmxCodeLinks.Build(facts, model));
    }

    [Fact]
    public void Exact_ef6_type_and_dbcontext_links_are_source_backed()
    {
        var (facts, model, report) = Fixture();
        Assert.Empty(facts.Findings);
        Assert.Equal("docwizz.edmx-code-links/v1", report.Schema);
        Assert.Equal("docwizz.edmx-graph/v1", report.GraphSchema);
        Assert.Equal(5, report.Links.Count(x => x.Kind == "entity-class"));
        Assert.Equal(4, report.Links.Count(x => x.Kind == "dbcontext-persists"));
        Assert.Contains(model.Nodes, n => n.Id == "cs:Legacy.LegacyContext" && n.Tags?.Contains("dbcontext") == true);
        Assert.Contains(report.Links, x => x.Entity == "Legacy.Customer" &&
            x.CodeSymbol == "cs:Legacy.Customer" && x.Kind == "entity-class");
        Assert.All(report.Links, link =>
        {
            Assert.Equal(Edmx, link.EdmxSource.File);
            Assert.True(link.EdmxSource.Line > 0);
            Assert.Equal(CSharp, link.CodeSource.File.Replace('\\', '/'));
            Assert.True(link.CodeSource.Line > 0);
            Assert.Equal("observed", link.Status);
        });
        Assert.DoesNotContain(report.Links, x => x.Entity == "Legacy.Tag");
        Assert.Contains(report.Unknowns, x => x.Entity == "Legacy.Tag" && x.Kind == "csharp-type");
        Assert.Contains(report.Unknowns, x => x.Entity == "Legacy.PremiumCustomer" && x.Kind == "dbcontext");
        using var doc = JsonDocument.Parse(EdmxCodeLinks.Json(report));
        Assert.Equal(EdmxCodeLinks.Schema, doc.RootElement.GetProperty("schema").GetString());
    }

    [Fact]
    public void Controller_paths_only_follow_scanner_edges_and_do_not_invent_routes()
    {
        var (_, _, report) = Fixture();
        Assert.NotEmpty(report.Traces);
        Assert.Contains(report.Traces, t => t.Action.Contains("OrdersController.Index", StringComparison.Ordinal) &&
            t.Entity == "Legacy.Customer" && t.Route == "orders" && t.RouteStatus == "observed");
        Assert.Contains(report.Traces, t => t.Action.Contains("PlainController.List", StringComparison.Ordinal) &&
            t.Entity == "Legacy.Order" && t.Route is null && t.RouteStatus == "unknown");
        Assert.DoesNotContain(report.Traces, t => t.Action.Contains("Unrelated", StringComparison.Ordinal));
        Assert.All(report.Traces, t =>
        {
            Assert.NotEmpty(t.Hops);
            Assert.Equal("persists", t.Hops[^1].Kind);
            Assert.True(t.EdmxSource.Line > 0);
            Assert.All(t.Hops, hop =>
            {
                Assert.Equal(CSharp, hop.FromDeclaration.File.Replace('\\', '/'));
                Assert.True(hop.FromDeclaration.Line > 0);
                Assert.True(hop.ToDeclaration.Line > 0);
            });
        });
        Assert.All(report.DomainEvidence, hint =>
        {
            Assert.Equal("inferred", hint.Status);
            Assert.True(hint.EdmxSource.Line > 0);
            Assert.True(hint.CodeSource.Line > 0);
        });
        Assert.NotEmpty(report.DomainEvidence);
        Assert.Equal(EdmxCodeLinks.Json(report), EdmxCodeLinks.Json(Fixture().Links));
    }

    [Fact]
    public void Without_any_csharp_evidence_all_entities_are_unknown()
    {
        var facts = EdmxParser.Parse(Root(), Edmx);
        var report = EdmxCodeLinks.Build(facts, new CodeModel(null, [], []));
        Assert.Empty(report.Links);
        Assert.Empty(report.Traces);
        Assert.Empty(report.DomainEvidence);
        Assert.Equal(facts.Entities.Count, report.Unknowns.Count(x => x.Kind == "csharp-type"));
        Assert.DoesNotContain(report.Unknowns, x => x.Status != "unknown");
    }
}
