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
    if (args[i] == "--ai") opts["ai"] = "";
    else if (args[i].StartsWith("--") && valued.Contains(args[i][2..]) && i + 1 < args.Length) opts[args[i][2..]] = args[++i];
    else if (args[i].StartsWith("--")) return Usage($"unknown option {args[i]}");
    else pos.Add(args[i]);
}
var cmd = pos.ElementAtOrDefault(0);
var path = pos.ElementAtOrDefault(1) ?? ".";
if (opts.GetValueOrDefault("format") is { } format && format is not ("console" or "json")) return Usage($"unknown format {format}");
var json = opts.GetValueOrDefault("format") == "json";

if (cmd is null) return Usage(null);
if (!Directory.Exists(path))
{
    Console.Error.WriteLine($"not a directory: {path}");
    return 1;
}
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
        return DiffCommand(path, since, config, enforce: true);
    case "check":
        return Analyze(path, config, enforce: true, json);
    case "diff":
        return DiffCommand(path, pos.ElementAtOrDefault(2), config, enforce: false);
    case "architecture":
        return ArchitectureCommand(path, config, json);
    case "generate":
        return await Generate(path, pos.ElementAtOrDefault(2) ?? Path.Combine(path, "docs"), config, opts.ContainsKey("ai"));
    default:
        return Usage($"unknown command {cmd}");
}

static int Usage(string? error)
{
    if (error is not null) Console.Error.WriteLine(error);
    Console.Error.WriteLine($"""
        usage:
          docwizz scan <dir> [model.json]   write the code model
          docwizz analyze <dir>             documentation report
          docwizz check <dir>               report + exit 1 if thresholds fail (CI)
          docwizz check <dir> --since <ref> exit 1 only on gaps/violations introduced since <ref>
          docwizz diff <dir> [ref]          what changed vs <ref> (default: docs/.docwizz/model.json)
          docwizz architecture <dir>        layers, dependencies, violations; exit 1 above check thresholds
          docwizz generate <dir> [out]      write Markdown docs (default <dir>/docs)
            [--ai]                          draft missing summaries with Claude (cached per code hash)
        options:
          --profile <name>                  documentation profile: {string.Join(", ", Profiles.Names)}
          --format console|json             analyze/check/architecture output
        """);
    return 1;
}

static (CodeModel Model, List<string> Files) BuildModel(string root, Config config)
{
    string[] skip = ["bin", "obj", "node_modules", "dist"];
    string[] exts = [".cs", ".vue", ".ts"];

    // Prefer git's view (honours .gitignore, skips nested worktrees); fall back to a directory walk.
    var candidates = (Git(root, "ls-files --cached --others --exclude-standard")?
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(f => Path.Combine(root, f)).Where(File.Exists)
        ?? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar).SkipLast(1)
            .Any(d => d.StartsWith('.') || skip.Contains(d)))
        .Where(f => !config.Exclude.Any(g =>
            FileSystemName.MatchesSimpleExpression(g, Path.GetRelativePath(root, f).Replace('\\', '/'))))
        .Distinct()
        .ToList();
    var scanned = candidates.Where(f => exts.Contains(Path.GetExtension(f)) && !f.EndsWith(".d.ts")).ToList();

    var (nodes, edges) = CSharpScanner.Scan(root, scanned.Where(f => f.EndsWith(".cs")));
    var (feNodes, feEdges) = Frontend.Scan(root, scanned.Where(f => !f.EndsWith(".cs")).ToList());
    var (projNodes, projEdges) = Projects.Scan(root, candidates.Where(f => f.EndsWith(".csproj") || Path.GetFileName(f) == "package.json"));
    var ids = nodes.Select(n => n.Id).ToHashSet();
    nodes.AddRange(feNodes.Concat(projNodes).Where(n => ids.Add(n.Id)));
    edges = Frontend.LinkHttp(nodes, [.. edges, .. feEdges, .. projEdges]);

    // Test code leaves only `tests` edges (test symbol → code it uses) behind.
    var testFiles = scanned.Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
        .Where(f => config.Tests.Any(g => FileSystemName.MatchesSimpleExpression(g, f))).ToHashSet();
    var testIds = nodes.Where(n => testFiles.Contains(n.File)).Select(n => n.Id).ToHashSet();
    nodes.RemoveAll(n => testIds.Contains(n.Id));
    edges = edges.Where(e => !testIds.Contains(e.To) && (!testIds.Contains(e.From) || e.Kind is "calls" or "imports" or "renders" or "injects" or "http"))
        .Select(e => testIds.Contains(e.From) ? new Edge(e.From, e.To, "tests") : e).Distinct().ToList();

    var files = scanned.Where(f => !testFiles.Contains(Path.GetRelativePath(root, f).Replace('\\', '/'))).ToList();
    return (new CodeModel(Git(root, "rev-parse --short HEAD")?.Trim(), nodes, edges), files);
}

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
static object DocumentationJson(CodeModel model, Config config, List<DocumentationItem> items) => new
{
    commit = model.Commit,
    profile = config.Profile,
    note = $"coverage against the '{config.Profile}' profile; not a statement of standards compliance",
    coverage = Math.Round(Analyzer.Coverage(items), 1),
    items = items.Select(i => new
    {
        id = i.Node.Id, kind = i.Node.Kind, file = i.Node.File, line = i.Node.Line,
        level = i.Level, status = i.Status, pattern = i.Pattern, required = i.Required,
        sections = i.Sections, missing = i.Missing, reasons = i.Reasons, sources = i.Sources, tested = i.Tested,
    }),
};

