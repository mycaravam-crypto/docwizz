using System.Text.Json;

// The graph around a symbol or module, as facts: its neighbours by edge kind, names as the docs show them, its own
// source. One implementation for both consumers: AI drafting (AiProse sends the facts JSON to a local model) and agent
// context packages (AgentContext hands the same neighbours to a coding agent), so the two never disagree on what a
// symbol is connected to.
class ContextBuilder(string root, CodeModel model)
{
    const int MaxSourceLines = 150;

    readonly Dictionary<string, Node> nodes = model.Nodes.ToDictionary(n => n.Id);
    readonly ILookup<string, Edge> outgoing = model.Edges.ToLookup(e => e.From);
    readonly ILookup<string, Edge> incoming = model.Edges.ToLookup(e => e.To);

    public CodeModel Model => model;
    public IReadOnlyDictionary<string, Node> Nodes => nodes;

    // Edges leaving / reaching a symbol, optionally of the given kinds only.
    public IEnumerable<Edge> Out(string id, params string[] kinds) => kinds.Length == 0 ? outgoing[id] : outgoing[id].Where(e => kinds.Contains(e.Kind));
    public IEnumerable<Edge> In(string id, params string[] kinds) => kinds.Length == 0 ? incoming[id] : incoming[id].Where(e => kinds.Contains(e.Kind));

    // A symbol as the facts and pages name it: its id without the scanner prefix; an unresolved HTTP call as its URL.
    public string Name(string id) => nodes.TryGetValue(id, out var n) ? Generator.Display(n) : id.Replace("http:", "");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep source readable: no <
    };

    // name as the facts show it → id, for resolving a draft's citations.
    Dictionary<string, string> Names(IEnumerable<string> ids, string self) =>
        ids.Where(nodes.ContainsKey).DistinctBy(Name).ToDictionary(Name, id => id).Append(new("source", self))
            .DistinctBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value);

    // The <summary> of a written doc comment, or null when there is none (or it isn't XML).
    public static string? WrittenSummary(Node n)
    {
        try { return n.Doc is null ? null : System.Xml.Linq.XElement.Parse($"<r>{n.Doc}</r>").Descendants("summary").FirstOrDefault() is { } e ? Analyzer.Text(e) : null; }
        catch (System.Xml.XmlException) { return null; }
    }

    // A module's facts: its members with their docs (written, else drafted), and the modules on either side.
    public (string Json, List<string> Sources, Dictionary<string, string> Names) ForModule(string folder, List<Node> members, Dictionary<string, AiProse.Draft> drafts)
    {
        var ids = members.Select(m => m.Id).ToHashSet();
        string Module(string id) => Generator.Folder(nodes[id].File);
        var deps = model.Edges.Where(e => CodeModel.DependencyKinds.Contains(e.Kind) && nodes.ContainsKey(e.From) && nodes.ContainsKey(e.To)).ToList();
        var shown = members.Where(m => m.Kind is not ("property" or "event" or "constructor")).Take(40).ToList();
        var json = JsonSerializer.Serialize(new
        {
            module = folder,
            members = shown.Select(m => new
            {
                name = Name(m.Id), kind = m.Kind, route = m.Route,
                docs = WrittenSummary(m) ?? drafts.GetValueOrDefault(m.Id)?.Text,
            }),
            dependsOn = deps.Where(e => ids.Contains(e.From) && !ids.Contains(e.To)).Select(e => Module(e.To)).Where(f => f != folder).Distinct().Order(),
            usedBy = deps.Where(e => ids.Contains(e.To) && !ids.Contains(e.From)).Select(e => Module(e.From)).Where(f => f != folder).Distinct().Order(),
            externalSystems = model.Edges.Where(e => e.Kind == "connects" && ids.Contains(e.From)).Select(e => nodes.GetValueOrDefault(e.To)?.Name).OfType<string>().Distinct(),
        }, Options);
        var names = Names(shown.Select(m => m.Id), shown.FirstOrDefault()?.Id ?? folder);
        foreach (var m in deps.SelectMany(e => new[] { Module(e.From), Module(e.To) }).Distinct()) names.TryAdd(m, $"module:{m}");
        return (json, [.. shown.Select(m => m.Id)], names);
    }

    // The facts JSON for one symbol, the symbols it mentions (a draft's provenance) and their names for citations.
    public (string Json, List<string> Sources, Dictionary<string, string> Names) For(Node n, DocumentationItem f)
    {
        var sources = new List<string> { n.Id };
        IEnumerable<string> Pick(IEnumerable<string> ids) { var l = ids.Distinct().ToList(); sources.AddRange(l.Where(nodes.ContainsKey)); return l.Select(Name); }
        IEnumerable<string> To(params string[] kinds) => Pick(Out(n.Id, kinds).Select(e => e.To));
        IEnumerable<string> From(params string[] kinds) => Pick(In(n.Id, kinds).Select(e => e.From));

        var json = JsonSerializer.Serialize(new
        {
            symbol = Generator.Display(n),
            kind = n.Kind,
            file = n.File,
            route = n.Route is null ? null : $"{n.Tags?.ElementAtOrDefault(1)} {n.Route}".Trim(),
            visibility = n.Visibility,
            complexity = n.Complexity,
            parameters = n.Parameters,
            returns = n.Returns,
            throws = n.Throws,
            emits = n.Events,
            existingDocs = n.Doc,
            calls = To("calls"),
            httpCalls = To("http"),
            injects = To("injects"),
            renders = To("renders"),
            publishes = To("publishes"),
            calledBy = From("calls", "http"),
            renderedBy = From("renders"),
            derived = f.Sections.Where(kv => kv.Value.Origin is Origin.Fact or Origin.Inferred)
                .ToDictionary(kv => kv.Key, kv => $"{kv.Value.Text} ({kv.Value.Origin.ToString().ToLowerInvariant()})"),
            whyItNeedsDocs = f.Reasons,
            source = Source(n),
        }, Options);
        var all = sources.Concat(f.Sources).Distinct().ToList();
        return (json, all, Names(all, n.Id));
    }

    // The symbol's own source lines (at most MaxSourceLines), or null when the file isn't there.
    string? Source(Node n)
    {
        var path = Path.Combine(root, n.File);
        if (!File.Exists(path)) return null;
        var end = Math.Min(n.EndLine ?? n.Line + MaxSourceLines, n.Line + MaxSourceLines);
        return string.Join('\n', File.ReadLines(path).Skip(n.Line - 1).Take(end - n.Line + 1));
    }
}
