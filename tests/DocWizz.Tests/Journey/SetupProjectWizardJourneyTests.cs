namespace DocWizz.Tests.Journey;

public class SetupProjectWizardJourneyTests
{
    static Sources Repo() => new(
        ("Backend.slnx", """<Solution><Project Path="A/A.csproj" /><Project Path="B/B.csproj" /></Solution>"""),
        ("Other.slnx", """<Solution><Project Path="C/C.csproj" /></Solution>"""),
        ("A/A.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
        ("B/B.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
        ("C/C.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
        ("A/A.cs", "public class A { }"),
        ("B/B.cs", "public class B { }"),
        ("C/C.cs", "public class C { }")
    );

    static string Config => """
        # authored configuration that wizard must keep
        profile: default
        check:
          min_coverage: 1
        scope:
          projects:
            include: ["A/A.csproj"]
            references: metadata
            test_links: true
          unassigned_sources: exclude
          repository_files: include
        """;

    [Fact]
    public void Explicit_selection_refuses_redirected_input_instead_of_blocking_ci()
    {
        using var repo = Repo();
        var r = Docwizz.Run("setup", repo.Root, "--select-projects");
        Assert.Equal(1, r.Exit);
        Assert.Contains("requires an interactive terminal", r.Err);
        Assert.False(File.Exists(Path.Combine(repo.Root, "docwizz.yaml")));
        Assert.False(Directory.Exists(Path.Combine(repo.Root, "docs")));
    }

    [Fact]
    public void Yes_skips_prompt_and_does_not_infer_scope_for_multi_solution_repos()
    {
        using var repo = Repo();
        var r = Docwizz.Run("setup", repo.Root, "--yes");
        Assert.Equal(0, r.Exit);
        Assert.Contains("[1/7] discover", r.Out);
        Assert.DoesNotContain("[2/8] select", r.Out);
        var config = File.ReadAllText(Path.Combine(repo.Root, "docwizz.yaml"));
        Assert.DoesNotContain("\nscope:\n", config);
        Assert.Contains("# Project inventory inferred by docwizz discover", config);
        Assert.Contains("`A`", File.ReadAllText(Path.Combine(repo.Root, "docs", "index.md")));
    }

    [Fact]
    public void All_projects_clears_only_existing_scope_before_source_scan()
    {
        using var repo = Repo();
        var file = Path.Combine(repo.Root, "docwizz.yaml");
        File.WriteAllText(file, Config);
        var r = Docwizz.Run("setup", repo.Root, "--all-projects", "--yes");
        Assert.Equal(0, r.Exit);
        Assert.Contains("[2/8] select", r.Out);
        Assert.Contains("[3/8] scan", r.Out);
        Assert.Contains("[4/8] config", r.Out);
        Assert.Contains("scope-updated", r.Out);
        var after = File.ReadAllText(file);
        Assert.StartsWith("# authored configuration that wizard must keep", after);
        Assert.Contains("  min_coverage: 1", after);
        Assert.DoesNotContain("\nscope:\n", after);
        var docs = File.ReadAllText(Path.Combine(repo.Root, "docs", "index.md"));
        Assert.DoesNotContain("## Documentation scope", docs);
        Assert.Contains("A", docs);
        Assert.Contains("B", docs);
        Assert.Contains("C", docs);
        var again = Docwizz.Run("setup", repo.Root, "--all-projects", "--yes");
        Assert.Equal(0, again.Exit);
        Assert.Contains("kept", again.Out);
        Assert.Equal(after, File.ReadAllText(file));
    }

    [Fact]
    public void Yes_preserves_existing_selected_scope_and_user_configuration()
    {
        using var repo = Repo();
        var file = Path.Combine(repo.Root, "docwizz.yaml");
        File.WriteAllText(file, Config);
        var r = Docwizz.Run("setup", repo.Root, "--yes");
        Assert.Equal(0, r.Exit);
        Assert.Contains("[3/7] config       ok       kept", r.Out);
        Assert.Equal(Config, File.ReadAllText(file));
        var index = File.ReadAllText(Path.Combine(repo.Root, "docs", "index.md"));
        Assert.Contains("## Documentation scope", index);
        Assert.Contains("A/A.csproj", index);
        Assert.DoesNotContain("B/B.csproj (source-scanned)", index);
    }

    [Fact]
    public void Explicit_all_projects_without_existing_config_keeps_legacy_generated_default()
    {
        using var repo = Repo();
        var r = Docwizz.Run("setup", repo.Root, "--all-projects");
        Assert.Equal(0, r.Exit);
        Assert.Contains("[2/8] select", r.Out);
        Assert.Contains("[4/8] config       ok       written", r.Out);
        Assert.Null(global::Config.Load(repo.Root).Scope);
    }
}
