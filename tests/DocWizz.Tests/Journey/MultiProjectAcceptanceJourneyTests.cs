using System.Text.Json;

namespace DocWizz.Tests.Journey;

// Final acceptance fixture: classic ASP.NET MVC + modern .NET SDK projects, shared solutions,
// test evidence, Java, npm and an orphan file in one repository. No project restore/build is required.
public class MultiProjectAcceptanceJourneyTests
{
    static Sources Repo() => new(
        ("Backend.slnx", """
            <Solution><Folder Name="/Backend/"><Project Path="backend/Api/Api.csproj" /></Folder>
            <Project Path="backend/Core/Core.csproj" />
            <Project Path="backend/LegacyMvc/LegacyMvc.csproj" />
            <Project Path="backend/Tests/Tests.csproj" /></Solution>
            """),
        ("Ops.slnx", """
            <Solution><Project Path="backend/Core/Core.csproj" />
            <Project Path="apps/Mailer/Mailer.csproj" /></Solution>
            """),
        ("backend/Api/Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><ProjectReference Include="../Core/Core.csproj" /></ItemGroup></Project>
            """),
        ("backend/Core/Core.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""),
        ("backend/Tests/Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
            <ItemGroup><ProjectReference Include="../Api/Api.csproj" /></ItemGroup></Project>
            """),
        ("backend/LegacyMvc/LegacyMvc.csproj", """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion></PropertyGroup>
              <ItemGroup><Reference Include="System.Web.Mvc" /></ItemGroup>
            </Project>
            """),
        ("apps/Mailer/Mailer.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""),
        ("apps/Other/Other.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
        ("web/package.json", """{"name":"web","dependencies":{"private-excluded-package":"1.2.3"}}"""),
        ("java/pom.xml", """<project><modelVersion>4.0.0</modelVersion><groupId>example</groupId><artifactId>worker</artifactId></project>"""),
        ("backend/Api/Controllers/OrdersController.cs", """
            namespace Backend.Api.Controllers;
            public class OrdersController { public int Find(int id) => id; }
            """),
        ("backend/Core/Domain/Order.cs", "namespace Backend.Core.Domain; public record Order(int Id);"),
        ("backend/Tests/OrderTests.cs", "public class OrderTests { public void Verifies() { } }"),
        ("backend/LegacyMvc/Controllers/LegacyController.cs", """
            using System.Web.Mvc;
            namespace LegacyMvc.Controllers;
            public class LegacyController : Controller
            {
                public ActionResult Index() { return View(); }
            }
            """),
        ("apps/Mailer/Services/Mailer.cs", "namespace Mailer.Services; public class MailService { }"),
        ("apps/Other/Services/ExcludedOnlyMarker.cs", "public class ExcludedOnlyMarker { }"),
        ("web/src/App.ts", "export const HiddenFrontendMarker = 123;"),
        ("java/src/main/java/example/Worker.java", "package example; class HiddenJavaMarker { }"),
        ("Loose/Free.cs", "public class Free { }")
    );

    static string GoldenPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "DocWizz.Tests", "Golden", "mixed-project-inventory.txt");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Mixed-repository golden inventory not found in the test checkout");
    }

    static string Contract(ProjectDiscovery.Inventory inv)
    {
        var lines = new List<string> { "solutions:" };
        lines.AddRange(inv.Solutions.Select(s => $"{s.Path}: {string.Join(", ", s.Projects.Select(m => m.ProjectId[5..]))}"));
        lines.Add("projects:");
        lines.AddRange(inv.Projects.Select(p => $"{p.Path}: {p.Role}"));
        lines.Add("unassigned:");
        lines.AddRange(inv.UnassignedSources);
        return string.Join("\n", lines) + "\n";
    }

    static string Config => """
        profile: default
        scope:
          projects:
            include: ["backend/Api/Api.csproj", "backend/LegacyMvc/LegacyMvc.csproj", "apps/Mailer/Mailer.csproj"]
            references: metadata
            test_links: true
          unassigned_sources: exclude
          repository_files: include
        """;

