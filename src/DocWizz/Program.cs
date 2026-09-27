using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

// ponytail: hand-rolled arg switch; move to System.CommandLine once commands grow options
var cmd = args.ElementAtOrDefault(0);
var path = args.ElementAtOrDefault(1) ?? ".";

if (cmd is "scan" or "analyze" or "check" && !Directory.Exists(path))
{
    Console.Error.WriteLine($"not a directory: {path}");
    return 1;
}

switch (cmd)
{
    case "scan":
        return Scan(path, args.ElementAtOrDefault(2) ?? "model.json");
    case "analyze":
        Analyzer.Report(Analyzer.Analyze(BuildModel(path).Model, Config.Load(path)), Console.Out);
        return 0;
    case "check":
        return Check(path);
    default:
        Console.Error.WriteLine("""
            usage:
              docwizz scan <dir> [model.json]   write the code model
              docwizz analyze <dir>             documentation report
              docwizz check <dir>               report + exit 1 if thresholds fail (CI)
            """);
        return 1;
}

static (Model Model, List<string> Files) BuildModel(string root)
{
    string[] skip = ["bin", "obj", "node_modules", ".git"];
    string[] exts = [".cs", ".vue", ".ts"];

    var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(f => exts.Contains(Path.GetExtension(f)))
        .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar).Any(skip.Contains))
        .ToList();

    var (nodes, edges) = CSharpScanner.Scan(root, files.Where(f => f.EndsWith(".cs")));
    return (new Model(GitCommit(root), nodes, edges), files);
}

static int Scan(string root, string outFile)
{
    var (model, files) = BuildModel(root);
    File.WriteAllText(outFile, JsonSerializer.Serialize(model, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    }));

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

static int Check(string root)
{
    var config = Config.Load(root);
    var findings = Analyzer.Analyze(BuildModel(root).Model, config);
    Analyzer.Report(findings, Console.Out);

    var coverage = Analyzer.Coverage(findings);
    var critical = findings.Count(Analyzer.IsCritical);
    var failures = new List<string>();
    if (coverage < config.Check.MinCoverage) failures.Add($"coverage {coverage:0}% < {config.Check.MinCoverage}%");
    if (critical > config.Check.MaxCritical) failures.Add($"{critical} critical > {config.Check.MaxCritical}");

    Console.WriteLine();
    Console.WriteLine(failures.Count == 0 ? "check: PASS" : "check: FAIL — " + string.Join("; ", failures));
    return failures.Count == 0 ? 0 : 1;
}

static string? GitCommit(string dir)
{
    try
    {
        var p = Process.Start(new ProcessStartInfo("git", "rev-parse --short HEAD")
            { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var sha = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return p.ExitCode == 0 ? sha : null;
    }
    catch { return null; }
}
