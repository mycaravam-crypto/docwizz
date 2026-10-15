using static DocWizz.Tests.Models;

namespace DocWizz.Tests.Unit;

// #226: a route page shows the work items linked to the code its flow reaches, and the work item links back to it.
public class RoutePagesTests
{
    static readonly CodeModel Shop = Model([
        N("route:/orders", "src/router.ts", "route", route: "/orders"),
        N("vue:src/Orders.vue", "src/Orders.vue", "component"),
        N("ts:src/api.ts#loadOrders", "src/api.ts", "function"),
        N("route:/about", "src/router.ts", "route", route: "/about"),
        N("vue:src/About.vue", "src/About.vue", "component"),
    ], [
        E("route:/orders", "vue:src/Orders.vue", "routes-to"),
        E("vue:src/Orders.vue", "ts:src/api.ts#loadOrders"),
        E("route:/about", "vue:src/About.vue", "routes-to"),
    ]);

    // One commit, linked to WI-7 by the provider, changed loadOrders.
    static RequirementIndex.Index History() => new(RequirementIndex.Schema, "f",
        new(RequirementIndex.HistorySchema, "d", "ado", null, null, null, null, false, false, null, []),
        [new("abcdef1234567", "verified", [7], [], [], "mapped", [new("ts:src/api.ts#loadOrders", "function", null, "src/api.ts", "3", "1-5")], [])],
        [],
        [new(7, 2, 2, true, "User Story", "Active", "List orders", null, null, null, null, null, ["Orders are listed newest first", "[open](javascript:alert(1)) <b>x</b>"], "x", [])],
        []);

    static (string Orders, string About, string Item, string Quality) Generate(RequirementIndex.Index? index)
    {
        using var repo = new Sources(("src/router.ts", "// routes"));
        var outDir = Path.Combine(repo.Root, "docs");
        var config = Config();
        new Generator(repo.Root, outDir, Shop, [], Architecture.Check(Shop, config), config, []) { Requirements = index }.Run();
        string Read(string rel) => File.Exists(Path.Combine(outDir, rel)) ? File.ReadAllText(Path.Combine(outDir, rel)) : "";
        return (Read(Generator.RoutePage("/orders")), Read(Generator.RoutePage("/about")), Read(Generator.RequirementPage(7)), Read("quality.md"));
    }

    [Fact]
    public void Route_and_work_item_link_both_ways_and_unlinked_routes_are_a_gap()
    {
        var (orders, about, item, quality) = Generate(History());
        Assert.Contains("| [WI-7](../requirements/wi-7.md) | List orders | confirmed | ts#loadOrders | — |", orders);
        Assert.Contains($"- [`/orders`](../{Generator.RoutePage("/orders")})", item);
        Assert.DoesNotContain("/about", item);
        Assert.Contains("- **AC1** Orders are listed newest first", item);
        Assert.Contains("- **AC2** \\[open\\](javascript:alert(1)) \\<b>x\\</b>", item);   // exported text never becomes a link or HTML
        Assert.Contains("- No work item is linked to the code behind this route.", about);
        // The quality page collects them, with the page each gap is on, and says no criterion is verified.
        Assert.Contains($"| [`/about`]({Generator.RoutePage("/about")}) | No work item is linked to the code behind this route. |", quality);
        Assert.DoesNotContain($"| [`/orders`]({Generator.RoutePage("/orders")}) | No work item", quality);
        Assert.Contains("Acceptance criteria: 2 in 1 work items, all **unverified**", quality);
    }

    [Fact]
    public void Without_an_index_route_pages_say_none_was_imported()
    {
        var (orders, _, item, quality) = Generate(null);
        Assert.Contains("No requirement history imported (`docwizz requirements import`): work items and acceptance criteria are unknown.", quality);
        Assert.Contains("No requirement history imported (`docwizz requirements import`).", orders);
        Assert.DoesNotContain("No work item is linked", orders);
        Assert.Equal("", item);
    }
}
