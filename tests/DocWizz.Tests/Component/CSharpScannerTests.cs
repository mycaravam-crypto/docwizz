namespace DocWizz.Tests.Component;

// Scanner → CodeModel: C# snippets in, nodes and edges out.
public class CSharpScannerTests
{
    static (Dictionary<string, Node> Nodes, HashSet<(string, string, string)> Edges) Scan(params (string, string)[] files)
    {
        using var src = new Sources(files);
        var (nodes, edges) = CSharpScanner.Scan(src.Root, src.Files);
        return (CodeModel.MergeHashes(nodes).DistinctBy(n => n.Id).ToDictionary(n => n.Id), edges.Select(e => (e.Kind, e.From, e.To)).ToHashSet());
    }

    [Fact]
    public void Constructor_injection_interface_dispatch_and_calls()
    {
        var (nodes, edges) = Scan(("Orders.cs", """
            namespace Shop;
            /// <summary>Places orders.</summary>
            public interface IOrders { void Place(int id); }
            public class Orders(IRepo repo) : IOrders { public void Place(int id) => repo.Save(id); }
            public interface IRepo { void Save(int id); }
            """));
        Assert.Contains(("injects", "cs:Shop.Orders", "cs:Shop.IRepo"), edges);
        Assert.Contains(("implements", "cs:Shop.Orders", "cs:Shop.IOrders"), edges);
        Assert.Contains(("calls", "cs:Shop.Orders.Place(int)", "cs:Shop.IRepo.Save(int)"), edges);
        Assert.Contains("<summary>Places orders.</summary>", nodes["cs:Shop.IOrders"].Doc);
    }

    // Regression: a record's compiler-generated copy constructor once showed up as the record injecting itself.
    [Fact]
    public void Record_copy_constructor_is_not_injection()
    {
        var (_, edges) = Scan(("Money.cs", "namespace Shop; public record Money(decimal Amount, string Currency);"));
        Assert.DoesNotContain(("injects", "cs:Shop.Money", "cs:Shop.Money"), edges);
    }

    // Regression: a partial class's hash must not depend on which file is read first.
    [Fact]
    public void Partial_class_hash_is_independent_of_file_order()
    {
        (string, string) a = ("A.cs", "namespace Shop; public partial class Report { public int Total() => 1; }");
        (string, string) b = ("B.cs", "namespace Shop; public partial class Report { int Extra() => 2; }");
        Assert.Equal(Scan(a, b).Nodes["cs:Shop.Report"].Hash, Scan(b, a).Nodes["cs:Shop.Report"].Hash);
    }

    // Regression: MapGroup prefixes and group-level RequireAuthorization reach the endpoints mapped on the group.
    [Fact]
    public void Minimal_api_group_prefix_and_authorization()
    {
        var (nodes, _) = Scan(("Program.cs", """
            var app = WebApplication.Create();
            var admin = app.MapGroup("/admin").RequireAuthorization();
            admin.MapDelete("/cache", () => Results.NoContent());
            app.MapGet("/health", () => "ok").AllowAnonymous();
            """));
        var delete = nodes["cs:endpoint:DELETE /admin/cache"];
        Assert.Contains("authorize", delete.Tags!);
        Assert.Contains("anonymous", nodes["cs:endpoint:GET /health"].Tags!);
    }

    [Fact]
    public void Controller_actions_are_endpoints_with_binding_sources()
    {
        var (nodes, _) = Scan(("OrdersController.cs", """
            using Microsoft.AspNetCore.Mvc;
            namespace Shop;
            [ApiController, Route("api/orders")]
            public class OrdersController : ControllerBase
            {
                [HttpGet("{id}")]
                public IActionResult Get(int id, [FromQuery] bool full) => Ok();
            }
            """));
        var get = nodes["cs:Shop.OrdersController.Get(int, bool)"];
        Assert.Equal(["endpoint", "GET"], get.Tags!.Take(2));
        Assert.Equal("api/orders/{id}", get.Route);
        Assert.Contains("controller", nodes["cs:Shop.OrdersController"].Tags!);
    }
}
