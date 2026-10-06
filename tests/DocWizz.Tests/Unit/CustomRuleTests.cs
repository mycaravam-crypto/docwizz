using static DocWizz.Tests.Models;

namespace DocWizz.Tests.Unit;

// architecture.rules: user-defined dependency rules over the same code model as the built-in ones.
public class CustomRuleTests
{
    const string Ctrl = "cs:Shop.Api.OrdersController", Get = "cs:Shop.Api.OrdersController.Get(int)", Db = "cs:Shop.Infrastructure.ShopDb",
        Order = "cs:Shop.Domain.Order", Billing = "cs:Shop.Billing.Invoices", Contract = "cs:Shop.Billing.Contracts.IInvoices",
        Ordering = "cs:Shop.Ordering.Checkout";

    static readonly Node[] Nodes =
    [
        N(Ctrl, "src/Api/OrdersController.cs", tags: ["controller"]), N(Get, "src/Api/OrdersController.cs", kind: "method"),
        N(Db, "src/Infrastructure/ShopDb.cs", tags: ["dbcontext"]), N(Order, "src/Domain/Order.cs"),
        N(Billing, "src/Billing/Invoices.cs"), N(Contract, "src/Billing/Contracts/IInvoices.cs", kind: "interface"),
        N(Ordering, "src/Ordering/Checkout.cs"),
    ];
    static readonly Edge[] Contains = [E(Ctrl, Get, "contains")];

    static List<Violation> Check(string rules, params Edge[] edges) =>
        Architecture.Check(Model(Nodes, [.. Contains, .. edges]), WithRules(rules).Architecture).Violations;

    const string NoEf = """
        - id: ARCH-DOMAIN-001
          severity: high
          description: The domain stays persistence-ignorant.
          from: { layer: domain }
          forbid: { package: ["Microsoft.EntityFrameworkCore", "org.springframework.data"] }
        """;

    [Fact]
    public void Forbidden_package_by_prefix()
    {
        var v = Assert.Single(Check(NoEf, E(Order, "ns:Microsoft.EntityFrameworkCore.Metadata", "uses-namespace"),
            E(Order, "ns:Microsoft.Extensions.Logging", "uses-namespace")));
        Assert.Equal(("ARCH-DOMAIN-001", Level.High, true), (v.Rule, v.Severity, v.Custom));
        Assert.Equal("Microsoft.EntityFrameworkCore.Metadata", v.To);
        Assert.Equal("src/Domain/Order.cs", v.FromFile);
    }

    [Fact]
    public void Prefix_matches_whole_segments_only() =>
        Assert.Empty(Check(NoEf, E(Order, "ns:Microsoft.EntityFrameworkCoreExtras", "uses-namespace")));

    [Fact]
    public void Members_match_their_type_tag_and_edge_kinds_narrow_the_rule()
    {
        const string rules = """
            - id: ARCH-API-001
              from: { tag: controller }
              forbid: { to: { tag: dbcontext }, edge: [injects, accesses] }
            """;
        var v = Assert.Single(Check(rules, E(Get, Db, "accesses")));
        Assert.Equal(("ARCH-API-001", Level.Medium, "api", "infrastructure"), (v.Rule, v.Severity, v.FromLayer, v.ToLayer));
        Assert.Contains("OrdersController.Get accesses", v.Example);
        Assert.Empty(Check(rules, E(Get, Db, "calls")));   // not one of the listed kinds
    }

    [Fact]
    public void Except_keeps_declared_contracts_allowed()
    {
        const string rules = """
            - id: BC-001
              from: { path: "src/Ordering/*" }
              forbid: { to: { path: "src/Billing/*" } }
              except: { path: "src/Billing/Contracts/*" }
            """;
        Assert.Empty(Check(rules, E(Ordering, Contract, "injects")));
        Assert.Equal("src/Billing/Invoices.cs", Assert.Single(Check(rules, E(Ordering, Billing, "creates"))).To);
    }

    [Fact]
    public void Built_in_rules_are_unchanged_by_custom_ones()
    {
        var withRules = Check(NoEf, E(Order, Db, "injects"));
        var v = Assert.Single(withRules);
        Assert.Equal(("ARCH-001", false), (v.Rule, v.Custom));
    }

    [Fact]
    public void Architecture_severity_overrides_the_rules_own() =>
        Assert.Equal(Level.Low, Assert.Single(Architecture.Check(Model(Nodes, [E(Order, "ns:Microsoft.EntityFrameworkCore", "uses-namespace")]),
            WithRules(NoEf, "{ ARCH-DOMAIN-001: low }").Architecture).Violations).Severity);

    [Theory]
    [InlineData("- { from: { layer: domain }, forbid: { package: [X] } }", "needs an id")]
    [InlineData("- { id: ARCH-001, from: { layer: domain }, forbid: { package: [X] } }", "reserved")]
    [InlineData("- { id: R1, forbid: { package: [X] } }", "`from` must set")]
    [InlineData("- { id: R1, from: { layer: domain }, forbid: {} }", "`forbid` must set")]
    [InlineData("- { id: R1, from: { layer: core }, forbid: { package: [X] } }", "unknown layer 'core'")]
    [InlineData("- { id: R1, from: { layer: domain }, forbid: { edge: [uses] } }", "unknown edge kind 'uses'")]
    [InlineData("- { id: R1, severity: urgent, from: { layer: domain }, forbid: { package: [X] } }", "unknown severity")]
    [InlineData("- { id: R1, from: { layer: domain }, forbid: { package: [X] } }\n- { id: R1, from: { layer: domain }, forbid: { package: [Y] } }", "duplicate id")]
    [InlineData("- { id: R1, from: { layr: domain }, forbid: { package: [X] } }", "layr")]
    public void Invalid_rules_fail_fast_naming_the_problem(string rule, string message)
    {
        var e = Assert.Throws<ArgumentException>(() => WithRules(rule));
        Assert.Contains(message, e.Message);
    }
}
