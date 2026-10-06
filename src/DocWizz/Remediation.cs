using System.Diagnostics;
using System.Text.RegularExpressions;

// Remediation suggestions for package findings: what to change (an exact `dotnet add package` command, or a minimal
// patch to Directory.Packages.props when versions are managed centrally), what the change touches (static impact), and,
// only when asked for with --validate, what happened when the change was applied to a temporary copy and built.
//
// Provenance stays apart: versions, declaring lines and commands are *detected* from project files; code that uses a
// package and the flows and tests around it are *inferred* (from namespaces, since DocWizz doesn't read package
// contents); restore/build/test results are *validated*. Nothing is ever called safe: passing checks are evidence, not
// proof of behavioral compatibility, and a major update is never judged by its version number alone.
//
// Static mode runs nothing. Validation runs the repository's build (and so its code) in a temporary copy, never in the
// developer's working tree. Only NuGet gets commands so far; other ecosystems are reported without one.
class RemediationConfig
{
    // Package → version to propose (for instance the fixed version of a security advisory).
    public Dictionary<string, string> Targets { get; set; } = [];
    // Commands --validate runs in the temporary copy, in order, stopping at the first failure. {target}: the solution if
    // the directory has exactly one, else each affected project; {tests}: the solution, else each affected test project.
    public List<ValidationStep> Validate { get; set; } =
    [
        new() { Name = "restore", Run = "dotnet restore {target}" },
        new() { Name = "build", Run = "dotnet build {target} --no-restore" },
        new() { Name = "test", Run = "dotnet test {tests} --no-build" },
    ];
    public int TimeoutMinutes { get; set; } = 20;
}

class ValidationStep
{
    public string Name { get; set; } = "";
    public string Run { get; set; } = "";
}

// Literal package versions (1.2, 13.0.3, 2.0.0-rc.1): parsed, compared, and the update between two classified.
static class Versions
{
    public record Version(int[] Parts, string? Pre);

    // Null for anything but a literal version: ranges, floating versions, MSBuild properties.
    public static Version? Parse(string? s)
    {
        if (s is null || Regex.Match(s.Trim(), @"^(\d+(?:\.\d+){0,3})(?:-([0-9A-Za-z.\-]+))?(?:\+[0-9A-Za-z.\-]+)?$") is not { Success: true } m) return null;
        var parts = m.Groups[1].Value.Split('.').Select(int.Parse).ToList();
        while (parts.Count < 3) parts.Add(0);
        return new([.. parts], m.Groups[2].Success ? m.Groups[2].Value : null);
    }

    // Numeric parts first, then prerelease labels part by part (numbers numerically), a release after its prereleases.
    public static int Compare(Version a, Version b)
    {
        for (var i = 0; i < Math.Max(a.Parts.Length, b.Parts.Length); i++)
            if (a.Parts.ElementAtOrDefault(i).CompareTo(b.Parts.ElementAtOrDefault(i)) is var c and not 0) return c;
        if (a.Pre is null || b.Pre is null) return (a.Pre is null).CompareTo(b.Pre is null); // a release sorts after its prereleases
        var (x, y) = (a.Pre.Split('.'), b.Pre.Split('.'));
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var c = int.TryParse(x[i], out var p) && int.TryParse(y[i], out var q) ? p.CompareTo(q) : string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }

    // major, minor, patch, downgrade, same, or unknown when either side isn't a literal version (a range, `1.*`).
    public static string Kind(string? from, string? to)
    {
        if (Parse(from) is not { } a || Parse(to) is not { } b) return "unknown";
        var c = Compare(a, b);
        return c > 0 ? "downgrade" : c == 0 ? "same" : a.Parts[0] != b.Parts[0] ? "major" : a.Parts[1] != b.Parts[1] ? "minor" : "patch";
    }

    public static int Rank(string kind) => kind switch { "patch" => 1, "minor" => 2, "major" => 3, "downgrade" => 4, "unknown" => 5, _ => 0 };
}

