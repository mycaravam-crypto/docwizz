// Flows: how a request travels through the code, from a frontend route or an endpoint over calls, member access,
// interface dispatch and HTTP down to data and external systems. Shown per unit: a C# type, an endpoint, a TS
// function or store, a component, an external system.
partial class Generator
{
    static readonly string[] FlowKinds = ["calls", "accesses", "http", "renders", "routes-to", "connects"];

    // Units reached from the start, grouped by distance (the start itself excluded), and the unit-level steps between them.
    record FlowTrace(List<List<string>> Layers, List<(string From, string To)> Steps);

    ILookup<string, Edge>? flowOut;
    ILookup<string, string>? implementedBy;

    // A C# member is shown as its type, except endpoints; TS functions, components, routes and external systems as themselves.
    string Unit(string id) => nodes.TryGetValue(id, out var n) && n.Kind is "method" or "constructor" or "property" or "event"
        && n.Tags?.Contains("endpoint") != true && parent.ContainsKey(id) ? Top(id) : id;

    // stop: reached but not followed (e.g. endpoints, for frontend flows that link to api.md instead).
    FlowTrace Trace(string start, Func<Node, bool>? stop = null)
    {
        flowOut ??= model.Edges.Where(e => FlowKinds.Contains(e.Kind)
            || (e.Kind == "injects" && nodes.GetValueOrDefault(e.From)?.Kind == "endpoint")).ToLookup(e => e.From); // minimal-API handler parameters
        implementedBy ??= model.Edges.Where(e => e.Kind == "implements").ToLookup(e => e.To, e => e.From);

        // 0-1 BFS: staying inside a unit or dispatching to an implementation costs nothing, crossing to another unit costs 1.
        var depth = new Dictionary<string, int> { [start] = 0 };
        var from = new Dictionary<string, string>();  // symbol → the unit it was reached from
        var dispatched = new HashSet<string>();       // interfaces whose members were dispatched to implementations
        var queue = new LinkedList<string>([start]);
        void Reach(string next, int d, string? via, bool free)
        {
            if (depth.TryGetValue(next, out var old) && old <= d) return;
            depth[next] = d;
            if (via is not null) from[next] = via; else from.Remove(next);
            if (free) queue.AddFirst(next); else queue.AddLast(next);
        }
        while (queue.First is { } first)
        {
            queue.RemoveFirst();
            var cur = first.Value;
            if (cur != start && (cur.StartsWith("http:") || (nodes.TryGetValue(cur, out var n) && stop?.Invoke(n) == true))) continue;
            var unit = Unit(cur);
            var via = from.GetValueOrDefault(cur);
            foreach (var impl in implementedBy[cur])
            {
                dispatched.Add(unit);
                Reach(impl, depth[cur], via, free: true);
            }
            var next = flowOut[cur].Select(e => e.To)
                .Concat(parent.ContainsKey(cur) ? flowOut[Top(cur)].Where(e => e.Kind == "connects").Select(e => e.To) : []); // the type's external systems
            foreach (var x in next.Where(x => nodes.ContainsKey(x) || x.StartsWith("http:")))
                if (Unit(x) == unit) Reach(x, depth[cur], via, free: true);
                else Reach(x, depth[cur] + 1, unit, free: false);
        }

        var startUnit = Unit(start);
        var units = depth.GroupBy(kv => Unit(kv.Key)).Where(g => g.Key != startUnit && !dispatched.Contains(g.Key))
            .ToDictionary(g => g.Key, g => g.Min(kv => kv.Value));
        var layers = units.GroupBy(kv => kv.Value).OrderBy(g => g.Key)
            .Select(g => g.Select(kv => kv.Key).OrderBy(UnitLabel).ToList()).ToList();
        var steps = from.Where(kv => units.ContainsKey(Unit(kv.Key)) && (kv.Value == startUnit || units.ContainsKey(kv.Value)))
            .Select(kv => (kv.Value, Unit(kv.Key))).Where(s => s.Item1 != s.Item2).Distinct().ToList();
        return new(layers, steps);
    }

    string UnitLabel(string id) => nodes.TryGetValue(id, out var n)
        ? n.Kind == "external" ? ExternalLabel(n) : n.Tags?.Contains("endpoint") == true ? EndpointLabel(n) : n.Name
        : id.StartsWith("http:") ? $"{id[5..]} (no endpoint found)" : id;

    // "A → B, C → D": each hop lists every unit first reached at that distance.
    string Chain(FlowTrace t, int perLayer = 6) => string.Join(" → ", t.Layers.Select(l =>
        string.Join(", ", l.Take(perLayer).Select(UnitLabel)) + (l.Count > perLayer ? $", +{l.Count - perLayer} more" : "")));

    // What a flow ends up touching: DbContexts, and external systems.
    IEnumerable<Node> Reached(FlowTrace t, Func<Node, bool> match) =>
        t.Layers.SelectMany(l => l).Select(nodes.GetValueOrDefault).OfType<Node>().Where(match);
}
