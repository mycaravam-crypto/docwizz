namespace DocWizz.Tests.Journey;

// views/packages.md: the inventory behind `docwizz sbom`, as a page.
public class PackagesViewTests
{
    [Fact]
    public void Lists_packages_with_versions_conflicts_first()
    {
        using var src = new Sources(
            ("one/package.json", """{"name":"one","dependencies":{"common":"1.0.0","left-pad":"^1.0.0"}}"""),
            ("two/package.json", """{"name":"two","dependencies":{"common":"2.0.0"}}"""));
        var docs = Path.Combine(src.Root, "docs");
        var r = Docwizz.Run("generate", src.Root, docs);
        Assert.True(r.Exit == 0, r.ToString());

        var page = File.ReadAllText(Path.Combine(docs, "views", "packages.md"));
        var conflicts = page.IndexOf("## Declared at more than one version", StringComparison.Ordinal);
        Assert.InRange(conflicts, 0, page.IndexOf("## All packages", StringComparison.Ordinal));
        Assert.Contains("| `common` | npm | `1.0.0` in [one](../../one/package.json)<br>`2.0.0` in [two](../../two/package.json) |", page);
        Assert.Contains("| `left-pad` | npm | `^1.0.0` _(unresolved)_ in [one](../../one/package.json) | `pkg:npm/left-pad` |", page);
        Assert.Contains("[Packages](views/packages.md)", File.ReadAllText(Path.Combine(docs, "architecture-description.md")));
        Assert.Contains("[packages](packages.md)", File.ReadAllText(Path.Combine(docs, "views", "context.md")));
    }

    [Fact]
    public void No_declared_packages_means_no_page()
    {
        using var repo = Repos.ConsoleApp();
        var docs = Path.Combine(repo.Root, "docs");
        var r = Docwizz.Run("generate", repo.Root, docs);
        Assert.True(r.Exit == 0, r.ToString());
        Assert.False(File.Exists(Path.Combine(docs, "views", "packages.md")));
        Assert.DoesNotContain("packages.md", File.ReadAllText(Path.Combine(docs, "architecture-description.md")));
        Assert.DoesNotContain("packages.md", File.ReadAllText(Path.Combine(docs, "architecture.md")));
    }
}
