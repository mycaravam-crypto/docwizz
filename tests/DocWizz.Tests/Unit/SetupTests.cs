using static DocWizz.Tests.Models;

namespace DocWizz.Tests.Unit;

// `docwizz setup`: what it detects, the docwizz.yaml it derives from that, how it treats an existing file, and stages.
public class SetupTests
{
    static Node Code(string id, string file, string language) => N(id, file) with { Language = language };

    // A layered C# backend with a test project, a Vue store, a Dockerfile; nothing that matches `ui` or `infrastructure`.
    static (Sources Src, CodeModel Model, List<string> Files) Repo()
    {
        var src = new Sources(("Dockerfile", "FROM scratch"), ("deploy/app.yaml", "apiVersion: v1\nkind: Service\n"),
            ("notes.yaml", "title: not kubernetes\n"));
        var model = Model([
            Code("cs:Shop.Api.OrdersController", "src/Api/OrdersController.cs", "csharp"),
            Code("cs:Shop.Api.CartController", "src/Api/CartController.cs", "csharp"),
            Code("cs:Shop.Domain.Order", "src/Domain/Order.cs", "csharp"),
            Code("ts:web/src/stores/cart.ts", "web/src/stores/cart.ts", "typescript"),
            Code("cs:Shop.Program", "src/Program.cs", "csharp"),
            N("proj:src/Shop.csproj", "src/Shop.csproj", kind: "project", tags: ["dotnet", "Microsoft.NET.Sdk.Web", "executable"]),
            N("proj:tests/Shop.Tests/Shop.Tests.csproj", "tests/Shop.Tests/Shop.Tests.csproj", kind: "project", tags: ["dotnet", "test"]),
            N("pkg:npm:vue", "web/package.json", kind: "package") with { Name = "vue" },
        ]);
        List<string> files = ["Dockerfile", "deploy/app.yaml", "notes.yaml", "src/Api/OrdersController.cs", "src/Api/CartController.cs",
            "src/Domain/Order.cs", "src/Program.cs", "src/Shop.csproj", "tests/Shop.Tests/OrderTests.cs",
            "tests/Shop.Tests/Shop.Tests.csproj", "web/package.json", "web/src/stores/cart.ts"];
        return (src, model, files);
    }

    [Fact]
    public void Detects_stack_projects_tests_deployment_and_layers_from_evidence()
    {
        var (src, model, files) = Repo();
        using var _ = src;
        var d = Setup.Detect(src.Root, model, files, Config());

        Assert.Equal(new Dictionary<string, int> { ["csharp"] = 4, ["typescript"] = 1 }, d.Languages);
        Assert.Equal(["ASP.NET Core", "Vue"], d.Frameworks);
        Assert.True(d.Backend && d.Frontend);
        Assert.Equal(new Dictionary<string, int> { [".csproj"] = 2, ["package.json"] = 1 }, d.ProjectFiles);
        Assert.Equal(["state", "api", "domain"], d.Layers.Keys);   // config order; Program.cs matches no layer
        Assert.Equal((2, "src/Api/CartController.cs"), d.Layers["api"]);
        Assert.Equal(new Dictionary<string, int> { ["tests/*"] = 1, ["*.Tests/*"] = 1 }, d.TestGlobs);   // the unmatched default globs are dropped
        Assert.Equal(1, d.TestProjects);
        Assert.Equal(["Dockerfile", "deploy/app.yaml"], d.Deployment);   // a YAML file without apiVersion/kind is not a manifest
    }

