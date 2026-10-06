namespace DocWizz.Tests.Component;

// Project files → remediation suggestions: the command or patch, impact, and validation in a temporary copy.
public class RemediationTests
{
    const string Codec = "namespace Api; using Newtonsoft.Json; public class Codec { public string W(object o) => JsonConvert.SerializeObject(o); }";

    static string Csproj(string packages, string props = "") => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework>{props}</PropertyGroup>
          <ItemGroup>
            {packages}
          </ItemGroup>
        </Project>
        """;

    const string Central = """
        <Project>
          <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
          <ItemGroup>
            <PackageVersion Include="Newtonsoft.Json" Version="12.0.3" />
          </ItemGroup>
        </Project>
        """;

    static CodeModel Model(Sources src)
    {
        var (nodes, edges) = CSharpScanner.Scan(src.Root, src.Files.Where(f => f.EndsWith(".cs")));
        var (projNodes, projEdges) = Projects.Scan(src.Root, src.Files.Where(f => f.EndsWith(".csproj")));
        return new(null, [.. nodes, .. projNodes], [.. edges, .. projEdges]);
    }

    static List<Remediation> Plan(Sources src, (string, string)? request = null, Config? config = null) =>
        Remediations.Plan(src.Root, Model(src), config ?? Models.Config(), request);

    [Fact]
    public void Project_local_version_gets_a_dotnet_add_command_and_impact()
    {
        using var src = new Sources(("src/Api/Api.csproj", Csproj("""<PackageReference Include="Newtonsoft.Json" Version="12.0.3" />""")), ("src/Api/Codec.cs", Codec));
        var r = Assert.Single(Plan(src, ("Newtonsoft.Json", "13.0.3")));
        var change = Assert.Single(r.Changes);
        Assert.Equal("dotnet add src/Api/Api.csproj package Newtonsoft.Json --version 13.0.3", change.Command);
        Assert.Equal(("12.0.3", "13.0.3", "major"), (change.Current, change.Target, change.Kind));
        Assert.Contains("""Version="13.0.3" """, change.Edit!.After);
        Assert.Equal(["cs:Api.Codec"], r.Impact.Uses.Select(u => u.Id));
        Assert.Equal("Newtonsoft.Json", r.Impact.Uses[0].Namespace);
        // A major update is never judged by its version number, and nothing is validated yet.
        Assert.Equal(("potentially breaking", "unvalidated", "low"), (r.Assessment.Risk, r.Status, r.Confidence));
        Assert.DoesNotContain(r.Assessment.Reasons, x => x.Contains("safe"));
    }

    [Fact]
    public void Central_management_gets_a_Directory_Packages_props_patch_not_a_project_command()
    {
        using var src = new Sources(("Directory.Packages.props", Central), ("src/Api/Api.csproj", Csproj("""<PackageReference Include="Newtonsoft.Json" />""")), ("src/Api/Codec.cs", Codec));
        var change = Assert.Single(Assert.Single(Plan(src, ("Newtonsoft.Json", "12.0.4"))).Changes);
        Assert.Null(change.Command);
        Assert.Equal(("Directory.Packages.props", 4, "patch"), (change.Edit!.File, change.Edit.Line, change.Kind));
        Assert.Contains("""-    <PackageVersion Include="Newtonsoft.Json" Version="12.0.3" />""", change.Edit.Patch);
        Assert.Contains("""+    <PackageVersion Include="Newtonsoft.Json" Version="12.0.4" />""", change.Edit.Patch);
        Assert.StartsWith("--- a/Directory.Packages.props\n+++ b/Directory.Packages.props\n@@ -2,5 +2,5 @@", change.Edit.Patch);
    }

    [Fact]
    public void Version_override_is_patched_in_the_project()
    {
        using var src = new Sources(("Directory.Packages.props", Central),
            ("src/Api/Api.csproj", Csproj("""<PackageReference Include="Newtonsoft.Json" VersionOverride="13.0.1" />""")));
        var change = Assert.Single(Assert.Single(Plan(src, ("Newtonsoft.Json", "13.0.3"))).Changes);
        Assert.Null(change.Command);
        Assert.Equal(("src/Api/Api.csproj", "13.0.1"), (change.Edit!.File, change.Current));
        Assert.Contains("""VersionOverride="13.0.3" """, change.Edit.After);
    }

    [Fact]
    public void No_command_for_a_layout_it_cannot_identify()
    {
        using var src = new Sources(
            ("a/A.csproj", Csproj("""<PackageReference Include="Newtonsoft.Json" Version="$(JsonVersion)" />""")),
            ("b/B.csproj", Csproj("""<PackageReference Include="Newtonsoft.Json" Version="12.*" />""")),
            ("c/C.csproj", Csproj("""<PackageReference Include="Newtonsoft.Json" />""", "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>")));
        var r = Assert.Single(Plan(src, ("Newtonsoft.Json", "13.0.3")));
        Assert.All(r.Changes, c => Assert.True(c.Command is null && c.Edit is null && c.Problem is not null));
        Assert.Contains("MSBuild property", r.Changes[0].Problem);
        Assert.Contains("floating", r.Changes[1].Problem);
        Assert.Contains("no PackageVersion", r.Changes[2].Problem);
        Assert.Equal("not-actionable", r.Status);
    }

    [Fact]
    public void Version_drift_and_configured_targets_without_a_request()
    {
        using var src = new Sources(
            ("a/A.csproj", Csproj("""<PackageReference Include="Newtonsoft.Json" Version="12.0.3" /><PackageReference Include="Serilog" Version="3.0.0" />""")),
            ("b/B.csproj", Csproj("""<PackageReference Include="Newtonsoft.Json" Version="13.0.1" />""")));
        var config = Models.Config(Config.Default.Replace("  targets: {}", "  targets: { Serilog: 3.0.1 }"));
        var list = Plan(src, config: config);
        Assert.Equal(["Newtonsoft.Json", "Serilog"], list.Select(r => r.Package));
        Assert.Equal(("13.0.1", "detected", "a/A.csproj"), (list[0].Target, list[0].ReasonBasis, Assert.Single(list[0].Changes).Project));
        Assert.Equal(("3.0.1", "configured", "patch", "medium"), (list[1].Target, list[1].ReasonBasis, list[1].Kind, list[1].Confidence));
    }

    [Fact]
    public void Configured_validation_steps_replace_the_defaults()
    {
        Assert.Equal(["restore", "build", "test"], Models.Config().Remediation.Validate.Select(s => s.Name));
        var config = Models.Config(Config.Default.Replace("  timeout_minutes: 20", "  timeout_minutes: 5\n  validate:\n    - { name: build, run: make }"));
        Assert.Equal(("build", "make", 5), (Assert.Single(config.Remediation.Validate).Name, config.Remediation.Validate[0].Run, config.Remediation.TimeoutMinutes));
    }

    [Fact]
    public void Validation_runs_in_a_temporary_copy_and_leaves_the_working_tree_alone()
    {
        if (OperatingSystem.IsWindows()) return; // the steps below are sh
        using var src = new Sources(("src/Api/Api.csproj", Csproj("""<PackageReference Include="Newtonsoft.Json" Version="12.0.3" />""")));
        var model = Model(src);
        var r = Assert.Single(Remediations.Plan(src.Root, model, Models.Config(), ("Newtonsoft.Json", "13.0.3")));
        var before = File.ReadAllText(src.Files[0]);

        var config = new RemediationConfig { Validate = [new() { Name = "build", Run = "grep -q 'Version=\"13.0.3\"' {target}" }] };
        var passed = Remediations.Validate(src.Root, model, r, config);
        Assert.True(passed.Pass);
        Assert.Equal(("pass", "grep -q 'Version=\"13.0.3\"' src/Api/Api.csproj"), (passed.Steps[0].Status, passed.Steps[0].Command));
        Assert.Equal(before, File.ReadAllText(src.Files[0]));
        Assert.Equal(("validated", "medium"), ((r with { Validation = passed }).Status, (r with { Validation = passed }).Confidence));

        config.Validate = [new() { Name = "restore", Run = "echo 'error NU1102: Unable to find package' && exit 3" }, new() { Name = "build", Run = "true" }];
        var failed = Remediations.Validate(src.Root, model, r, config);
        Assert.False(failed.Pass);
        Assert.Equal(("fail", 3, "skipped"), (failed.Steps[0].Status, failed.Steps[0].ExitCode, failed.Steps[1].Status));
        Assert.Equal(["error NU1102: Unable to find package"], failed.Steps[0].Evidence);
        Assert.Equal(("potentially-breaking", "low"), ((r with { Validation = failed }).Status, (r with { Validation = failed }).Confidence));
        Assert.Equal(before, File.ReadAllText(src.Files[0]));
    }
}
