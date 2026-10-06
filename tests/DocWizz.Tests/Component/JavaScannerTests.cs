namespace DocWizz.Tests.Component;

public class JavaScannerTests
{
    const string J = "java:com.example.";

    static (Dictionary<string, Node> Nodes, HashSet<(string, string, string)> Edges) Scan(params (string, string)[] files)
    {
        using var src = new Sources(files);
        var (nodes, edges) = JavaScanner.Scan(src.Root, src.Files);
        return (nodes.DistinctBy(n => n.Id).ToDictionary(n => n.Id), edges.Select(e => (e.Kind, e.From, e.To)).ToHashSet());
    }

    static readonly (string, string) Service = ("src/main/java/com/example/OrderService.java", """
        package com.example;
        public interface OrderService { Order find(Long id); }
        """);

    [Fact]
    public void Spring_endpoint_route_parameters_authorization_and_unwrapped_return()
    {
        var (nodes, edges) = Scan(Service, ("src/main/java/com/example/OrderController.java", """
            package com.example;
            import org.springframework.http.ResponseEntity;
            import org.springframework.web.bind.annotation.*;
            @RestController
            @RequestMapping("/api/orders")
            public class OrderController {
                private final OrderService orders;
                public OrderController(OrderService orders) { this.orders = orders; }
                @PreAuthorize("hasRole('ADMIN')")
                @GetMapping(path = "/{id}")
                public ResponseEntity<Order> get(@PathVariable Long id) { return ResponseEntity.ok(orders.find(id)); }
            }
            """));
        var get = nodes[J + "OrderController.get(Long)"];
        Assert.Equal(["endpoint", "GET", "authorize"], get.Tags);
        Assert.Equal("api/orders/{id}", get.Route);
        Assert.Equal(["[route] id: Long"], get.Parameters);
        Assert.Equal("Order", get.Returns);
        Assert.Contains(("injects", J + "OrderController", J + "OrderService"), edges);
        Assert.Contains(("calls", J + "OrderController.get(Long)", J + "OrderService.find(Long)"), edges);
    }

    // Regression: braces and quotes inside comments and string literals must not end a method body early.
    [Fact]
    public void Braces_in_comments_and_strings_do_not_break_member_detection()
    {
        var (nodes, edges) = Scan(Service, ("src/main/java/com/example/DefaultOrderService.java", """
            package com.example;
            @Service
            public class DefaultOrderService implements OrderService {
                @Override
                public Order find(Long id) {
                    // a comment with { braces } and "quotes"
                    throw new IllegalArgumentException("no order {" + id + "}");
                }
                public int count() { return 0; }
            }
            """));
        Assert.Contains(J + "DefaultOrderService.count()", nodes.Keys);
        Assert.Contains(("implements", J + "DefaultOrderService.find(Long)", J + "OrderService.find(Long)"), edges);
    }
}
