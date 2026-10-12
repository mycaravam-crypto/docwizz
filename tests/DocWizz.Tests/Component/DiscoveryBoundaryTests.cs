namespace DocWizz.Tests.Component;

public class DiscoveryBoundaryTests
{
    [Fact]
    public void Root_npm_manifest_does_not_hide_unassigned_csharp_or_java()
    {
        using var repo = new Sources(
            ("package.json", """{"name":"root-tools","version":"1.0.0"}"""),
            ("src/client.ts", "export const selected = true;"),
            ("Loose/Orphan.cs", "public class Orphan { }"),
            ("Java/Worker.java", "class Worker { }"));
        var inventory = ProjectDiscovery.Scan(repo.Root, repo.Files);
        Assert.Single(inventory.Projects);
        Assert.Equal("package.json", inventory.Projects[0].Path);
        Assert.Equal(new[] { "Java/Worker.java", "Loose/Orphan.cs" }, inventory.UnassignedSources);
        var reversed = ProjectDiscovery.Scan(repo.Root, repo.Files.AsEnumerable().Reverse());
        Assert.Equal(inventory.UnassignedSources, reversed.UnassignedSources);
    }

    [Fact]
    public void Project_discovery_cannot_follow_symlinked_manifests_outside_the_root()
    {
        if (OperatingSystem.IsWindows()) return; // Windows symlinks may require administrator privileges.
        using var repo = new Sources(
            ("App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
            ("App/App.cs", "public class App { }"));
        var external = Directory.CreateTempSubdirectory("docwizz-outside-");
        try
        {
            var target = Path.Combine(external.FullName, "Outside.csproj");
            File.WriteAllText(target, """<Project Sdk="Microsoft.NET.Sdk" />""");
            var folderLink = Path.Combine(repo.Root, "Linked");
            Directory.CreateSymbolicLink(folderLink, external.FullName);
            var fileLink = Path.Combine(repo.Root, "LinkedFile.csproj");
            File.CreateSymbolicLink(fileLink, target);

            var candidates = repo.Files.Concat(new[]
            {
                Path.Combine(folderLink, "Outside.csproj"),
                fileLink,
                target
            }).ToList();
            var inventory = ProjectDiscovery.Scan(repo.Root, candidates);
            Assert.Single(inventory.Projects);
            Assert.Equal("App/App.csproj", inventory.Projects[0].Path);
            Assert.False(ProjectDiscovery.SafeFile(repo.Root, Path.Combine(folderLink, "Outside.csproj")));
            Assert.False(ProjectDiscovery.SafeFile(repo.Root, fileLink));
            Assert.False(ProjectDiscovery.SafeFile(repo.Root, target));

            var scope = new ScopeConfig
            {
                Projects = new ScopeProjectsConfig { Include = ["Linked/Outside.csproj"] }
            };
            var error = Assert.Throws<ScopeException>(() => ProjectScope.Resolve(repo.Root, scope, candidates));
            Assert.Contains("was not discovered below the scan root", error.Message);
            Assert.Equal("""<Project Sdk="Microsoft.NET.Sdk" />""", File.ReadAllText(target));
        }
        finally { external.Delete(recursive: true); }
    }

    [Fact]
    public void Missing_and_outside_root_references_remain_disclosed_without_leaking_source()
    {
        using var repo = new Sources(
            ("App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
                  <ProjectReference Include="../Gone/Gone.csproj" />
                  <ProjectReference Include="../../outside/Outside.csproj" />
                </ItemGroup></Project>
                """),
            ("App/App.cs", "public class App { }"));
        var inventory = ProjectDiscovery.Scan(repo.Root, repo.Files);
        var unresolved = inventory.Projects.Single().UnresolvedReferences;
        Assert.Contains(unresolved, r => r.Path == "Gone/Gone.csproj" && r.Reason == "not-found");
        Assert.Contains(unresolved, r => r.Reason == "outside-root");
        var scope = new ScopeConfig { Projects = new ScopeProjectsConfig { Include = ["App/App.csproj"] } };
        var plan = ProjectScope.Resolve(repo.Root, scope, repo.Files);
        Assert.DoesNotContain(plan.Files, f => f.Contains("Outside.csproj", StringComparison.Ordinal));
        Assert.Equal(2, plan.Inventory!.UnresolvedReferences.Count);
        Assert.Empty(plan.Inventory.MetadataOnlyProjects);
    }
}
