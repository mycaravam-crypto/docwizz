using static DocWizz.Tests.Models;

namespace DocWizz.Tests.Unit;

// SEC-001…005 on small models: positive, negative and ambiguous cases per rule.
public class SecurityTests
{
    static readonly Config On = Config(global::Config.Default.Replace("security:\n  enabled: false", "security:\n  enabled: true"));

    static Node Endpoint(string verb, string route, params string[] auth) =>
        N($"cs:endpoint:{verb} /{route}", "src/Program.cs", kind: "endpoint", tags: ["endpoint", verb, "minimal-api", .. auth], route: route)
            with { Name = $"{verb} /{route}" };

    static List<Violation> Check(IEnumerable<Node> nodes, IEnumerable<Edge>? edges = null, Config? config = null) =>
        Architecture.Check(Model(nodes, edges), config ?? On).Violations.Where(v => v.Rule.StartsWith("SEC-")).ToList();

    [Fact]
    public void Off_by_default() =>
        Assert.Empty(Architecture.Check(Model([Endpoint("POST", "orders")]), Config()).Violations);

    [Fact]
    public void SEC_001_read_endpoint_without_any_auth_metadata()
    {
        var v = Assert.Single(Check([Endpoint("GET", "orders")]));
        Assert.Equal(("SEC-001", Level.Medium, "confidentiality", Origin.Fact), (v.Rule, v.Severity, v.Concern, v.Basis));
        Assert.Contains("declares neither authorization nor anonymous access", v.Example);
        Assert.NotNull(v.Risk);
    }

    // Explicit [AllowAnonymous] on a read is a decision someone made, not missing metadata.
    [Theory]
    [InlineData("authorize")]
    [InlineData("anonymous")]
    public void SEC_001_not_for_declared_reads(string auth) => Assert.Empty(Check([Endpoint("GET", "orders", auth)]));

    [Theory]
    [InlineData("POST", null, "declares no authorization")]
    [InlineData("DELETE", "anonymous", "explicitly anonymous")]
    public void SEC_002_mutating_endpoint_without_authorization(string verb, string? auth, string detected)
    {
        var v = Assert.Single(Check([Endpoint(verb, "orders", auth is null ? [] : [auth])]));
        Assert.Equal(("SEC-002", Level.High, "integrity"), (v.Rule, v.Severity, v.Concern));
        Assert.Contains(detected, v.Example);
    }

    [Fact]
    public void SEC_002_not_for_authorized_mutations() => Assert.Empty(Check([Endpoint("PUT", "orders", "authorize")]));

    const string Db = "cs:Shop.Infrastructure.ShopDb", Ctrl = "cs:Shop.Api.OrdersController", Get = "cs:Shop.Api.OrdersController.Get(int)",
        Svc = "cs:Shop.Application.OrderService";
    static readonly Node[] Layers =
    [
        N(Db, "src/Infrastructure/ShopDb.cs", tags: ["dbcontext"]), N(Ctrl, "src/Api/OrdersController.cs", tags: ["controller"]),
        N(Get, "src/Api/OrdersController.cs", kind: "method", tags: ["endpoint", "GET", "authorize"], route: "orders/{id}"),
        N(Svc, "src/Application/OrderService.cs"),
    ];

    [Fact]
    public void SEC_003_controller_reaching_the_DbContext_directly()
    {
        var v = Assert.Single(Check(Layers, [E(Ctrl, Get, "contains"), E(Get, Db, "accesses"), E(Ctrl, Db, "injects")]));
        Assert.Equal(("SEC-003", "api", "infrastructure"), (v.Rule, v.FromLayer, v.ToLayer));
        Assert.Equal("Shop.Api.OrdersController → ShopDb", v.To);   // one finding per controller, not per action
    }

    [Fact]
    public void SEC_003_not_for_the_application_layer() => Assert.Empty(Check(Layers, [E(Svc, Db, "injects")]));

