using System.Text.Json;
using System.Text.RegularExpressions;

// Configuration keys defined in appsettings*.json and .env files, as `config` nodes (`config:<key, lowercase>`).
// Code that reads or binds a key points at it (`reads`, `binds`; see CSharpScanner). Values are never kept — they may be
// secrets — except the host of a URL value (`url:<environment>=<host>` tag), which names the external system a key points to.
// Tags: `env:<environment>@<file>` per file that defines the key (appsettings.json → default, appsettings.Production.json → Production).
static class Configuration
{
    public static bool IsConfigFile(string file) =>
        Path.GetFileName(file) is var n && (n == ".env" || n.EndsWith(".env") || Regex.IsMatch(n, @"^appsettings(\.[\w-]+)?\.json$", RegexOptions.IgnoreCase));

    static string Environment(string file) =>
        Regex.Match(Path.GetFileName(file), @"^appsettings\.([\w-]+)\.json$", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value
            : file.EndsWith(".json") ? "default" : Path.GetFileName(file);

    public static string Id(string key) => $"config:{key.ToLowerInvariant()}";

    public static List<Node> Scan(string root, IEnumerable<string> files)
    {
        var keys = new Dictionary<string, Node>();
        void Define(string key, string rel, int line, string env, string? value)
        {
            var tags = new List<string> { $"env:{env}@{rel}" };
            if (Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme is "http" or "https") tags.Add($"url:{env}={u.Host}");
            keys[Id(key)] = keys.TryGetValue(Id(key), out var n) ? n with { Tags = [.. n.Tags!.Union(tags)] }
                : new Node(Id(key), "config", key, rel, line, Tags: tags);
        }

        // appsettings.json first: a key's location is where its default is defined.
        foreach (var file in files.OrderBy(f => Environment(f) != "default").ThenBy(f => f))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var env = Environment(file);
            try
            {
                var lines = File.ReadAllLines(file);
                if (file.EndsWith(".json"))
                {
                    // Keys come in document order, so each one's line is the next line that names it.
                    var next = 0;
                    void Walk(JsonElement e, string prefix)
                    {
                        foreach (var p in e.EnumerateObject())
                        {
                            var key = prefix.Length == 0 ? p.Name : $"{prefix}:{p.Name}";
                            var at = next < lines.Length ? Array.FindIndex(lines, next, l => l.Contains($"\"{p.Name}\"")) : -1;
                            if (at >= 0) next = at; // nested keys may share the line
                            if (p.Value.ValueKind == JsonValueKind.Object) Walk(p.Value, key);
                            else Define(key, rel, next + 1, env, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null);
                        }
                    }
                    using var doc = JsonDocument.Parse(string.Join('\n', lines),
                        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    if (doc.RootElement.ValueKind == JsonValueKind.Object) Walk(doc.RootElement, "");
                }
                else
                    // KEY=value; ASP.NET reads A__B as A:B.
                    for (var i = 0; i < lines.Length; i++)
                        if (Regex.Match(lines[i], @"^\s*(?:export\s+)?([A-Za-z_][\w.]*)\s*=\s*(.*)$") is { Success: true } m)
                            Define(m.Groups[1].Value.Replace("__", ":"), rel, i + 1, env, m.Groups[2].Value.Trim().Trim('"', '\''));
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                Console.Error.WriteLine($"docwizz: skipped {rel}: {e.Message}");
            }
        }
        return [.. keys.Values];
    }

    // Sections ASP.NET or common libraries read without application code asking for them.
    static readonly string[] FrameworkSections = ["Logging", "AllowedHosts", "Kestrel", "Urls", "Serilog", "DetailedErrors", "HostFilteringOptions"];

    // (environment, file that defines the key for it)
    public static IEnumerable<(string Env, string File)> Definitions(Node key) => (key.Tags ?? []).Where(t => t.StartsWith("env:"))
        .Select(t => t[4..].Split('@', 2)).Select(p => (p[0], p.ElementAtOrDefault(1) ?? key.File));
    public static IEnumerable<string> Environments(Node key) => Definitions(key).Select(d => d.Env);
    // environment → host of a URL value; the default environment's host names the system.
    public static Dictionary<string, string> UrlHosts(Node key) => (key.Tags ?? []).Where(t => t.StartsWith("url:"))
        .Select(t => t[4..].Split('=', 2)).DistinctBy(p => p[0]).ToDictionary(p => p[0], p => p[1]);
    public static string? UrlHost(Node key) => UrlHosts(key) is var h && h.Count > 0 ? h.GetValueOrDefault("default") ?? h.Values.First() : null;
    public static bool Defined(Node key) => Environments(key).Any();

    // A key read in code counts as defined when a file defines it or anything below it (reading a section).
    public static bool DefinedFor(Node read, IEnumerable<Node> keys) => Defined(read)
        || keys.Any(k => Defined(k) && k.Name.StartsWith(read.Name + ":", StringComparison.OrdinalIgnoreCase));

    public static bool IsFramework(Node key) => FrameworkSections.Any(s => key.Name.Equals(s, StringComparison.OrdinalIgnoreCase)
        || key.Name.StartsWith(s + ":", StringComparison.OrdinalIgnoreCase));

    // After scanning: a typed HttpClient whose address comes from a key with a URL value is named after that host.
    public static void Link(List<Node> nodes, List<Edge> edges)
    {
        // Reads in top-level statements belong to the file's project.
        var projects = nodes.Where(n => n.Kind == "project").ToList();
        for (var i = edges.Count - 1; i >= 0; i--)
            if (edges[i].From.StartsWith("file:"))
            {
                if (global::Projects.Of(projects, edges[i].From[5..]) is { } p) edges[i] = edges[i] with { From = p.Id };
                else edges.RemoveAt(i);
            }

        var byId = nodes.Select((n, i) => (n, i)).ToDictionary(x => x.n.Id, x => x.i);
        foreach (var connect in edges.Where(e => e.Kind == "connects" && e.To.StartsWith("ext:http:")))
        {
            if (!byId.TryGetValue(connect.To, out var ei) || !nodes[ei].Name.StartsWith("HTTP API used by ")) continue;
            var host = edges.Where(e => e.Kind == "reads" && e.From == connect.From && byId.ContainsKey(e.To))
                .Select(e => UrlHost(nodes[byId[e.To]])).FirstOrDefault(h => h is not null);
            if (host is not null) nodes[ei] = nodes[ei] with { Name = host };
        }
    }
}