// One project's reference and what to change in it. Command and Edit are null when DocWizz can't say confidently (Problem).
record PackageChange(string Project, string? Current, string Target, string Kind, string? Command, FileEdit? Edit, string? Problem = null);

// A one-line change; Patch is the same change as a unified diff, to apply with `git apply` (or `patch -p1`) from the scanned directory.
record FileEdit(string File, int Line, string Before, string After, string Patch);

// A code symbol that uses the package: Namespace is the evidence (an import of one of the package's namespaces).
record PackageUse(string Id, string Location, string Namespace, List<string> Tests);

// Static impact. Projects, Dependents and TestProjects are detected (project files); Uses, and the Flows and Tests
// around them, are inferred from namespaces.
record PackageImpact(List<string> Projects, List<string> Dependents, List<PackageUse> Uses, List<string> Flows, List<string> Tests,
    List<string> TestProjects, string Note)
{
    public int Public { get; init; }
}

record StaticAssessment(string Risk, List<string> Reasons);

record StepResult(string Name, string Command, string Status, int? ExitCode, double Seconds, List<string>? Evidence = null, string? Tests = null);

record ValidationResult(bool Pass, List<StepResult> Steps, string Note);

record Remediation(string Id, string Ecosystem, string Package, string Target, string Reason, string ReasonBasis,
    List<PackageChange> Changes, string Kind, PackageImpact Impact, StaticAssessment Assessment)
{
    public ValidationResult? Validation { get; init; }
    // `remediate --since`: whether the code the package touches lies inside the change or beyond it.
    public string? Scope { get; init; }

    public bool Actionable => Changes.Any(c => c.Edit is not null);

    // unvalidated, validated (all checks passed: evidence, not proof), potentially-breaking (a check failed), not-actionable.
    public string Status => !Actionable ? "not-actionable" : Validation is null ? "unvalidated" : Validation.Pass ? "validated" : "potentially-breaking";

    // How much evidence supports compatibility. High needs executed tests and a linked test for every symbol using the package.
    public string Confidence => Status switch
    {
        "validated" when Validation!.Steps.Any(s => s.Name == "test" && s.Status == "pass") && Impact.Uses.All(u => u.Tests.Count > 0) => "high",
        "validated" => "medium",
        "unvalidated" when Kind == "patch" => "medium",
        _ => "low",
    };

    public object Provenance => new
    {
        versions = "detected", command = "detected", impact = "inferred",
        validation = Validation is null ? "not run" : "validated",
    };
}

static class Remediations
{
    public const string ValidationNote = "passing restore, build and tests is evidence, not proof of behavioral compatibility";
    public const string ImpactNote = "code using a package is inferred from imported namespaces; APIs removed or changed between versions are not assessed (DocWizz doesn't read package contents)";