    [Fact]
    public void Generated_yaml_keeps_only_detected_layers_and_test_globs_and_loads()
    {
        var (src, model, files) = Repo();
        using var _ = src;
        var yaml = Setup.Yaml(Setup.Detect(src.Root, model, files, Config()), "1.2.3");
        var config = Config(yaml);

        Assert.Equal(["state", "api", "domain"], config.Architecture.Layers.Keys);
        Assert.Equal(["*/Api/*.cs", "*.Api/*.cs", "*/Controllers/*.cs", "*/Endpoints/*.cs", "*/controller/*.java", "*/web/*.java"],
            config.Architecture.Layers["api"]);   // the globs themselves stay as defaults: evidence picks layers, not patterns
        Assert.Equal(["domain"], config.Architecture.Allow["api"]);   // application/infrastructure aren't there to allow
        Assert.Equal(["state", "api", "domain"], config.Architecture.Allow.Keys);
        Assert.Equal(["tests/*", "*.Tests/*"], config.Tests);
        Assert.Contains("# inferred: 2 files, e.g. src/Api/CartController.cs", yaml);
        Assert.Contains("docwizz 1.2.3", yaml);
        // Intent is never inferred: thresholds, profile and security stay at their defaults.
        var defaults = Config();
        Assert.Equal(defaults.Profile, config.Profile);
        Assert.Equal(defaults.Check.MinCoverage, config.Check.MinCoverage);
        Assert.False(config.Security.Enabled);
    }

    [Fact]
    public void Generated_yaml_is_deterministic()
    {
        var (src, model, files) = Repo();
        using var _ = src;
        var reversed = Model(model.Nodes.AsEnumerable().Reverse());
        Assert.Equal(Setup.Yaml(Setup.Detect(src.Root, model, files, Config()), "1"),
            Setup.Yaml(Setup.Detect(src.Root, reversed, [.. files.AsEnumerable().Reverse()], Config()), "1"));
    }

    [Fact]
    public void Without_evidence_the_defaults_stay_and_say_so()
    {
        using var src = new Sources();
        var d = Setup.Detect(src.Root, Model([Code("cs:App.Program", "Program.cs", "csharp")]), ["Program.cs"], Config());
        var yaml = Setup.Yaml(d, "1");
        var config = Config(yaml);

        Assert.Empty(d.Layers);
        Assert.Equal(Config().Architecture.Layers.Keys, config.Architecture.Layers.Keys);
        Assert.Equal(Config().Tests, config.Tests);
        Assert.Contains("no source file matches a default layer", yaml);
        Assert.Contains(Setup.FollowUp(d, "written", config, checkPassed: true), t => t.StartsWith("no layers detected"));
        Assert.Contains(Setup.FollowUp(d, "written", config, checkPassed: true), t => t.StartsWith("no test code found"));
    }

    [Fact]
    public void Profile_is_suggested_for_a_single_stack_never_written()
    {
        using var src = new Sources();
        var backend = Model([Code("cs:Api.C", "Api/C.cs", "csharp"),
            N("proj:Api.csproj", "Api.csproj", kind: "project", tags: ["dotnet", "Microsoft.NET.Sdk.Web"])]);
        var d = Setup.Detect(src.Root, backend, ["Api/C.cs", "Api.csproj"], Config());

        Assert.Contains("profile: default", Setup.Yaml(d, "1"));
        Assert.Contains(Setup.FollowUp(d, "written", Config(), checkPassed: true), t => t.Contains("`profile: aspnet`"));
    }

    [Fact]
    public void Config_is_written_once_and_never_overwritten_without_force()
    {
        using var src = new Sources();
        var file = Path.Combine(src.Root, "docwizz.yaml");

        Assert.Equal("written", Setup.WriteConfig(src.Root, "profile: default\n", force: false));
        Assert.Equal("kept", Setup.WriteConfig(src.Root, "profile: api\n", force: false));
        Assert.Equal("profile: default\n", File.ReadAllText(file));

        File.WriteAllText(file, "# mine\nprofile: software\n");
        Assert.Equal("kept", Setup.WriteConfig(src.Root, "profile: default\n", force: false));
        Assert.Equal("# mine\nprofile: software\n", File.ReadAllText(file));

        Assert.Equal("overwritten", Setup.WriteConfig(src.Root, "profile: default\n", force: true));
        Assert.Equal("unchanged", Setup.WriteConfig(src.Root, "profile: default\n", force: true));
        Assert.Equal("profile: default\n", File.ReadAllText(file));
    }

    [Fact]
    public void A_failing_stage_is_reported_and_later_stages_still_run()
    {
        var ran = new List<string>();
        var log = new StringWriter();
        var results = Setup.Run([
            new("scan", () => { ran.Add("scan"); return "ok"; }, Required: true),
            new("analyze", () => throw new InvalidOperationException("boom")),
            new("check", () => { ran.Add("check"); return "PASS"; }),
        ], log);

        Assert.Equal(["scan", "check"], ran);
        Assert.Equal(["ok", "failed", "ok"], results.Select(r => r.Status));
        Assert.Equal("boom", results[1].Detail);
        Assert.Contains("[2/3] analyze", log.ToString());
    }

