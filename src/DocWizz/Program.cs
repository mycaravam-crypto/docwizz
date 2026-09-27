using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

// ponytail: hand-rolled arg switch; move to System.CommandLine once commands grow options
var cmd = args.ElementAtOrDefault(0);
var path = args.ElementAtOrDefault(1) ?? ".";
var outFile = args.ElementAtOrDefault(2) ?? "model.json";

switch (cmd)
{
    case "scan":
        return Scan(path, outFile);
    default:
        Console.Error.WriteLine("usage: docwizz scan <dir> [model.json]");
        return 1;
}

static int Scan(string root, string outFile)
{
    if (!Directory.Exists(root))
    {
        Console.Error.WriteLine($"not a directory: {root}");
        return 1;
    }

    string[] skip = ["bin", "obj", "node_modules", ".git"];
    string[] exts = [".cs", ".vue", ".ts"];

    var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(f => exts.Contains(Path.GetExtension(f)))
        .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar).Any(skip.Contains))
        .ToList();

    var (nodes, edges) = CSharpScanner.Scan(root, files.Where(f => f.EndsWith(".cs")));
    var model = new Model(GitCommit(root), nodes, edges);
    File.WriteAllText(outFile, JsonSerializer.Serialize(model, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    }));

    Console.WriteLine($"Files  {files.Count}");
    foreach (var g in files.GroupBy(Path.GetExtension).OrderBy(g => g.Key))
        Console.WriteLine($"  {g.Key,-5} {g.Count()}");
    Console.WriteLine($"Nodes  {nodes.Count}");
    foreach (var g in nodes.GroupBy(n => n.Kind).OrderBy(g => g.Key))
        Console.WriteLine($"  {g.Key,-12} {g.Count()}");
    Console.WriteLine($"Edges  {edges.Count}");
    Console.WriteLine($"→ {outFile}");
    return 0;
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
