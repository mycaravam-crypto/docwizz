using static DocWizz.Tests.Models;

namespace DocWizz.Tests.Unit;

public class AnalyzerTests
{
    const string Iface = "cs:Shop.IOrderService", Impl = "cs:Shop.OrderService", Other = "cs:Shop.Invoice", Test = "cs:Shop.Tests.OrderServiceTests.Places()";

    // Every symbol needs a summary at medium level, so small models produce items.
    static readonly Config AllMedium = Config("patterns: { all: { level: medium, sections: [summary] } }");

    static List<DocumentationItem> Analyze(params Edge[] edges) => Analyzer.Analyze(Model(
        [N(Iface, "src/IOrderService.cs", kind: "interface", doc: Doc("Places orders.")), N(Impl, "src/OrderService.cs"), N(Other, "src/Invoice.cs")],
        [E(Impl, Iface, "implements"), .. edges]), AllMedium);

    [Fact]
    public void Missing_summary_is_a_gap_written_summary_is_documented()
    {
        var items = Analyze().ToDictionary(i => i.Node.Id);
        Assert.Equal(Status.Documented, items[Iface].Status);
        Assert.Equal(Status.Undocumented, items[Other].Status);
        Assert.Contains("summary", items[Other].Missing);
        Assert.Equal(Origin.Written, items[Iface].Sections["summary"].Origin);
    }

    [Fact]
    public void An_implementation_inherits_its_interface_summary()
    {
        var impl = Analyze().Single(i => i.Node.Id == Impl);
        Assert.Equal(Status.Documented, impl.Status);
        Assert.Equal("Places orders.", impl.Sections["summary"].Text);
    }

    [Fact]
    public void A_test_through_the_interface_marks_the_implementation_tested()
    {
        Assert.All(Analyze(), i => Assert.False(i.Tested));
        var tested = Analyze(E(Test, Iface, "tests")).Where(i => i.Tested).Select(i => i.Node.Id).Order();
        Assert.Equal([Iface, Impl], tested);
    }

    [Fact]
    public void Coverage_counts_documented_items()
    {
        Assert.Equal(200 / 3.0, Analyzer.Coverage(Analyze()), 3);
    }

    [Fact]
    public void Symbols_below_medium_need_no_docs() =>
        Assert.Empty(Analyzer.Analyze(Model([N("cs:Shop.Invoice", "src/Invoice.cs")]), Config()));

    [Fact]
    public void Only_high_level_gaps_are_critical()
    {
        var endpoint = N("cs:endpoint:GET /orders", "src/Program.cs", kind: "endpoint", tags: ["endpoint", "GET"], route: "orders");
        var item = Assert.Single(Analyzer.Analyze(Model([endpoint]), Config()));
        Assert.Equal(Level.High, item.Level);
        Assert.True(Analyzer.IsCritical(item));
    }
}
