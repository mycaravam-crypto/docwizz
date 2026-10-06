using System.IO.Enumeration;

class ArchitectureConfig
{
    // layer → path globs; first matching layer wins, unmatched files belong to no layer.
    public Dictionary<string, List<string>> Layers { get; set; } = [];
    // layer → layers it may depend on (`http` = calling HTTP directly). Layers not listed are unrestricted.
    public Dictionary<string, List<string>> Allow { get; set; } = [];
    // rule → high/medium/low, overriding the defaults (ARCH-001 high, ARCH-002 medium, ARCH-004 low).
    public Dictionary<string, string> Severity { get; set; } = [];
    // User-defined dependency rules, checked next to the built-in ones.
    public List<Rule> Rules { get; set; } = [];
}

// A user-defined rule: code matching `from` must not depend on what `forbid` names (targets, external packages or
// namespaces, edge kinds), except on targets matching `except`.
class Rule
{
    public string Id { get; set; } = "";
    public string? Severity { get; set; }
    public string? Description { get; set; }
    public Selector From { get; set; } = new();
    public Forbid Forbid { get; set; } = new();
    public Selector? Except { get; set; }
}

// All fields set must match. `layer` and `path` (glob) match the file; `kind`, `tag` and `name` (glob) match the symbol
// or a type that contains it, so a controller's methods count as the controller.
class Selector
{
    public string? Layer { get; set; }
    public string? Path { get; set; }
    public string? Kind { get; set; }
    public string? Tag { get; set; }
    public string? Name { get; set; }

    public bool IsEmpty => Layer is null && Path is null && Kind is null && Tag is null && Name is null;
}

// `to`: forbidden targets. `package`: forbidden namespaces/packages (C# `using`, Java `import`), by prefix.
// `edge`: the edge kinds that count (default: the dependency kinds); alone, every edge of those kinds is forbidden.
class Forbid
{
    public Selector? To { get; set; }
    public List<string> Package { get; set; } = [];
    public List<string> Edge { get; set; } = [];
}

record ArchitectureResult(List<Violation> Violations, List<List<string>> Cycles, Dictionary<string, int> LayerFiles,
    List<LayerDependency> LayerDependencies);

// Actual dependencies between layers (`http` = calls HTTP directly), Count = number of references.
record LayerDependency(string From, string To, int Count, bool Allowed);

// Custom: the rule is user-defined (`architecture.rules`), not built in. Security findings (SEC-*) add the basis of the
// detection (fact or inferred), the concern (confidentiality, integrity, availability) and the risk it may carry.
record Violation(string Rule, string FromLayer, string ToLayer, string FromFile, string To, string Example, Level Severity = Level.Low,
    bool Custom = false, Origin? Basis = null, string? Concern = null, string? Risk = null);

static class Architecture
{
    static readonly Dictionary<string, Level> DefaultSeverity = new()
        { ["ARCH-001"] = Level.High, ["ARCH-002"] = Level.Medium, ["ARCH-004"] = Level.Low };
    static readonly string[] BuiltIn = ["ARCH-001", "ARCH-002", "ARCH-003", "ARCH-004"];

    // Every edge kind the scanners emit; a rule naming another one would silently never match.
    public static readonly string[] EdgeKinds = [.. CodeModel.DependencyKinds, "contains", "accesses", "registers", "connects", "reads", "binds",
        "references", "depends-on", "tests", "uses-namespace", "pipeline", "hosts"];

