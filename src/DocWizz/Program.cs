using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Enumeration;
using System.Text.Json;
using System.Text.Json.Serialization;

// ponytail: hand-rolled arg parsing; move to System.CommandLine if options keep growing
string[] valued = ["format", "profile", "since"];
var opts = new Dictionary<string, string>();
var pos = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--ai" or "--html" or "--timings") opts[args[i][2..]] = "";
    else if (args[i].StartsWith("--") && valued.Contains(args[i][2..]) && i + 1 < args.Length) opts[args[i][2..]] = args[++i];
    else if (args[i].StartsWith("--")) return Usage($"unknown option {args[i]}");
    else pos.Add(args[i]);
}
var cmd = pos.ElementAtOrDefault(0);
// `docwizz diff HEAD~1 HEAD`: no directory given, refs only.
if (cmd == "diff" && pos.Count >= 2 && !Directory.Exists(pos[1])) pos.Insert(1, ".");
var path = pos.ElementAtOrDefault(1) ?? ".";
if (opts.GetValueOrDefault("format") is { } format && format is not ("console" or "json")) return Usage($"unknown format {format}");
var json = opts.GetValueOrDefault("format") == "json";

if (opts.ContainsKey("timings"))
{
    Timings.Enabled = true;
    AppDomain.CurrentDomain.ProcessExit += (_, _) => Timings.Report(Console.Error);
}
if (cmd is null) return Usage(null);
if (!Directory.Exists(path))
{
    Console.Error.WriteLine($"not a directory: {path}");
    return 1;
}
if (cmd == "init") return Init(path);
Config config;
try { config = Config.Load(path, opts.GetValueOrDefault("profile")); }
catch (ArgumentException e) { return Usage(e.Message); }

switch (cmd)
{
    case "scan":
        return Scan(path, pos.ElementAtOrDefault(2) ?? "model.json", config);
    case "analyze":
        return Analyze(path, config, enforce: false, json);
    case "check" when opts.GetValueOrDefault("since") is { } since:
        return DiffCommand(path, since, config, enforce: true, json: json);
    case "check":
        return Analyze(path, config, enforce: true, json);
    case "diff":
        return DiffCommand(path, pos.ElementAtOrDefault(2), config, enforce: false, head: pos.ElementAtOrDefault(3), json: json);
    case "architecture":
        return ArchitectureCommand(path, config, json);
    case "generate":
        return await Generate(path, pos.ElementAtOrDefault(2) ?? Path.Combine(path, "docs"), config, opts.ContainsKey("ai"), opts.ContainsKey("html"));
    default:
        return Usage($"unknown command {cmd}");
}

// Starter config: the defaults, spelled out, to edit in place.
static int Init(string dir)
{
    var file = Path.Combine(dir, "docwizz.yaml");
    if (File.Exists(file))
    {
        Console.Error.WriteLine($"{file} exists; not overwriting");
        return 1;
    }
    File.WriteAllText(file, Config.Default);
    Console.WriteLine($"→ {file}");
    return 0;
}

static int Usage(string? error)
{
    if (error is not null) Console.Error.WriteLine(error);
    Console.Error.WriteLine($"""
        usage:
          docwizz init [dir]                write a starter docwizz.yaml
          docwizz scan <dir> [model.json]   write the code model
          docwizz analyze <dir>             documentation report
          docwizz check <dir>               report + exit 1 if thresholds fail (CI)
          docwizz check <dir> --since <ref> exit 1 only on gaps/violations introduced since <ref>
          docwizz diff <dir> [ref]          what changed vs <ref> (default: docs/.docwizz/model.json)
          docwizz diff [dir] <base> <head>  what changed between two git refs
          docwizz architecture <dir>        layers, dependencies, violations; exit 1 above check thresholds
          docwizz generate <dir> [out]      write Markdown docs (default <dir>/docs)
            [--ai]                          draft missing summaries with a local Ollama (cached per code hash)
            [--html]                        also write an HTML page next to every Markdown page
        options:
          --profile <name|file.yaml>        documentation profile: {string.Join(", ", Profiles.Names)}, or your own file
          --format console|json             analyze/check/architecture/diff output
          --timings                         time per stage and peak memory, on stderr (benchmarks)
        """);
    return 1;
}

