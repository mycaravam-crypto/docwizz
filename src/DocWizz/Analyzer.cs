using System.IO.Enumeration;
using System.Xml.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

class Config
{
    public Dictionary<string, Pattern> Patterns { get; set; } = [];
    public CheckConfig Check { get; set; } = new();
    public List<string> Exclude { get; set; } = [];

    public const string Default = """
        # Patterns are matched in order; the first match decides which XML doc
        # sections a symbol needs. `level` raises the requirement to at least that.
        patterns:
          endpoint:
            match: { tag: endpoint }
            level: high
            sections: [summary, param]
          controller:
            match: { tag: controller }
            level: high
            sections: [summary]
          service:
            match: { type: "*Service" }
            sections: [summary, param]
          default:
            sections: [summary]
        check:
          min_coverage: 80
          max_critical: 0
        # Path globs (relative, `/`-separated) left out of the model entirely.
        exclude: ["tests/*", "test/*", "*.Tests/*", "*.Test/*"]
        """;

    public static Config Load(string dir)
    {
        var file = new[] { Path.Combine(dir, "docwizz.yaml"), "docwizz.yaml" }.FirstOrDefault(File.Exists);
        return new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build()
            .Deserialize<Config>(file is null ? Default : File.ReadAllText(file));
    }
}

class Pattern
{
    public Match Match { get; set; } = new();
    public string? Level { get; set; }
    public List<string> Sections { get; set; } = ["summary"];
}

// Globs (`*`, `?`) against the node's name, containing type name, kind, tag, visibility.
class Match
{
    public string? Kind { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Tag { get; set; }
    public string? Visibility { get; set; }
}

class CheckConfig
{
    public double MinCoverage { get; set; } = 80;
    public int MaxCritical { get; set; }
}

enum Level { None, Low, Medium, High }
enum Status { Documented, Partial, Undocumented }

record Finding(Node Node, Level Level, Status Status, string Pattern, List<string> Missing, List<string> Reasons);

static class Analyzer
{
    static readonly string[] Analyzed = ["class", "record", "struct", "interface", "enum", "method", "endpoint"];

    public static List<Finding> Analyze(Model model, Config config)
    {
        var nodes = model.Nodes.ToDictionary(n => n.Id);
        var parent = model.Edges.Where(e => e.Kind == "contains").ToDictionary(e => e.To, e => e.From);
        var implOf = model.Edges.Where(e => e.Kind == "implements").ToLookup(e => e.From, e => e.To); // impl → interface
        var implBy = model.Edges.Where(e => e.Kind == "implements").ToLookup(e => e.To, e => e.From); // interface → impl
        var calls = model.Edges.Where(e => e.Kind == "calls").ToLookup(e => e.From, e => e.To);
        var callers = model.Edges.Where(e => e.Kind == "calls").ToLookup(e => e.To, e => e.From);
        var injects = model.Edges.Where(e => e.Kind == "injects").ToLookup(e => e.From, e => e.To);
        var inbound = model.Edges.Where(e => e.Kind is "calls" or "injects").ToLookup(e => e.To, e => e.From);

        var effectsCache = new Dictionary<string, HashSet<string>>();

        // ponytail: side effects by naming heuristics (DbContext injection, Publish*/Send* calls); swap for symbol-based rules if noisy.
        HashSet<string> Effects(string id)
        {
            if (effectsCache.TryGetValue(id, out var cached)) return cached;
            var found = effectsCache[id] = [];
            if (!nodes.TryGetValue(id, out var n)) return found;
            var injected = parent.TryGetValue(id, out var owner) ? injects[owner].Concat(injects[id]) : injects[id];
            if (injected.Any(t => nodes.GetValueOrDefault(t)?.Tags?.Contains("dbcontext") == true))
                found.Add("db");
            if (n.Name.StartsWith("Publish") || n.Name.StartsWith("Send")) found.Add("event");
            foreach (var next in calls[id].Concat(implBy[id]))
                found.UnionWith(Effects(next));
            return found;
        }

        var findings = new List<Finding>();
        foreach (var node in model.Nodes.Where(n => Analyzed.Contains(n.Kind)))
        {
            var typeName = parent.TryGetValue(node.Id, out var p) ? nodes[p].Name : null;
            var (patternName, pattern) = config.Patterns.FirstOrDefault(kv => Matches(kv.Value.Match, node, typeName));
            pattern ??= new Pattern();

            var reasons = new List<string>();
            var score = 0;
            if (node.Visibility is "public" or "protected") { score += 2; reasons.Add(node.Visibility); }
            if (node.Complexity >= 15) { score += 3; reasons.Add($"complexity {node.Complexity}"); }
            else if (node.Complexity >= 8) { score += 2; reasons.Add($"complexity {node.Complexity}"); }
            else if (node.Complexity >= 4) { score += 1; reasons.Add($"complexity {node.Complexity}"); }
            if (node.Params >= 4) { score += 1; reasons.Add($"{node.Params} params"); }
            var fanIn = inbound[node.Id].Concat(implOf[node.Id].SelectMany(i => inbound[i])).Distinct().Count();
            if (fanIn >= 3) { score += 1; reasons.Add($"{fanIn} callers"); }
            if (node.Kind is "method" or "endpoint" && Effects(node.Id) is { Count: > 0 } fx)
            {
                score += 1;
                reasons.Add("side effects: " + string.Join(", ", fx.Order()));
            }

            // Tuned on a real repo: public + one weak signal (e.g. touches the DB) stays Low.
            var level = score >= 6 ? Level.High : score >= 4 ? Level.Medium : score >= 1 ? Level.Low : Level.None;
            if (Enum.TryParse<Level>(pattern.Level, true, out var min) && min > level)
            {
                level = min;
                reasons.Add($"pattern {patternName}");
            }
            if (level < Level.Medium) continue;

            // An interface member's docs cover its implementations.
            var doc = node.Doc ?? implOf[node.Id].Select(i => nodes.GetValueOrDefault(i)?.Doc).FirstOrDefault(d => d is not null);
            var sections = pattern.Sections.Where(s => s != "param" || node.Params > 0).ToList();
            var missing = Missing(doc, sections, node.Params ?? 0);
            var status = missing.Count == 0 ? Status.Documented
                : missing.Count == sections.Count ? Status.Undocumented : Status.Partial;
            findings.Add(new(node, level, status, patternName ?? "", missing, reasons));
        }
        return findings;
    }

