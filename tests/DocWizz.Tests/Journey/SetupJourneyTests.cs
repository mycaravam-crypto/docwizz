using System.Net;
using System.Net.Sockets;

namespace DocWizz.Tests.Journey;

// `docwizz setup` end to end on small repositories of each kind: what it writes, what it leaves alone, that a re-run
// changes nothing, that findings are not failures, and the safe defaults (no AI, no secrets copied, no repository code run).
// Detection and YAML details are unit-tested in Unit/SetupTests; here only what a user sees on a first run.
public class SetupJourneyTests
{
    static Docwizz.Result Setup(Sources repo, params string[] extra) => Docwizz.Run(["setup", repo.Root, .. extra]);

    static string Yaml(Sources repo) => File.ReadAllText(Path.Combine(repo.Root, "docwizz.yaml"));

    static string Line(Docwizz.Result r, string label) =>
        r.Out.Split('\n').FirstOrDefault(l => l.StartsWith(label + ":")) ?? throw new Xunit.Sdk.XunitException($"no {label}: line\n{r}");

    // What every first run has, whatever the repository: a config, docs, a status, a summary, and what to do next.
    static void FirstRun(Docwizz.Result r, Sources repo)
    {
        Assert.True(r.Exit == 0, r.ToString());
        Docwizz.NoStackTrace(r);
        Assert.Contains("[2/6] config       ok       written", r.Out);
        Assert.True(File.Exists(Path.Combine(repo.Root, "docwizz.yaml")));
        Assert.True(File.Exists(Path.Combine(repo.Root, "docs", "index.md")));
        foreach (var label in new[] { "Status", "Stack", "Layers", "Configuration", "Documentation", "Coverage", "Check" }) Line(r, label);
        Assert.Contains("Follow-up:", r.Out);
        Assert.Contains("Next useful actions:", r.Out);
        Assert.Contains($"docwizz check {repo.Root}", r.Out);
    }

