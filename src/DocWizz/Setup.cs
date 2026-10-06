using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;

// `docwizz setup`: what the repository shows (stack, projects, tests, deployment, layers by naming convention), a
// docwizz.yaml derived from it, and the stages run in a fixed order. Only evidence narrows the defaults: a layer or test
// glob is kept when files match it; nothing about intent (allowed dependencies, thresholds, profile) is inferred.
static class Setup
{
    // Evidence: files per layer and per test glob are counted against what scanning produced, so they are exactly
    // what `architecture` and the test linker will see.
    public record Detection(
        Dictionary<string, int> Languages,       // language → symbols
        List<string> Frameworks,
        Dictionary<string, int> ProjectFiles,    // kind (.sln, .csproj, package.json, …) → count
        Dictionary<string, (int Files, string Example)> Layers,
        Dictionary<string, int> TestGlobs,       // glob → test source files it matches
        int TestProjects,
        List<string> Deployment)
    {
        public bool Backend => Languages.Keys.Any(l => l is "csharp" or "java");
        public bool Frontend => Languages.Keys.Any(l => l is "vue" or "typescript" or "javascript");
    }

    public record Stage(string Name, Func<string> Run, bool Required = false);
    public record StageResult(string Name, string Status, string Detail)
    {
        public bool Ok => Status == "ok";
    }

    static readonly string[] CodeKinds = ["project", "package", "external", "config"];
    static readonly string[] SourceExts = [".cs", ".vue", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".sql", ".java"];

    // Package name (a trailing `*` matches a prefix) → the framework it shows.
    static readonly (string Package, string Framework)[] FrameworkPackages =
    [
        ("Microsoft.AspNetCore.*", "ASP.NET Core"), ("Microsoft.EntityFrameworkCore*", "Entity Framework Core"),
        ("org.springframework.boot:*", "Spring Boot"), ("vue", "Vue"), ("react", "React"), ("@angular/core", "Angular"),
        ("next", "Next.js"), ("express", "Express"),
    ];

    // `files`: every file the scan could read (relative, `/`-separated, dot-dirs for CI included).
    public static Detection Detect(string root, CodeModel model, IReadOnlyCollection<string> files, Config config)
    {
        var code = model.Nodes.Where(n => !CodeKinds.Contains(n.Kind)).ToList();
        var languages = code.Where(n => n.Language is not null).GroupBy(n => n.Language!)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());

        var projects = model.Nodes.Where(n => n.Kind == "project").ToList();
        var packages = model.Nodes.Where(n => n.Kind == "package").Select(n => n.Name).ToList();
        var frameworks = new List<string>();
        if (projects.Any(p => p.Tags?.Contains("Microsoft.NET.Sdk.Web") == true)) frameworks.Add("ASP.NET Core");
        foreach (var (package, framework) in FrameworkPackages)
            if (!frameworks.Contains(framework) && packages.Any(p => package.EndsWith('*')
                    ? p.StartsWith(package[..^1], StringComparison.Ordinal) : p == package))
                frameworks.Add(framework);

