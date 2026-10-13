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
    public string Name(string id) => nodes.TryGetValue(id, out var n) ? Naming.Display(n) : id.Replace("http:", "");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep source readable: no <
    };

    // name as the facts show it → the ids a citation of it rests on, for resolving a draft's citations.
    Dictionary<string, List<string>> Names(IEnumerable<string> ids, string self) =>
        ids.Where(nodes.ContainsKey).DistinctBy(Name).ToDictionary(Name, id => new List<string> { id }).Append(new("source", [self]))
            .DistinctBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value);

    // The <summary> of a written doc comment, or null when there is none (or it isn't XML).
    public static string? WrittenSummary(Node n)
    {
        try { return n.Doc is null ? null : System.Xml.Linq.XElement.Parse($"<r>{n.Doc}</r>").Descendants("summary").FirstOrDefault() is { } e ? Analyzer.Text(e) : null; }
        catch (System.Xml.XmlException) { return null; }
    }

    // A member a module overview is about: what a reader looks for, not a property, event or constructor.
    public static bool Shown(Node m) => m.Kind is not ("property" or "event" or "constructor");

    // A module's facts: its members with their docs (written, else drafted), and the modules on either side.
    public (string Json, List<string> Sources, Dictionary<string, List<string>> Names) ForModule(string folder, List<Node> members, Dictionary<string, AiProse.Draft> drafts)
    {
        var ids = members.Select(m => m.Id).ToHashSet();
        string Module(string id) => Naming.Folder(nodes[id].File);
        var deps = model.Edges.Where(e => CodeModel.DependencyKinds.Contains(e.Kind) && nodes.ContainsKey(e.From) && nodes.ContainsKey(e.To)).ToList();
        var shown = members.Where(Shown).Take(40).ToList();
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
        foreach (var m in deps.SelectMany(e => new[] { Module(e.From), Module(e.To) }).Distinct()) names.TryAdd(m, [$"module:{m}"]);
        return (json, [.. shown.Select(m => m.Id)], names);
    }

    // The facts JSON for one symbol, the symbols it mentions (a draft's provenance) and their names for citations.
    // Models cite the facts' own fields too ("parameters", "calls"): a field of the symbol's own facts rests on the
    // symbol, a list of neighbours on the neighbours in it. `business` (only with --requirements) adds the symbol's
    // historical requirements as `businessContext`; each is cited by its own id (WI-12), never by the field's name.
    public (string Json, List<string> Sources, Dictionary<string, List<string>> Names) For(Node n, DocumentationItem f, BusinessContext.Selection? business = null)
    {
        var sources = new List<string> { n.Id };
        var lists = new Dictionary<string, List<string>>();
        IEnumerable<string> Pick(string field, IEnumerable<string> ids)
        {
            var l = ids.Distinct().ToList();
            if (l.Where(nodes.ContainsKey).ToList() is { Count: > 0 } known) { sources.AddRange(known); lists[field] = known; }
            return l.Select(Name);
        }
        IEnumerable<string> To(string field, params string[] kinds) => Pick(field, Out(n.Id, kinds).Select(e => e.To));
        IEnumerable<string> From(string field, params string[] kinds) => Pick(field, In(n.Id, kinds).Select(e => e.From));

        var json = JsonSerializer.Serialize(new
        {
            symbol = Naming.Display(n),
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
            calls = To("calls", "calls"),
            httpCalls = To("httpCalls", "http"),
            injects = To("injects", "injects"),
            renders = To("renders", "renders"),
            publishes = To("publishes", "publishes"),
            calledBy = From("calledBy", "calls", "http"),
            renderedBy = From("renderedBy", "renders"),
            derived = f.Sections.Where(kv => kv.Value.Origin is Origin.Fact or Origin.Inferred)
                .ToDictionary(kv => kv.Key, kv => $"{kv.Value.Text} ({kv.Value.Origin.ToString().ToLowerInvariant()})"),
            whyItNeedsDocs = f.Reasons,
            businessContext = business is null ? null : BusinessContext.Facts(business),
            source = Source(n),
        }, Options);
        var all = sources.Concat(f.Sources).Distinct().ToList();
        var names = Names(all, n.Id);
        foreach (var e in business?.Entries ?? []) names.TryAdd(e.Ref, [$"workitem:{e.Id}"]);
        foreach (var field in JsonDocument.Parse(json).RootElement.EnumerateObject().Where(p => p.Name != "businessContext" && p.Value.ValueKind switch
            {
                JsonValueKind.Array => p.Value.GetArrayLength() > 0,
                JsonValueKind.Object => p.Value.EnumerateObject().Any(),
                JsonValueKind.String => p.Value.GetString()!.Length > 0,
                _ => true,
            }))
            names.TryAdd(field.Name, lists.GetValueOrDefault(field.Name) ?? [n.Id]);
        return (json, all, names);
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