    [Fact]
    public void Stages_run_in_order_and_each_is_reported()
    {
        using var repo = Repos.AspNet();
        var r = Setup(repo);
        FirstRun(r, repo);
        var at = new[] { "scan", "config", "analyze", "architecture", "generate", "check" }
            .Select((s, i) => r.Out.IndexOf($"[{i + 1}/6] {s,-13}ok", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, at);
        Assert.Equal(at.Order(), at);
    }

    [Fact]
    public void AspNet_backend()
    {
        using var repo = Repos.AspNet();
        var r = Setup(repo);
        FirstRun(r, repo);
        Assert.Contains("csharp", Line(r, "Stack"));
        Assert.Contains("ASP.NET Core", Line(r, "Stack"));
        Assert.Contains("backend", Line(r, "Stack"));
        Assert.Equal("Layers:         api (1), application (1), domain (1)", Line(r, "Layers"));
        Assert.Contains("`profile: aspnet`", r.Out);   // suggested, never written
        Assert.Contains("profile: default", Yaml(repo));
    }

    [Fact]
    public void Java_spring_backend()
    {
        using var repo = Repos.Java();
        var r = Setup(repo);
        FirstRun(r, repo);
        Assert.Contains("java", Line(r, "Stack"));
        Assert.Contains("Spring Boot", Line(r, "Stack"));
        Assert.Contains("api (1)", Line(r, "Layers"));
        Assert.Contains("application (1)", Line(r, "Layers"));
        Assert.Contains("src/test/* (1 files)", Line(r, "Tests"));   // Maven layout at the root
    }

    [Fact]
    public void Frontend_only()
    {
        Docwizz.RequireFrontendScanner();
        using var repo = Repos.Frontend();
        var r = Setup(repo);
        FirstRun(r, repo);
        Assert.Contains("Vue", Line(r, "Stack"));
        Assert.EndsWith("; frontend", Line(r, "Stack").TrimEnd());
        Assert.Contains("ui (1)", Line(r, "Layers"));
        Assert.Contains("state (1)", Line(r, "Layers"));
    }

    [Fact]
    public void Mixed_frontend_and_backend()
    {
        Docwizz.RequireFrontendScanner();
        using var repo = Repos.Mixed();
        var r = Setup(repo);
        FirstRun(r, repo);
        Assert.Contains("backend + frontend", Line(r, "Stack"));
        Assert.Contains("ui (1)", Line(r, "Layers"));
        Assert.Contains("api (1)", Line(r, "Layers"));
        Assert.DoesNotContain("`profile: aspnet`", r.Out);   // a profile is only suggested for a single stack
    }

    [Fact]
    public void Small_console_project()
    {
        using var repo = Repos.ConsoleApp();
        var r = Setup(repo);
        FirstRun(r, repo);
        Assert.Contains("csharp", Line(r, "Stack"));
        Assert.Equal("Layers:         none detected", Line(r, "Layers"));
    }

    [Fact]
    public void Partially_supported_repository_reports_what_it_read()
    {
        using var repo = Repos.Partial();
        var r = Setup(repo);
        FirstRun(r, repo);
        Assert.StartsWith("Stack:          csharp (", Line(r, "Stack"));
        Assert.DoesNotContain("python", r.Out);
    }

    [Fact]
    public void Nearly_empty_repository_still_sets_up_and_says_what_is_missing()
    {
        using var repo = Repos.Empty();
        var r = Setup(repo);
        FirstRun(r, repo);
        Assert.Equal("Stack:          no supported source files found", Line(r, "Stack"));
        Assert.Contains("no supported source files found: check the directory", r.Out);
    }

    [Fact]
    public void No_recognizable_layers_keeps_the_defaults_and_asks_for_them()
    {
        using var repo = Repos.NoLayers();
        var r = Setup(repo);
        FirstRun(r, repo);
        Assert.Equal("Layers:         none detected", Line(r, "Layers"));
        Assert.Contains("no layers detected: set architecture.layers", r.Out);
        Assert.Contains("# no source file matches a default layer", Yaml(repo));
    }

    [Fact]
    public void An_existing_config_is_kept_byte_for_byte()
    {
        using var repo = Repos.AspNet();
        const string mine = "# hand-written, keep\nprofile: api\ncheck:\n  min_coverage: 10\n";
        File.WriteAllText(Path.Combine(repo.Root, "docwizz.yaml"), mine);

        var r = Setup(repo);
        Assert.Equal(0, r.Exit);
        Assert.Contains("[2/6] config       ok       kept", r.Out);
        Assert.Contains("(--force to regenerate)", r.Out);
        Assert.Contains("profile api", Line(r, "Coverage"));   // and it is the config the run used
        Assert.Equal(mine, Yaml(repo));
    }

    [Fact]
    public void Force_replaces_the_config_only_when_asked()
    {
        using var repo = Repos.AspNet();
        File.WriteAllText(Path.Combine(repo.Root, "docwizz.yaml"), "profile: api\n");
        var r = Setup(repo, "--force");
        Assert.Equal(0, r.Exit);
        Assert.Contains("[2/6] config       ok       overwritten", r.Out);
        Assert.StartsWith("# Written by `docwizz setup`", Yaml(repo));
    }

    [Fact]
    public void A_second_run_changes_nothing()
    {
        using var repo = Repos.AspNet();
        var first = Setup(repo);
        Assert.Equal(0, first.Exit);
        var yaml = Yaml(repo);
        var docs = Docwizz.Snapshot(Path.Combine(repo.Root, "docs"));

        var second = Setup(repo);
        Assert.Equal(0, second.Exit);
        Assert.Contains("[2/6] config       ok       kept", second.Out);
        Assert.Equal(yaml, Yaml(repo));
        Assert.Equal(docs, Docwizz.Snapshot(Path.Combine(repo.Root, "docs")));

        var forced = Setup(repo, "--force");
        Assert.Contains("[2/6] config       ok       unchanged", forced.Out);   // regenerated: the same file
        Assert.Equal(yaml, Yaml(repo));
        Assert.Equal(docs, Docwizz.Snapshot(Path.Combine(repo.Root, "docs")));
    }

    [Fact]
    public void The_same_repository_gives_the_same_config_and_docs_with_nothing_duplicated()
    {
        // Two copies under the same directory name: the name is part of the repository state (it names the system).
        using var parentA = new Sources();
        using var parentB = new Sources();
        var (a, b) = (Repos.WriteTo(parentA.Root, Repos.MixedFiles), Repos.WriteTo(parentB.Root, Repos.MixedFiles));
        Assert.Equal(0, Docwizz.Run("setup", a).Exit);
        Assert.Equal(0, Docwizz.Run("setup", b).Exit);
        var yaml = File.ReadAllText(Path.Combine(a, "docwizz.yaml"));
        Assert.Equal(yaml, File.ReadAllText(Path.Combine(b, "docwizz.yaml")));
        Assert.Equal(Docwizz.Snapshot(Path.Combine(a, "docs")), Docwizz.Snapshot(Path.Combine(b, "docs")));

        var keys = yaml.Split('\n').Where(l => !l.TrimStart().StartsWith('#') && l.Contains(':'))
            .Select(l => (Indent: l.Length - l.TrimStart().Length, Key: l.Trim().Split(':')[0]));
        // Under each parent, every key once: setup never appends to what it wrote.
        var path = new List<(int Indent, string Key)>();
        var seen = new HashSet<string>();
        foreach (var k in keys)
        {
            path.RemoveAll(p => p.Indent >= k.Indent);
            path.Add(k);
            Assert.True(seen.Add(string.Join(".", path.Select(p => p.Key))), $"duplicate key {string.Join(".", path.Select(p => p.Key))}");
        }
    }

    [Fact]
    public void Findings_are_reported_as_a_successful_setup()
    {
        using var repo = Repos.AspNet();   // undocumented code: coverage is below the default threshold
        var r = Setup(repo);
        Assert.Equal(0, r.Exit);
        Assert.StartsWith("Setup complete, with findings:", r.Out.Split('\n').First(l => l.StartsWith("Setup ")));
        Assert.Equal("Status:         SUCCESS_WITH_FINDINGS", Line(r, "Status"));
        Assert.StartsWith("Check:          FAIL", Line(r, "Check"));
        Assert.DoesNotContain("Error:", r.Out);
        Assert.EndsWith(string.Join("\n", ["Next useful actions:", .. global::Setup.NextActions(repo.Root, global::Setup.Status.SuccessWithFindings, false)]),
            r.Out.TrimEnd());
    }

    [Fact]
    public void A_clean_repository_is_a_plain_success()
    {
        using var repo = Repos.ConsoleApp();   // documented, no layers, nothing to violate
        var r = Setup(repo);
        Assert.True(r.Exit == 0, r.ToString());
        Assert.Equal("Status:         SUCCESS", Line(r, "Status"));
        Assert.Contains("Setup complete.", r.Out);
        Assert.Equal("Check:          PASS", Line(r, "Check").TrimEnd());
    }

    [Fact]
    public void Invalid_configuration_fails_setup_and_skips_what_needs_it()
    {
        using var repo = Repos.AspNet();
        File.WriteAllText(Path.Combine(repo.Root, "docwizz.yaml"), "check: [oops\n");
        var r = Setup(repo);
        Assert.Equal(1, r.Exit);
        Docwizz.NoStackTrace(r);
        Assert.Equal("Status:         FAILED", Line(r, "Status"));
        Assert.Contains("[1/6] scan         failed", r.Out);
        Assert.Contains("[6/6] check        skipped  needs scan", r.Out);
        Assert.StartsWith("Error:          scan: ", Line(r, "Error"));
        Assert.EndsWith(string.Join("\n", ["Next useful actions:", .. global::Setup.NextActions(repo.Root, global::Setup.Status.Failed, configKept: true)]),
            r.Out.TrimEnd());   // re-run, or regenerate the file that broke
        Assert.Equal("check: [oops\n", Yaml(repo));   // a broken file is still the user's file
    }

    [Fact]
    public void A_stage_that_fails_late_keeps_the_earlier_results_visible()
    {
        using var repo = Repos.AspNet();
        File.WriteAllText(Path.Combine(repo.Root, "docs"), "a file where the docs directory goes");
        var r = Setup(repo);
        Assert.Equal(1, r.Exit);
        Docwizz.NoStackTrace(r);
        Assert.Equal("Status:         FAILED", Line(r, "Status"));
        Assert.Contains("[5/6] generate     failed", r.Out);
        Assert.Contains("[6/6] check        ok", r.Out);       // not required by later stages: they still run
        Line(r, "Coverage");                                   // what completed is still summarized
        Line(r, "Architecture");
        Assert.StartsWith("Error:          generate: ", Line(r, "Error"));
        Assert.DoesNotContain("Documentation:", r.Out);         // and what didn't happen isn't claimed
        Assert.DoesNotContain("--force", r.Out);                // the config was fine: nothing to regenerate
    }

    // Output can't depend on the order the filesystem lists files in: the same files, written in opposite orders,
    // give the same summary, config, docs and JSON reports, and a second report run gives the same bytes again.
    [Fact]
    public void Output_does_not_depend_on_file_order_or_on_the_run()
    {
        Docwizz.RequireFrontendScanner();
        using var parentA = new Sources();
        using var parentB = new Sources();
        var a = Repos.WriteTo(parentA.Root, Repos.MixedFiles);
        var b = Repos.WriteTo(parentB.Root, Repos.MixedFiles.Reverse());
        // Run from each parent on `shop`, as a user would from their checkout; only the absolute path in the header
        // (`docwizz setup /tmp/…/shop`) names the parent.
        Docwizz.Result Run(Sources parent, params string[] args)
        {
            var r = Docwizz.Run(parent.Root, null, args);
            return r with { Out = r.Out.Replace(parent.Root, "<parent>") };
        }

        Assert.Equal(Run(parentA, "setup", "shop").Out, Run(parentB, "setup", "shop").Out);
        Assert.Equal(File.ReadAllText(Path.Combine(a, "docwizz.yaml")), File.ReadAllText(Path.Combine(b, "docwizz.yaml")));
        Assert.Equal(Docwizz.Snapshot(Path.Combine(a, "docs")), Docwizz.Snapshot(Path.Combine(b, "docs")));

        foreach (var report in new[] { "analyze", "architecture" })
        {
            var first = Run(parentA, report, "shop", "--format", "json");
            Assert.Equal(first.Out, Run(parentB, report, "shop", "--format", "json").Out);
            Assert.Equal(first.Out, Run(parentA, report, "shop", "--format", "json").Out);
        }
    }

    // AI is opt-in: a listener stands in for Ollama; without --ai nothing connects to it, with --ai something does
    // (so the first half isn't passing just because the test can't see connections).
    [Fact]
    public async Task Ai_is_never_contacted_without_ai()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var connections = 0;
        var accept = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var c = await listener.AcceptTcpClientAsync();
                    Interlocked.Increment(ref connections);   // closed at once: the client fails fast and skips AI
                }
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        });
        var env = new Dictionary<string, string?> { ["OLLAMA_HOST"] = $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}" };

        using var repo = Repos.AspNet();
        var plain = Docwizz.Run(null, env, "setup", repo.Root);
        Assert.Equal(0, plain.Exit);
        Assert.Equal(0, Volatile.Read(ref connections));
        Assert.DoesNotContain("AI", plain.Err);

        var ai = Docwizz.Run(null, env, "setup", repo.Root, "--ai");
        Assert.Equal(0, ai.Exit);
        Assert.True(Volatile.Read(ref connections) > 0, ai.ToString());
        Assert.Contains("AI", ai.Err);   // and the failure to reach it is said, not hidden
        listener.Stop();
        await accept;
    }

    [Fact]
    public void Secret_values_are_never_copied_into_the_config_or_docs()
    {
        const string secret = "SuperSecret123";
        using var repo = new Sources(
            (".env", $"DB_PASSWORD={secret}\nAPI_KEY={secret}\n"),
            ("src/Shop/appsettings.json", $$"""{ "ConnectionStrings": { "Db": "Server=db;Password={{secret}}" }, "Jwt": { "Key": "{{secret}}" } }"""),
            ("src/main/resources/application.properties", $"spring.datasource.password={secret}\n"),
            ("docker-compose.yml", $"services:\n  db:\n    image: postgres\n    environment:\n      POSTGRES_PASSWORD: {secret}\n"),
            ("src/Shop/Shop.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""),
            ("src/Shop/Api/HealthController.cs", "namespace Shop.Api;\npublic class HealthController { public string Get() => \"ok\"; }\n"));
        var r = Setup(repo);
        Assert.Equal(0, r.Exit);
        Assert.DoesNotContain(secret, Yaml(repo));
        Assert.DoesNotContain(secret, r.Out + r.Err);
        Assert.All(Docwizz.Snapshot(Path.Combine(repo.Root, "docs")), f => Assert.False(f.Value.Contains(secret), $"{f.Key} contains the secret"));
    }

    // Build, test, install and container tools are replaced by stubs that log their use, and the repository has hooks
    // that would leave a marker if anything ran them. setup and a plain remediate must touch neither.
    [Fact]
    public void Repository_code_and_package_tools_are_never_run_implicitly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the tool stubs are shell scripts");
        using var bin = new Sources();
        var log = Path.Combine(bin.Root, "calls.log");
        foreach (var tool in new[] { "dotnet", "npm", "npx", "yarn", "pnpm", "mvn", "gradle", "docker", "docker-compose", "make", "pip" })
        {
            var stub = Path.Combine(bin.Root, tool);
            File.WriteAllText(stub, $"#!/bin/sh\necho \"{tool} $*\" >> '{log}'\n");
            File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        using var repo = Repos.AspNet();
        File.WriteAllText(Path.Combine(repo.Root, "src/Shop/Shop.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Newtonsoft.Json" Version="12.0.1" /></ItemGroup>
              <Target Name="Mark" BeforeTargets="Restore;Build"><Touch Files="$(MSBuildThisFileDirectory)built.marker" AlwaysCreate="true" /></Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(repo.Root, "package.json"),
            """{ "name": "hooks", "scripts": { "preinstall": "touch installed.marker", "test": "touch tested.marker" } }""");
        foreach (var wrapper in new[] { "mvnw", "gradlew" })
        {
            var w = Path.Combine(repo.Root, wrapper);
            File.WriteAllText(w, $"#!/bin/sh\ntouch '{repo.Root}/{wrapper}.marker'\n");
            File.SetUnixFileMode(w, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        File.WriteAllText(Path.Combine(repo.Root, "docwizz.yaml"), "remediation:\n  targets: { Newtonsoft.Json: 13.0.3 }\n");
        var env = new Dictionary<string, string?> { ["PATH"] = bin.Root + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") };

        var setup = Docwizz.Run(null, env, "setup", repo.Root);
        Assert.True(setup.Exit == 0, setup.ToString());
        var remediate = Docwizz.Run(null, env, "remediate", repo.Root);
        Assert.True(remediate.Exit == 0, remediate.ToString());
        Assert.Contains("Newtonsoft.Json", remediate.Out);   // it did propose something, without running anything

        Assert.False(File.Exists(log), File.Exists(log) ? File.ReadAllText(log) : "");
        Assert.Empty(Directory.EnumerateFiles(repo.Root, "*.marker", SearchOption.AllDirectories));
    }
}
