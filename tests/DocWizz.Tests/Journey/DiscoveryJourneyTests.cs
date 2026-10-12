using System.Text.Json;
namespace DocWizz.Tests.Journey;

public class DiscoveryJourneyTests
{
    static Sources Fixture() => new(
        ("Backend.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Cargo", "Cargo/Cargo.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Shared", "Shared/Shared.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Global
            EndGlobal
            """),
        ("Frontend.slnx", """
            <Solution><Project Path="Shared/Shared.csproj" /></Solution>
            """),
        ("Cargo/Cargo.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><ProjectReference Include="../Shared/Shared.csproj" /></ItemGroup></Project>
            """),
        ("Shared/Shared.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""),
        ("Other/Shared.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"""),
        ("Loose/NotInProject.cs", "public class Loose { }")
    );

    [Fact]
    public void Multi_solution_inventory_is_json_and_deterministic()
    {
        using var repo = Fixture();
        var a = Docwizz.Run("discover", repo.Root, "--format", "json");
        var b = Docwizz.Run("discover", repo.Root, "--format", "json");
        Assert.Equal(0, a.Exit);
        Assert.Equal("", a.Err);
        Assert.Equal(a.Out, b.Out);
        using var doc = JsonDocument.Parse(a.Out);
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("solutions").GetArrayLength());
        Assert.Equal(3, root.GetProperty("projects").GetArrayLength());
        Assert.Equal("Loose/NotInProject.cs", root.GetProperty("unassignedSources")[0].GetString());
        var projects = root.GetProperty("projects").EnumerateArray().ToList();
        Assert.Equal(2, projects.Count(p => p.GetProperty("name").GetString() == "Shared"));
        var shared = projects.Single(p => p.GetProperty("path").GetString() == "Shared/Shared.csproj");
        Assert.Equal(2, shared.GetProperty("solutions").GetArrayLength());
        Assert.Equal("library", shared.GetProperty("role").GetString());
        var cargo = projects.Single(p => p.GetProperty("name").GetString() == "Cargo");
        Assert.Equal("executable", cargo.GetProperty("role").GetString());
        Assert.Equal("proj:Shared/Shared.csproj", cargo.GetProperty("references")[0].GetString());
    }

    [Fact]
    public void Discovery_does_not_modify_repository()
    {
        using var repo = Fixture();
        var before = Docwizz.Snapshot(repo.Root);
        var result = Docwizz.Run("discover", repo.Root);
        Assert.Equal(0, result.Exit);
        Assert.Contains("Solutions: 2; projects: 3", result.Out);
        Assert.Equal(before, Docwizz.Snapshot(repo.Root));
    }

    [Fact]
    public void Missing_solution_project_is_reported()
    {
        using var repo = new Sources(("Missing.slnx", "<Solution><Project Path=\"Missing/Missing.csproj\" /></Solution>"));
        var r = Docwizz.Run("discover", repo.Root, "--format", "json");
        Assert.Equal(0, r.Exit);
        using var d = JsonDocument.Parse(r.Out);
        Assert.Equal("not-found", d.RootElement.GetProperty("solutions")[0].GetProperty("unresolvedProjects")[0].GetProperty("reason").GetString());
    }
}
