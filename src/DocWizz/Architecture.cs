using System.IO.Enumeration;

class ArchitectureConfig
{
    // layer → path globs; first matching layer wins, unmatched files belong to no layer.
    public Dictionary<string, List<string>> Layers { get; set; } = [];
    // layer → layers it may depend on (`http` = calling HTTP directly). Layers not listed are unrestricted.
    public Dictionary<string, List<string>> Allow { get; set; } = [];
}

record ArchitectureResult(List<Violation> Violations, List<List<string>> Cycles, Dictionary<string, int> LayerFiles);

record Violation(string Rule, string FromLayer, string ToLayer, string FromFile, string To, string Example);

static class Architecture
{
    static readonly string[] DependencyKinds = ["calls", "injects", "implements", "inherits", "imports", "renders", "routes", "dbset", "http"];

    public static ArchitectureResult Check(Model model, ArchitectureConfig config)
    {
        var nodes = model.Nodes.ToDictionary(n => n.Id);
        var deps = model.Edges.Where(e => DependencyKinds.Contains(e.Kind) && nodes.ContainsKey(e.From)).ToList();

        var layerCache = new Dictionary<string, string?>();
        string? LayerOf(string file) => layerCache.TryGetValue(file, out var l) ? l : layerCache[file] =
            config.Layers.FirstOrDefault(kv => kv.Value.Any(g => FileSystemName.MatchesSimpleExpression(g, file))).Key;

        var violations = new List<Violation>();
        foreach (var e in deps)
        {
            var from = nodes[e.From];
            var fromLayer = LayerOf(from.File);
            if (fromLayer is null || !config.Allow.TryGetValue(fromLayer, out var allowed)) continue;

            if (e.Kind == "http")
            {
                if (!allowed.Contains("http"))
                    violations.Add(new("ARCH-002", fromLayer, "http", from.File, e.To.Replace("http:", ""), Short(from.Id)));
                continue;
            }
            if (!nodes.TryGetValue(e.To, out var to) || LayerOf(to.File) is not { } toLayer || toLayer == fromLayer) continue;
            if (!allowed.Contains(toLayer))
                violations.Add(new("ARCH-001", fromLayer, toLayer, from.File, to.File, $"{Short(from.Id)} {e.Kind} {Short(to.Id)}"));
        }

        // One finding per file pair: the first edge is the example.
        violations = violations.DistinctBy(v => (v.Rule, v.FromFile, v.To)).ToList();

        // Folder-level cycles (a folder ≈ a namespace/module in both C# and Vue projects).
        var graph = deps.Where(e => e.Kind != "http" && nodes.ContainsKey(e.To))
            .Select(e => (From: Folder(nodes[e.From].File), To: Folder(nodes[e.To].File)))
            .Where(p => p.From != p.To).Distinct()
            .ToLookup(p => p.From, p => p.To);
        var folders = model.Nodes.Select(n => Folder(n.File)).Distinct();
        var layerFiles = model.Nodes.Select(n => n.File).Distinct()
            .GroupBy(f => LayerOf(f) ?? "(none)").ToDictionary(g => g.Key, g => g.Count());
        return new(violations, StronglyConnected(folders, graph).Where(c => c.Count > 1).ToList(), layerFiles);
    }

    static string Folder(string file) => Path.GetDirectoryName(file)?.Replace('\\', '/') ?? "";
    static string Short(string id) => id[(id.IndexOf(':') + 1)..].Split('(')[0];

    // Tarjan's SCC.
    static List<List<string>> StronglyConnected(IEnumerable<string> vertices, ILookup<string, string> graph)
    {
        var index = new Dictionary<string, int>();
        var low = new Dictionary<string, int>();
        var stack = new Stack<string>();
        var onStack = new HashSet<string>();
        var result = new List<List<string>>();

        void Visit(string v)
        {
            index[v] = low[v] = index.Count;
            stack.Push(v);
            onStack.Add(v);
            foreach (var w in graph[v])
            {
                if (!index.ContainsKey(w)) { Visit(w); low[v] = Math.Min(low[v], low[w]); }
                else if (onStack.Contains(w)) low[v] = Math.Min(low[v], index[w]);
            }
            if (low[v] != index[v]) return;
            var scc = new List<string>();
            string x;
            do { x = stack.Pop(); onStack.Remove(x); scc.Add(x); } while (x != v);
            result.Add(scc.Order().ToList());
        }

        foreach (var v in vertices)
            if (!index.ContainsKey(v)) Visit(v);
        return result;
    }

    public static void Report(ArchitectureResult r, TextWriter o)
    {
        var (violations, cycles, layerFiles) = r;
        o.WriteLine();
        o.WriteLine($"Architecture ({violations.Count} violations, {cycles.Count} cycles)");
        o.WriteLine(new string('─', 40));
        o.WriteLine("  layers: " + string.Join(", ", layerFiles.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
        foreach (var g in violations.GroupBy(v => (v.Rule, v.FromLayer, v.ToLayer)).OrderBy(g => g.Key.Rule))
        {
            o.WriteLine($"  {g.Key.Rule}  {g.Key.FromLayer} → {g.Key.ToLayer}");
            foreach (var v in g)
                o.WriteLine($"    {v.FromFile} → {v.To}  ({v.Example})");
        }
        foreach (var c in cycles)
            o.WriteLine($"  ARCH-003  cycle: {string.Join(" ↔ ", c)}");
    }
}
