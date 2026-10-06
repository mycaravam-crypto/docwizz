using static DocWizz.Tests.Models;

namespace DocWizz.Tests.Unit;

// `tests` edges as change signal: which test code is linked to what a change touches. Structural, not coverage.
public class TestTraceabilityTests
{
    const string Iface = "cs:Shop.IOrders", IPlace = "cs:Shop.IOrders.Place(int)", Impl = "cs:Shop.Orders", Place = "cs:Shop.Orders.Place(int)",
        Cancel = "cs:Shop.Orders.Cancel(int)", Report = "cs:Shop.Report", PlaceTest = "cs:Shop.Tests.OrdersTests.Places(Shop.Orders)",
        IfaceTest = "cs:Shop.Tests.OrdersTests.ThroughInterface()";

    static readonly Node[] Nodes =
    [
        N(Iface, "src/IOrders.cs", kind: "interface", hash: "1"), N(IPlace, "src/IOrders.cs", kind: "method", hash: "1"),
        N(Impl, "src/Orders.cs", hash: "1"), N(Place, "src/Orders.cs", kind: "method", hash: "1"), N(Cancel, "src/Orders.cs", kind: "method", hash: "1"),
        N(Report, "src/Report.cs", hash: "1"),
    ];
    static readonly Edge[] Edges =
    [
        E(Iface, IPlace, "contains"), E(Impl, Place, "contains"), E(Impl, Cancel, "contains"),
        E(Impl, Iface, "implements"), E(Place, IPlace, "implements"),
        E(PlaceTest, Place, "tests"), E(IfaceTest, Iface, "tests"),
    ];

    [Fact]
    public void Direct_link_and_indirect_links_through_interface_implementation_and_members()
    {
        var links = new TestLinks(Model(Nodes, Edges));
        Assert.Equal([new LinkedTest(PlaceTest)], links.Of(Place));
        Assert.Equal([new LinkedTest(PlaceTest, Place)], links.Of(IPlace));              // via the implementation
        Assert.Equal([new LinkedTest(PlaceTest, Place), new LinkedTest(IfaceTest, Iface)], links.Of(Impl));   // via interface, via member
        Assert.Empty(links.Of(Cancel));
        Assert.Empty(links.Of(Report));
    }

    [Fact]
    public void Test_names_for_reports_and_pages()
    {
        Assert.Equal("Shop.Tests.OrdersTests.Places", TestLinks.Name(PlaceTest));
        Assert.Equal("OrdersTests.Places", TestLinks.Short(PlaceTest));
        Assert.Equal("Table.test.ts", TestLinks.Short("ts:web/src/__tests__/Table.test.ts"));
    }

    [Fact]
    public void Diff_lists_linked_tests_and_symbols_without_any()
    {
        var before = Model(Nodes, Edges);
        var after = Model([.. Nodes.Select(n => n.Id is Place or Cancel ? n with { Hash = "2" } : n),
            N("cs:Shop.Orders.Refund(int)", "src/Orders.cs", kind: "method"), N("sql:dbo.orders", "db/V1.sql", kind: "table")],
            [.. Edges, E(Impl, "cs:Shop.Orders.Refund(int)", "contains")]);
        var t = Diff.Compare(before, after, Config()).Tests.ToDictionary(x => x.Symbol.Id);

        Assert.Equal([PlaceTest], t[Place].Tests.Select(l => l.Test));
        Assert.False(t[Place].Added);
        Assert.Empty(t[Cancel].Tests);
        Assert.True(t["cs:Shop.Orders.Refund(int)"].Added);
        Assert.Empty(t["cs:Shop.Orders.Refund(int)"].Tests);
        Assert.DoesNotContain("sql:dbo.orders", t.Keys);   // tables aren't something tests link to
    }

    [Fact]
    public void Report_says_a_link_is_not_coverage()
    {
        var before = Model(Nodes, Edges);
        var after = Model(Nodes.Select(n => n.Id == Place ? n with { Hash = "2" } : n), Edges);
        var o = new StringWriter();
        Diff.Report(Diff.Compare(before, after, Config()), "test", o);
        Assert.Contains("Test traceability (1 of 1 changed symbols linked) — a link means test code uses the symbol; it is not code coverage", o.ToString());
        Assert.Contains("~ Shop.Orders.Place(int)  ← Shop.Tests.OrdersTests.Places", o.ToString());
    }

    [Fact]
    public void Analyzer_tested_flag_uses_the_same_links()
    {
        var cfg = Config("patterns: { all: { level: medium, sections: [summary] } }");
        var tested = Analyzer.Analyze(Model(Nodes, Edges), cfg).Where(i => i.Tested).Select(i => i.Node.Id).Order();
        Assert.Equal([Iface, IPlace, Impl, Place], tested);
    }

    [Theory]
    [InlineData("high", true)]
    [InlineData("medium", true)]
    [InlineData("low", false)]
    [InlineData("always", false)]
    public void Require_tests_accepts_tracked_levels_only(string level, bool ok)
    {
        var yaml = global::Config.Default.Replace("  # require_tests: high", $"  require_tests: {level}");
        if (ok) Assert.Equal(level, Config(yaml).Check.RequireTests);
        else Assert.Contains("check.require_tests", Assert.Throws<ArgumentException>(() => Config(yaml)).Message);
    }
}
