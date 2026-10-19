namespace DocWizz.Tests.Journey;

public class ProductDeploymentJourneyTests
{
    [Fact]
    public void Product_and_check_share_scoped_descriptor_inventory_and_keep_rollout_open()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "templates/it-operations/betriebskonzept.yaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var template = Path.Join(root!.FullName, "templates/it-operations/betriebskonzept.yaml");

        using var repo = new Sources(
            ("compose.yml", "services:\n  api:\n    image: secret-registry.example/private\n"),
            (".github/workflows/deploy.yml", "name: internal-deploy\n"),
            ("appsettings.json", "{\"Key\":\"HIDDEN-VALUE\"}"));

        var first = Docwizz.Run("product", repo.Root, template);
        Assert.Equal(0, first.Exit);
        var markdownPath = Path.Join(repo.Root, "docs", "products", "it-betriebskonzept.md");
        var reportPath = Path.Join(repo.Root, "docs", "products", "it-betriebskonzept.evidence.json");
        var markdown = File.ReadAllText(markdownPath);
        var report = File.ReadAllText(reportPath);
        Assert.Contains("Compose-Deskriptor", markdown);
        Assert.Contains(".github/workflows/deploy.yml", markdown);
        Assert.DoesNotContain("secret-registry.example", markdown);
        Assert.DoesNotContain("HIDDEN-VALUE", report);

        using (var json = System.Text.Json.JsonDocument.Parse(report))
        {
            var sections = json.RootElement.GetProperty("sections").EnumerateArray().ToDictionary(
                s => s.GetProperty("id").GetString()!, s => s);
            Assert.Equal("evidenced", sections["deployment-descriptors"].GetProperty("status").GetString());
            Assert.Equal("open", sections["deployment"].GetProperty("status").GetString());
            Assert.Equal("detected", sections["deployment-descriptors"].GetProperty("evidence")[0]
                .GetProperty("origin").GetString());
        }

        var check = Docwizz.Run("product", "check", repo.Root, template, "--format", "json");
        Assert.Equal(0, check.Exit);
        Assert.Contains("PROD-001", check.Out); // Rollout and responsibilities still require human evidence.
        Assert.Contains("unchanged:", Docwizz.Run("product", repo.Root, template).Out);

        File.WriteAllText(Path.Join(repo.Root, "Dockerfile"), "FROM private-image\n");
        var stale = Docwizz.Run("product", "check", repo.Root, template, "--format", "json");
        Assert.Equal(1, stale.Exit);
        Assert.Contains("PROD-004", stale.Out);
        Assert.Equal(0, Docwizz.Run("product", repo.Root, template).Exit);
        Assert.Equal(0, Docwizz.Run("product", "check", repo.Root, template).Exit);
    }
}