    static bool Matches(Match m, Node n, string? typeName) =>
        Glob(m.Kind, n.Kind) && Glob(m.Name, n.Name) && Glob(m.Type, typeName) && Glob(m.Visibility, n.Visibility)
        && (m.Tag is null || n.Tags?.Any(t => Glob(m.Tag, t)) == true);

    static bool Glob(string? pattern, string? value) =>
        pattern is null || (value is not null && FileSystemName.MatchesSimpleExpression(pattern, value));

    static List<string> Missing(string? doc, List<string> sections, int paramCount)
    {
        if (doc is null) return [.. sections];
        XElement xml;
        try { xml = XElement.Parse(doc); }
        catch { return [.. sections]; }
        if (xml.Element("inheritdoc") is not null) return [];

        return sections.Where(s => s == "param"
            ? xml.Elements("param").Count(e => !string.IsNullOrWhiteSpace(e.Value)) < paramCount
            : string.IsNullOrWhiteSpace(xml.Element(s)?.Value)).ToList();
    }

    public static double Coverage(IEnumerable<Finding> fs) =>
        fs.Any() ? 100 * fs.Sum(f => f.Status switch { Status.Documented => 1, Status.Partial => 0.5, _ => 0 }) / fs.Count() : 100;

    public static void Report(List<Finding> findings, TextWriter o)
    {
        var cov = Coverage(findings);
        var bar = (int)Math.Round(cov / 5);
        o.WriteLine($"Documentation  {new string('█', bar)}{new string('░', 20 - bar)} {cov:0}%");
        o.WriteLine();
        foreach (var g in findings.GroupBy(f => f.Pattern).OrderBy(g => g.Key))
            o.WriteLine($"  {(g.Key == "" ? "(none)" : g.Key),-16} {Coverage(g),4:0}%  ({g.Count()})");

        Section("Critical", findings.Where(IsCritical));
        Section("Warnings", findings.Where(f => f.Level == Level.Medium && f.Status != Status.Documented));

        o.WriteLine();
        o.WriteLine($"{findings.Count(f => f.Status == Status.Documented)} documented, " +
                    $"{findings.Count(f => f.Status == Status.Partial)} partial, " +
                    $"{findings.Count(f => f.Status == Status.Undocumented)} undocumented");

        void Section(string title, IEnumerable<Finding> fs)
        {
            var list = fs.ToList();
            o.WriteLine();
            o.WriteLine($"{title} ({list.Count})");
            o.WriteLine(new string('─', 40));
            foreach (var f in list)
            {
                o.WriteLine($"  {f.Node.File}:{f.Node.Line}  {f.Node.Id[3..]}");
                o.WriteLine($"    {f.Status.ToString().ToLowerInvariant()}, missing: {string.Join(", ", f.Missing)}  [{string.Join("; ", f.Reasons)}]");
            }
        }
    }

    public static bool IsCritical(Finding f) => f.Level == Level.High && f.Status != Status.Documented;
}