    [Fact]
    public void A_failing_required_stage_skips_the_stages_that_need_it()
    {
        var results = Setup.Run([
            new("scan", () => throw new ArgumentException("bad yaml"), Required: true),
            new("analyze", () => "never"),
        ], TextWriter.Null);

        Assert.Equal(["failed", "skipped"], results.Select(r => r.Status));
        Assert.Equal("needs scan", results[1].Detail);
    }

    [Fact]
    public void Findings_are_a_successful_setup_and_only_a_stage_that_could_not_run_fails_it()
    {
        List<Setup.StageResult> ok = [new("scan", "ok", ""), new("check", "ok", "")];
        var clean = new ArchitectureResult([], [], [], []);
        var cycle = clean with { Cycles = [["a", "b"]] };

        Assert.Equal(Setup.Status.Success, Setup.Outcome(ok, checkPassed: true, clean));
        Assert.Equal(Setup.Status.SuccessWithFindings, Setup.Outcome(ok, checkPassed: false, clean));
        Assert.Equal(Setup.Status.SuccessWithFindings, Setup.Outcome(ok, checkPassed: true, cycle));
        Assert.Equal(Setup.Status.Failed, Setup.Outcome([.. ok, new("generate", "failed", "disk full")], checkPassed: true, clean));
        Assert.Equal(Setup.Status.Failed, Setup.Outcome([new("scan", "failed", "bad yaml"), new("check", "skipped", "needs scan")], null, null));
        Assert.Equal(["SUCCESS", "SUCCESS_WITH_FINDINGS", "FAILED"], Enum.GetValues<Setup.Status>().Select(Setup.Label));
    }

    [Fact]
    public void Next_actions_follow_the_outcome_and_line_up()
    {
        Assert.Equal([
            "  docwizz generate .       # refresh documentation",
            "  docwizz check .          # run the quality gate",
            "  docwizz architecture .   # inspect architecture findings",
        ], Setup.NextActions(".", Setup.Status.SuccessWithFindings, configKept: false));
        Assert.Equal(Setup.NextActions("app", Setup.Status.SuccessWithFindings, configKept: true),
            Setup.NextActions("app", Setup.Status.Success, configKept: true));

        // After a failure: re-run, and with a kept docwizz.yaml (which may be what failed) the way to regenerate it.
        Assert.Equal(["  docwizz setup app   # re-run after fixing the error above"],
            Setup.NextActions("app", Setup.Status.Failed, configKept: false));
        Assert.Equal([
            "  docwizz setup app           # re-run after fixing the error above",
            "  docwizz setup app --force   # or replace docwizz.yaml with a generated one",
        ], Setup.NextActions("app", Setup.Status.Failed, configKept: true));
    }

    [Fact]
    public void Follow_up_is_in_a_fixed_order_and_only_asks_what_the_repository_did_not_show()
    {
        var empty = new Setup.Detection([], [], [], [], [], 0, []);
        Assert.Equal([
            "no supported source files found: check the directory, `exclude:` and .gitignore (README: What it reads)",
            "no layers detected: set architecture.layers and architecture.allow in docwizz.yaml",
            "no test code found by the default globs: set `tests:` so changes can be traced to tests",
            $"pick a documentation profile if `default` doesn't fit ({string.Join(", ", Profiles.Names)})",
            "security rules are off: set security.enabled: true for a security review (local, no network)",
            "check fails at the current thresholds: fix the gaps, or adjust `check:` to a baseline you will raise over time",
        ], Setup.FollowUp(empty, "written", Config(), checkPassed: false));

        // A kept file is the user's: no advice to edit what setup didn't write, only how to compare with the defaults.
        var kept = Setup.FollowUp(empty, "kept", Config(), checkPassed: true);
        Assert.StartsWith("docwizz.yaml existed and was kept", kept[1]);
        Assert.DoesNotContain(kept, t => t.StartsWith("no layers detected") || t.StartsWith("check fails"));
    }
}