    // Suggestions for explicit (package, version) requests, else for remediation.targets and for NuGet version drift
    // (one package at several versions across projects: align on the highest).
    public static List<Remediation> Plan(string root, CodeModel model, Config config, (string Package, string Version)? request = null)
    {
        var packages = model.Nodes.Where(n => n.Kind == "package").ToList();
        var refs = model.Edges.Where(e => e.Kind == "depends-on").ToLookup(e => e.To);
        var nuget = model.Nodes.Where(n => n.Kind == "project" && n.File.EndsWith(".csproj")).SelectMany(p =>
        {
            try { return Projects.NuGet(root, Path.Combine(root, p.File)); }
            catch (Exception e) when (e is System.Xml.XmlException or IOException) { return []; }
        }).ToLookup(r => r.Package, StringComparer.OrdinalIgnoreCase);

        var wanted = new List<(Node Package, string Target, string Reason, string Basis)>();
        Node? Find(string name) => packages.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (request is { } r)
        {
            if (Find(r.Package) is not { } p) throw new ArgumentException($"no project references package {r.Package}");
            wanted.Add((p, r.Version, "requested", "requested"));
        }
        else
        {
            foreach (var (name, version) in config.Remediation.Targets)
                if (Find(name) is { } p) wanted.Add((p, version, "configured target (remediation.targets)", "configured"));
            foreach (var p in packages.Where(p => p.Tags?.Contains("nuget") == true && wanted.All(w => w.Package.Id != p.Id)))
            {
                var versions = nuget[p.Name].Select(x => x.Version).Where(v => Versions.Parse(v) is not null).Distinct().ToList();
                if (versions.Count < 2) continue;
                var highest = versions.MaxBy(v => Versions.Parse(v)!, Comparer<Versions.Version>.Create(Versions.Compare))!;
                wanted.Add((p, highest, $"version drift: {string.Join(", ", versions.OrderBy(v => Versions.Parse(v)!, Comparer<Versions.Version>.Create(Versions.Compare)))} " +
                    $"across {nuget[p.Name].Select(x => x.Project).Distinct().Count()} projects", "detected"));
            }
        }

        var result = new List<Remediation>();
        foreach (var (pkg, target, reason, basis) in wanted.OrderBy(w => w.Package.Name, StringComparer.OrdinalIgnoreCase))
        {
            var ecosystem = pkg.Tags?.FirstOrDefault() ?? "unknown";
            var changes = ecosystem == "nuget"
                ? NuGetChanges(root, nuget[pkg.Name], pkg.Name, target)
                : refs[pkg.Id].Where(e => e.Label != target).Select(e => new PackageChange(e.From[5..], e.Label, target, Versions.Kind(e.Label, target), null, null,
                    $"{ecosystem}: no command generated; only NuGet updates are supported so far")).ToList();
            if (changes.Count == 0) continue; // already at the target everywhere
            var kind = changes.Select(c => c.Kind).MaxBy(Versions.Rank)!;
            var impact = Impact(model, config, pkg, changes.Select(c => $"proj:{c.Project}"));
            result.Add(new($"DEP-{result.Count + 1:000}", ecosystem, pkg.Name, target, reason, basis, changes, kind, impact, Assess(kind, target, changes, impact)));
        }
        return result;
    }

    // One change per reference not yet at the target: the command (project-local versions) or the patch, or why there's neither.
    static List<PackageChange> NuGetChanges(string root, IEnumerable<Projects.NuGetReference> refs, string package, string target)
    {
        var changes = new List<PackageChange>();
        foreach (var r in refs.OrderBy(r => r.Project, StringComparer.Ordinal))
        {
            var kind = Versions.Kind(r.Version, target);
            if (kind == "same") continue;
            string? problem = r.Problem ?? (Versions.Parse(r.Version) is null ? $"floating version or range ({r.Version}): not rewritten" : null);
            var edit = problem is null ? Edit(root, r, target) : null;
            if (problem is null && edit is null) problem = $"couldn't find the version {r.Version} on or after {r.DeclaredIn}:{r.Line}";
            // Central management: `dotnet add package` would write the wrong file (or fail); the patch to the declaring file is the suggestion.
            var command = edit is not null && !r.Central ? $"dotnet add {Quote(r.Project)} package {package} --version {target}" : null;
            changes.Add(new(r.Project, r.Version, target, kind, command, edit, problem));
        }
        return changes;
    }

    static string Quote(string s) => Regex.IsMatch(s, @"^[\w./\-]+$") ? s : $"\"{s}\"";

    // The declaring line with the version replaced: Version (or VersionOverride, which a centrally managed project uses
    // to set its own) as an attribute or a child element, searched from the element's line onwards.
    static FileEdit? Edit(string root, Projects.NuGetReference r, string target)
    {
        var lines = File.ReadAllLines(Path.Combine(root, r.DeclaredIn));
        var attribute = r.Central && r.DeclaredIn == r.Project ? "VersionOverride" : "Version";
        var v = Regex.Escape(r.Version!);
        for (var i = Math.Max(0, r.Line - 1); i < Math.Min(lines.Length, r.Line + 9); i++)
            if (Regex.Match(lines[i], $@"(\b{attribute}\s*=\s*[""']){v}([""'])|(<{attribute}>\s*){v}(\s*</{attribute}>)") is { Success: true } m)
            {
                var after = lines[i][..m.Index] + Regex.Replace(m.Value, v, target.Replace("$", "$$")) + lines[i][(m.Index + m.Length)..];
                return new(r.DeclaredIn, i + 1, lines[i], after, Patch(r.DeclaredIn, lines, i, after));
            }
        return null;
    }