        var projectFiles = files.Select(f => Path.GetFileName(f) switch
            {
                "package.json" or "pom.xml" or "build.gradle" or "build.gradle.kts" => Path.GetFileName(f),
                _ when Path.GetExtension(f) is ".sln" or ".slnx" or ".csproj" => Path.GetExtension(f),
                _ => null,
            })
            .Where(k => k is not null).GroupBy(k => k!).OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());

        // Layers by first match, as Architecture.Check assigns them: a layer no file lands in can be dropped without
        // moving any file to another layer.
        var codeFiles = code.Select(n => n.File).Distinct().Order(StringComparer.Ordinal).ToList();
        var byLayer = codeFiles.GroupBy(f => config.Architecture.Layers
            .FirstOrDefault(kv => kv.Value.Any(p => FileSystemName.MatchesSimpleExpression(p, f))).Key)
            .Where(g => g.Key is not null).ToDictionary(g => g.Key!, g => (g.Count(), g.First()));
        var layers = config.Architecture.Layers.Keys.Where(byLayer.ContainsKey).ToDictionary(l => l, l => byLayer[l]);

        var sources = files.Where(f => SourceExts.Contains(Path.GetExtension(f))).ToList();
        var testGlobs = config.Tests.Select(g => (g, sources.Count(f => FileSystemName.MatchesSimpleExpression(g, f))))
            .Where(x => x.Item2 > 0).ToDictionary(x => x.g, x => x.Item2);

        return new(languages, frameworks, projectFiles, layers, testGlobs,
            projects.Count(p => p.Tags?.Contains("test") == true),
            [.. files.Where(f => Generator.IsDeploymentFile(root, f)).Order(StringComparer.Ordinal)]);
    }

    // The default docwizz.yaml with `architecture.layers`/`allow` and `tests` narrowed to what the repository shows.
    // Without evidence a section keeps its defaults (they match nothing, so they change nothing) and says so.
    public static string Yaml(Detection d, string version)
    {
        var lines = Config.Default.Split('\n').ToList();
        var output = new List<string>
        {
            $"# Written by `docwizz setup` (docwizz {version}) from what the repository shows. Lines marked `inferred:`",
            "# come from file evidence; everything else is a default. Edit freely: setup never overwrites this file",
            "# without --force.",
        };
        var keep = d.Layers.Count > 0 ? d.Layers.Keys.ToHashSet() : null;
        string? block = null;
        foreach (var line in lines)
        {
            if (Regex.Match(line, @"^  (\w+):") is { Success: true } top) block = top.Groups[1].Value;
            else if (!line.StartsWith("    ")) block = null;
            var entry = Regex.Match(line, @"^    (\w+): (\[[^\]]*\])");
            if (entry.Success && block == "layers" && keep is not null)
            {
                var name = entry.Groups[1].Value;
                if (!keep.Contains(name)) continue;
                var (files, example) = d.Layers[name];
                output.Add($"    {name}: {entry.Groups[2].Value}   # inferred: {files} file{(files == 1 ? "" : "s")}, e.g. {example}");
                continue;
            }
            if (entry.Success && block == "allow" && keep is not null)
            {
                var name = entry.Groups[1].Value;
                if (!keep.Contains(name)) continue;
                var targets = entry.Groups[2].Value.Trim('[', ']').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => t == "http" || keep.Contains(t));
                output.Add($"    {name}: [{string.Join(", ", targets)}]");
                continue;
            }
            if (line.StartsWith("  layers:") && keep is null)
            {
                output.Add(line);
                output.Add("    # no source file matches a default layer: set your own globs (README: Architecture rules)");
                continue;
            }
            if (line.StartsWith("tests:") && d.TestGlobs.Count > 0)
            {
                output.Add($"tests: [{string.Join(", ", d.TestGlobs.Keys.Select(g => $"\"{g}\""))}]   # inferred: " +
                    string.Join(", ", d.TestGlobs.Select(t => $"{t.Key} {t.Value}")));
                continue;
            }
            output.Add(line);
        }
        return string.Join("\n", output).TrimEnd() + "\n";
    }

    // Writes docwizz.yaml unless one exists (and `force` is off). Returns what happened, for the report.
    public static string WriteConfig(string dir, string yaml, bool force)
    {
        var file = Path.Combine(dir, "docwizz.yaml");
        var existed = File.Exists(file);
        if (existed && !force) return "kept";
        if (existed && File.ReadAllText(file) == yaml) return "unchanged";
        File.WriteAllText(file, yaml);
        return existed ? "overwritten" : "written";
    }

    // Runs the stages in order. A failing stage is reported and the next one runs, except after a required stage:
    // then the rest are skipped, because they need its result.
    public static List<StageResult> Run(IReadOnlyList<Stage> stages, TextWriter log)
    {
        var results = new List<StageResult>();
        string? blockedBy = null;
        for (var i = 0; i < stages.Count; i++)
        {
            var s = stages[i];
            StageResult r;
            if (blockedBy is not null) r = new(s.Name, "skipped", $"needs {blockedBy}");
            else
                try { r = new(s.Name, "ok", s.Run()); }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    r = new(s.Name, "failed", e.Message);
                    if (s.Required) blockedBy = s.Name;
                }
            results.Add(r);
            log.WriteLine($"  [{i + 1}/{stages.Count}] {r.Name,-13}{r.Status,-8} {r.Detail}");
        }
        return results;
    }

    // FAILED: a stage could not run (a docwizz or configuration problem). SUCCESS_WITH_FINDINGS: everything ran and the
    // repository has something to fix: the check fails, or there are architecture or security findings. Findings never
    // make setup fail, so a first run on an imperfect repository is still a successful setup.
    public enum Status { Success, SuccessWithFindings, Failed }

    public static Status Outcome(IReadOnlyList<StageResult> results, bool? checkPassed, ArchitectureResult? arch) =>
        results.Any(r => !r.Ok) ? Status.Failed
        : checkPassed == false || arch is { Violations.Count: > 0 } or { Cycles.Count: > 0 } ? Status.SuccessWithFindings
        : Status.Success;

    public static string Label(Status s) => s switch
    {
        Status.Success => "SUCCESS",
        Status.SuccessWithFindings => "SUCCESS_WITH_FINDINGS",
        _ => "FAILED",
    };

    // The commands to run next, aligned, with why. After a failure: re-run (or regenerate a kept docwizz.yaml, which
    // may be what failed); otherwise the everyday loop.
    public static List<string> NextActions(string dir, Status outcome, bool configKept)
    {
        List<(string Command, string Why)> next = outcome == Status.Failed
            ? [($"docwizz setup {dir}", "re-run after fixing the error above"),
               .. configKept ? [($"docwizz setup {dir} --force", "or replace docwizz.yaml with a generated one")] : new List<(string, string)>()]
            : [($"docwizz generate {dir}", "refresh documentation"), ($"docwizz check {dir}", "run the quality gate"),
               ($"docwizz architecture {dir}", "inspect architecture findings")];
        var width = next.Max(n => n.Command.Length) + 3;
        return [.. next.Select(n => $"  {n.Command.PadRight(width)}# {n.Why}")];
    }

    // One line: languages by symbol count, frameworks, and whether there is a backend, a frontend or both.
    public static string Stack(Detection d)
    {
        if (d.Languages.Count == 0) return "no supported source files found";
        var sb = new StringBuilder(string.Join(", ", d.Languages.Select(l => $"{l.Key} ({l.Value} symbols)")));
        if (d.Frameworks.Count > 0) sb.Append($"; {string.Join(", ", d.Frameworks)}");
        var parts = new[] { d.Backend ? "backend" : null, d.Frontend ? "frontend" : null }.OfType<string>().ToList();
        if (parts.Count > 0) sb.Append($"; {string.Join(" + ", parts)}");
        return sb.ToString();
    }

    // What the repository didn't show, so it needs a human decision.
    public static List<string> FollowUp(Detection d, string configStatus, Config config, bool checkPassed)
    {
        var todo = new List<string>();
        if (d.Languages.Count == 0) todo.Add("no supported source files found: check the directory, `exclude:` and .gitignore (README: What it reads)");
        if (configStatus == "kept") todo.Add("docwizz.yaml existed and was kept; run `docwizz init` in an empty directory to compare with the current defaults");
        else
        {
            if (d.Layers.Count == 0) todo.Add("no layers detected: set architecture.layers and architecture.allow in docwizz.yaml");
            else todo.Add("review architecture.allow: the allowed dependencies are defaults, not your architecture's intent");
            if (d.TestGlobs.Count == 0 && d.TestProjects == 0) todo.Add("no test code found by the default globs: set `tests:` so changes can be traced to tests");
        }
        // A profile is a choice about what needs docs, so it's suggested, never written: only for a single-stack repo.
        var suggested = d.Frameworks.Contains("ASP.NET Core") && !d.Frontend ? "aspnet" : d.Frameworks.Contains("Vue") && !d.Backend ? "vue" : null;
        if (config.Profile == "default")
            todo.Add(suggested is not null
                ? $"the stack matches the `{suggested}` profile: set `profile: {suggested}` if its rules fit (README: Profiles)"
                : $"pick a documentation profile if `default` doesn't fit ({string.Join(", ", Profiles.Names)})");
        if (!config.Security.Enabled) todo.Add("security rules are off: set security.enabled: true for a security review (local, no network)");
        if (!checkPassed) todo.Add("check fails at the current thresholds: fix the gaps, or adjust `check:` to a baseline you will raise over time");
        return todo;
    }
}
