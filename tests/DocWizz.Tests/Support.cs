namespace DocWizz.Tests;

// Source snippets written to a scratch directory, for scanner tests: `using var src = new Sources(("A.cs", "..."));`
sealed class Sources : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("docwizz-test-").FullName;
    public List<string> Files { get; } = [];

    public Sources(params (string Path, string Text)[] files)
    {
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
            Files.Add(full);
        }
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

// In-memory code models for analysis tests: nodes by id, file defaulting to a path per id.
static class Models
{
    public static Node N(string id, string file, string kind = "class", string? doc = null, string? hash = null,
        List<string>? tags = null, List<string>? parameters = null, string visibility = "public", string? route = null) =>
        new(id, kind, id[(id.IndexOf(':') + 1)..].Split('(')[0].Split('.').Last(), file, 1, visibility, doc,
            Hash: hash, Tags: tags, Parameters: parameters, Route: route);

    public static Edge E(string from, string to, string kind = "calls", string? label = null) => new(from, to, kind, label);

    public static CodeModel Model(IEnumerable<Node> nodes, IEnumerable<Edge>? edges = null) => new(null, [.. nodes], [.. edges ?? []]);

    public static string Doc(string summary) => $"<member><summary>{summary}</summary></member>";

    // The built-in defaults (layers, allow rules, profile). Written out rather than left to Config.Load's fallback,
    // which would pick up a docwizz.yaml in the working directory.
    public static Config Config() => Config(global::Config.Default);

    // The default config with `architecture.rules` (YAML, indented under `rules:`) and optionally `architecture.severity`.
    public static Config WithRules(string rules, string severity = "{}") => Config(System.Text.RegularExpressions.Regex.Replace(
        global::Config.Default, @"(?m)^  severity: \{\}.*$", $"  severity: {severity}\n  rules:\n" + string.Join("\n", rules.Split('\n').Select(l => "    " + l))));

    // Default config with its YAML replaced, for rules under test.
    public static Config Config(string yaml)
    {
        var dir = Directory.CreateTempSubdirectory("docwizz-cfg-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "docwizz.yaml"), yaml);
            return global::Config.Load(dir.FullName);
        }
        finally { dir.Delete(recursive: true); }
    }
}