    // Unified diff of one changed line with up to two lines of context.
    static string Patch(string file, string[] lines, int i, string after)
    {
        var from = Math.Max(0, i - 2);
        var to = Math.Min(lines.Length - 1, i + 2);
        var count = to - from + 1;
        var body = Enumerable.Range(from, count).SelectMany(j => j == i ? new[] { "-" + lines[j], "+" + after } : [" " + lines[j]]);
        return string.Join("\n", new[] { $"--- a/{file}", $"+++ b/{file}", $"@@ -{from + 1},{count} +{from + 1},{count} @@" }.Concat(body)) + "\n";
    }

    // Namespaces a package's code likely lives in: its id (Newtonsoft.Json) and, for a provider package, its parent
    // (Microsoft.EntityFrameworkCore.SqlServer → Microsoft.EntityFrameworkCore). Maven: the groupId.
    static bool InPackage(string ns, Node package)
    {
        var id = package.Tags?.Contains("maven") == true ? package.Name.Split(':')[0] : package.Name;
        if (ns.Equals(id, StringComparison.OrdinalIgnoreCase) || ns.StartsWith(id + ".", StringComparison.OrdinalIgnoreCase)) return true;
        var parent = id.Contains('.') ? id[..id.LastIndexOf('.')] : "";
        return package.Tags?.Contains("nuget") == true && parent.Contains('.') && ns.Equals(parent, StringComparison.OrdinalIgnoreCase);
    }

    // What updating the package in these projects touches: the projects, the code importing it, its flows and tests.
    public static PackageImpact Impact(CodeModel model, Config config, Node package, IEnumerable<string> projectIds)
    {
        var nodes = model.Nodes.ToDictionary(n => n.Id);
        var direct = projectIds.Where(nodes.ContainsKey).Distinct().Order(StringComparer.Ordinal).ToList();
        // Projects referencing those, transitively: packages flow to them through ProjectReference.
        var referencedBy = model.Edges.Where(e => e.Kind == "references").ToLookup(e => e.To, e => e.From);
        var reach = new HashSet<string>(direct);
        for (var queue = new Queue<string>(direct); queue.TryDequeue(out var p);)
            foreach (var up in referencedBy[p].Where(nodes.ContainsKey)) if (reach.Add(up)) queue.Enqueue(up);
        var projects = model.Nodes.Where(n => n.Kind == "project").ToList();

        var links = new TestLinks(model);
        var uses = model.Edges.Where(e => e.Kind == "uses-namespace" && InPackage(e.To[3..], package) && nodes.ContainsKey(e.From)
                && Projects.Of(projects, nodes[e.From].File) is { } p && reach.Contains(p.Id))
            .GroupBy(e => e.From).Select(g => new PackageUse(g.Key, CodeModel.Location(nodes[g.Key]), g.First().To[3..],
                [.. links.Of(g.Key).Select(l => l.Test)]))
            .OrderBy(u => u.Location, StringComparer.Ordinal).ToList();
        var flows = Generator.FlowsThrough(model, config, uses.Select(u => u.Id))
            .Select(n => n.Kind == "route" ? $"route {n.Route ?? n.Name}" : Generator.EndpointLabel(n)).Distinct().ToList();
        var testProjects = reach.Where(id => nodes[id].Tags?.Contains("test") == true).Select(id => nodes[id].File).Order(StringComparer.Ordinal).ToList();
        var tests = uses.SelectMany(u => u.Tests).Select(TestLinks.Name).Distinct().Order(StringComparer.Ordinal).ToList();
        return new([.. direct.Select(id => nodes[id].File)], [.. reach.Except(direct).Select(id => nodes[id].File).Order(StringComparer.Ordinal)],
            uses, flows, tests, testProjects, ImpactNote) { Public = uses.Count(u => nodes[u.Id].Visibility == "public") };
    }

