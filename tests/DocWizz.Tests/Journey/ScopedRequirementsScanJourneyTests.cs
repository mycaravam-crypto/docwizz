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
        Assert.Contains("completed elapsed=", result.Err);
        Assert.Contains("materializedFiles=", result.Err);
        Assert.Contains("changedFiles=", result.Err);
        Assert.Contains("actualMode=", result.Err);
        // Selective mode can legitimately fall back when the historical project layout
        // cannot be reconstructed. Profiling must disclose the outcome rather than hide it.
        Assert.Contains("fallback=", result.Err);
        var index = RequirementIndex.Load(Path.Combine(repo.Root, RequirementIndex.DefaultFile));
        Assert.Contains(index.Commits.SelectMany(c => c.Touches), t => t.Symbol.Contains("Service.Value"));
        // Rebuild using the historical full scan as an independent comparison baseline.
        // Compare evidence mappings, not profiling text or elapsed time.
        File.Delete(Path.Combine(repo.Root, RequirementIndex.DefaultFile));
        var baseline = Docwizz.Run(repo.Root,
            new Dictionary<string, string?>
            {
                ["DOCWIZZ_REQUIREMENTS_PROFILE"] = "1",
                ["DOCWIZZ_REQUIREMENTS_FORCE_FULL"] = "1"
            },
            "requirements", "import", ".", history);
        Assert.Equal(0, baseline.Exit);
        Assert.Contains("actualMode=scoped-full", baseline.Err);
        Assert.Contains("mode=scoped-full", baseline.Err);
        Assert.DoesNotContain("mode=selective", baseline.Err);
        var fullIndex = RequirementIndex.Load(Path.Combine(repo.Root, RequirementIndex.DefaultFile));
        Assert.Equal(
            index.Commits.SelectMany(c => c.Touches).Select(t => t.Symbol).OrderBy(x => x),
            fullIndex.Commits.SelectMany(c => c.Touches).Select(t => t.Symbol).OrderBy(x => x));

    }
}
