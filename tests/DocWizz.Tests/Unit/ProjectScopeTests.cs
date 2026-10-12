namespace DocWizz.Tests.Unit;

public class ProjectScopeTests
{
    [Theory]
    [InlineData("[]", "scope.projects.include")]
    [InlineData("[../Outside.csproj]", "scope.projects.include")]
    [InlineData("[/absolute/A.csproj]", "scope.projects.include")]
    [InlineData("[A/A.csproj, A/A.csproj]", "scope.projects.include")]
    public void Invalid_project_paths_fail_during_config_parse(string manifests, string message)
    {
        var yaml = $"scope:\n  projects:\n    include: {manifests}\n";
        var ex = Assert.Throws<ArgumentException>(() => Models.Config(yaml));
        Assert.Contains(message, ex.Message);
    }

    [Theory]
    [InlineData("bad", "exclude", "scope.projects.references")]
    [InlineData("metadata", "bad", "scope.unassigned_sources")]
    public void Invalid_scope_modes_fail_during_config_parse(string references, string unassigned, string message)
    {
        var yaml = $"scope:\n  projects:\n    include: [A/A.csproj]\n    references: {references}\n  unassigned_sources: {unassigned}\n";
        var ex = Assert.Throws<ArgumentException>(() => Models.Config(yaml));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Absence_of_scope_preserves_all_candidate_files()
    {
        using var repo = new Sources(("A/A.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
            ("B/B.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
            ("B/B.cs", "public class B { }"));
        var plan = ProjectScope.Resolve(repo.Root, scope: null, repo.Files);
        Assert.Equal(repo.Files, plan.Files);
        Assert.Empty(plan.MetadataOnly);
    }

    [Fact]
    public void A_manifest_pair_in_one_folder_must_be_selected_together()
    {
        using var repo = new Sources(("A/One.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
            ("A/Two.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
            ("A/Class.cs", "public class Shared { }"));
        var scope = new ScopeConfig { Projects = new ScopeProjectsConfig { Include = ["A/One.csproj"] } };
        var ex = Assert.Throws<ArgumentException>(() => ProjectScope.Resolve(repo.Root, scope, repo.Files));
        Assert.Contains("share directory", ex.Message);
    }

    [Fact]
    public void Selected_project_does_not_claim_files_in_a_nested_unselected_project()
    {
        using var repo = new Sources(("Root.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
            ("Root.cs", "public class Root { }"),
            ("Nested/Nested.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
            ("Nested/Nested.cs", "public class Nested { }"));
        var scope = new ScopeConfig { Projects = new ScopeProjectsConfig { Include = ["Root.csproj"] } };
        var plan = ProjectScope.Resolve(repo.Root, scope, repo.Files);
        Assert.Contains(plan.Files, p => p.EndsWith("Root.cs"));
        Assert.DoesNotContain(plan.Files, p => p.EndsWith("Nested/Nested.cs"));
    }
}
