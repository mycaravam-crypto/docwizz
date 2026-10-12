using System.Text.Json;

namespace DocWizz.Tests.Journey;

public class ProjectNavigationJourneyTests
{
    static Sources Repo() => new(
        ("Backend.slnx", """
            <Solution><Folder Name="/Core/"><Project Path="Core/Shared.csproj" /></Folder>
            <Project Path="Api/Api.csproj" /></Solution>
            """),
        ("Admin.slnx", """<Solution><Project Path="Core/Shared.csproj" /></Solution>"""),
        ("Core/Shared.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""),
        ("Api/Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup>
              <ProjectReference Include="../Core/Shared.csproj" />
            </ItemGroup></Project>
            """),
        ("Other/Shared.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>"""),
        ("Utility/Utility.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
        ("Core/Domain/Item.cs", "namespace Core.Domain; public record Item(int Id);"),
        ("Api/Controllers/ItemsController.cs", """
            namespace Api.Controllers;
            public class ItemsController { public int Find(int id) => id; }
            """),
        ("Other/Services/OtherService.cs", "namespace Other.Services; public class OtherService { }"),
        ("Utility/Tools/Utility.cs", "namespace Utility.Tools; public class Utility { }"),
        ("Loose/Standalone.cs", "namespace Standalone; public class Extra { }")
    );

    static string FileAt(Sources repo, string page) =>
        Path.Combine(repo.Root, "docs", page.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void Generates_unique_project_and_solution_pages_and_cross_links()
    {
        using var repo = Repo();
        var r = Docwizz.Run("generate", repo.Root, "--html");
        Assert.True(r.Exit == 0, r.ToString());
        var a = ProjectHierarchy.ProjectPage("Core/Shared.csproj");
        var b = ProjectHierarchy.ProjectPage("Other/Shared.csproj");
        Assert.NotEqual(a, b);
        Assert.True(File.Exists(FileAt(repo, a)));
        Assert.True(File.Exists(FileAt(repo, b)));
        Assert.True(File.Exists(FileAt(repo, ProjectHierarchy.SolutionPage("Backend.slnx"))));
        Assert.True(File.Exists(FileAt(repo, ProjectHierarchy.SolutionPage("Admin.slnx"))));
        var core = File.ReadAllText(FileAt(repo, a));
        Assert.Contains("## Solution membership", core);
        Assert.Contains("[Backend.slnx]", core);
        Assert.Contains("[Admin.slnx]", core);
        Assert.Contains("modules/Core-Domain.md", core);
        Assert.Contains("Item", core);
        Assert.Contains("## Components", core);
        var api = File.ReadAllText(FileAt(repo, ProjectHierarchy.ProjectPage("Api/Api.csproj")));
        Assert.Contains("../" + a, api);
        Assert.Contains("modules/Api-Controllers.md", api);
        var index = File.ReadAllText(FileAt(repo, "index.md"));
        Assert.Contains("## Solutions and projects", index);
        Assert.Contains("Backend.slnx", index);
        Assert.Contains("Admin.slnx", index);
        Assert.Contains(a, index);
        Assert.Contains(b, index);
        Assert.Contains("Projects without a solution", index);
        Assert.Contains("Unassigned modules", index);

        // Legacy module path remains stable and gains a project backlink.
        var module = FileAt(repo, "modules/Core-Domain.md");
        Assert.True(File.Exists(module));
        Assert.Contains("../" + a, File.ReadAllText(module));
        var container = File.ReadAllText(FileAt(repo, "views/containers.md"));
        Assert.Contains("Core/Shared.csproj", container);
        Assert.Contains("Other/Shared.csproj", container);
        Assert.DoesNotContain("[Api/Api.csproj]", container);   // a unique name keeps its plain label
    }

    [Fact]
    public void Html_breadcrumbs_nested_sidebar_and_search_reference_generated_pages()
    {
        using var repo = Repo();
        Assert.Equal(0, Docwizz.Run("generate", repo.Root, "--html").Exit);
        var projectMd = ProjectHierarchy.ProjectPage("Core/Shared.csproj");
        var solutionMd = ProjectHierarchy.SolutionPage("Backend.slnx");
        var projectHtml = File.ReadAllText(FileAt(repo, Path.ChangeExtension(projectMd, ".html")));
        Assert.Contains("Solutions &amp; projects", projectHtml);
        Assert.Contains("href=\"../" + Path.ChangeExtension(solutionMd, ".html") + "\"", projectHtml);
        Assert.Contains("href=\"../modules/Core-Domain.html\"", projectHtml);
        Assert.Contains("aria-label=\"Site\"", projectHtml);
        Assert.Contains("class=\"breadcrumbs\"", projectHtml);
        var moduleHtml = File.ReadAllText(FileAt(repo, "modules/Core-Domain.html"));
        Assert.Contains("href=\"../" + Path.ChangeExtension(projectMd, ".html") + "\"", moduleHtml);
        using var json = JsonDocument.Parse(File.ReadAllText(FileAt(repo, "search.json")));
        var entries = json.RootElement.EnumerateArray().ToList();
        Assert.Contains(entries, e => e.GetProperty("kind").GetString() == "solution"
            && e.GetProperty("page").GetString() == solutionMd);
        Assert.Contains(entries, e => e.GetProperty("kind").GetString() == "project"
            && e.GetProperty("page").GetString() == projectMd);
        Assert.True(File.Exists(FileAt(repo, "search.js")));
    }

    [Fact]
    public void Scoped_regeneration_keeps_metadata_stubs_and_removes_stale_generated_pages()
    {
        using var repo = Repo();
        Assert.Equal(0, Docwizz.Run("generate", repo.Root, "--html").Exit);
        var old = ProjectHierarchy.ProjectPage("Other/Shared.csproj");
        Assert.True(File.Exists(FileAt(repo, old)));
        Directory.CreateDirectory(Path.GetDirectoryName(FileAt(repo, "projects/notes.md"))!);
        File.WriteAllText(FileAt(repo, "projects/notes.md"), "# Keep this human-written note");
        File.WriteAllText(Path.Combine(repo.Root, "docwizz.yaml"), """
            profile: default
            scope:
              projects:
                include: ["Api/Api.csproj"]
                references: metadata
                test_links: false
              unassigned_sources: exclude
              repository_files: include
            """);
        var scoped = Docwizz.Run("generate", repo.Root, "--html");
        Assert.True(scoped.Exit == 0, scoped.ToString());
        Assert.False(File.Exists(FileAt(repo, old)));
        Assert.False(File.Exists(FileAt(repo, Path.ChangeExtension(old, ".html"))));
        Assert.False(File.Exists(FileAt(repo, ProjectHierarchy.SolutionPage("Admin.slnx"))));
        Assert.True(File.Exists(FileAt(repo, "projects/notes.md")));
        var shared = File.ReadAllText(FileAt(repo, ProjectHierarchy.ProjectPage("Core/Shared.csproj")));
        Assert.Contains("Metadata-only dependency", shared);
        Assert.DoesNotContain("Item", shared);
        var index = File.ReadAllText(FileAt(repo, "index.md"));
        Assert.DoesNotContain("Other/Shared.csproj", index);
        Assert.Contains("Core/Shared.csproj", index);
        using var search = JsonDocument.Parse(File.ReadAllText(FileAt(repo, "search.json")));
        var entries = search.RootElement.EnumerateArray().ToList();
        Assert.DoesNotContain(entries, e => e.GetProperty("name").GetString()?.Contains("OtherService") == true);
        Assert.Contains(entries, e => e.GetProperty("page").GetString() == ProjectHierarchy.ProjectPage("Core/Shared.csproj")
            && e.GetProperty("kind").GetString() == "dependency-only project");
    }

    [Fact]
    public void Project_overview_lists_structurally_linked_test_evidence()
    {
        using var repo = new Sources(
            ("App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
            ("App/Logic/Thing.cs", """
                namespace App.Logic;
                public class Thing { public int Plus(int value) => value + 1; }
                """),
            ("Tests/App.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
                <ItemGroup><ProjectReference Include="../App/App.csproj" /></ItemGroup></Project>
                """),
            ("Tests/ThingTests.cs", """
                using App.Logic;
                namespace App.Tests;
                public class ThingTests { public void Verifies() { var subject = new Thing(); subject.Plus(1); } }
                """)
        );
        var result = Docwizz.Run("generate", repo.Root);
        Assert.True(result.Exit == 0, result.ToString());
        var project = File.ReadAllText(FileAt(repo, ProjectHierarchy.ProjectPage("App/App.csproj")));
        Assert.Contains("Source files represented in the code model:", project);
        Assert.Contains("## Test traceability", project);
        Assert.Contains("ThingTests.Verifies", project);
        Assert.DoesNotContain("Tests/ThingTests.cs", File.ReadAllText(FileAt(repo, "index.md")));
    }

    [Fact]
    public void Rerunning_the_same_generation_is_deterministic()
    {
        using var repo = Repo();
        Assert.Equal(0, Docwizz.Run("generate", repo.Root, "--html").Exit);
        var before = Docwizz.Snapshot(Path.Combine(repo.Root, "docs"));
        var run = Docwizz.Run("generate", repo.Root, "--html");
        Assert.True(run.Exit == 0, run.ToString());
        Assert.Equal(before, Docwizz.Snapshot(Path.Combine(repo.Root, "docs")));
    }
}
