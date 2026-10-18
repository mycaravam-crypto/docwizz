using System.Text.Json;

namespace DocWizz.Tests.Journey;

// #232: a commit that changes one file is mapped from that file's blob alone. Its symbol ids must still be the ones a
// full scan gives, or historical touches silently stop matching today's symbols.
public class SelectiveRequirementsScanJourneyTests
{
    static HashSet<string> FullScanIds(FakeHistory repo)
    {
        var model = Path.Combine(repo.Root, "full-model.json");
        var scan = Docwizz.Run(repo.Root, new Dictionary<string, string?>(), "scan", ".", model);
        Assert.True(scan.Exit == 0, scan.ToString());
        using var doc = JsonDocument.Parse(File.ReadAllText(model));
        return [.. doc.RootElement.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString()!)];
    }

    static List<RequirementIndex.Touch> ImportLastCommitTouches(FakeHistory repo, string last)
    {
        var history = Path.Combine(repo.Root, "history.json");
        File.WriteAllText(history, repo.History());
        var result = Docwizz.Run(repo.Root, new Dictionary<string, string?> { ["DOCWIZZ_REQUIREMENTS_PROFILE"] = "1" },
            "requirements", "import", ".", history);
        Assert.True(result.Exit == 0, result.ToString());
        Assert.Contains("mode=selective", result.Err);
        var index = RequirementIndex.Load(Path.Combine(repo.Root, RequirementIndex.DefaultFile));
        return index.Commits.Single(c => c.Sha == last).Touches;
    }

    [Fact]
    public void CSharpTouchesInOneChangedFileUseFullScanSymbolIds()
    {
        using var repo = new FakeHistory();
        repo.Write("Shop.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        repo.Write("Order.cs", "namespace Shop;\npublic record Order(int Id, decimal Total);\n");
        repo.Write("Cart.Lines.cs", "namespace Shop;\npublic partial class Cart { public int Count() => 0; }\n");
        repo.Write("Cart.cs", "namespace Shop;\npublic partial class Cart\n{\n    public decimal Total(Order order) => 0;\n}\n");
        repo.Commit("initial", FakeHistory.Verified(1));
        // Only Cart.cs changes: Order and the other half of Cart live in files the selective scan doesn't read.
        repo.Write("Cart.cs", "namespace Shop;\npublic partial class Cart\n{\n    public decimal Total(Order order) => order.Total;\n}\n");
        var last = repo.Commit("total from order", FakeHistory.Verified(1));

        var touches = ImportLastCommitTouches(repo, last);
        var full = FullScanIds(repo);
        Assert.NotEmpty(touches);
        Assert.All(touches, t => Assert.True(full.Contains(t.Symbol),
            $"selective id {t.Symbol} is not in the full scan: {string.Join(", ", full.Where(id => id.Contains("Total")))}"));
    }

    [Fact]
    public void FrontendTouchesInOneChangedFileUseFullScanSymbolIds()
    {
        Docwizz.RequireFrontendScanner();
        using var repo = new FakeHistory();
        repo.Write("web/package.json", """{ "name": "web", "dependencies": { "vue": "^3.5.0" } }""");
        repo.Write("web/src/order.ts", "export interface Order { id: number, total: number }\n");
        repo.Write("web/src/cart.ts", "import type { Order } from './order'\nexport function total(order: Order): number {\n  return 0\n}\n");
        repo.Commit("initial", FakeHistory.Verified(1));
        repo.Write("web/src/cart.ts", "import type { Order } from './order'\nexport function total(order: Order): number {\n  return order.total\n}\n");
        var last = repo.Commit("total from order", FakeHistory.Verified(1));

        var touches = ImportLastCommitTouches(repo, last);
        var full = FullScanIds(repo);
        Assert.NotEmpty(touches);
        Assert.All(touches, t => Assert.True(full.Contains(t.Symbol),
            $"selective id {t.Symbol} is not in the full scan: {string.Join(", ", full)}"));
    }
}
