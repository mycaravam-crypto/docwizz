using static DocWizz.Tests.Models;

namespace DocWizz.Tests.Unit;

public class ArchitectureTests
{
    const string Ctrl = "cs:Shop.Api.OrdersController", Svc = "cs:Shop.Application.OrderService",
        Order = "cs:Shop.Domain.Order", Repo = "cs:Shop.Infrastructure.OrderRepository";

    static readonly Node[] Layered =
    [
        N(Ctrl, "src/Api/OrdersController.cs"), N(Svc, "src/Application/OrderService.cs"),
        N(Order, "src/Domain/Order.cs"), N(Repo, "src/Infrastructure/OrderRepository.cs"),
    ];

    static ArchitectureResult Check(params Edge[] edges) => Architecture.Check(Model(Layered, edges), Config().Architecture);

    [Fact]
    public void Allowed_dependencies_are_no_violation()
    {
        var r = Check(E(Ctrl, Svc), E(Svc, Order), E(Repo, Order, "implements"));
        Assert.Empty(r.Violations);
        Assert.Contains(new LayerDependency("api", "application", 1, true), r.LayerDependencies);
    }

    [Fact]
    public void Forbidden_direction_is_ARCH_001_high()
    {
        var v = Assert.Single(Check(E(Order, Repo)).Violations);
        Assert.Equal(("ARCH-001", "domain", "infrastructure", Level.High), (v.Rule, v.FromLayer, v.ToLayer, v.Severity));
        Assert.Equal("src/Infrastructure/OrderRepository.cs", v.To);
    }

    [Fact]
    public void Skipping_a_layer_is_ARCH_004_naming_the_layer_bypassed()
    {
        // application → infrastructure isn't allowed, but infrastructure → application → domain is a path: ui-style skip.
        var cfg = Config("""
            architecture:
              layers: { ui: ["ui/*"], app: ["app/*"], data: ["data/*"] }
              allow: { ui: [app], app: [data], data: [] }
            """);
        var model = Model([N("ts:ui/Page", "ui/Page.ts"), N("ts:data/Db", "data/Db.ts")], [E("ts:ui/Page", "ts:data/Db")]);
        var v = Assert.Single(Architecture.Check(model, cfg.Architecture).Violations);
        Assert.Equal("ARCH-004", v.Rule);
        Assert.Contains("bypassing app", v.Example);
        Assert.Equal(Level.Low, v.Severity);
    }

    [Fact]
    public void Direct_HTTP_from_a_layer_without_http_is_ARCH_002()
    {
        var page = N("vue:src/components/Orders.vue", "src/components/Orders.vue", kind: "component");
        var r = Architecture.Check(Model([page], [E(page.Id, "http:GET /api/orders", "http")]), Config().Architecture);
        var v = Assert.Single(r.Violations);
        Assert.Equal(("ARCH-002", "ui", "http", Level.Medium), (v.Rule, v.FromLayer, v.ToLayer, v.Severity));
    }

    [Fact]
    public void One_violation_per_file_pair_and_configured_severity_wins()
    {
        var cfg = Config(global::Config.Default.Replace("severity: {}", "severity: { ARCH-001: low }"));
        var save = "cs:Shop.Domain.Order.Save()";
        var model = Model([.. Layered, N(save, "src/Domain/Order.cs", kind: "method")], [E(Order, Repo, "injects"), E(save, Repo)]);
        var v = Assert.Single(Architecture.Check(model, cfg.Architecture).Violations);
        Assert.Equal(Level.Low, v.Severity);
    }

    [Fact]
    public void Folder_cycles_are_reported_once_per_strongly_connected_set()
    {
        var a = N("ts:a/x", "a/x.ts"); var b = N("ts:b/y", "b/y.ts"); var c = N("ts:c/z", "c/z.ts");
        var r = Architecture.Check(Model([a, b, c], [E(a.Id, b.Id), E(b.Id, c.Id), E(c.Id, a.Id)]), new ArchitectureConfig());
        Assert.Equal(["a", "b", "c"], Assert.Single(r.Cycles));
    }

    [Fact]
    public void Unknown_severity_fails_fast() =>
        Assert.Throws<ArgumentException>(() => Config(global::Config.Default.Replace("severity: {}", "severity: { ARCH-001: severe }")));
}