static async Task<int> Generate(string root, string outDir, Config config, bool ai)
{
    var model = BuildModel(root, config).Model;
    var findings = Analyzer.Analyze(model, config);
    var arch = Architecture.Check(model, config.Architecture);
    // Cached drafts are always used; new ones are only requested with --ai.
    var drafts = await AiProse.Summaries(root, model, findings, Path.Combine(outDir, ".docwizz", "ai-cache.json"), ai);
    foreach (var f in findings)
        if (drafts.TryGetValue(f.Node.Id, out var draft)) f.Sections["summary"] = new(Origin.Ai, draft);
    var written = new Generator(root, outDir, model, findings, arch, config, drafts).Run();

    // Fingerprint for `docwizz diff`: the model these docs were generated from.
    Directory.CreateDirectory(Path.Combine(outDir, ".docwizz"));
    WriteModel(model, Path.Combine(outDir, ".docwizz", "model.json"));
    File.WriteAllText(Path.Combine(outDir, ".docwizz", "documentation.json"),
        JsonSerializer.Serialize(DocumentationJson(model, config, findings), JsonOptions()));
    Console.WriteLine($"{written.Count} pages → {outDir} (commit {model.Commit ?? "unknown"})");
    return 0;
}

static int DiffCommand(string root, string? gitRef, Config config, bool enforce)
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

    var result = Diff.Compare(before, BuildModel(root, config).Model, config);
    Diff.Report(result, baseline, Console.Out);
    if (!enforce) return 0;

    var critical = result.NewGaps.Count(Analyzer.IsCritical);
    var ok = critical == 0 && result.NewViolations.Count == 0;
    Console.WriteLine();
    Console.WriteLine(ok ? "check: PASS" : $"check: FAIL — introduced {critical} critical gaps, {result.NewViolations.Count} violations");
    return ok ? 0 : 1;
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
    var findings = Analyzer.Analyze(model, config);
    var arch = Architecture.Check(model, config.Architecture);
    var (violations, cycles, _, _) = arch;

    var coverage = Analyzer.Coverage(findings);
    var critical = findings.Count(Analyzer.IsCritical);
    var failures = new List<string>();
    if (coverage < config.Check.MinCoverage) failures.Add($"coverage {coverage:0}% < {config.Check.MinCoverage}%");
    if (critical > config.Check.MaxCritical) failures.Add($"{critical} critical > {config.Check.MaxCritical}");
    if (violations.Count > config.Check.MaxViolations) failures.Add($"{violations.Count} violations > {config.Check.MaxViolations}");
    if (cycles.Count > config.Check.MaxCycles) failures.Add($"{cycles.Count} cycles > {config.Check.MaxCycles}");
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
    var arch = Architecture.Check(BuildModel(root, config).Model, config.Architecture);
    if (json) Console.WriteLine(JsonSerializer.Serialize(arch, JsonOptions()));
    else Architecture.Report(arch, Console.Out);
    return arch.Violations.Count > config.Check.MaxViolations || arch.Cycles.Count > config.Check.MaxCycles ? 1 : 0;
}

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