    [Fact]
    public void SEC_003_minimal_api_handler_injecting_the_DbContext()
    {
        var post = Endpoint("POST", "orders", "authorize");
        Assert.Equal("POST /orders → ShopDb", Assert.Single(Check([.. Layers, post], [E(post.Id, Db, "injects")])).To);
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore.Identity", true)]
    [InlineData("org.springframework.security.core", true)]
    [InlineData("Microsoft.AspNetCoreExtras", false)]          // a prefix only counts on a namespace boundary
    [InlineData("Microsoft.Extensions.Logging", false)]
    public void SEC_004_domain_importing_a_security_or_web_framework(string ns, bool flagged)
    {
        var order = N("cs:Shop.Domain.Order", "src/Domain/Order.cs");
        var found = Check([order], [E(order.Id, $"ns:{ns}", "uses-namespace")]);
        Assert.Equal(flagged, found.Any(v => v.Rule == "SEC-004" && v.To == ns));
    }

    [Fact]
    public void SEC_004_only_applies_to_the_domain_layer()
    {
        var ctrl = N(Ctrl, "src/Api/OrdersController.cs");
        Assert.Empty(Check([ctrl], [E(ctrl.Id, "ns:Microsoft.AspNetCore.Mvc", "uses-namespace")]));
    }

    static readonly Node Sql = new("ext:sqlserver", "external", "SQL Server", "src/Shop.csproj", 1, Tags: ["database", "inferred"]);
    static readonly Node Rates = new("ext:http:rates.example.org", "external", "rates.example.org", "src/Shop.csproj", 1, Tags: ["http-api", "detected"]);

    [Fact]
    public void SEC_005_unprotected_endpoint_whose_flow_reaches_a_database()
    {
        var get = Endpoint("GET", "orders", "anonymous");
        Node[] members = [N(Svc + ".List()", "src/Application/OrderService.cs", kind: "method"), N(Db + ".Orders", "src/Infrastructure/ShopDb.cs", kind: "property")];
        var found = Check([get, .. Layers, .. members, Sql], [E(get.Id, Svc + ".List()"), E(Svc + ".List()", Db + ".Orders"), E(Db, Sql.Id, "connects"),
            E(Svc, Svc + ".List()", "contains"), E(Db, Db + ".Orders", "contains")]);
        var v = Assert.Single(found, v => v.Rule == "SEC-005");
        Assert.Equal((Origin.Inferred, "database", "GET /orders → SQL Server"), (v.Basis, v.ToLayer, v.To));
        Assert.Contains("(anonymous) can reach SQL Server (inferred)", v.Example);
    }

    [Fact]
    public void SEC_005_not_when_authorized_or_the_system_is_not_sensitive()
    {
        var authorized = Endpoint("GET", "orders", "authorize");
        Assert.DoesNotContain(Check([authorized, Sql], [E(authorized.Id, Sql.Id, "connects")]), v => v.Rule == "SEC-005");
        var open = Endpoint("GET", "rates", "anonymous");
        Assert.DoesNotContain(Check([open, Rates], [E(open.Id, Rates.Id, "connects")]), v => v.Rule == "SEC-005");
    }

    [Fact]
    public void Severity_can_be_overridden_and_unknown_rules_fail_fast()
    {
        var cfg = Config(global::Config.Default.Replace("security:\n  enabled: false", "security:\n  enabled: true\n  severity: { SEC-001: low }"));
        Assert.Equal(Level.Low, Assert.Single(Check([Endpoint("GET", "orders")], config: cfg)).Severity);
        var e = Assert.Throws<ArgumentException>(() => Config(global::Config.Default.Replace("security:\n  enabled: false", "security:\n  severity: { SEC-009: low }")));
        Assert.Contains("unknown rule 'SEC-009'", e.Message);
    }

    [Fact]
    public void A_new_unprotected_endpoint_is_a_new_finding_for_check_since()
    {
        var before = Model([Endpoint("GET", "orders", "authorize")]);
        var after = Model([Endpoint("GET", "orders", "authorize"), Endpoint("DELETE", "orders")]);
        var v = Assert.Single(Diff.Compare(before, after, On).NewViolations);
        Assert.Equal(("SEC-002", "DELETE /orders"), (v.Rule, v.To));
    }

    [Fact]
    public void No_rule_claims_compliance() =>
        Assert.All(Security.Rules, r => Assert.DoesNotContain("complian", r.Risk + r.Title, StringComparison.OrdinalIgnoreCase));
}
