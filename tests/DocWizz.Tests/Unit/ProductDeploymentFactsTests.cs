namespace DocWizz.Tests.Unit;

// Proves that the product consumes only descriptor presence, never configuration or runtime assertions.
public class ProductDeploymentFactsTests
{
    [Fact]
    public void Scoped_descriptors_are_evidenced_without_content_and_without_confirming_deployment()
    {
        using var repo = new Sources(
            ("Dockerfile", "FROM private.registry.example/app:supersecret\n"),
            ("compose.yml", "services:\n  backend:\n    image: private:secret\n"),
            ("deploy/pod.yaml", "apiVersion: v1\nkind: Pod\nmetadata:\n  name: internal-name\n"),
            (".github/workflows/release.yml", "name: Secret release process\n"),
            ("README.md", "not a deployment file"),
            ("notes.yaml", "just: a plain document"));

        var model = new CodeModel("abc", [], []);
        var enriched = ProductDeploymentFacts.Add(model, repo.Root, repo.Files);
        Assert.Empty(model.Nodes);
        var descriptors = enriched.Nodes.Where(n => n.Kind == "deployment-descriptor").ToList();
        Assert.Equal(4, descriptors.Count);
        Assert.All(descriptors, n =>
        {
            Assert.Equal(1, n.Line);
            Assert.DoesNotContain("secret", n.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private.registry", n.Name);
        });

        var template = new ProductTemplate("it-betriebskonzept", "", "", "",
        [
            new ProductSection("deployment-descriptors", "Deskriptoren", true, ["code"]),
            new ProductSection("deployment", "Installationsverfahren und Rollback", true, ["project"])
        ], [], "it-operations");
        var report = ProductDraft.Report(template, enriched);
        var draft = ProductDraft.Render(template, enriched);
        Assert.Contains("CI-Deskriptor", draft);
        Assert.Contains("Compose-Deskriptor", draft);
        Assert.Contains("Dockerfile", draft);
        Assert.Contains("deploy/pod.yaml", draft);
        Assert.Contains("OFFEN", draft); // No claim of rollout or rollback.
        Assert.DoesNotContain("private.registry.example", draft);
        Assert.DoesNotContain("internal-name", report);
        Assert.DoesNotContain("supersecret", report);
        Assert.Equal(draft, ProductDraft.Render(template, enriched));
        Assert.Contains("\"origin\": \"detected\"", report);
    }

    [Fact]
    public void Out_of_scope_files_do_not_enter_the_product_model()
    {
        using var repo = new Sources(("Dockerfile", "FROM internal"), ("README.md", "text"));
        var model = new CodeModel("abc", [], []);
        var result = ProductDeploymentFacts.Add(model, repo.Root, [repo.Files[1]]);
        Assert.Empty(result.Nodes);
    }
}
