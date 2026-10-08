using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Enumeration;
using System.Text.Json;
using System.Text.Json.Serialization;

// An exception that escapes a command is a bug or an environment problem (disk, permissions), never a finding: one
// line and exit 2, so CI can tell "docwizz broke" from "the gate failed" (1). DOCWIZZ_DEBUG=1 lets it through with the trace.
try { return await Dispatch(args); }
catch (Exception e) when (Environment.GetEnvironmentVariable("DOCWIZZ_DEBUG") is not { Length: > 0 })
{
    Console.Error.WriteLine($"docwizz: error: {e.Message}");
    Console.Error.WriteLine("This is a problem running docwizz, not a finding about the repository. Set DOCWIZZ_DEBUG=1 for details.");
    return 2;
}

static async Task<int> Dispatch(string[] args)
{
    var request = Cli.Parse(args);
    var (cmd, opts, pos) = (request.Command, request.Options, request.Positional);
    if (request.Error is not null) return Usage(request.Error, cmd);
    if (cmd == "version" || opts.ContainsKey("version"))
    {
        Console.WriteLine(AppVersion.Line);
        return 0;
    }
    if (cmd == "help") return Help(pos.ElementAtOrDefault(1));
    if (opts.ContainsKey("help")) return Help(cmd);
    // `docwizz diff HEAD~1 HEAD`: no directory given, refs only.
    if (cmd == "diff" && pos.Count >= 2 && !Directory.Exists(pos[1])) pos.Insert(1, ".");
    var path = pos.ElementAtOrDefault(1) ?? ".";
    var json = opts.GetValueOrDefault("format") == "json";

    if (opts.ContainsKey("timings"))
    {
        Timings.Enabled = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Timings.Report(Console.Error);
    }
    if (cmd is null) return Start();
    if (!Directory.Exists(path))
    {
        Console.Error.WriteLine($"not a directory: {path}");
        return 1;
    }
    if (cmd == "init") return Init(path);
    if (cmd == "setup") return await SetupCommand(path, opts.GetValueOrDefault("profile"), opts.ContainsKey("force"), opts.ContainsKey("ai"), opts.ContainsKey("html"));
    Config config;
    try { config = Config.Load(path, opts.GetValueOrDefault("profile")); }
    catch (ArgumentException e)
    {
        Console.Error.WriteLine(e.Message);   // names the file or profile and what is wrong with it
        return 1;
    }

    switch (cmd)
    {
        case "sbom":
            return SbomCommand(path, pos.ElementAtOrDefault(2) ?? Path.Combine(path, "sbom.cdx.json"), config);
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
        case "remediate":
            return Remediate(path, config, opts.GetValueOrDefault("package"), opts.GetValueOrDefault("to"), opts.ContainsKey("validate"), opts.GetValueOrDefault("since"), json);
        case "generate":
            Console.WriteLine(await Generate(path, pos.ElementAtOrDefault(2) ?? Path.Combine(path, "docs"), config, opts.ContainsKey("ai"), opts.ContainsKey("html")));
            return 0;
        default:
            return Usage($"unknown command {cmd}");
    }
}

