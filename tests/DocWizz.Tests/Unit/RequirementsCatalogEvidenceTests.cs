using System.Text.Json;

namespace DocWizz.Tests.Unit;

// Reuse the existing ADO history fixtures and symbol model: these tests exercise the real resolver, not a parallel mapping.
public partial class RequirementIndexTests
{
    static string AreaCatalogFixture(string source = "https://ado.example", string project = "shop") =>
        $$"""
          {
            "schema": "ado.workitem-catalog/v1",
            "project": "{{project}}", "area": "shop\\Auth", "source": "{{source}}",
            "complete": true, "warnings": [],
            "items": [
              {"id":42,"type":"User Story","title":"Credit limit","state":"Done","area":"shop\\Auth",
               "iteration":"shop\\Sprint 1","description":"Reject orders above limit.",
               "criteria":["Reject excess orders"],
               "relations":[{"kind":"parent","id":10},{"kind":"child","id":45}]},
              {"id":43,"type":"User Story","title":"Reset password","state":"New","area":"shop\\Auth",
               "iteration":"shop\\Sprint 2","description":"No implementation yet.",
               "criteria":[], "relations":[{"kind":"parent","id":10}]}
            ]
          }
          """;

    [Fact]
    public void AreaPagesJoinOnlyVerifiedHistoryAndKeepUnlinkedRequirements()
    {
        using var document = JsonDocument.Parse(AreaCatalogFixture());
        var history = Build(History([Commit(1, Verified(42), null, (7, 2))]),
            Items((42, 3, "Credit limit")));
        var pages = RequirementsCatalog.Pages(document.RootElement, history, Sha(99), At(), [])
            .ToDictionary(x => x.Page, x => x.Body);

        var inventory = pages["requirements/area-catalog.md"];
        Assert.Contains("| [42](items/42.md)", inventory);
        Assert.Contains("| [43](items/43.md)", inventory);
        Assert.Contains("confirmed link", inventory);
        Assert.Contains("not-linked (imported history)", inventory);
        Assert.Contains("not-assessed (no ADO test snapshot)", inventory);
        Assert.Contains("AreaPath selects organizational scope", inventory);
        var linked = pages["requirements/items/42.md"];
        Assert.Contains("cs:Shop.OrderService.Place(int)", linked);
        Assert.Contains("confirmed", linked);
        Assert.Contains("Historical requirement details: [WI-42](../wi-42.md)", linked);
        Assert.Contains("WI-45 (outside the exported area)", linked);
        Assert.Contains("does not verify", linked);
        Assert.Contains("not-linked (imported history)", pages["requirements/items/43.md"]);
        Assert.Equal(inventory, RequirementsCatalog.Pages(document.RootElement, history, Sha(99), At(), [])
            .Single(x => x.Page == "requirements/area-catalog.md").Body);
    }

    [Fact]
    public void ImportedAdoTestCasesAndStructuralTestCodeAreIndependentEvidence()
    {
        using var document = JsonDocument.Parse(AreaCatalogFixture());
        var history = Build(History([Commit(1, Verified(42), null, (7, 2))]), Items((42, 3, "Credit limit")));
        var testModel = At() with { Edges = [new Edge("cs:Tests.PlaceTest.Checks", Place, "tests")] };
        var snapshot = new AdoTestSnapshot.ImportData("ado.test-traceability/v2", "shop", "shop\\Auth",
            DateTimeOffset.UnixEpoch, [1], 1, 1, 1, """
            {"testCases":[{"id":500,"title":"Reject high orders"}],
             "requirements":[{"id":42,"testCaseIds":[500]}]}
            """);

        var page = RequirementsCatalog.Pages(document.RootElement, history, Sha(99), testModel, [snapshot])
            .Single(x => x.Page == "requirements/items/42.md").Body;
        Assert.Contains("ADO test case #500: Reject high orders", page);
        Assert.Contains("Test-code reference: PlaceTest.Checks", page);
        Assert.Contains("linked (ADO cases)", page);
        Assert.Contains("not establish successful execution", page);
        Assert.Contains("unavailable (not in snapshot)", RequirementsCatalog.Pages(document.RootElement,
            history, Sha(99), testModel, [snapshot]).Single(x => x.Page == "requirements/items/43.md").Body);
    }

    [Fact]
    public void DifferentAdoOriginMustNeverJoinNumericIds()
    {
        using var document = JsonDocument.Parse(AreaCatalogFixture(source: "https://other.example"));
        var history = Build(History([Commit(1, Verified(42), null, (7, 2))]), Items((42, 3, "Unrelated")));
        var pages = RequirementsCatalog.Pages(document.RootElement, history, Sha(99), At(), []).ToList();
        Assert.Contains("different history origin", pages.Single(x => x.Page == "requirements/area-catalog.md").Body);
        Assert.DoesNotContain("confirmed link", pages.Single(x => x.Page == "requirements/items/42.md").Body);
        Assert.DoesNotContain("Historical requirement details:", pages.Single(x => x.Page == "requirements/items/42.md").Body);
    }

    [Fact]
    public void CatalogWorksWithoutHistoryOrTestsAndHtmlNavigationIncludesIt()
    {
        using var document = JsonDocument.Parse(AreaCatalogFixture());
        var pages = RequirementsCatalog.Pages(document.RootElement, null, null, At(), []).ToDictionary(x => x.Page, x => x.Body);
        Assert.Contains("not-assessed (no history imported)", pages["requirements/area-catalog.md"]);
        Assert.Contains("not-assessed (no ADO test snapshot)", pages["requirements/area-catalog.md"]);

        var html = Html.Page("requirements/items/42.md", pages["requirements/items/42.md"],
            new Dictionary<string, string> {
                ["index.md"] = "Overview",
                ["requirements/area-catalog.md"] = "Area requirements catalog",
                ["requirements/items/42.md"] = "WI-42"
            });
        Assert.Contains("Area requirements catalog", html);
        Assert.Contains("area-catalog.html", html);
    }
    [Fact]
    public void ImportedMarkdownCannotCreateRemoteTrackingImages()
    {
        using var document = JsonDocument.Parse(AreaCatalogFixture().Replace("Credit limit",
            "![tracking](https://attacker.example/track.png)"));
        var pages = RequirementsCatalog.Pages(document.RootElement, null, null, At(), [])
            .ToDictionary(x => x.Page, x => x.Body);
        var html = Html.Page("requirements/items/42.md", pages["requirements/items/42.md"],
            new Dictionary<string, string>
            {
                ["index.md"] = "Overview",
                ["requirements/area-catalog.md"] = "Area catalog",
                ["requirements/items/42.md"] = "Work item 42"
            });
        Assert.DoesNotContain("src=\"https://attacker.example", html);
        Assert.Contains("tracking", html);
    }

}
