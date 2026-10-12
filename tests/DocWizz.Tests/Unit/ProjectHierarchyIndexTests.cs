using System.Diagnostics;

namespace DocWizz.Tests.Unit;

public class ProjectHierarchyIndexTests
{
    [Fact]
    public void Nested_project_files_are_indexed_once_under_the_nearest_manifest()
    {
        using var repo = new Sources(
            ("Root.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
            ("Root.cs", "public class Root { }"),
            ("Nested/Nested.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
            ("Nested/Nested.cs", "public class Nested { }"),
            ("Loose/Other.ts", "export const other = 1;"));
        var nodes = new List<Node>
        {
            new("proj:Root.csproj", "project", "Root", "Root.csproj", 1, Tags: ["dotnet", "library"]),
            new("proj:Nested/Nested.csproj", "project", "Nested", "Nested/Nested.csproj", 1, Tags: ["dotnet", "library"]),
            new("cs:Root", "class", "Root", "Root.cs", 1),
            new("cs:Nested", "class", "Nested", "Nested/Nested.cs", 1),
            new("ts:Loose", "module", "Other", "Loose/Other.ts", 1),
        };
        var model = new CodeModel(null, nodes, []);
        var hierarchy = new ProjectHierarchy(repo.Root, model, repo.Files, ["Nested", ".", "Loose"]);
        Assert.Equal(["cs:Root"], hierarchy.NodesFor("proj:Root.csproj").Select(n => n.Id));
        Assert.Equal(["cs:Nested"], hierarchy.NodesFor("proj:Nested/Nested.csproj").Select(n => n.Id));
        Assert.Empty(hierarchy.NodesFor("proj:Missing.csproj"));
        Assert.Equal("proj:Nested/Nested.csproj", hierarchy.Owner("Nested/Nested.cs")?.Id);
        Assert.Equal("proj:Root.csproj", hierarchy.Owner("Root.cs")?.Id);
        Assert.Null(hierarchy.Owner("Loose/Other.ts"));
        Assert.Contains("Loose", hierarchy.UnassignedModules);
        Assert.Contains("Nested", hierarchy.ById("proj:Nested/Nested.csproj")!.Modules);
        Assert.Contains(".", hierarchy.ById("proj:Root.csproj")!.Modules);
    }

    [Fact]
    public void Large_multi_project_index_has_bounded_time_and_correct_ownership()
    {
        const int projectCount = 80;
        const int classesPerProject = 160;
        var manifests = Enumerable.Range(0, projectCount).Select(i =>
            ($"Project{i:D3}/Project{i:D3}.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""))
            .ToArray();
        using var repo = new Sources(manifests);
        var nodes = new List<Node>(projectCount * (classesPerProject + 1));
        var folders = new List<string>(projectCount);
        for (var i = 0; i < projectCount; i++)
        {
            var project = $"Project{i:D3}";
            nodes.Add(new($"proj:{project}/{project}.csproj", "project", project, $"{project}/{project}.csproj", 1,
                Tags: ["dotnet", "library"]));
            folders.Add($"{project}/Services");
            for (var j = 0; j < classesPerProject; j++)
                nodes.Add(new($"cs:{project}.Service{j:D3}", "class", $"Service{j:D3}",
                    $"{project}/Services/Service{j:D3}.cs", j + 1));
        }

        var stopwatch = Stopwatch.StartNew();
        var hierarchy = new ProjectHierarchy(repo.Root, new CodeModel(null, nodes, []), repo.Files, folders);
        stopwatch.Stop();

        Assert.Equal(projectCount, hierarchy.Projects.Count);
        Assert.Empty(hierarchy.UnassignedModules);
        Assert.Equal(projectCount, hierarchy.Projects.SelectMany(p => p.Modules).Count());
        foreach (var project in hierarchy.Projects)
        {
            Assert.Single(project.Modules);
            Assert.Equal(classesPerProject, hierarchy.NodesFor(project.Id).Count);
            Assert.All(hierarchy.NodesFor(project.Id), n => Assert.StartsWith(project.Path.Split('/')[0] + "/", n.File));
        }
        // A broad CI guard, not a microbenchmark; the important invariant is that lookup
        // work is indexed by node/file, not repeated for every project and module.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"Project hierarchy indexing of {projectCount} projects / {nodes.Count} nodes took {stopwatch.Elapsed}.");
    }
}
