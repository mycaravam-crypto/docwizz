using System.Text.Json;

namespace DocWizz.Tests.Journey;

// Exercise the persisted scope through the real CLI, not just a post-scan model filter.
public class ProjectScopeJourneyTests
{
    static Sources Repo() => new(
        ("System.slnx", """<Solution><Project Path="A/A.csproj" /><Project Path="B/B.csproj" /><Project Path="C/C.csproj" /><Project Path="Tests/Tests.csproj" /></Solution>"""),
        ("A/A.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><ProjectReference Include="../B/B.csproj" /></ItemGroup></Project>
            """),
        ("B/B.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Excluded.Dependency" Version="1.0.0" /></ItemGroup></Project>"""),
        ("C/C.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
        ("Tests/Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
            <ItemGroup><ProjectReference Include="../A/A.csproj" /><ProjectReference Include="../C/C.csproj" /></ItemGroup></Project>
            """),
        ("A/AThing.cs", "public class AThing { public int Value() => 42; }"),
        ("B/BThing.cs", "public class BThing { public int Value() => 1; }"),
        ("C/CThing.cs", "public class CThing { public int Value() => 2; }"),
        ("Tests/ATest.cs", "public class ATest { public int Check() => new AThing().Value(); }"),
        ("Loose/Orphan.cs", "public class Orphan { }")
    );

    static string Yaml(string referenceMode = "metadata", bool testLinks = true, string unassigned = "exclude") => $$"""
        profile: default
        scope:
          projects:
            include: ["A/A.csproj"]
            references: {{referenceMode}}
            test_links: {{testLinks.ToString().ToLowerInvariant()}}
          unassigned_sources: {{unassigned}}
        """;

    static JsonElement Scan(Sources repo, string yaml)
    {
        File.WriteAllText(Path.Combine(repo.Root, "docwizz.yaml"), yaml);
        var file = Path.Combine(repo.Root, "scan.json");
        var result = Docwizz.Run("scan", repo.Root, file);
        Assert.Equal(0, result.Exit);
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        return json.RootElement.Clone();
    }

    [Fact]
    public void Metadata_stubs_preserve_edges_without_scanning_excluded_code()
    {
        using var repo = Repo();
        var model = Scan(repo, Yaml());
        var nodes = model.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Contains(nodes, n => n.GetProperty("id").GetString() == "proj:A/A.csproj");
        Assert.Contains(nodes, n => n.GetProperty("id").GetString() == "proj:B/B.csproj"
            && n.GetProperty("tags").EnumerateArray().Any(t => t.GetString() == "metadata-only"));
        Assert.Contains(nodes, n => n.GetProperty("id").GetString() == "proj:Tests/Tests.csproj");
        Assert.DoesNotContain(nodes, n => n.GetProperty("file").GetString() is "B/BThing.cs" or "C/CThing.cs" or "Loose/Orphan.cs" or "Tests/ATest.cs");
        var edges = model.GetProperty("edges").EnumerateArray().ToList();
        Assert.Contains(edges, e => e.GetProperty("kind").GetString() == "references"
            && e.GetProperty("from").GetString() == "proj:A/A.csproj"
            && e.GetProperty("to").GetString() == "proj:B/B.csproj");
        Assert.DoesNotContain(nodes, n => n.GetProperty("id").GetString() == "proj:C/C.csproj");
        var inventory = model.GetProperty("scopeInventory");
        Assert.Contains(inventory.GetProperty("scannedProjects").EnumerateArray(),
            p => p.GetString() == "Tests/Tests.csproj");
        Assert.DoesNotContain(inventory.GetProperty("metadataOnlyProjects").EnumerateArray(),
            p => p.GetString() == "C/C.csproj");
    }

    [Fact]
    public void Recursive_references_include_code_but_exclude_orphans_and_other_projects()
    {
        using var repo = Repo();
        var model = Scan(repo, Yaml("include", false));
        var nodes = model.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Contains(nodes, n => n.GetProperty("file").GetString() == "A/AThing.cs");
        Assert.Contains(nodes, n => n.GetProperty("file").GetString() == "B/BThing.cs");
        Assert.DoesNotContain(nodes, n => n.GetProperty("file").GetString() is "C/CThing.cs" or "Loose/Orphan.cs" or "Tests/ATest.cs");
        Assert.DoesNotContain(nodes, n => n.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            && tags.EnumerateArray().Any(t => t.GetString() == "metadata-only"));
    }

    [Fact]
    public void Exclude_mode_never_leaves_dangling_references()
    {
        using var repo = Repo();
        var model = Scan(repo, Yaml("exclude", false));
        var nodes = model.GetProperty("nodes").EnumerateArray().ToList();
        Assert.DoesNotContain(nodes, n => n.GetProperty("id").GetString() == "proj:B/B.csproj");
        var ids = nodes.Select(n => n.GetProperty("id").GetString()).ToHashSet();
        Assert.All(model.GetProperty("edges").EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "references"),
            e => Assert.Contains(e.GetProperty("to").GetString(), ids));
        var info = model.GetProperty("scopeInventory");
        Assert.Contains(info.GetProperty("excludedReferences").EnumerateArray(),
            reference => reference.GetString() == "A/A.csproj → B/B.csproj");
        File.WriteAllText(Path.Combine(repo.Root, "docwizz.yaml"), Yaml("exclude", false));
        Assert.Equal(0, Docwizz.Run("generate", repo.Root).Exit);
        var index = File.ReadAllText(Path.Combine(repo.Root, "docs", "index.md"));
        Assert.Contains("Excluded project references (1)", index);
        Assert.Contains("A/A.csproj → B/B.csproj", index);
    }

    [Fact]
    public void Discover_remains_complete_and_setup_respects_existing_scope()
    {
        using var repo = Repo();
        var yaml = Yaml();
        File.WriteAllText(Path.Combine(repo.Root, "docwizz.yaml"), yaml);
        var discover = Docwizz.Run("discover", repo.Root, "--format", "json");
        Assert.Equal(0, discover.Exit);
        using var inventory = JsonDocument.Parse(discover.Out);
        Assert.Equal(4, inventory.RootElement.GetProperty("projects").GetArrayLength());
        var setup = Docwizz.Run("setup", repo.Root);
        Assert.Equal(0, setup.Exit);
        Assert.Contains("config       ok       kept", setup.Out);
        Assert.Equal(yaml, File.ReadAllText(Path.Combine(repo.Root, "docwizz.yaml")));
        Assert.True(File.Exists(Path.Combine(repo.Root, "docs", "index.md")));
        var second = Docwizz.Run("setup", repo.Root);
        Assert.Equal(0, second.Exit);
        Assert.Equal(yaml, File.ReadAllText(Path.Combine(repo.Root, "docwizz.yaml")));
    }

    [Fact]
    public void Generated_docs_and_sbom_share_the_selected_scope()
    {
        using var repo = Repo();
        File.WriteAllText(Path.Combine(repo.Root, "docwizz.yaml"), Yaml());
        var generated = Docwizz.Run("generate", repo.Root);
        Assert.Equal(0, generated.Exit);
        var index = File.ReadAllText(Path.Combine(repo.Root, "docs", "index.md"));
        Assert.Contains("## Documentation scope", index);
        Assert.Contains("A/A.csproj", index);
        Assert.Contains("metadata-only", index);
        Assert.DoesNotContain("CThing", index);
        var sbomFile = Path.Combine(repo.Root, "sbom.json");
        var sbom = Docwizz.Run("sbom", repo.Root, sbomFile);
        Assert.Equal(0, sbom.Exit);
        Assert.DoesNotContain("Excluded.Dependency", File.ReadAllText(sbomFile));
    }

    [Fact]
    public void Repository_level_deployment_and_ci_remain_visible_under_scope()
    {
        using var repo = Repo();
        File.WriteAllText(Path.Combine(repo.Root, "docker-compose.yml"),
            "services:\n  cache:\n    image: redis\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, ".github", "workflows"));
        File.WriteAllText(Path.Combine(repo.Root, ".github", "workflows", "ci.yml"),
            "name: ci\non: push\njobs: {}\n");
        File.WriteAllText(Path.Combine(repo.Root, "docwizz.yaml"), Yaml());
        var generation = Docwizz.Run("generate", repo.Root);
        Assert.Equal(0, generation.Exit);
        var deployment = File.ReadAllText(Path.Combine(repo.Root, "docs", "views", "deployment.md"));
        Assert.Contains("docker-compose.yml", deployment);
        Assert.Contains(".github/workflows/ci.yml", deployment);
        Assert.DoesNotContain("No deployment descriptors found", deployment);
        var index = File.ReadAllText(Path.Combine(repo.Root, "docs", "index.md"));
        Assert.Contains("repository", index, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Diff_rejects_saved_model_after_selection_changes()
    {
        using var repo = Repo();
        File.WriteAllText(Path.Combine(repo.Root, "docwizz.yaml"), Yaml());
        Assert.Equal(0, Docwizz.Run("generate", repo.Root).Exit);
        var modelPath = Path.Combine(repo.Root, "docs", ".docwizz", "model.json");
        using var before = JsonDocument.Parse(File.ReadAllText(modelPath));
        Assert.Equal(JsonValueKind.String, before.RootElement.GetProperty("scopeFingerprint").ValueKind);

        File.WriteAllText(Path.Combine(repo.Root, "docwizz.yaml"), Yaml().Replace("A/A.csproj", "C/C.csproj"));
        var diff = Docwizz.Run("diff", repo.Root);
        Assert.Equal(1, diff.Exit);
        Assert.Contains("scope changed since the last generate", diff.Err);
    }

    [Fact]
    public void Missing_selected_manifest_fails_before_source_scan()
    {
        using var repo = Repo();
        var yaml = Yaml().Replace("A/A.csproj", "Missing/Missing.csproj");
        File.WriteAllText(Path.Combine(repo.Root, "docwizz.yaml"), yaml);
        var result = Docwizz.Run("scan", repo.Root, Path.Combine(repo.Root, "out.json"));
        Assert.Equal(1, result.Exit); // invalid user scope is a configuration error, not an internal crash
        Assert.Contains("scope.projects.include", result.Err);
        Assert.False(File.Exists(Path.Combine(repo.Root, "out.json")));
    }
}
