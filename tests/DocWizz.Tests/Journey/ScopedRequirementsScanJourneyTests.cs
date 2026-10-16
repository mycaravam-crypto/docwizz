namespace DocWizz.Tests.Journey;

public class ScopedRequirementsScanJourneyTests
{
    [Fact]
    public void IsolatedScopedCommitUsesChangedBlobsAndKeepsSymbolMapping()
    {
        using var repo = new FakeHistory();
        repo.Write("Selected/Selected.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        repo.Write("Selected/Service.cs", "namespace Demo; public class Service { public int Value() => 1; }");
        repo.Write("Unrelated/Other.cs", "public class Other { }");
        repo.Write("docwizz.yaml", """
            scope:
              projects:
                include: ["Selected/Selected.csproj"]
                references: exclude
                test_links: false
              unassigned_sources: exclude
            """);
        repo.Commit("initial", FakeHistory.Verified(123));
        repo.Write("Selected/Service.cs", "namespace Demo; public class Service { public int Value() => 2; }");
        repo.Commit("change value", FakeHistory.Verified(123));
        var history = Path.Combine(repo.Root, "history.json");
        File.WriteAllText(history, repo.History());
        var result = Docwizz.Run(repo.Root,
            new Dictionary<string, string?> { ["DOCWIZZ_REQUIREMENTS_PROFILE"] = "1" },
            "requirements", "import", ".", history);
        Assert.Equal(0, result.Exit);
        Assert.Contains("mode=selective", result.Err);
        Assert.DoesNotContain("mode=scoped-full", result.Err);
        var index = RequirementIndex.Load(Path.Combine(repo.Root, RequirementIndex.DefaultFile));
        Assert.Contains(index.Commits.SelectMany(c => c.Touches), t => t.Symbol.Contains("Service.Value"));
    }
}
