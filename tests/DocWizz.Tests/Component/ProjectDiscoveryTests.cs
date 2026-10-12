namespace DocWizz.Tests.Component;

public class ProjectDiscoveryTests
{
    [Fact]
    public void Shared_projects_and_same_named_projects_have_distinct_ids()
    {
        using var files = new Sources(
            ("Backend.slnx", """
                <Solution><Folder Name="/Backend/"><Project Path="Shared/Shared.csproj" /></Folder></Solution>
                """),
            ("Web.slnx", """
                <Solution><Folder Name="/Frontend/"><Folder Name="/Common/"><Project Path="Shared/Shared.csproj" /></Folder></Folder></Solution>
                """),
            ("Shared/Shared.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""),
            ("Other/Shared.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"""),
            ("Loose/Free.cs", "public class Free { }")
        );
        var inventory = ProjectDiscovery.Scan(files.Root, files.Files);
        Assert.Equal(2, inventory.Solutions.Count);
        Assert.Equal(2, inventory.Projects.Count);
        Assert.NotEqual(inventory.Projects[0].Id, inventory.Projects[1].Id);
        var shared = inventory.Projects.Single(p => p.Path == "Shared/Shared.csproj");
        Assert.Equal(new[] { "Backend.slnx", "Web.slnx" }, shared.Solutions.Select(x => x.Id[4..]));
        Assert.Equal(new[] { "Backend", "Frontend/Common" }, shared.Solutions.Select(x => x.Folder));
        Assert.Contains("Loose/Free.cs", inventory.UnassignedSources);
    }

    [Fact]
    public void Missing_and_outside_root_project_references_are_not_followed()
    {
        using var files = new Sources(
            ("Application/Application.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
                <ProjectReference Include="../Absent/Absent.csproj" />
                <ProjectReference Include="../../Outside/Outside.csproj" />
                </ItemGroup></Project>
                """)
        );
        var inventory = ProjectDiscovery.Scan(files.Root, files.Files);
        var missing = inventory.Projects.Single().UnresolvedReferences;
        Assert.Contains(missing, x => x.Path == "Absent/Absent.csproj" && x.Reason == "not-found");
        Assert.Contains(missing, x => x.Path.StartsWith("../", StringComparison.Ordinal) && x.Reason == "outside-root");
    }

    [Fact]
    public void Discovery_is_independent_of_manifest_input_order()
    {
        using var files = new Sources(
            ("App.slnx", """<Solution><Project Path="App/App.csproj" /></Solution>"""),
            ("App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web" />"""),
            ("App/Program.cs", "public class Program { }")
        );
        var first = ProjectDiscovery.Scan(files.Root, files.Files);
        var second = ProjectDiscovery.Scan(files.Root, files.Files.AsEnumerable().Reverse());
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first), System.Text.Json.JsonSerializer.Serialize(second));
        Assert.False(Setup.NeedsProjectSelection(first));
        Assert.Equal("", Setup.DiscoveryProposal(first));
    }

    [Fact]
    public void Multi_solution_configuration_proposal_is_only_comments()
    {
        using var files = new Sources(
            ("A.slnx", """<Solution><Project Path="App/App.csproj" /></Solution>"""),
            ("B.slnx", """<Solution><Project Path="App/App.csproj" /></Solution>"""),
            ("App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web" />""")
        );
        var inventory = ProjectDiscovery.Scan(files.Root, files.Files);
        Assert.True(Setup.NeedsProjectSelection(inventory));
        var proposal = Setup.DiscoveryProposal(inventory);
        Assert.Contains("App/App.csproj", proposal);
        Assert.DoesNotContain("\nscope:", proposal);
        Assert.All(proposal.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => Assert.StartsWith("#", line));
    }
}