    // Risk from the kind of update and what the code shows; never "safe", whatever the version numbers say.
    static StaticAssessment Assess(string kind, string target, List<PackageChange> changes, PackageImpact impact)
    {
        var reasons = new List<string>();
        var risk = kind switch
        {
            "patch" => "low by version number",
            "minor" => "moderate by version number",
            "major" => "potentially breaking",
            "downgrade" => "potentially breaking",
            _ => "unknown",
        };
        reasons.Add(kind switch
        {
            "patch" => "a patch update should be compatible if the package follows semantic versioning; not verified",
            "minor" => "a minor update may add behavior and deprecations; not verified",
            "major" => "a major version may remove or change APIs; not judged by version number alone",
            "downgrade" => "a downgrade can remove APIs the code uses",
            _ => "a version isn't a literal DocWizz can compare",
        });
        if (Versions.Parse(target) is { } t && t.Parts[0] == 0 && kind is "minor" or "patch")
        {
            risk = "potentially breaking";
            reasons.Add("0.x versions carry no compatibility promise");
        }
        if (Versions.Parse(target)?.Pre is not null) reasons.Add($"{target} is a prerelease");
        reasons.Add(impact.Uses.Count == 0 ? "no code imports the package's namespaces (it may still be used through another package or by reflection)"
            : $"{impact.Uses.Count} symbols import its namespaces, {impact.Uses.Count(u => u.Tests.Count > 0)} of them with linked tests");
        if (changes.Any(c => c.Problem is not null)) reasons.Add($"{changes.Count(c => c.Problem is not null)} references can't be changed with confidence");
        return new(risk, reasons);
    }

    // `remediate --since <ref>`: does the code the package touches lie inside what the change already touches?
    // `changed`: the symbols the change added or changed.
    public static string Scope(PackageImpact impact, IEnumerable<Node> changed, CodeModel model, Config config)
    {
        var parent = model.Edges.Where(e => e.Kind == "contains").GroupBy(e => e.To).ToDictionary(g => g.Key, g => g.First().From);
        string Top(string id) => parent.TryGetValue(id, out var p) ? Top(p) : id;
        var touched = changed.Select(n => n.Id).ToList();
        var types = touched.Select(Top).ToHashSet();
        var flows = Generator.FlowsThrough(model, config, touched).Select(n => n.Kind == "route" ? $"route {n.Route ?? n.Name}" : Generator.EndpointLabel(n)).ToHashSet();
        var outside = impact.Uses.Count(u => !types.Contains(Top(u.Id)));
        var outsideFlows = impact.Flows.Count(f => !flows.Contains(f));
        if (impact.Uses.Count + impact.Flows.Count == 0) return "no code DocWizz can see imports the package, inside the change or outside it";
        return outside + outsideFlows == 0 ? "within the change: every symbol and flow it touches is already changed"
            : $"broader than the change: {outside} of {impact.Uses.Count} symbols and {outsideFlows} of {impact.Flows.Count} flows it touches are outside it";
    }

