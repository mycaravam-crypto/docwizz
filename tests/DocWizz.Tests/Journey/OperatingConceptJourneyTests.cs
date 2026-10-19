namespace DocWizz.Tests.Journey;

public class OperatingConceptJourneyTests
{
    [Fact]
    public void Cli_creates_a_repeatable_concept_with_gaps_without_exporting_secret_values()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "templates/it-operations/betriebskonzept.yaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var template = Path.Join(root!.FullName, "templates/it-operations/betriebskonzept.yaml");

        using var repo = new Sources(
            ("Example.cs", "public class Example {}"),
            ("appsettings.json", "{\"Db\":{\"Password\":\"SECRET_DO_NOT_WRITE\"}}"));
        var first = Docwizz.Run("product", repo.Root, template);
        Assert.Equal(0, first.Exit);
        var markdownPath = Path.Join(repo.Root, "docs", "products", "it-betriebskonzept.md");
        var evidencePath = Path.Join(repo.Root, "docs", "products", "it-betriebskonzept.evidence.json");
        var markdown = File.ReadAllText(markdownPath);
        var evidence = File.ReadAllText(evidencePath);
        Assert.Contains("Konfigurationsschluessel: Db:Password", markdown);
        Assert.Contains("OFFEN", markdown);
        Assert.DoesNotContain("SECRET_DO_NOT_WRITE", markdown);
        Assert.DoesNotContain("SECRET_DO_NOT_WRITE", evidence);

        var second = Docwizz.Run("product", repo.Root, template);
        Assert.Equal(0, second.Exit);
        Assert.Contains("unchanged:", second.Out);
        Assert.Equal(markdown, File.ReadAllText(markdownPath));

        var check = Docwizz.Run("product", "check", repo.Root, template, "--format", "json");
        Assert.Equal(0, check.Exit);
        Assert.Contains("PROD-001", check.Out);
    }
}