    // Fails fast on a rule that can't work, naming the rule and what is wrong with it.
    public static void Validate(ArchitectureConfig config)
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < config.Rules.Count; i++)
        {
            var r = config.Rules[i];
            var at = $"architecture.rules[{i}]{(r.Id is { Length: > 0 } ? $" ({r.Id})" : "")}";
            void Fail(string why) => throw new ArgumentException($"{at}: {why}");
            if (r.Id is not { Length: > 0 }) Fail("needs an id");
            if (BuiltIn.Contains(r.Id) || Security.Rules.Any(s => s.Id == r.Id)) Fail($"id {r.Id} is reserved for a built-in rule");
            if (!seen.Add(r.Id)) Fail("duplicate id");
            if (r.Severity is not null)
                try { ParseSeverity(r.Severity); } catch (ArgumentException e) { Fail(e.Message); }
            if (r.From.IsEmpty) Fail("`from` must set at least one of layer, path, kind, tag, name");
            if (r.Forbid.To is null && r.Forbid.Package.Count == 0 && r.Forbid.Edge.Count == 0) Fail("`forbid` must set to, package or edge");
            if (r.Forbid.To is { IsEmpty: true }) Fail("`forbid.to` must set at least one of layer, path, kind, tag, name");
            if (r.Forbid.Package.Count > 0 && r.Forbid.To is not null) Fail("use separate rules for `forbid.to` and `forbid.package`");
            if (r.Forbid.Edge.FirstOrDefault(k => !EdgeKinds.Contains(k)) is { } kind) Fail($"unknown edge kind '{kind}' ({string.Join(", ", EdgeKinds)})");
            foreach (var layer in new[] { r.From, r.Forbid.To, r.Except }.Select(x => x?.Layer).OfType<string>())
                if (!config.Layers.ContainsKey(layer)) Fail($"unknown layer '{layer}' ({string.Join(", ", config.Layers.Keys)})");
        }
    }

    public static Level ParseSeverity(string s) => Enum.TryParse<Level>(s, true, out var l) && l != Level.None && !int.TryParse(s, out _)
        ? l : throw new ArgumentException($"unknown severity '{s}' (high, medium, low)");

    // The violations `check` counts: those at or above `check.fail_on`.
    public static List<Violation> Failing(IEnumerable<Violation> violations, CheckConfig check) =>
        violations.Where(v => v.Severity >= ParseSeverity(check.FailOn)).ToList();

    // The layer rules, plus the security rules when `security.enabled`.
    public static ArchitectureResult Check(CodeModel model, Config config)
    {
        var result = Check(model, config.Architecture);
        return config.Security.Enabled ? result with { Violations = [.. result.Violations, .. Security.Check(model, config)] } : result;
    }

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

        violations.AddRange(Custom(model, config, nodes, LayerOf));

        // One finding per file pair: the first edge is the example. architecture.severity wins over a rule's own.
        var ruleSeverity = config.Rules.Where(r => r.Severity is not null).ToDictionary(r => r.Id, r => ParseSeverity(r.Severity!));
        violations = violations.DistinctBy(v => (v.Rule, v.FromFile, v.To)).Select(v => v with
        {
            Severity = config.Severity.TryGetValue(v.Rule, out var s) ? ParseSeverity(s)
                : ruleSeverity.TryGetValue(v.Rule, out var l) ? l : DefaultSeverity.GetValueOrDefault(v.Rule, Level.Medium),
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

    // User-defined rules over the same edges: every edge from a `from` symbol to a forbidden target, package or edge kind.
    static IEnumerable<Violation> Custom(CodeModel model, ArchitectureConfig config, Dictionary<string, Node> nodes, Func<string, string?> layerOf)
    {
        if (config.Rules.Count == 0) yield break;
        var parent = model.Edges.Where(e => e.Kind == "contains").GroupBy(e => e.To).ToDictionary(g => g.Key, g => g.First().From);
        IEnumerable<Node> SelfAndContainers(Node n)
        {
            for (Node? x = n; x is not null; x = parent.TryGetValue(x.Id, out var p) ? nodes.GetValueOrDefault(p) : null) yield return x;
        }
        bool Glob(string? pattern, string value) => pattern is null || FileSystemName.MatchesSimpleExpression(pattern, value);
        bool Matches(Selector s, Node n) => (s.Layer is null || layerOf(n.File) == s.Layer) && Glob(s.Path, n.File)
            && SelfAndContainers(n).Any(x => (s.Kind is null || x.Kind == s.Kind) && (s.Tag is null || x.Tags?.Contains(s.Tag) == true) && Glob(s.Name, x.Name));
        string Top(Node n) => SelfAndContainers(n).Last().Id;

        foreach (var rule in config.Rules)
        {
            var kinds = rule.Forbid.Edge.Count > 0 ? rule.Forbid.Edge : rule.Forbid.Package.Count > 0 ? ["uses-namespace"] : [.. CodeModel.DependencyKinds];
            foreach (var e in model.Edges.Where(e => kinds.Contains(e.Kind)))
            {
                if (!nodes.TryGetValue(e.From, out var from) || !Matches(rule.From, from)) continue;
                var fromLayer = layerOf(from.File) ?? "—";
                if (rule.Forbid.Package.Count > 0)
                {
                    if (e.Kind != "uses-namespace") continue;
                    var ns = e.To[3..];
                    if (!rule.Forbid.Package.Any(p => ns == p || ns.StartsWith(p + ".") || p.Contains('*') && Glob(p, ns))) continue;
                    yield return new(rule.Id, fromLayer, "package", from.File, ns, $"{Short(from.Id)} uses {ns}", Custom: true);
                    continue;
                }
                var to = nodes.GetValueOrDefault(e.To);
                if (rule.Forbid.To is not null && (to is null || !Matches(rule.Forbid.To, to) || Top(to) == Top(from))) continue;
                if (rule.Except is not null && to is not null && Matches(rule.Except, to)) continue;
                yield return new(rule.Id, fromLayer, to is null ? e.Kind : layerOf(to.File) ?? "—", from.File, to?.File ?? e.To,
                    $"{Short(from.Id)} {e.Kind} {(to is null ? e.To : Short(to.Id))}", Custom: true);
            }
        }
    }

    // "domain must not use Microsoft.EntityFrameworkCore", for the docs.
    public static string Describe(Rule r)
    {
        static string Sel(Selector s) => string.Join(" ", new[] { s.Layer is { } l ? $"layer {l}" : null, s.Tag is { } t ? $"tag {t}" : null,
            s.Kind is { } k ? $"kind {k}" : null, s.Name is { } n ? $"name {n}" : null, s.Path is { } p ? $"path {p}" : null }.OfType<string>());
        var what = r.Forbid.Package.Count > 0 ? $"use {string.Join(", ", r.Forbid.Package)}"
            : r.Forbid.To is { } to ? $"depend on {Sel(to)}" : "have edges";
        var via = r.Forbid.Edge.Count > 0 ? $" through {string.Join("/", r.Forbid.Edge)}" : "";
        return $"{Sel(r.From)} must not {what}{via}" + (r.Except is { } ex ? $", except {Sel(ex)}" : "");
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
        var (all, cycles, layerFiles, layerDeps) = r;
        var violations = all.Where(v => v.Concern is null).ToList();
        o.WriteLine();
        o.WriteLine($"Architecture ({violations.Count} violations, {cycles.Count} cycles)");
        o.WriteLine(new string('─', 40));
        o.WriteLine("  layers: " + string.Join(", ", layerFiles.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
        o.WriteLine("  dependencies: " + string.Join(", ", layerDeps.Select(d => $"{d.From} → {d.To} {d.Count}{(d.Allowed ? "" : " ✗")}")));
        foreach (var g in violations.GroupBy(v => (v.Rule, v.FromLayer, v.ToLayer, v.Severity)).OrderByDescending(g => g.Key.Severity).ThenBy(g => g.Key.Rule))
        {
            o.WriteLine($"  {g.Key.Rule}{(g.First().Custom ? " (custom)" : "")}  {g.Key.FromLayer} → {g.Key.ToLayer}  [{g.Key.Severity.ToString().ToLowerInvariant()}]");
            foreach (var v in g)
                o.WriteLine($"    {v.FromFile} → {v.To}  ({v.Example})");
        }
        foreach (var c in cycles)
            o.WriteLine($"  ARCH-003  cycle: {string.Join(" ↔ ", c)}");

        var security = all.Where(v => v.Concern is not null).ToList();
        if (security.Count == 0) return;
        o.WriteLine();
        o.WriteLine($"Security ({security.Count} findings) — for a security review; not a compliance assessment");
        o.WriteLine(new string('─', 40));
        foreach (var g in security.GroupBy(v => (v.Rule, v.Severity)).OrderByDescending(g => g.Key.Severity).ThenBy(g => g.Key.Rule))
        {
            var first = g.First();
            var title = Security.Rules.FirstOrDefault(s => s.Id == g.Key.Rule)?.Title;
            o.WriteLine($"  {g.Key.Rule}  {title}  [{g.Key.Severity.ToString().ToLowerInvariant()}, {first.Concern}]");
            foreach (var v in g)
                o.WriteLine($"    {v.FromFile}  {v.Example}  ({first.Basis.ToString()!.ToLowerInvariant()})");
            o.WriteLine($"    risk (inferred): {first.Risk}");
        }
    }
}