static int SbomCommand(string root, string output, Config config)
{
    var files = RepoFiles(root, config);
    var json = Sbom.Export(root, files);
    var parent = Path.GetDirectoryName(Path.GetFullPath(output));
    if (parent is not null) Directory.CreateDirectory(parent);
    File.WriteAllText(output, json);
    Console.WriteLine($"→ {output}");
    return 0;
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

// `docwizz` alone: the next useful action, not the full reference.
static int Start()
{
    Console.Error.WriteLine("""
        No command specified.

        For a new repository:
          docwizz setup .

        Common workflows:
          docwizz generate .       # write or refresh the docs
          docwizz check .          # quality gate (CI)
          docwizz architecture .   # layers, violations, cycles

        Run 'docwizz help' for all commands, 'docwizz help <command>' for one.
        """);
    return 1;
}

static int Help(string? command)
{
    if (command is not null && Cli.Commands.TryGetValue(command, out var c))
    {
        Console.WriteLine($"""
            docwizz {c.Syntax}

              {c.Purpose}
              for: {c.Use}

            example:
              {c.Example}
            """);
        if (c.Options.Length > 0) Console.WriteLine($"\noptions:\n  {c.Options}\n");
        Console.WriteLine("All commands, options and how they combine: CLI.md");
        return 0;
    }
    if (command is not null && command != "help") return Usage($"unknown command {command}");
    Console.WriteLine(Reference());
    return 0;
}

static int Usage(string? error, string? command = null)
{
    if (error is not null) Console.Error.WriteLine(error);
    Console.Error.WriteLine(command is not null && Cli.Commands.ContainsKey(command)
        ? $"Run 'docwizz help {command}' for its options."
        : "Run 'docwizz help' for all commands and options.");
    return 1;
}

static string Reference() => $"""
    usage: docwizz <command> [dir] [options]

    start here:
      docwizz setup [dir]               detect the stack, write docwizz.yaml, analyze, generate docs, check
        [--force]                       overwrite an existing docwizz.yaml

    everyday:
      docwizz generate <dir> [out]      write Markdown docs (default <dir>/docs)
        [--html]                        also write an HTML page next to every Markdown page
        [--ai]                          draft missing summaries with a local Ollama (cached per code hash)
      docwizz analyze <dir>             documentation report
      docwizz check <dir>               report + exit 1 if thresholds fail (CI)
      docwizz check <dir> --since <ref> exit 1 only on gaps/violations introduced since <ref>
      docwizz architecture <dir>        layers, dependencies, violations; exit 1 above check thresholds
      docwizz diff <dir> [ref]          what changed vs <ref> (default: docs/.docwizz/model.json)
      docwizz diff [dir] <base> <head>  what changed between two git refs
      docwizz sbom [dir] [out]          export direct manifest dependencies as CycloneDX JSON
      docwizz remediate <dir>           package update suggestions: command or patch, impact, confidence
        [--package <name> --to <ver>]   propose this update (default: remediation.targets and version drift)
        [--validate]                    apply each in a temporary copy and run restore, build, tests
        [--since <ref>]                 say whether each touches only what changed since <ref>

    manual setup and debugging:
      docwizz init [dir]                write a starter docwizz.yaml with every default
      docwizz scan <dir> [model.json]   write the raw code model

    options:
      --profile <name|file.yaml>        documentation profile: {string.Join(", ", Profiles.Names)}, or your own file
      --format console|json             analyze/check/architecture/diff/remediate output
      --timings                         time per stage and peak memory, on stderr (benchmarks)
      --help, -h                        help for a command: docwizz <command> --help
      --version, -v                     version, commit and runtime (also: docwizz version)

    Every command, option and how they combine: CLI.md
    """;

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

// Writes the docs; returns the one-line result.
static async Task<string> Generate(string root, string outDir, Config config, bool ai, bool html = false, CodeModel? model = null)
{
    model ??= BuildModel(root, config).Model;
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
    return $"{pages.Count} pages → {outDir} (commit {model.Commit ?? "unknown"}): {changed.Count} changed" +
        (changed.Count is > 0 and <= 10 ? $" ({string.Join(", ", changed.Order())})" : "");
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
        packages = d.Packages.Select(p => new { project = p.Project, package = p.Package, ecosystem = p.Ecosystem, before = p.Before, after = p.After, kind = p.Kind,
            symbols = p.Impact.Uses.Select(u => u.Id), flows = p.Impact.Flows, tests = p.Impact.Tests, scope = p.Scope, note = Remediations.ImpactNote }),
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
    var (failures, missingSections) = CheckFailures(root, model, config, findings, arch);

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

// The `check` thresholds against one analysis: what fails, and the profile's architecture sections that are missing.
static (List<string> Failures, List<string> MissingSections) CheckFailures(string root, CodeModel model, Config config,
    List<DocumentationItem> findings, ArchitectureResult arch)
{
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
    if (arch.Cycles.Count > config.Check.MaxCycles) failures.Add($"{arch.Cycles.Count} cycles > {config.Check.MaxCycles}");
    if (complex.Count > 0) failures.Add($"complexity > {config.Check.MaxComplexity}: " +
        string.Join(", ", complex.Take(5).Select(n => $"{Generator.Display(n)} ({n.Complexity})")) + (complex.Count > 5 ? ", …" : ""));
    // Human-authored architecture sections the profile requires (docs/architecture/<name>.md).
    var missingSections = config.ArchitectureSections
        .Where(s => !File.Exists(Path.Combine(root, "docs", "architecture", $"{s}.md"))).ToList();
    if (missingSections.Count > 0) failures.Add($"missing architecture sections: {string.Join(", ", missingSections)}");
    return (failures, missingSections);
}

// First run on a repository, in a fixed order: scan (and detect the stack) → config → analyze → architecture →
// generate → check. A failed stage is reported and doesn't hide the others; scan and config are required by the rest.
// Exit 1 only when a stage fails: a failing check is the baseline to improve on, reported with what to do next.
static async Task<int> SetupCommand(string root, string? profile, bool force, bool ai, bool html)
{
    var file = Path.Combine(root, "docwizz.yaml");
    var outDir = Path.Combine(root, "docs");
    var existing = File.Exists(file) && !force;
    Config config = null!;
    CodeModel model = null!;
    Setup.Detection detection = null!;
    List<DocumentationItem>? findings = null;
    ArchitectureResult? arch = null;
    string configStatus = "";
    bool? passed = null;

    Console.WriteLine($"docwizz setup {Path.GetFullPath(root)}");
    var results = Setup.Run(
    [
        new("scan", () =>
        {
            // The repository's own docwizz.yaml when it is kept, else the built-in defaults (never one from the cwd).
            config = existing ? Config.Load(root, profile) : Config.Parse(Config.Default, root, profile);
            var (m, files) = BuildModel(root, config);
            model = m;
            var all = RepoFiles(root, config, [".github", ".circleci"]).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).ToList();
            detection = Setup.Detect(root, model, all, config);
            return $"{files.Count} source files, {model.Nodes.Count} nodes; {Setup.Stack(detection)}";
        }, Required: true),
        new("config", () =>
        {
            configStatus = existing ? "kept" : Setup.WriteConfig(root, Setup.Yaml(detection, AppVersion.Number), force);
            // The generated file only narrows layers and test globs to those files matched, so the model stands.
            if (!existing) config = Config.Load(root, profile);
            return $"{configStatus} {file}" + (existing ? " (--force to regenerate)" : $" ({detection.Layers.Count} layers, {detection.TestGlobs.Count} test globs inferred)");
        }, Required: true),
        new("analyze", () =>
        {
            findings = Analyzer.Analyze(model, config);
            return $"coverage {Analyzer.Coverage(findings):0}%, {findings.Count(Analyzer.IsCritical)} critical gaps, profile {config.Profile}";
        }),
        new("architecture", () =>
        {
            arch = Architecture.Check(model, config);
            var security = arch.Violations.Count(v => v.Rule.StartsWith("SEC-"));
            return $"{arch.Violations.Count - security} violations ({Architecture.Failing(arch.Violations, config.Check).Count - security} failing), {arch.Cycles.Count} cycles" +
                (config.Security.Enabled ? $", {security} security findings" : ", security rules off");
        }),
        new("generate", () => Generate(root, outDir, config, ai, html, model).GetAwaiter().GetResult()),
        new("check", () =>
        {
            findings ??= Analyzer.Analyze(model, config);
            arch ??= Architecture.Check(model, config);
            var (failures, _) = CheckFailures(root, model, config, findings, arch);
            passed = failures.Count == 0;
            return passed.Value ? "PASS" : "FAIL — " + string.Join("; ", failures);
        }),
    ], Console.Out);

    var failed = results.Where(r => !r.Ok).ToList();
    var rel = (string p) => "./" + Path.GetRelativePath(".", p).Replace('\\', '/');
    var outcome = Setup.Outcome(results, passed, arch);
    Console.WriteLine();
    var skipped = failed.Count(r => r.Status == "skipped");
    Console.WriteLine(outcome switch
    {
        Setup.Status.Success => "Setup complete.",
        Setup.Status.SuccessWithFindings => "Setup complete, with findings: they are about the repository; docwizz itself ran without errors.",
        _ => $"Setup incomplete: {string.Join(", ", failed.Where(r => r.Status == "failed").Select(r => r.Name))} failed" +
            (skipped > 0 ? $", {skipped} stage{(skipped == 1 ? "" : "s")} skipped." : "."),
    });
    Console.WriteLine();
    Console.WriteLine($"Status:         {Setup.Label(outcome)}");
    if (detection is not null)
    {
        Console.WriteLine($"Stack:          {Setup.Stack(detection)}");
        Console.WriteLine($"Projects:       {(detection.ProjectFiles.Count == 0 ? "none" : string.Join(", ", detection.ProjectFiles.Select(p => $"{p.Value} {p.Key}")))}");
        Console.WriteLine($"Tests:          {(detection.TestGlobs.Count == 0 && detection.TestProjects == 0 ? "none found" : string.Join(", ", detection.TestGlobs.Select(t => $"{t.Key} ({t.Value} files)").Append(detection.TestProjects > 0 ? $"{detection.TestProjects} test projects" : null).OfType<string>()))}");
        Console.WriteLine($"Deployment:     {(detection.Deployment.Count == 0 ? "none found" : string.Join(", ", detection.Deployment.Take(5)) + (detection.Deployment.Count > 5 ? $", … ({detection.Deployment.Count})" : ""))}");
        Console.WriteLine($"Layers:         {(detection.Layers.Count == 0 ? "none detected" : string.Join(", ", detection.Layers.Select(l => $"{l.Key} ({l.Value.Files})")))}");
    }
    if (configStatus.Length > 0) Console.WriteLine($"Configuration:  {rel(file)} ({configStatus})");
    if (results.Single(r => r.Name == "generate").Ok) Console.WriteLine($"Documentation:  {rel(outDir)}");
    if (findings is not null) Console.WriteLine($"Coverage:       {Analyzer.Coverage(findings):0}% (profile {config.Profile})");
    if (arch is not null) Console.WriteLine($"Architecture:   {arch.Violations.Count(v => !v.Rule.StartsWith("SEC-"))} findings, {arch.Cycles.Count} cycles");
    if (arch is not null) Console.WriteLine($"Security:       {(config.Security.Enabled ? $"{arch.Violations.Count(v => v.Rule.StartsWith("SEC-"))} findings" : "off")}");
    if (passed is not null) Console.WriteLine($"Check:          {results.Single(r => r.Name == "check").Detail}");
    foreach (var r in failed.Where(r => r.Status == "failed")) Console.WriteLine($"Error:          {r.Name}: {r.Detail}");

    if (detection is not null && configStatus.Length > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Follow-up:");
        foreach (var t in Setup.FollowUp(detection, configStatus, config, passed != false)) Console.WriteLine($"  - {t}");
    }
    Console.WriteLine();
    Console.WriteLine("Next useful actions:");
    foreach (var line in Setup.NextActions(root, outcome, existing)) Console.WriteLine(line);
    return failed.Count == 0 ? 0 : 1;
}

// Package remediation: static by default; --validate runs the repository's build and tests in a temporary copy.
static int Remediate(string root, Config config, string? package, string? to, bool validate, string? since, bool json)
{
    var model = BuildModel(root, config).Model;
    List<Remediation> list;
    try { list = Timings.Measure("remediate", () => Remediations.Plan(root, model, config, package is null ? null : (package, to!))); }
    catch (ArgumentException e)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
    if (since is not null)
    {
        if (ModelAt(root, since, config) is not { } before) return 1;
        var diff = Diff.Compare(before, model, config);
        list = [.. list.Select(r => r with { Scope = Remediations.Scope(r.Impact, diff.Added.Concat(diff.Changed), model, config) })];
    }
    if (validate)
    {
        // Explicit, and announced: validation executes the repository's build (and so its code), never in the working tree.
        Console.Error.WriteLine("docwizz: --validate runs the configured restore/build/test commands in a temporary copy of " + root);
        list = [.. list.Select(r => r.Actionable ? r with { Validation = Remediations.Validate(root, model, r, config.Remediation, Console.Error) } : r)];
    }
    if (json) Console.WriteLine(JsonSerializer.Serialize(Remediations.Json(list), JsonOptions()));
    else Remediations.Report(list, root, Console.Out);
    // Fails only when an executed check failed; suggestions alone never fail.
    return list.Any(r => r.Validation is { Pass: false }) ? 1 : 0;
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
