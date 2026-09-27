using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Enumeration;
using System.Text.Json;
using System.Text.Json.Serialization;

// ponytail: hand-rolled arg switch; move to System.CommandLine once commands grow options
var ai = args.Contains("--ai");
args = args.Where(a => a != "--ai").ToArray();
var cmd = args.ElementAtOrDefault(0);
var path = args.ElementAtOrDefault(1) ?? ".";

if (cmd is "scan" or "analyze" or "check" or "generate" or "diff" && !Directory.Exists(path))
{
    Console.Error.WriteLine($"not a directory: {path}");
    return 1;
}

switch (cmd)
{
    case "scan":
        return Scan(path, args.ElementAtOrDefault(2) ?? "model.json");
    case "analyze":
        return Analyze(path, enforce: false);
    case "check" when args.ElementAtOrDefault(2) == "--since" && args.ElementAtOrDefault(3) is { } since:
        return DiffCommand(path, since, enforce: true);
    case "check":
        return Analyze(path, enforce: true);
    case "diff":
        return DiffCommand(path, args.ElementAtOrDefault(2), enforce: false);
    case "generate":
        return await Generate(path, args.ElementAtOrDefault(2) ?? Path.Combine(path, "docs"), ai);
    default:
        Console.Error.WriteLine("""
            usage:
              docwizz scan <dir> [model.json]   write the code model
              docwizz analyze <dir>             documentation report
              docwizz check <dir>               report + exit 1 if thresholds fail (CI)
              docwizz check <dir> --since <ref> exit 1 only on gaps/violations introduced since <ref>
              docwizz diff <dir> [ref]          what changed vs <ref> (default: docs/.docwizz/model.json)
              docwizz generate <dir> [out]      write Markdown docs (default <dir>/docs)
                [--ai]                          draft missing summaries with Claude (cached per code hash)
            """);
        return 1;
}

static (Model Model, List<string> Files) BuildModel(string root, Config config)
{
    string[] skip = ["bin", "obj", "node_modules", "dist"];
    string[] exts = [".cs", ".vue", ".ts"];

    // Prefer git's view (honours .gitignore, skips nested worktrees); fall back to a directory walk.
    var candidates = Git(root, "ls-files --cached --others --exclude-standard")?
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(f => Path.Combine(root, f)).Where(File.Exists)
        ?? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
    var files = candidates
        .Where(f => exts.Contains(Path.GetExtension(f)) && !f.EndsWith(".d.ts"))
        .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar).SkipLast(1)
            .Any(d => d.StartsWith('.') || skip.Contains(d)))
        .Where(f => !config.Exclude.Any(g =>
            FileSystemName.MatchesSimpleExpression(g, Path.GetRelativePath(root, f).Replace('\\', '/'))))
        .Distinct()
        .ToList();

    var (nodes, edges) = CSharpScanner.Scan(root, files.Where(f => f.EndsWith(".cs")));
    var (feNodes, feEdges) = Frontend.Scan(root, files.Where(f => !f.EndsWith(".cs")).ToList());
    nodes.AddRange(feNodes.Where(n => nodes.All(x => x.Id != n.Id)));
    edges = Frontend.LinkHttp(nodes, [.. edges, .. feEdges]);
    return (new Model(Git(root, "rev-parse --short HEAD")?.Trim(), nodes, edges), files);
}

static int Scan(string root, string outFile)
{
    var (model, files) = BuildModel(root, Config.Load(root));
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

static void WriteModel(Model model, string file) =>
    File.WriteAllText(file, JsonSerializer.Serialize(model, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    }));

static async Task<int> Generate(string root, string outDir, bool ai)
{
    var config = Config.Load(root);
    var model = BuildModel(root, config).Model;
    var findings = Analyzer.Analyze(model, config);
    var arch = Architecture.Check(model, config.Architecture);
    // Cached drafts are always used; new ones are only requested with --ai.
    var drafts = await AiProse.Summaries(root, model, findings, Path.Combine(outDir, ".docwizz", "ai-cache.json"), ai);
    var written = new Generator(root, outDir, model, findings, arch, config.Architecture, drafts).Run();

    // Fingerprint for `docwizz diff`: the model these docs were generated from.
    Directory.CreateDirectory(Path.Combine(outDir, ".docwizz"));
    WriteModel(model, Path.Combine(outDir, ".docwizz", "model.json"));
    Console.WriteLine($"{written.Count} pages → {outDir} (commit {model.Commit ?? "unknown"})");
    return 0;
}

static int DiffCommand(string root, string? gitRef, bool enforce)
{
    var config = Config.Load(root);
    Model? before;
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
        before = JsonSerializer.Deserialize<Model>(File.ReadAllText(fingerprint), new JsonSerializerOptions(JsonSerializerDefaults.Web));
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
static Model? ModelAt(string root, string gitRef, Config config)
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

static int Analyze(string root, bool enforce)
{
    var config = Config.Load(root);
    var model = BuildModel(root, config).Model;
    var findings = Analyzer.Analyze(model, config);
    Analyzer.Report(findings, Console.Out);
    var arch = Architecture.Check(model, config.Architecture);
    Architecture.Report(arch, Console.Out);
    if (!enforce) return 0;
    var (violations, cycles, _) = arch;

    var coverage = Analyzer.Coverage(findings);
    var critical = findings.Count(Analyzer.IsCritical);
    var failures = new List<string>();
    if (coverage < config.Check.MinCoverage) failures.Add($"coverage {coverage:0}% < {config.Check.MinCoverage}%");
    if (critical > config.Check.MaxCritical) failures.Add($"{critical} critical > {config.Check.MaxCritical}");
    if (violations.Count > config.Check.MaxViolations) failures.Add($"{violations.Count} violations > {config.Check.MaxViolations}");
    if (cycles.Count > config.Check.MaxCycles) failures.Add($"{cycles.Count} cycles > {config.Check.MaxCycles}");

    Console.WriteLine();
    Console.WriteLine(failures.Count == 0 ? "check: PASS" : "check: FAIL — " + string.Join("; ", failures));
    return failures.Count == 0 ? 0 : 1;
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