    // Applies the changes to a temporary copy of the directory and runs the configured checks there. The working tree
    // is only read.
    public static ValidationResult Validate(string root, CodeModel model, Remediation r, RemediationConfig config, TextWriter? log = null)
    {
        var tmp = Directory.CreateTempSubdirectory("docwizz-validate-");
        try
        {
            var work = Path.Combine(tmp.FullName, "tree");
            Copy(root, work);
            foreach (var edit in r.Changes.Select(c => c.Edit).OfType<FileEdit>().DistinctBy(e => (e.File, e.Line)))
            {
                var file = Path.Combine(work, edit.File);
                var text = File.ReadAllText(file);
                var lines = text.Split('\n');
                if (lines.ElementAtOrDefault(edit.Line - 1)?.TrimEnd('\r') != edit.Before)
                    return new(false, [new("apply", $"edit {edit.File}:{edit.Line}", "fail", null, 0, [$"{edit.File}:{edit.Line} no longer reads: {edit.Before.Trim()}"])], ValidationNote);
                lines[edit.Line - 1] = edit.After + (lines[edit.Line - 1].EndsWith('\r') ? "\r" : "");
                File.WriteAllText(file, string.Join('\n', lines));
            }

            var nodes = model.Nodes.ToDictionary(n => n.Id);
            var solutions = Directory.EnumerateFiles(work, "*.sln*", SearchOption.AllDirectories).Where(f => f.EndsWith(".sln") || f.EndsWith(".slnx")).ToList();
            var affected = r.Impact.Projects.Concat(r.Impact.Dependents).ToList();
            List<string> Targets(bool tests) => solutions.Count == 1 ? [Path.GetRelativePath(work, solutions[0]).Replace('\\', '/')]
                : [.. affected.Where(p => !tests || nodes.GetValueOrDefault($"proj:{p}")?.Tags?.Contains("test") == true)];

            var steps = new List<StepResult>();
            var failed = false;
            foreach (var step in config.Validate)
            {
                var placeholder = step.Run.Contains("{tests}") ? "{tests}" : step.Run.Contains("{target}") ? "{target}" : null;
                var targets = placeholder is null ? [""] : Targets(placeholder == "{tests}");
                if (failed || targets.Count == 0)
                {
                    steps.Add(new(step.Name, step.Run, "skipped", null, 0, [failed ? "an earlier step failed" : placeholder == "{tests}" ? "no affected test projects" : "no affected projects"]));
                    continue;
                }
                foreach (var target in targets)
                {
                    var command = placeholder is null ? step.Run : step.Run.Replace(placeholder, Quote(target));
                    log?.WriteLine($"  {r.Id} {step.Name}: {command}");
                    var result = Run(work, step.Name, command, TimeSpan.FromMinutes(config.TimeoutMinutes));
                    steps.Add(result);
                    if (result.Status != "pass") { failed = true; break; }
                }
            }
            return new(!failed, steps, ValidationNote);
        }
        finally
        {
            try { tmp.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // The files git tracks or would track (or, outside git, everything but build output), copied as they are on disk now.
    static void Copy(string root, string to)
    {
        string[] skip = ["bin", "obj", "node_modules", ".git", ".vs"];
        var files = Git(root, "ls-files --cached --others --exclude-standard")?.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => Path.Combine(root, f))
            ?? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar).SkipLast(1).Any(skip.Contains));
        foreach (var f in files.Where(File.Exists))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(root, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
    }

    static string? Git(string dir, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    static StepResult Run(string dir, string name, string command, TimeSpan timeout)
    {
        var psi = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe", ["/c", command]) : new ProcessStartInfo("/bin/sh", ["-c", command]);
        psi.WorkingDirectory = dir;
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        // No MSBuild nodes outliving the step (they'd hold the temporary directory), no first-run banners.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        var output = new List<string>();
        var watch = Stopwatch.StartNew();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.Add(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.Add(e.Data); };
        try { p.Start(); }
        catch (System.ComponentModel.Win32Exception e) { return new(name, command, "fail", null, 0, [e.Message]); }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        var done = p.WaitForExit(timeout);
        if (!done) p.Kill(entireProcessTree: true);
        p.WaitForExit();
        var seconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
        var tests = TestSummary(output);
        // Evidence names files as they are in the scanned directory, not in the temporary copy.
        var lines = output.Select(l => l.Replace(dir + Path.DirectorySeparatorChar, "").Replace(dir, ".")).ToList();
        if (!done) return new(name, command, "timeout", null, seconds, [$"no result after {timeout.TotalMinutes:0} minutes", .. Evidence(lines)], tests);
        return p.ExitCode == 0 ? new(name, command, "pass", 0, seconds, null, tests) : new(name, command, "fail", p.ExitCode, seconds, Evidence(lines), tests);
    }

    // Concise failure evidence: the error lines (distinct, at most ten), else the last ten lines of output.
    static List<string> Evidence(List<string> output)
    {
        static string Cut(string s) => s.Length > 300 ? s[..300] + "…" : s;
        var errors = output.Where(l => Regex.IsMatch(l, @"\berror\b|\bfailed\b|\[FAIL\]", RegexOptions.IgnoreCase)).Select(l => Cut(l.Trim())).Distinct().Take(10).ToList();
        return errors.Count > 0 ? errors : [.. output.TakeLast(10).Select(l => Cut(l.Trim()))];
    }

    // Test counts from `dotnet test` output (VSTest or Microsoft.Testing.Platform), summed over projects; null if none found.
    static string? TestSummary(List<string> output)
    {
        int passed = 0, failed = 0, found = 0;
        foreach (var l in output)
            if (Regex.Match(l, @"Failed:\s*(\d+),\s*Passed:\s*(\d+)") is { Success: true } v)
                (failed, passed, found) = (failed + int.Parse(v.Groups[1].Value), passed + int.Parse(v.Groups[2].Value), found + 1);
            else if (Regex.Match(l, @"Total:\s*(\d+),\s*Errors:\s*\d+,\s*Failed:\s*(\d+)") is { Success: true } x)
                (failed, passed, found) = (failed + int.Parse(x.Groups[2].Value), passed + int.Parse(x.Groups[1].Value) - int.Parse(x.Groups[2].Value), found + 1);
            else if (Regex.Match(l, @"^\s*total:\s*(\d+)\s*$", RegexOptions.IgnoreCase) is { Success: true } t)
                (passed, found) = (passed + int.Parse(t.Groups[1].Value), found + 1);
            else if (Regex.Match(l, @"^\s*failed:\s*(\d+)\s*$", RegexOptions.IgnoreCase) is { Success: true } f && found > 0)
                (failed, passed) = (failed + int.Parse(f.Groups[1].Value), passed - int.Parse(f.Groups[1].Value));
        return found == 0 ? null : $"{passed} passed, {failed} failed";
    }

    // Console report: per suggestion the change to copy, the static impact, the validation steps, status and confidence.
    public static void Report(List<Remediation> list, string root, TextWriter o)
    {
        o.WriteLine($"Remediation suggestions ({list.Count}) — advisory; commands and patches run from {root}");
        if (list.Count == 0) o.WriteLine("  none: no version drift and no remediation.targets (or --package/--to) to propose");
        foreach (var r in list)
        {
            o.WriteLine();
            var versions = r.Changes.Select(c => c.Current ?? "?").Distinct().ToList();
            o.WriteLine($"{r.Id}  {r.Package} {string.Join(", ", versions)} → {r.Target}  [{r.Kind}]  ({r.Ecosystem}; {r.Reason})");
            o.WriteLine();
            var commands = r.Changes.Select(c => c.Command).OfType<string>().ToList();
            var patches = r.Changes.Where(c => c.Command is null).Select(c => c.Edit).OfType<FileEdit>().DistinctBy(e => (e.File, e.Line)).ToList();
            if (commands.Count > 0)
            {
                o.WriteLine("Suggested update:");
                foreach (var c in commands) o.WriteLine($"  {c}");
            }
            if (patches.Count > 0)
            {
                o.WriteLine(r.Changes.Any(c => c.Edit is not null && c.Command is null && c.Edit.File.EndsWith("Directory.Packages.props"))
                    ? "Suggested patch (versions are managed centrally; apply with git apply):" : "Suggested patch (apply with git apply):");
                foreach (var e in patches) foreach (var l in e.Patch.TrimEnd('\n').Split('\n')) o.WriteLine($"  {l}");
            }
            foreach (var c in r.Changes.Where(c => c.Problem is not null))
                o.WriteLine($"  no command for {c.Project}: {c.Problem}");
            foreach (var c in r.Changes.Where(c => c.Kind != r.Kind && c.Problem is null))
                o.WriteLine($"  {c.Project}: {c.Current} → {c.Target} is a {c.Kind} update");

            var i = r.Impact;
            o.WriteLine();
            o.WriteLine("Impact (static):");
            o.WriteLine($"  - projects: {string.Join(", ", i.Projects)}" + (i.Dependents.Count > 0 ? $"; through project references: {string.Join(", ", i.Dependents)}" : "") + "  [detected]");
            o.WriteLine($"  - {i.Uses.Count} symbols import the package's namespaces ({i.Public} public)" +
                (i.Uses.Count > 0 ? ": " + string.Join(", ", i.Uses.Take(6).Select(u => $"{u.Id[(u.Id.IndexOf(':') + 1)..]} ({u.Location})")) + (i.Uses.Count > 6 ? ", …" : "") : "") + "  [inferred]");
            if (i.Flows.Count > 0) o.WriteLine($"  - {i.Flows.Count} flows pass through them: {string.Join(", ", i.Flows.Take(6))}{(i.Flows.Count > 6 ? ", …" : "")}  [inferred]");
            o.WriteLine($"  - {i.Tests.Count} linked tests" + (i.Tests.Count > 0 ? $": {string.Join(", ", i.Tests.Take(6))}{(i.Tests.Count > 6 ? ", …" : "")}" : "") + $"  [inferred; {TestLinks.Note}]");
            if (i.TestProjects.Count > 0) o.WriteLine($"  - test projects: {string.Join(", ", i.TestProjects)}  [detected]");
            o.WriteLine($"  - not assessed: APIs removed or changed between {string.Join("/", versions)} and {r.Target}");
            o.WriteLine($"Static assessment: {r.Assessment.Risk} — {string.Join("; ", r.Assessment.Reasons)}");
            if (r.Scope is not null) o.WriteLine($"Scope: {r.Scope}");

            o.WriteLine();
            if (r.Validation is null) o.WriteLine($"Validation: not run{(r.Actionable ? " (--validate applies the change to a temporary copy and runs restore, build and tests)" : "")}");
            else
            {
                o.WriteLine($"Validation (executed in a temporary copy; {ValidationNote}):");
                foreach (var s in r.Validation.Steps)
                {
                    o.WriteLine($"  - {s.Name}: {s.Status}{(s.ExitCode is { } x and not 0 ? $" (exit {x})" : "")}{(s.Tests is { } t ? $", {t}" : "")}  `{s.Command}`" + (s.Seconds > 0 ? $"  {s.Seconds}s" : ""));
                    if (s.Status is "fail" or "timeout") foreach (var e in s.Evidence ?? []) o.WriteLine($"      {e}");
                    else if (s.Status == "skipped" && s.Evidence is [var why]) o.WriteLine($"      {why}");
                }
            }
            o.WriteLine($"Status: {r.Status}   Confidence: {r.Confidence}");
        }
    }

    public static object Json(List<Remediation> list) => new
    {
        note = $"advisory: nothing is labelled safe; {ValidationNote}",
        remediations = list.Select(r => new
        {
            id = r.Id, r.Ecosystem, r.Package, r.Target, r.Kind, reason = r.Reason, reasonBasis = r.ReasonBasis,
            changes = r.Changes.Select(c => new { c.Project, current = c.Current, c.Target, c.Kind, c.Command, patch = c.Edit?.Patch, file = c.Edit?.File, line = c.Edit?.Line, c.Problem }),
            impact = new
            {
                projects = r.Impact.Projects, dependents = r.Impact.Dependents, publicSymbols = r.Impact.Public,
                symbols = r.Impact.Uses.Select(u => new { id = u.Id, location = u.Location, @namespace = u.Namespace, tests = u.Tests }),
                flows = r.Impact.Flows, tests = r.Impact.Tests, testProjects = r.Impact.TestProjects, note = r.Impact.Note,
            },
            assessment = new { risk = r.Assessment.Risk, reasons = r.Assessment.Reasons, basis = "static" },
            validation = r.Validation is null ? null : new { pass = r.Validation.Pass, steps = r.Validation.Steps, note = r.Validation.Note },
            scope = r.Scope, status = r.Status, confidence = r.Confidence, provenance = r.Provenance,
        }),
    };
}
