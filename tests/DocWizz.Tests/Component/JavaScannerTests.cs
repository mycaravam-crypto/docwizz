namespace DocWizz.Tests.Component;

public class JavaScannerTests
{
    const string J = "java:com.example.";

    // Java is parsed by scanner-vue/java.mjs (tree-sitter), so these need scanner-vue installed, like the Vue/TS ones.
    static (Dictionary<string, Node> Nodes, HashSet<(string, string, string)> Edges) Scan(params (string, string)[] files)
    {
        Journey.Docwizz.RequireFrontendScanner();
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

    // Imports become uses-namespace edges per top-level type, like C# usings: own packages and java.* left out.
    [Fact]
    public void Imported_packages_are_namespace_edges()
    {
        var (_, edges) = Scan(Service, ("src/main/java/com/example/Order.java", """
            package com.example;
            import com.example.OrderService;
            import jakarta.persistence.Entity;
            import jakarta.persistence.*;
            import java.util.List;
            import static org.junit.Assert.assertEquals;
            import org.springframework.data.jpa.repository.JpaRepository.Inner;
            @Entity
            public class Order { static class Line { } }
            """));
        var ns = edges.Where(e => e.Item1 == "uses-namespace").ToList();
        Assert.Equal(["ns:jakarta.persistence", "ns:org.junit", "ns:org.springframework.data.jpa.repository"],
            ns.Where(e => e.Item2 == J + "Order").Select(e => e.Item3).Order());
        Assert.DoesNotContain(ns, e => e.Item2 == J + "Order.Line");
    }

    [Fact]
    public void Generics_and_multiline_annotations_keep_signatures_routes_and_the_declaration_line()
    {
        var (nodes, _) = Scan(("src/main/java/com/example/ReportController.java", """
            package com.example;
            import java.util.*;
            @RestController
            @RequestMapping(
                value = "/api/reports",
                produces = "application/json")
            public class ReportController {
                /** Sorted rows. */
                @GetMapping(
                    path = "/{kind}",
                    produces = "application/json")
                public <T extends Comparable<T>> ResponseEntity<List<Map<String, T>>> rows(
                        @PathVariable("kind") String kind,
                        @RequestParam(name = "limit",
                                      defaultValue = "10") int limit) {
                    return null;
                }
            }
            """));
        var rows = nodes[J + "ReportController.rows(String, int)"];
        Assert.Equal("api/reports/{kind}", rows.Route);
        Assert.Equal(["[route] kind: String", "[query] limit: int"], rows.Parameters);
        Assert.Equal("List<Map<String,T>>", rows.Returns);
        Assert.Equal(12, rows.Line);   // `public <T ...>`, not the annotation above it
        Assert.Contains("Sorted rows.", rows.Doc);
    }

    [Fact]
    public void Records_nested_types_and_enums_are_declarations_of_their_own()
    {
        var (nodes, edges) = Scan(("src/main/java/com/example/Money.java", """
            package com.example;
            public record Money(java.math.BigDecimal amount, String currency) {
                public Money {
                    if (amount == null || currency == null) throw new IllegalArgumentException();
                }
                public Money add(Money other) { return new Money(amount.add(other.amount()), currency); }
                public static class Rates {
                    public double rate(String from, String to) { return 1.0; }
                    enum Source { ECB, MANUAL; public String label() { return name(); } }
                }
            }
            """));
        Assert.Equal("record", nodes[J + "Money"].Kind);
        var canonical = nodes[J + "Money.Money(java.math.BigDecimal, String)"];   // compact constructor: the components
        Assert.Equal(("constructor", 3), (canonical.Kind, canonical.Complexity));
        Assert.Contains(J + "Money.add(Money)", nodes.Keys);
        Assert.Contains(("contains", J + "Money", J + "Money.Rates"), edges);
        Assert.Contains(("contains", J + "Money.Rates", J + "Money.Rates.rate(String, String)"), edges);
        Assert.DoesNotContain(J + "Money.rate(String, String)", nodes.Keys);   // a nested type's members stay its own
        Assert.Equal("enum", nodes[J + "Money.Rates.Source"].Kind);
        Assert.Contains(J + "Money.Rates.Source.label()", nodes.Keys);
    }

    [Fact]
    public void Calls_in_lambdas_through_locals_statics_and_this_are_linked_and_overloads_resolve_by_argument_count()
    {
        var (nodes, edges) = Scan(Service, ("src/main/java/com/example/Orders.java", """
            package com.example;
            import java.util.List;
            public class Orders {
                private final OrderService orders;
                public Orders(OrderService orders) { this.orders = orders; }
                public void touch(List<Long> ids) {
                    ids.forEach(id -> orders.find(id));                 // lambda: still this method's call
                    var audit = new Audit();
                    audit.record(ids.size());                           // a local, typed by `new`
                    Audit.flush();                                      // a static call on a type in the source
                    check(ids);                                         // this class, no receiver
                    new Runnable() { public void run() { orders.find(0L); } };   // an anonymous class's own code
                }
                void check(List<Long> ids) { if (ids.isEmpty() && ids != null) throw new IllegalStateException(); }
            }
            """), ("src/main/java/com/example/Audit.java", """
            package com.example;
            public class Audit {
                public void record(int n) { }
                public void record(int n, String who) { }
                public void record(String what) { }
                public static void flush() { }
            }
            """));
        var touch = J + "Orders.touch(List<Long>)";
        Assert.Contains(("calls", touch, J + "OrderService.find(Long)"), edges);
        Assert.Contains(("calls", J + "Orders.touch(List<Long>)", J + "Orders.check(List<Long>)"), edges);
        Assert.Contains(("calls", touch, J + "Audit.flush()"), edges);
        // record(int) and record(String) both take one argument: no guess, the type is still recorded as used.
        Assert.DoesNotContain(edges, e => e.Item1 == "calls" && e.Item2 == touch && e.Item3.StartsWith(J + "Audit.record("));
        Assert.Contains(("accesses", touch, J + "Audit"), edges);
        Assert.Equal(3, nodes.Keys.Count(k => k.StartsWith(J + "Audit.record(")));   // three overloads, three nodes
        Assert.DoesNotContain(nodes.Keys, k => k.EndsWith(".run()"));
        Assert.Equal(3, nodes[J + "Orders.check(List<Long>)"].Complexity);   // if + &&
    }

    [Fact]
    public void Output_is_deterministic_and_a_syntax_error_costs_only_what_it_breaks()
    {
        (string, string)[] files = [Service, ("src/main/java/com/example/Broken.java", """
            package com.example;
            public class Broken {
                public int ok() { return 1; }
                public void broken( { 
            }
            """)];
        var first = Scan(files);
        var second = Scan(files);
        Assert.Equal(first.Nodes.Keys.Order(), second.Nodes.Keys.Order());
        Assert.Equal(first.Edges.Order(), second.Edges.Order());
        Assert.Contains(J + "Broken", first.Nodes.Keys);
        Assert.Contains(J + "OrderService.find(Long)", first.Nodes.Keys);
    }
}
