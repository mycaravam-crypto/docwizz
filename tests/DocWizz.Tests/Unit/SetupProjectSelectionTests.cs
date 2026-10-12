namespace DocWizz.Tests.Unit;

public class SetupProjectSelectionTests
{
    static Sources Fixtures() => new(
        ("Backend.slnx", """
            <Solution>
              <Folder Name="/Core/"><Project Path="A/A.csproj" /></Folder>
              <Project Path="Tests/Tests.csproj" />
            </Solution>
            """),
        ("Client.slnx", """<Solution><Project Path="B/B.csproj" /></Solution>"""),
        ("A/A.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../B/B.csproj" /></ItemGroup></Project>"""),
        ("B/B.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
        ("Tests/Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>"""),
        ("C/C.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""")
    );

    static ProjectDiscovery.Inventory Inventory(Sources repo) => ProjectDiscovery.Scan(repo.Root, repo.Files);

    [Fact]
    public void Default_selection_prefers_root_solutions_and_skips_test_projects()
    {
        using var repo = Fixtures();
        var initial = SetupProjectSelection.Initial(Inventory(repo), null);
        Assert.Equal(new[] { "A/A.csproj", "B/B.csproj" }, initial);
    }

    [Fact]
    public void Saved_selection_is_reused_instead_of_overwritten()
    {
        using var repo = Fixtures();
        var config = new ScopeConfig { Projects = new ScopeProjectsConfig { Include = ["C/C.csproj"] } };
        Assert.Equal(["C/C.csproj"], SetupProjectSelection.Initial(Inventory(repo), config));
    }

    [Fact]
    public void Wizard_can_toggle_projects_and_select_a_whole_solution()
    {
        using var repo = Fixtures();
        var output = new StringWriter();
        var initial = SetupProjectSelection.Initial(Inventory(repo), null);
        // First clear, then add the first solution (A + its test project), then toggle B by index.
        // Project ordering: A, B, C, Tests.
        var picked = SetupProjectSelection.Prompt(Inventory(repo), initial, new StringReader("n\ns1\n2\nd\n"), output);
        Assert.False(picked.Cancelled);
        Assert.Equal(new[] { "A/A.csproj", "B/B.csproj", "Tests/Tests.csproj" }, picked.Projects);
        Assert.Contains("s1  solution Backend.slnx", output.ToString());
        Assert.Contains("[test]", output.ToString());
        Assert.Contains("project references", output.ToString());
    }

    [Fact]
    public void All_and_none_commands_require_nonempty_confirmation()
    {
        using var repo = Fixtures();
        var output = new StringWriter();
        var picked = SetupProjectSelection.Prompt(Inventory(repo), [], new StringReader("d\na\nd\n"), output);
        Assert.Equal(4, picked.Projects.Count);
        Assert.Contains("Select at least one project", output.ToString());
    }

    [Theory]
    [InlineData("q\n")]
    [InlineData("")]
    public void Cancel_and_eof_make_no_choice(string commands)
    {
        using var repo = Fixtures();
        var picked = SetupProjectSelection.Prompt(Inventory(repo), ["A/A.csproj"], new StringReader(commands), new StringWriter());
        Assert.True(picked.Cancelled);
        Assert.Empty(picked.Projects);
    }

    [Theory]
    [InlineData(false, false, true, null, true)]
    [InlineData(false, false, true, "true", false)]
    [InlineData(false, false, true, "1", false)]
    [InlineData(false, false, true, "false", false)]
    [InlineData(false, false, false, null, false)]
    [InlineData(true, false, true, null, false)]
    [InlineData(false, true, true, null, false)]
    public void Tty_policy_never_prompts_when_CI_is_set(bool inputRedirected, bool outputRedirected,
        bool userInteractive, string? ci, bool expected) =>
        Assert.Equal(expected, SetupProjectSelection.CanPrompt(inputRedirected, outputRedirected, userInteractive, ci));

    [Fact]
    public void Whole_solution_command_toggles_all_of_its_projects()
    {
        using var repo = Fixtures();
        var picked = SetupProjectSelection.Prompt(Inventory(repo), [], new StringReader("s1\ns1\n3\nd\n"),
            new StringWriter());
        Assert.Equal(["C/C.csproj"], picked.Projects);
    }

