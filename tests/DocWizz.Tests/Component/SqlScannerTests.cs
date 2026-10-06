namespace DocWizz.Tests.Component;

public class SqlScannerTests
{
    static (Dictionary<string, Node> Nodes, HashSet<(string, string, string)> Edges) Scan(params (string, string)[] files)
    {
        using var src = new Sources(files);
        var (nodes, edges) = Sql.Scan(src.Root, src.Files);
        return (nodes.DistinctBy(n => n.Id).ToDictionary(n => n.Id), edges.Select(e => (e.Kind, e.From, e.To)).ToHashSet());
    }

    // Regression: bracketed names and comments. `-- FROM dbo.Ignored` is a comment, not an access of that (existing) table.
    [Fact]
    public void Procedures_their_parameters_and_what_they_touch()
    {
        var (nodes, edges) = Scan(("db/migrations/V1__materials.sql", "CREATE TABLE dbo.Materials (Id int); CREATE TABLE dbo.Ignored (Id int);"),
            ("db/procedures.sql", """
            -- Materials whose stock is below a threshold.
            CREATE OR ALTER PROCEDURE [dbo].[GetLowStock] @threshold int
            AS
            BEGIN
                SELECT m.Id FROM dbo.Materials m WHERE m.Quantity < @threshold
            END
            GO

            CREATE PROCEDURE dbo.ResetStock @id int AS
                UPDATE dbo.Materials SET Quantity = 0 WHERE Id = @id; -- FROM dbo.Ignored in a comment
                EXEC dbo.GetLowStock 1
            GO
            """));
        var low = nodes["sql:dbo.getlowstock"];
        Assert.Equal("procedure", low.Kind);
        Assert.Equal(["@threshold: int"], low.Parameters);
        Assert.Contains("below a threshold", low.Doc);
        Assert.Contains(("accesses", "sql:dbo.getlowstock", "sql:dbo.materials"), edges);
        Assert.Contains(("calls", "sql:dbo.resetstock", "sql:dbo.getlowstock"), edges);
        Assert.DoesNotContain(edges, e => e.Item3 == "sql:dbo.ignored");
    }

    [Fact]
    public void Migrations_declare_tables()
    {
        var (nodes, _) = Scan(("db/migrations/V1__orders.sql", "CREATE TABLE dbo.Orders (Id int PRIMARY KEY);"));
        Assert.Equal("table", nodes["sql:dbo.orders"].Kind);
        Assert.Equal("migration", nodes["sql:migration:db/migrations/v1__orders.sql"].Kind);
    }
}
