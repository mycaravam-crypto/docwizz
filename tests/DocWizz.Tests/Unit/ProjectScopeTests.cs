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
        var ex = Assert.Throws<ScopeException>(() => Models.Config(yaml));
        Assert.Contains(message, ex.Message);
    }

    [Theory]
    [InlineData("bad", "exclude", "scope.projects.references")]
    [InlineData("metadata", "bad", "scope.unassigned_sources")]
    public void Invalid_scope_modes_fail_during_config_parse(string references, string unassigned, string message)
    {
        var yaml = $"scope:\n  projects:\n    include: [A/A.csproj]\n    references: {references}\n  unassigned_sources: {unassigned}\n";
        var ex = Assert.Throws<ScopeException>(() => Models.Config(yaml));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Historical_import_prefilter_skips_clearly_unrelated_projects()
    {
        var scope = new ScopeConfig
        {
            Projects = new ScopeProjectsConfig
            {
                Include = ["Backend/Cargo-Core/Cargo-Core.csproj", "Frontend/logdbvue/package.json"],
                References = "metadata",
                TestLinks = true
            },
            UnassignedSources = "exclude"
        };
        Assert.True(ProjectScope.MightBelongToScope(scope, "Backend/Cargo-Core/Service.cs"));
        Assert.True(ProjectScope.MightBelongToScope(scope, "Frontend/logdbvue/src/App.vue"));
        Assert.True(ProjectScope.MightBelongToScope(scope, "Backend/Cargo-Core.Tests/ServiceTests.cs"));
        Assert.False(ProjectScope.MightBelongToScope(scope, "BZUe/Other.cs"));
        Assert.False(ProjectScope.MightBelongToScope(scope, "Backend/OtherService/Other.cs"));
        Assert.True(ProjectScope.MightBelongToScope(null, "BZUe/Other.cs"));
        scope.UnassignedSources = "include";
        Assert.True(ProjectScope.MightBelongToScope(scope, "BZUe/Other.cs"));
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
        var ex = Assert.Throws<ScopeException>(() => ProjectScope.Resolve(repo.Root, scope, repo.Files));
        Assert.Contains("share directory", ex.Message);
    }

    [Fact]
    public void Fingerprint_is_order_independent_but_detects_scope_changes()
    {
        var a = new ScopeConfig { Projects = new ScopeProjectsConfig { Include = ["B/B.csproj", "A/A.csproj"] } };
        var b = new ScopeConfig { Projects = new ScopeProjectsConfig { Include = ["A/A.csproj", "B/B.csproj"] } };
        Assert.Equal(ProjectScope.Fingerprint(a), ProjectScope.Fingerprint(b));
        b.Projects.References = "include";
        Assert.NotEqual(ProjectScope.Fingerprint(a), ProjectScope.Fingerprint(b));
        Assert.Null(ProjectScope.Fingerprint(null));
    }

    [Fact]
    public void Root_npm_manifest_does_not_own_arbitrary_unassigned_sources_or_deployment()
    {
        using var repo = new Sources(
            ("package.json", """{"name":"tooling","version":"1.0.0"}"""),
            ("App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
            ("App/App.cs", "public class App { }"),
            ("Other/Loose.cs", "public class Loose { }"),
            ("docker-compose.yml", "services:\n  cache:\n    image: redis"),
            (".github/workflows/ci.yml", "name: CI\non: push\njobs: {}"));
        var scope = new ScopeConfig
        {
            Projects = new ScopeProjectsConfig { Include = ["App/App.csproj"] },
            UnassignedSources = "include"
        };
        var included = ProjectScope.Resolve(repo.Root, scope, repo.Files);
        Assert.Contains(included.Files, p => p.EndsWith("Other/Loose.cs"));
        Assert.DoesNotContain(included.Files, p => p.EndsWith("package.json"));
        Assert.Contains(included.Files, p => p.EndsWith("docker-compose.yml"));
        Assert.Contains(included.Files, p => p.EndsWith(".github/workflows/ci.yml"));

        scope.UnassignedSources = "exclude";
        var restricted = ProjectScope.Resolve(repo.Root, scope, repo.Files);
        Assert.DoesNotContain(restricted.Files, p => p.EndsWith("Other/Loose.cs"));
        Assert.Contains(restricted.Files, p => p.EndsWith("docker-compose.yml"));
        Assert.Contains(restricted.Files, p => p.EndsWith(".github/workflows/ci.yml"));

        scope.RepositoryFiles = "exclude";
        var privateDeployment = ProjectScope.Resolve(repo.Root, scope, repo.Files);
        Assert.DoesNotContain(privateDeployment.Files, p => p.EndsWith("docker-compose.yml"));
        Assert.DoesNotContain(privateDeployment.Files, p => p.EndsWith(".github/workflows/ci.yml"));
    }

    [Fact]
    public void Root_npm_manifest_only_owns_its_ecosystem_sources()
    {
        using var repo = new Sources(
            ("package.json", """{"name":"website","version":"1.0.0"}"""),
            ("src/index.ts", "export const app = true;"),
            ("Loose.cs", "public class Loose { }"));
        var scope = new ScopeConfig { Projects = new ScopeProjectsConfig { Include = ["package.json"] } };
        var plan = ProjectScope.Resolve(repo.Root, scope, repo.Files);
        Assert.Contains(plan.Files, p => p.EndsWith("src/index.ts"));
        Assert.DoesNotContain(plan.Files, p => p.EndsWith("Loose.cs"));
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