    [Fact]
    public void Discovery_matches_versioned_golden_and_never_changes_the_repo()
    {
        using var repo = Repo();
        var before = Docwizz.Snapshot(repo.Root);
        var inventory = ProjectDiscovery.Scan(repo.Root, repo.Files);
        var actual = Contract(inventory);
        Assert.Equal(File.ReadAllText(GoldenPath()).Replace("\r\n", "\n"), actual);
        Assert.Equal(actual, Contract(ProjectDiscovery.Scan(repo.Root, repo.Files.AsEnumerable().Reverse())));
        var cli = Docwizz.Run("discover", repo.Root, "--format", "json");
        Assert.True(cli.Exit == 0, cli.ToString());
        using var json = JsonDocument.Parse(cli.Out);
        Assert.Equal(2, json.RootElement.GetProperty("solutions").GetArrayLength());
        Assert.Equal(8, json.RootElement.GetProperty("projects").GetArrayLength());
        Assert.Equal(before, Docwizz.Snapshot(repo.Root));
        Assert.Equal(2, inventory.Projects.Single(p => p.Path == "backend/Core/Core.csproj").Solutions.Count);
        Assert.Equal("library", inventory.Projects.Single(p => p.Path == "backend/LegacyMvc/LegacyMvc.csproj").Role);
    }

    [Fact]
    public void Three_of_eight_projects_produce_scoped_docs_without_excluded_symbols()
    {
        using var repo = Repo();
        File.WriteAllText(Path.Combine(repo.Root, "docwizz.yaml"), Config);
        var result = Docwizz.Run("generate", repo.Root, "--html");
        Assert.True(result.Exit == 0, result.ToString());
        var docs = Path.Combine(repo.Root, "docs");
        var index = File.ReadAllText(Path.Combine(docs, "index.md"));
        Assert.Contains("## Documentation scope", index);
        Assert.Contains("apps/Mailer/Mailer.csproj", index);
        Assert.Contains("backend/Tests/Tests.csproj` (test evidence only)", index);
        Assert.Contains("backend/Core/Core.csproj` (metadata-only)", index);
        var api = File.ReadAllText(Path.Combine(docs, "api.md"));
        Assert.Contains("ASP.NET MVC conventional routes", api);
        Assert.Contains("not a complete runtime routing inventory", api);
        var stub = File.ReadAllText(Path.Combine(docs,
            ProjectHierarchy.ProjectPage("backend/Core/Core.csproj")));
        Assert.Contains("Metadata-only dependency", stub);
        Assert.DoesNotContain("record Order", stub);
        Assert.True(File.Exists(Path.Combine(docs,
            ProjectHierarchy.ProjectPage("backend/LegacyMvc/LegacyMvc.csproj"))));
        using var search = JsonDocument.Parse(File.ReadAllText(Path.Combine(docs, "search.json")));
        var entries = search.RootElement.EnumerateArray().ToList();
        Assert.DoesNotContain(entries, e => e.GetProperty("name").GetString()?.Contains("ExcludedOnlyMarker") == true
            || e.GetProperty("name").GetString()?.Contains("HiddenFrontendMarker") == true
            || e.GetProperty("name").GetString()?.Contains("HiddenJavaMarker") == true);
        var modelFile = Path.Combine(docs, ".docwizz", "model.json");
        var model = File.ReadAllText(modelFile);
        Assert.DoesNotContain("ExcludedOnlyMarker", model);
        Assert.DoesNotContain("HiddenFrontendMarker", model);
        Assert.DoesNotContain("HiddenJavaMarker", model);
        Assert.DoesNotContain("private-excluded-package", model);
        Assert.Equal(Config, File.ReadAllText(Path.Combine(repo.Root, "docwizz.yaml")));

        // Golden-file-style regression for all generated pages: identical input implies identical output.
        // The comparison includes Markdown, HTML, search entries, API warnings and the cached model.
        var first = Docwizz.Snapshot(docs);
        var second = Docwizz.Run("generate", repo.Root, "--html");
        Assert.True(second.Exit == 0, second.ToString());
        Assert.Equal(first, Docwizz.Snapshot(docs));
    }
}
