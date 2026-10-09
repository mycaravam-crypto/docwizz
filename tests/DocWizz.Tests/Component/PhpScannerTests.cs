namespace DocWizz.Tests.Component;

public class PhpScannerTests
{
    const string P = "php:App\\";

    static (Dictionary<string, Node> Nodes, HashSet<(string, string, string)> Edges) Scan(params (string, string)[] files)
    {
        using var src = new Sources(files);
        var (nodes, edges) = PhpScanner.Scan(src.Root, src.Files);
        return (nodes.DistinctBy(n => n.Id).ToDictionary(n => n.Id), edges.Select(e => (e.Kind, e.From, e.To)).ToHashSet());
    }

    static readonly (string, string) Repository = ("src/OrderRepository.php", """
        <?php
        namespace App;
        interface OrderRepository { public function find(int $id): ?Order; }
        """);

    [Fact]
    public void Class_method_docblock_promoted_constructor_injection_and_calls()
    {
        var (nodes, edges) = Scan(Repository, ("src/OrderService.php", """
            <?php
            namespace App;
            class OrderService
            {
                public function __construct(private OrderRepository $repository) {}

                /**
                 * Finds an order by id.
                 *
                 * @param int $id The order id.
                 * @return Order The order.
                 */
                public function find(int $id): Order
                {
                    return $this->repository->find($id);
                }
            }
            """));

        var find = nodes[P + "OrderService.find(int)"];
        Assert.Equal("<member><summary>Finds an order by id.</summary><param name=\"id\">The order id.</param><returns>The order.</returns></member>", find.Doc);
        Assert.Equal(["$id: int"], find.Parameters);
        Assert.Equal("Order", find.Returns);
        Assert.Contains(("injects", P + "OrderService", P + "OrderRepository"), edges);
        Assert.Contains(("calls", P + "OrderService.find(int)", P + "OrderRepository.find(int)"), edges);
    }

    [Fact]
    public void Interface_implemented_by_class()
    {
        var (nodes, edges) = Scan(Repository, ("src/DbOrderRepository.php", """
            <?php
            namespace App;
            class DbOrderRepository implements OrderRepository
            {
                public function find(int $id): ?Order { return null; }
            }
            """));
        Assert.Contains(("implements", P + "DbOrderRepository", P + "OrderRepository"), edges);
        Assert.Contains(P + "DbOrderRepository.find(int)", nodes.Keys);
    }

    [Fact]
    public void Trait_use_is_an_edge()
    {
        var (_, edges) = Scan(("src/Loggable.php", """
            <?php
            namespace App;
            trait Loggable { public function log(string $message): void {} }
            """), ("src/Worker.php", """
            <?php
            namespace App;
            class Worker
            {
                use Loggable;
                public function run(): void { $this->log('go'); }
            }
            """));
        Assert.Contains(("uses-trait", P + "Worker", P + "Loggable"), edges);
        Assert.Contains(("calls", P + "Worker.run()", P + "Loggable.log(string)"), edges);
    }

    [Fact]
    public void Free_function_outside_any_class()
    {
        var (nodes, _) = Scan(("src/helpers.php", """
            <?php
            namespace App;
            /** Formats a price. */
            function format_price(float $amount): string { return number_format($amount, 2); }
            """));
        var fn = nodes[P + "format_price(float)"];
        Assert.Equal("function", fn.Kind);
        Assert.Equal(["$amount: float"], fn.Parameters);
        Assert.Equal("string", fn.Returns);
    }

    // Regression: braces and quotes inside comments, strings and heredoc must not confuse member/body detection.
    [Fact]
    public void Braces_in_comments_strings_and_heredoc_do_not_break_scanning()
    {
        var (nodes, _) = Scan(("src/Report.php", """
            <?php
            namespace App;
            class Report
            {
                // a comment with { braces } and "quotes"
                public function render(): string
                {
                    $sql = <<<SQL
                    SELECT * FROM {orders} WHERE id = '{$this}'
                    SQL;
                    throw new \RuntimeException("no report {" . 1 . "}");
                }
                public function count(): int { return 0; }
            }
            """));
        Assert.Contains(P + "Report.render()", nodes.Keys);
        Assert.Contains(P + "Report.count()", nodes.Keys);
    }

    [Fact]
    public void Namespace_imports_become_edges_resolved_locally_or_external()
    {
        var (_, edges) = Scan(Repository, ("src/Order.php", """
            <?php
            namespace App;
            use App\OrderRepository;
            use Ramsey\Uuid\Uuid;
            class Order {}
            """));
        Assert.Contains(("imports", P + "Order", P + "OrderRepository"), edges);
        Assert.Contains(("uses-namespace", P + "Order", "ns:Ramsey.Uuid.Uuid"), edges);
    }
}