    [Fact]
    public void Invalid_selection_is_retained_for_correction_and_revalidated()
    {
        using var repo = new Sources(
            ("Shared/First.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
            ("Shared/Second.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""));
        var inventory = Inventory(repo);
        var output = new StringWriter();
        var choices = new List<string>();
        var picked = SetupProjectSelection.Prompt(inventory, [], new StringReader("1\nd\n2\nd\n"), output,
            paths =>
            {
                choices.Add(string.Join(",", paths));
                var selection = SetupProjectSelection.Scope(paths, null);
                ProjectScope.Resolve(repo.Root, selection, repo.Files);
            });
        Assert.Equal(["Shared/First.csproj", "Shared/Second.csproj"], picked.Projects);
        Assert.Equal(2, choices.Count);
        Assert.Contains("manifests share directory", output.ToString());
        Assert.Contains("Change the selection", output.ToString());
    }

    [Fact]
    public void Scope_editor_preserves_blank_lines_after_replaced_block()
    {
        var original = "profile: default\nscope:\n  projects:\n    include: [\"A/A.csproj\"]\n\n\ncheck:\n  min_coverage: 20\n";
        var edited = SetupProjectSelection.WithScope(original, SetupProjectSelection.Scope(["B/B.csproj"], null));
        Assert.Contains("repository_files: include\n\n\ncheck:\n", edited);
        var repeated = SetupProjectSelection.WithScope(edited, SetupProjectSelection.Scope(["B/B.csproj"], null));
        Assert.Equal(edited, repeated);
        var cleared = SetupProjectSelection.WithScope(edited, null);
        Assert.Equal("profile: default\n\n\ncheck:\n  min_coverage: 20\n", cleared);
    }

    [Fact]
    public void Generated_scope_uses_existing_reference_and_test_policies()
    {
        var original = new ScopeConfig { Projects = new ScopeProjectsConfig { Include = ["A/A.csproj"], References = "include", TestLinks = false }, UnassignedSources = "include", RepositoryFiles = "exclude" };
        var scope = SetupProjectSelection.Scope(["B/B.csproj", "A/A.csproj"], original);
        Assert.Equal(["A/A.csproj", "B/B.csproj"], scope.Projects.Include);
        Assert.Equal("include", scope.Projects.References);
        Assert.False(scope.Projects.TestLinks);
        Assert.Equal("include", scope.UnassignedSources);
        Assert.Equal("exclude", scope.RepositoryFiles);
    }

    [Fact]
    public void Scope_editor_preserves_other_yaml_keys_comments_and_crlf()
    {
        var original = "# my configuration\r\nprofile: api\r\nscope:\r\n  projects:\r\n    include: [\"A/A.csproj\"]\r\n    references: include\r\n# keep this comment\r\ncheck:\r\n  min_coverage: 10\r\n";
        var scope = SetupProjectSelection.Scope(["B/B.csproj"], null);
        var updated = SetupProjectSelection.WithScope(original, scope);
        Assert.StartsWith("# my configuration\r\nprofile: api\r\nscope:\r\n", updated);
        Assert.Contains("      - \"B/B.csproj\"\r\n", updated);
        Assert.EndsWith("# keep this comment\r\ncheck:\r\n  min_coverage: 10\r\n", updated);
        Assert.DoesNotContain("A/A.csproj", updated);
        var cleared = SetupProjectSelection.WithScope(updated, null);
        Assert.DoesNotContain("scope:", cleared);
        Assert.Equal("# my configuration\r\nprofile: api\r\n# keep this comment\r\ncheck:\r\n  min_coverage: 10\r\n", cleared);
    }

    [Fact]
    public void Scope_editor_appends_to_existing_config_without_changing_old_content()
    {
        var original = "# authored manually\nprofile: default\ncheck:\n  min_coverage: 10\n";
        var added = SetupProjectSelection.WithScope(original, SetupProjectSelection.Scope(["A/A.csproj"], null));
        Assert.StartsWith(original, added);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(added, "(?m)^scope:"));
    }

    [Fact]
    public void Scope_editor_rejects_duplicate_yaml_blocks()
    {
        var dup = "profile: default\nscope: {}\nscope: {}\n";
        Assert.Throws<ScopeException>(() => SetupProjectSelection.WithScope(dup, null));
    }

    [Fact]
    public void Scope_save_preserves_restrictive_unix_permissions()
    {
        if (OperatingSystem.IsWindows()) return;
        using var repo = Fixtures();
        var file = Path.Combine(repo.Root, "docwizz.yaml");
        File.WriteAllText(file, "profile: default\n");
        var restrictive = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(file, restrictive);
        Assert.Equal("scope-updated", SetupProjectSelection.Save(repo.Root,
            SetupProjectSelection.Scope(["A/A.csproj"], null)));
        Assert.Equal(restrictive, File.GetUnixFileMode(file));
    }

    [Fact]
    public void Scope_save_changes_only_scope_and_is_idempotent()
    {
        using var repo = Fixtures();
        var file = Path.Combine(repo.Root, "docwizz.yaml");
        var config = "# keep exactly\nprofile: default\ncheck:\n  min_coverage: 1\n";
        File.WriteAllText(file, config);
        var scope = SetupProjectSelection.Scope(["A/A.csproj"], null);
        Assert.Equal("scope-updated", SetupProjectSelection.Save(repo.Root, scope));
        var after = File.ReadAllText(file);
        Assert.StartsWith(config, after);
        Assert.NotNull(Config.Load(repo.Root).Scope);
        Assert.Equal("kept", SetupProjectSelection.Save(repo.Root, scope));
        Assert.Equal(after, File.ReadAllText(file));
        Assert.Equal("scope-updated", SetupProjectSelection.Save(repo.Root, null));
        Assert.Equal(config, File.ReadAllText(file));
    }
}
