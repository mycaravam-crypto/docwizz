using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

// Configuration keys defined in appsettings*.json and .env files, as `config` nodes (`config:<key, lowercase>`).
// Code that reads or binds a key points at it (`reads`, `binds`; see CSharpScanner). Values are never kept — they may be
// secrets — except the host of a URL value (`url:<environment>=<host>` tag), which names the external system a key points to.
// Tags: `env:<environment>@<file>` per file that defines the key (appsettings.json → default, appsettings.Production.json → Production,
// a Kubernetes ConfigMap → configmap/<name>, a Helm chart's values.yaml → helm, values-prod.yaml → helm-prod).
static class Configuration
{
    public static bool IsConfigFile(string file) =>
        Path.GetFileName(file) is var n && (n == ".env" || n.EndsWith(".env") || Regex.IsMatch(n, @"^appsettings(\.[\w-]+)?\.json$", RegexOptions.IgnoreCase)
            || IsSpring(file) || IsHelmValues(file) || IsConfigMap(file));

    // Spring Boot: application.yml / application-prod.properties; keys are dotted (server.port).
    static bool IsSpring(string file) => Regex.IsMatch(Path.GetFileName(file), @"^application(-[\w-]+)?\.(ya?ml|properties)$");

    static bool IsYaml(string file) => file.EndsWith(".yaml") || file.EndsWith(".yml");
    static bool IsHelmValues(string file) => Regex.IsMatch(Path.GetFileName(file), @"^values([.-][\w-]+)?\.ya?ml$")
        && File.Exists(Path.Combine(Path.GetDirectoryName(file) ?? "", "Chart.yaml"));
    static bool IsConfigMap(string file) => IsYaml(file) && File.ReadLines(file).Take(500).Any(l => l.TrimEnd() == "kind: ConfigMap");

    static string Environment(string file) =>
        Regex.Match(Path.GetFileName(file), @"^appsettings\.([\w-]+)\.json$", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value
            : IsSpring(file) ? Regex.Match(Path.GetFileName(file), @"^application-([\w-]+)\.") is { Success: true } sp ? sp.Groups[1].Value : "default"
            : IsYaml(file) ? Regex.Match(Path.GetFileName(file), @"^values[.-]([\w-]+)\.ya?ml$") is { Success: true } v ? $"helm-{v.Groups[1].Value}" : "helm"
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
        foreach (var file in files.OrderBy(f => Environment(f) != "default").ThenBy(f => IsYaml(f)).ThenBy(f => f))
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
                else if (IsSpring(file) && file.EndsWith(".properties"))
                {
                    for (var i = 0; i < lines.Length; i++)
                        if (Regex.Match(lines[i], @"^\s*([\w.\-\[\]]+)\s*[=:]\s*(.*)$") is { Success: true } m && !lines[i].TrimStart().StartsWith('#'))
                            Define(m.Groups[1].Value, rel, i + 1, env, m.Groups[2].Value.Trim());
                }
                else if (IsSpring(file))
                {
                    var yaml = new YamlStream();
                    yaml.Load(new StringReader(string.Join('\n', lines)));
                    void Walk(YamlNode n, string prefix)
                    {
                        if (n is YamlMappingNode m)
                            foreach (var (k, v) in m.Children)
                            {
                                var key = prefix.Length == 0 ? Scalar(k) : $"{prefix}.{Scalar(k)}";
                                if (v is YamlMappingNode) Walk(v, key!);
                                else Define(key!, rel, (int)k.Start.Line, env, Scalar(v));
                            }
                    }
                    foreach (var doc in yaml.Documents) Walk(doc.RootNode, "");
                }
                else if (IsYaml(file))
                {
                    var yaml = new YamlStream();
                    yaml.Load(new StringReader(string.Join('\n', lines)));
                    foreach (var doc in yaml.Documents)
                        if (Scalar(Child(doc.RootNode, "kind")) == "ConfigMap")
                        {
                            // data: { Key__Sub: value } → Key:Sub, for the workloads that mount it.
                            var name = Scalar(Child(Child(doc.RootNode, "metadata"), "name")) ?? "?";
                            if (Child(doc.RootNode, "data") is YamlMappingNode data)
                                foreach (var (k, v) in data.Children)
                                    if (Scalar(k) is { } key) Define(key.Replace("__", ":"), rel, (int)k.Start.Line, $"configmap/{name}", Scalar(v));
                        }
                        else
                            // Helm values: the environment variables the chart passes to its containers, wherever they sit
                            // (env / extraEnv / envVars / environment, as KEY: value or [{ name, value }]).
                            // ponytail: other values are chart-specific settings, not application keys; map them when a chart's template is read.
                            foreach (var envBlock in Descendants(doc.RootNode).Where(p => Scalar(p.Key) is "env" or "extraEnv" or "envVars" or "environment"))
                                foreach (var (k, v, line) in envBlock.Value switch
                                {
                                    YamlMappingNode m => m.Children.Select(c => (Scalar(c.Key), Scalar(c.Value), (int)c.Key.Start.Line)),
                                    YamlSequenceNode sq => sq.Children.Select(c => (Scalar(Child(c, "name")), Scalar(Child(c, "value")), (int)c.Start.Line)),
                                    _ => [],
                                })
                                    if (k is not null) Define(k.Replace("__", ":"), rel, line, env, v);
                }
                else
                    // KEY=value; ASP.NET reads A__B as A:B.
                    for (var i = 0; i < lines.Length; i++)
                        if (Regex.Match(lines[i], @"^\s*(?:export\s+)?([A-Za-z_][\w.]*)\s*=\s*(.*)$") is { Success: true } m)
                            Define(m.Groups[1].Value.Replace("__", ":"), rel, i + 1, env, m.Groups[2].Value.Trim().Trim('"', '\''));
            }
            catch (Exception e) when (e is JsonException or IOException or YamlDotNet.Core.YamlException)
            {
                Console.Error.WriteLine($"docwizz: skipped {rel}: {e.Message}");
            }
        }
        return [.. keys.Values];
    }

    static YamlNode? Child(YamlNode? n, string key) => n is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;
    static string? Scalar(YamlNode? n) => (n as YamlScalarNode)?.Value;
    static IEnumerable<KeyValuePair<YamlNode, YamlNode>> Descendants(YamlNode n) => n switch
    {
        YamlMappingNode m => m.Children.SelectMany(c => Descendants(c.Value).Prepend(c)),
        YamlSequenceNode s => s.Children.SelectMany(Descendants),
        _ => [],
    };

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