// Every file docwizz may read. Prefer git's view (honours .gitignore, skips nested worktrees); fall back to a directory walk.
// Dot-directories are skipped except those named in `dotDirs` (CI descriptors live in .github/.circleci).
static List<string> RepoFiles(string root, Config config, string[]? dotDirs = null)
{
    string[] skip = ["bin", "obj", "node_modules", "dist"];
    return (Git(root, "ls-files --cached --others --exclude-standard")?
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(f => Path.Combine(root, f)).Where(File.Exists)
        ?? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar).SkipLast(1)
            .Any(d => (d.StartsWith('.') && dotDirs?.Contains(d) != true) || skip.Contains(d)))
        .Where(f => !config.Exclude.Any(g =>
            FileSystemName.MatchesSimpleExpression(g, Path.GetRelativePath(root, f).Replace('\\', '/'))))
        .Distinct()
        // Sorted: a directory walk (a `git archive`d ref) and `git ls-files` must yield the same order, or which
        // declaration of a multi-file symbol wins below would differ between the two sides of a diff.
        .OrderBy(f => Path.GetRelativePath(root, f).Replace('\\', '/'), StringComparer.Ordinal)
        .ToList();
}

static (CodeModel Model, List<string> Files) BuildModel(string root, Config config)
{
    string[] exts = [".cs", ".vue", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".sql", ".java"];
    var candidates = Timings.Measure("files", () => RepoFiles(root, config));
    var scanned = candidates.Where(f => exts.Contains(Path.GetExtension(f)) && !f.EndsWith(".d.ts") && !f.EndsWith(".min.js")
        && !f.Replace('\\', '/').Contains("/wwwroot/lib/")).ToList(); // ponytail: libman/bower vendor dir only; other vendored JS needs exclude:

    var (nodes, edges) = Timings.Measure("scan:csharp", () => CSharpScanner.Scan(root, scanned.Where(f => f.EndsWith(".cs")), config.CommentDocs));
    var (feNodes, feEdges) = Timings.Measure("scan:frontend", () => Frontend.Scan(root, scanned.Where(f => Path.GetExtension(f) is ".vue" or ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs").ToList()));
    var (sqlNodes, sqlEdges) = Timings.Measure("scan:sql", () => Sql.Scan(root, scanned.Where(f => f.EndsWith(".sql"))));
    var (javaNodes, javaEdges) = Timings.Measure("scan:java", () => JavaScanner.Scan(root, scanned.Where(f => f.EndsWith(".java"))));
    var (projNodes, projEdges) = Timings.Measure("scan:projects", () => Projects.Scan(root, candidates.Where(f => f.EndsWith(".csproj") || f.EndsWith(".sln") || f.EndsWith(".slnx")
        || Path.GetFileName(f) is "package.json" or "pom.xml" or "build.gradle" or "build.gradle.kts")));
    // Keys defined in configuration files win over the bare key nodes code reads create.
    var settings = Timings.Measure("scan:configuration", () => Configuration.Scan(root, candidates.Where(Configuration.IsConfigFile)));
    var defined = settings.Select(n => n.Id).ToHashSet();
    nodes.RemoveAll(n => n.Kind == "config" && defined.Contains(n.Id));
    var link = Timings.Stage("link");
    var testFiles = scanned.Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
        .Where(f => config.Tests.Any(g => FileSystemName.MatchesSimpleExpression(g, f))
            || Projects.Of(projNodes, f)?.Tags?.Contains("test") == true).ToHashSet(); // test projects: by metadata, not only by path
    // Same id from test and production code (a test router's `route:/login`): production wins.
    nodes = CodeModel.MergeHashes(nodes, n => testFiles.Contains(n.File)).OrderBy(n => testFiles.Contains(n.File)).DistinctBy(n => n.Id).ToList();
    var ids = nodes.Select(n => n.Id).ToHashSet();
    nodes.AddRange(CodeModel.MergeHashes(feNodes.Concat(javaNodes).Concat(sqlNodes), n => testFiles.Contains(n.File)).OrderBy(n => testFiles.Contains(n.File)).Concat(projNodes).Concat(settings).Where(n => ids.Add(n.Id)));
    edges = Frontend.LinkHttp(nodes, Sql.Link(nodes, [.. edges, .. feEdges, .. javaEdges, .. sqlEdges, .. projEdges]));
    Externals.Link(nodes, edges);
    Configuration.Link(nodes, edges);

    // Test code leaves only `tests` edges (test symbol → code it uses) behind.
    var testIds = nodes.Where(n => testFiles.Contains(n.File)).Select(n => n.Id).ToHashSet();
    nodes.RemoveAll(n => testIds.Contains(n.Id));
    edges = edges.Where(e => !testIds.Contains(e.To) && (!testIds.Contains(e.From) || e.Kind is "calls" or "imports" or "renders" or "injects" or "http"))
        .Select(e => testIds.Contains(e.From) ? new Edge(e.From, e.To, "tests") : e).Distinct().ToList();

    nodes = nodes.Select(n => n.Kind is "external" or "config" ? n : n with { Language = Language(n.File) }).ToList();
    link.Dispose();

    var files = scanned.Where(f => !testFiles.Contains(Path.GetRelativePath(root, f).Replace('\\', '/'))).ToList();
    return (new CodeModel(Git(root, "rev-parse --short HEAD")?.Trim(), nodes, edges), files);
}

static string? Language(string file) => Path.GetExtension(file) switch
{
    ".cs" => "csharp", ".vue" => "vue", ".ts" or ".tsx" => "typescript", ".js" or ".jsx" or ".mjs" => "javascript", ".sql" => "sql", ".java" => "java", ".csproj" => "msbuild",
    _ => Path.GetFileName(file) switch { "package.json" => "npm", "pom.xml" => "maven", "build.gradle" or "build.gradle.kts" => "gradle", _ => null },
};

static int Scan(string root, string outFile, Config config)
{
    var (model, files) = BuildModel(root, config);
    WriteModel(model, outFile);

    Console.WriteLine($"Files  {files.Count}");
    foreach (var g in files.GroupBy(Path.GetExtension).OrderBy(g => g.Key))
        Console.WriteLine($"  {g.Key,-5} {g.Count()}");
    Console.WriteLine($"Nodes  {model.Nodes.Count}");
    foreach (var g in model.Nodes.GroupBy(n => n.Kind).OrderBy(g => g.Key))
        Console.WriteLine($"  {g.Key,-12} {g.Count()}");
    Console.WriteLine($"Edges  {model.Edges.Count}");
    Console.WriteLine($"→ {outFile}");
    return 0;
}

static JsonSerializerOptions JsonOptions() => new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

static void WriteModel(CodeModel model, string file) => File.WriteAllText(file, JsonSerializer.Serialize(model, JsonOptions()));

// The documentation model as data: per item what is required, what exists and where it comes from.
static object DocumentationJson(CodeModel model, Config config, List<DocumentationItem> items)
{
    var nodes = model.Nodes.ToDictionary(n => n.Id);
    // Evidence: the source location of every symbol an item's sections were derived or drafted from.
    List<string> Evidence(DocumentationItem i) => i.Sources.Concat(i.Sections.Values.SelectMany(s => s.From ?? []))
        .Distinct().Select(nodes.GetValueOrDefault).OfType<Node>().Select(CodeModel.Location).ToList();
    return new
    {
        commit = model.Commit,
        profile = config.Profile,
        note = $"coverage against the '{config.Profile}' profile; not a statement of standards compliance",
        coverage = Math.Round(Analyzer.Coverage(items), 1),
        quality = Math.Round(Analyzer.Quality(items), 1),
        items = items.Select(i => new
        {
            id = i.Node.Id, kind = i.Node.Kind, file = i.Node.File, line = i.Node.Line,
            level = i.Level, status = i.Status, pattern = i.Pattern, required = i.Required,
            sections = i.Sections, missing = i.Missing, reasons = i.Reasons, sources = i.Sources, evidence = Evidence(i), tested = i.Tested,
            flags = i.Flags is { Count: > 0 } ? i.Flags : null,
        }),
    };
}

static async Task<int> Generate(string root, string outDir, Config config, bool ai, bool html = false)
{
    var model = BuildModel(root, config).Model;
    var findings = Timings.Measure("analyze", () => Analyzer.Analyze(model, config));
    var arch = Timings.Measure("architecture", () => Architecture.Check(model, config));
    // Cached drafts are always used; new ones are only requested with --ai.
    var drafts = await AiProse.Summaries(root, model, findings, Path.Combine(outDir, ".docwizz", "ai-cache.json"), ai);
    // A draft fills what is missing, never what is written or derived: the summary, and sections the item doesn't have.
    foreach (var f in findings)
    {
        if (!drafts.TryGetValue(f.Node.Id, out var draft)) continue;
        if (draft.Text.Length > 0) f.Sections["summary"] = new(Origin.Ai, draft.Text, draft.Sources, draft.Sections?.GetValueOrDefault("summary"));
        foreach (var (name, sentences) in draft.Sections ?? [])
            if (name != "summary" && AiProse.SectionNames.Contains(name))
                f.Sections.TryAdd(name == "errors" ? "exception" : name,
                    new(Origin.Ai, string.Join(" ", sentences.Select(x => x.Text)), [.. sentences.SelectMany(x => x.From).Distinct()], sentences));
    }
    var summaries = drafts.Where(d => !d.Key.StartsWith("module:") && d.Value.Text.Length > 0).ToDictionary(d => d.Key, d => d.Value.Text);
    var overviews = drafts.Where(d => d.Key.StartsWith("module:")).ToDictionary(d => d.Key["module:".Length..], d => d.Value);
    // Advisory ratings of written docs: shown in quality.md, never part of doc quality % or check.
    var assessments = await AiProse.Assessments(root, model, findings, Path.Combine(outDir, ".docwizz", "ai-assessments.json"), ai);
    var (pages, changed) = Timings.Measure("generate", () => new Generator(root, outDir, model, findings, arch, config, summaries, overviews, assessments)
    {
        WriteHtml = html,
        Files = [.. RepoFiles(root, config, [".github", ".circleci"]).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))],
    }.Run());

    // Fingerprint for `docwizz diff`: the model these docs were generated from.
    Directory.CreateDirectory(Path.Combine(outDir, ".docwizz"));
    WriteModel(model, Path.Combine(outDir, ".docwizz", "model.json"));
    File.WriteAllText(Path.Combine(outDir, ".docwizz", "documentation.json"),
        JsonSerializer.Serialize(DocumentationJson(model, config, findings), JsonOptions()));
    Console.WriteLine($"{pages.Count} pages → {outDir} (commit {model.Commit ?? "unknown"}): {changed.Count} changed" +
        (changed.Count is > 0 and <= 10 ? $" ({string.Join(", ", changed.Order())})" : ""));
    return 0;
}

static int DiffCommand(string root, string? gitRef, Config config, bool enforce, string? head = null, bool json = false)
{
    CodeModel? before;
    string baseline;
    if (gitRef is not null)
    {
        before = ModelAt(root, gitRef, config);
        baseline = gitRef;
    }
    else
    {
        var fingerprint = Path.Combine(root, "docs", ".docwizz", "model.json");
        if (!File.Exists(fingerprint))
        {
            Console.Error.WriteLine($"no {fingerprint}; run docwizz generate first or pass a git ref");
            return 1;
        }
        before = JsonSerializer.Deserialize<CodeModel>(File.ReadAllText(fingerprint), JsonOptions());
        baseline = $"docs generated at {before?.Commit ?? "unknown"}";
    }
    if (before is null) return 1;

    var after = head is null ? BuildModel(root, config).Model : ModelAt(root, head, config);
    if (after is null) return 1;
    var result = Timings.Measure("diff", () => Diff.Compare(before, after, config));
    var against = head is null ? baseline : $"{baseline}, at {head}";
    if (!json) Diff.Report(result, against, Console.Out);
    if (!enforce)
    {
        if (json) Console.WriteLine(JsonSerializer.Serialize(DiffJson(result, against, null), JsonOptions()));
        return 0;
    }

    var critical = result.NewGaps.Count(Analyzer.IsCritical);
    var violations = Architecture.Failing(result.NewViolations, config.Check).Count;
    var complex = TooComplex(result.Added.Concat(result.Changed), config.Check);
    // Docs that now contradict the code fail; inferred flags and possibly stale docs are reported only.
    var contradictions = result.NewFlags.Count(x => x.Flag.Origin == Origin.Fact);
    // New symbols at or above check.require_tests with no linked test code (structural, not coverage).
    var untested = config.Check.RequireTests is { } rt && Enum.Parse<Level>(rt, true) is var min
        ? result.Tests.Where(t => t.Added && t.Tests.Count == 0 && t.Level >= min).ToList() : [];
    var ok = critical == 0 && violations == 0 && complex.Count == 0 && contradictions == 0 && untested.Count == 0;
    var summary = ok ? "check: PASS" : $"check: FAIL — introduced {critical} critical gaps, {violations} violations" +
        (contradictions > 0 ? $", {contradictions} docs contradicting the code" : "") +
        (complex.Count > 0 ? $", {complex.Count} symbols over complexity {config.Check.MaxComplexity}" : "") +
        (untested.Count > 0 ? $", {untested.Count} new {config.Check.RequireTests!.ToLowerInvariant()}-level symbols without linked tests ({string.Join(", ", untested.Select(t => Generator.Display(t.Symbol)))})" : "");
    if (json) Console.WriteLine(JsonSerializer.Serialize(DiffJson(result, against, new { pass = ok, summary }), JsonOptions()));
    else
    {
        Console.WriteLine();
        Console.WriteLine(summary);
    }
    return ok ? 0 : 1;
}

// `diff`/`check --since` as data: symbols by id and location, and test traceability with its caveat spelled out.
static object DiffJson(DiffResult d, string baseline, object? check)
{
    static object Symbol(Node n) => new { id = n.Id, kind = n.Kind, location = CodeModel.Location(n) };
    return new
    {
        baseline,
        added = d.Added.Select(Symbol), changed = d.Changed.Select(Symbol), removed = d.Removed.Select(Symbol),
        pages = d.Pages, dependencies = new { added = d.DepsAdded, removed = d.DepsRemoved }, decisions = d.Decisions,
        stale = d.Stale.Select(s => new { id = s.After.Id, location = CodeModel.Location(s.After), changes = s.Changes }),
        introduced = new
        {
            gaps = d.NewGaps.Select(f => new { id = f.Node.Id, location = CodeModel.Location(f.Node), level = f.Level, critical = Analyzer.IsCritical(f), missing = f.Missing }),
            violations = d.NewViolations,
            flags = d.NewFlags.Select(x => new { id = x.Item.Node.Id, x.Flag.Rule, basis = x.Flag.Origin, x.Flag.Detail }),
        },
        tests = new
        {
            note = TestLinks.Note,
            linked = d.Tests.Where(t => t.Tests.Count > 0).Select(t => new { id = t.Symbol.Id, location = CodeModel.Location(t.Symbol), change = t.Added ? "added" : "changed",
                level = t.Level, tests = t.Tests.Select(l => new { test = l.Test, link = l.Via is null ? "direct" : "indirect", via = l.Via }) }),
            unlinked = d.Tests.Where(t => t.Tests.Count == 0).Select(t => new { id = t.Symbol.Id, location = CodeModel.Location(t.Symbol), change = t.Added ? "added" : "changed", level = t.Level }),
        },
        check,
    };
}

// Scans the tree as it was at <ref>, extracted from git into a temp dir.
static CodeModel? ModelAt(string root, string gitRef, Config config)
{
    var prefix = Git(root, "rev-parse --show-prefix")?.Trim();
    var sha = Git(root, $"rev-parse --short {gitRef}")?.Trim();
    if (prefix is null || sha is null)
    {
        Console.Error.WriteLine($"not a git repo or unknown ref: {gitRef}");
        return null;
    }
    var tmp = Directory.CreateTempSubdirectory("docwizz-");
    try
    {
        var tar = Path.Combine(tmp.FullName, "tree.tar");
        var tree = prefix.Length == 0 ? gitRef : $"{gitRef}:{prefix}";
        // From a subdirectory git archive narrows to that subdirectory *within* <tree>; run it from the top.
        var top = Git(root, "rev-parse --show-toplevel")!.Trim();
        if (Git(top, $"archive --format=tar -o \"{tar}\" {tree}") is null) return null;
        var dir = Directory.CreateDirectory(Path.Combine(tmp.FullName, "tree")).FullName;
        TarFile.ExtractToDirectory(tar, dir, overwriteFiles: true);
        return BuildModel(dir, config).Model with { Commit = sha };
    }
    finally
    {
        tmp.Delete(recursive: true);
    }
}

static int Analyze(string root, Config config, bool enforce, bool json)
{
    var model = BuildModel(root, config).Model;
    var findings = Timings.Measure("analyze", () => Analyzer.Analyze(model, config));
    var arch = Timings.Measure("architecture", () => Architecture.Check(model, config));
    var (_, cycles, _, _) = arch;
    var violations = Architecture.Failing(arch.Violations, config.Check);
    var complex = TooComplex(model.Nodes, config.Check);

    var coverage = Analyzer.Coverage(findings);
    var critical = findings.Count(Analyzer.IsCritical);
    var failures = new List<string>();
    if (coverage < config.Check.MinCoverage) failures.Add($"coverage {coverage:0}% < {config.Check.MinCoverage}%");
    if (config.Check.MinQuality is { } minQuality && Analyzer.Quality(findings) is var quality && quality < minQuality)
        failures.Add($"doc quality {quality:0}% < {minQuality}%");
    if (critical > config.Check.MaxCritical) failures.Add($"{critical} critical > {config.Check.MaxCritical}");
    if (violations.Count > config.Check.MaxViolations) failures.Add($"{violations.Count} violations > {config.Check.MaxViolations}");
    if (cycles.Count > config.Check.MaxCycles) failures.Add($"{cycles.Count} cycles > {config.Check.MaxCycles}");
    if (complex.Count > 0) failures.Add($"complexity > {config.Check.MaxComplexity}: " +
        string.Join(", ", complex.Take(5).Select(n => $"{Generator.Display(n)} ({n.Complexity})")) + (complex.Count > 5 ? ", …" : ""));
    // Human-authored architecture sections the profile requires (docs/architecture/<name>.md).
    var missingSections = config.ArchitectureSections
        .Where(s => !File.Exists(Path.Combine(root, "docs", "architecture", $"{s}.md"))).ToList();
    if (missingSections.Count > 0) failures.Add($"missing architecture sections: {string.Join(", ", missingSections)}");

    if (json)
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            documentation = DocumentationJson(model, config, findings),
            architecture = arch,
            missingArchitectureSections = config.ArchitectureSections.Count > 0 ? missingSections : null,
            check = enforce ? new { pass = failures.Count == 0, failures } : null,
        }, JsonOptions()));
    else
    {
        Console.WriteLine($"Profile: {config.Profile}");
        Analyzer.Report(findings, Console.Out);
        Architecture.Report(arch, Console.Out);
        if (config.ArchitectureSections.Count > 0)
            Console.WriteLine($"  human-authored sections (docs/architecture/): " + string.Join(", ",
                config.ArchitectureSections.Select(s => missingSections.Contains(s) ? $"{s} ✗" : $"{s} ✓")));
        if (enforce)
        {
            Console.WriteLine();
            Console.WriteLine(failures.Count == 0 ? "check: PASS" : "check: FAIL — " + string.Join("; ", failures));
        }
    }
    return enforce && failures.Count > 0 ? 1 : 0;
}

static int ArchitectureCommand(string root, Config config, bool json)
{
    var model = BuildModel(root, config).Model;
    var arch = Timings.Measure("architecture", () => Architecture.Check(model, config));
    if (json) Console.WriteLine(JsonSerializer.Serialize(arch, JsonOptions()));
    else Architecture.Report(arch, Console.Out);
    return Architecture.Failing(arch.Violations, config.Check).Count > config.Check.MaxViolations || arch.Cycles.Count > config.Check.MaxCycles ? 1 : 0;
}

static List<Node> TooComplex(IEnumerable<Node> nodes, CheckConfig check) =>
    check.MaxComplexity is { } max ? nodes.Where(n => n.Complexity > max).OrderByDescending(n => n.Complexity).ToList() : [];

static string? Git(string dir, string args)
{
    try
    {
        var p = Process.Start(new ProcessStartInfo("git", args)
            { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output : null;
    }
    catch { return null; }
}
