using System.IO.Enumeration;

class ArchitectureConfig
{
    // layer → path globs; first matching layer wins, unmatched files belong to no layer.
    public Dictionary<string, List<string>> Layers { get; set; } = [];
    // layer → layers it may depend on (`http` = calling HTTP directly). Layers not listed are unrestricted.
    public Dictionary<string, List<string>> Allow { get; set; } = [];
    // rule → high/medium/low, overriding the defaults (ARCH-001 high, ARCH-002 medium, ARCH-004 low).
    public Dictionary<string, string> Severity { get; set; } = [];
}

record ArchitectureResult(List<Violation> Violations, List<List<string>> Cycles, Dictionary<string, int> LayerFiles,
    List<LayerDependency> LayerDependencies);

// Actual dependencies between layers (`http` = calls HTTP directly), Count = number of references.
record LayerDependency(string From, string To, int Count, bool Allowed);

record Violation(string Rule, string FromLayer, string ToLayer, string FromFile, string To, string Example, Level Severity = Level.Low);

static class Architecture
{
    static readonly Dictionary<string, Level> DefaultSeverity = new()
        { ["ARCH-001"] = Level.High, ["ARCH-002"] = Level.Medium, ["ARCH-004"] = Level.Low };

    public static Level ParseSeverity(string s) => Enum.TryParse<Level>(s, true, out var l) && l != Level.None && !int.TryParse(s, out _)
        ? l : throw new ArgumentException($"unknown severity '{s}' (high, medium, low)");

    // The violations `check` counts: those at or above `check.fail_on`.
    public static List<Violation> Failing(IEnumerable<Violation> violations, CheckConfig check) =>
        violations.Where(v => v.Severity >= ParseSeverity(check.FailOn)).ToList();

    public static ArchitectureResult Check(CodeModel model, ArchitectureConfig config)
    {
        var nodes = model.Nodes.ToDictionary(n => n.Id);
        var deps = model.Edges.Where(e => CodeModel.DependencyKinds.Contains(e.Kind) && nodes.ContainsKey(e.From)).ToList();

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
                    violations.Add(new("ARCH-002", fromLayer, "http", from.File,
                        nodes.TryGetValue(e.To, out var ep) && ep.Tags is ["endpoint", var verb, ..] ? $"{verb} /{ep.Route?.TrimStart('/')}" : e.To.Replace("http:", ""),
                        $"{from.Name} calls HTTP directly"));
                continue;
            }
            if (!nodes.TryGetValue(e.To, out var to) || LayerOf(to.File) is not { } toLayer || toLayer == fromLayer) continue;
            if (allowed.Contains(toLayer)) continue;
            // Reachable through allowed layers → a layer was skipped (ARCH-004); otherwise a forbidden direction (ARCH-001).
            var via = Via(config.Allow, fromLayer, toLayer);
            violations.Add(via is null
                ? new("ARCH-001", fromLayer, toLayer, from.File, to.File, $"{Short(from.Id)} {e.Kind} {Short(to.Id)}")
                : new("ARCH-004", fromLayer, toLayer, from.File, to.File, $"{Short(from.Id)} {e.Kind} {Short(to.Id)}, bypassing {via}"));
        }

        // One finding per file pair: the first edge is the example.
        violations = violations.DistinctBy(v => (v.Rule, v.FromFile, v.To)).Select(v => v with
        {
            Severity = config.Severity.TryGetValue(v.Rule, out var s) ? ParseSeverity(s) : DefaultSeverity.GetValueOrDefault(v.Rule, Level.Medium),
        }).ToList();

        // Folder-level cycles (a folder ≈ a namespace/module in both C# and Vue projects).
        var graph = deps.Where(e => e.Kind != "http" && nodes.ContainsKey(e.To))
            .Select(e => (From: Folder(nodes[e.From].File), To: Folder(nodes[e.To].File)))
            .Where(p => p.From != p.To).Distinct()
            .ToLookup(p => p.From, p => p.To);
        var code = model.Nodes.Where(n => n.Kind is not ("project" or "package" or "external" or "config")).ToList();
        var folders = code.Select(n => Folder(n.File)).Distinct();
        var layerFiles = code.Select(n => n.File).Distinct()
            .GroupBy(f => LayerOf(f) ?? "(none)").ToDictionary(g => g.Key, g => g.Count());
        var layerDeps = deps.Select(e => (From: LayerOf(nodes[e.From].File),
                To: e.Kind == "http" ? "http" : nodes.TryGetValue(e.To, out var t) ? LayerOf(t.File) : null))
            .Where(d => d.From is not null && d.To is not null && d.From != d.To)
            .GroupBy(d => d).OrderBy(g => g.Key.From).ThenBy(g => g.Key.To)
            .Select(g => new LayerDependency(g.Key.From!, g.Key.To!, g.Count(),
                !config.Allow.TryGetValue(g.Key.From!, out var a) || a.Contains(g.Key.To!))).ToList();
        return new(violations, StronglyConnected(folders, graph).Where(c => c.Count > 1).ToList(), layerFiles, layerDeps);
    }

    // The first intermediate layer on an allowed path from → … → to, or null if `to` isn't reachable.
    static string? Via(Dictionary<string, List<string>> allow, string from, string to)
    {
        var seen = new HashSet<string> { from };
        var queue = new Queue<(string Layer, string First)>(allow[from].Where(seen.Add).Select(l => (l, l)));
        while (queue.TryDequeue(out var cur))
            foreach (var next in allow.GetValueOrDefault(cur.Layer) ?? [])
            {
                if (next == to) return cur.First;
                if (seen.Add(next)) queue.Enqueue((next, cur.First));
            }
        return null;
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
        var (violations, cycles, layerFiles, layerDeps) = r;
        o.WriteLine();
        o.WriteLine($"Architecture ({violations.Count} violations, {cycles.Count} cycles)");
        o.WriteLine(new string('─', 40));
        o.WriteLine("  layers: " + string.Join(", ", layerFiles.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
        o.WriteLine("  dependencies: " + string.Join(", ", layerDeps.Select(d => $"{d.From} → {d.To} {d.Count}{(d.Allowed ? "" : " ✗")}")));
        foreach (var g in violations.GroupBy(v => (v.Rule, v.FromLayer, v.ToLayer, v.Severity)).OrderByDescending(g => g.Key.Severity).ThenBy(g => g.Key.Rule))
        {
            o.WriteLine($"  {g.Key.Rule}  {g.Key.FromLayer} → {g.Key.ToLayer}  [{g.Key.Severity.ToString().ToLowerInvariant()}]");
            foreach (var v in g)
                o.WriteLine($"    {v.FromFile} → {v.To}  ({v.Example})");
        }
        foreach (var c in cycles)
            o.WriteLine($"  ARCH-003  cycle: {string.Join(" ↔ ", c)}");
    }
}
