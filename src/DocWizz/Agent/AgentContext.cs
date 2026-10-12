using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// `docwizz context`: a token-budgeted package of facts about one target (a symbol, a file, a folder or an endpoint) for
// coding agents with small context windows. docwizz does the exploring; the agent gets the part of the model that
// matters for the target instead of whole pages. Every line says where it comes from (detected, inferred, human,
// ai-drafted). AI drafts are left out unless asked for, so an agent never reads a draft back as a fact. The same model,
// target and budget give byte-identical output.
static class AgentContext
{
    public const int DefaultBudget = 6000;
    public const int DefaultHops = 2;
    public const int MaxHops = 4;
    // Tokens are estimated as characters / 4: no tokenizer dependency, and close enough for code and English prose.
    public const int CharsPerToken = 4;

    // Edges that make a neighbour (contains, uses-namespace and depends-on are structure, tests are listed apart, and
    // connects/reads/binds are external systems and configuration, also listed apart).
    static readonly string[] NeighbourKinds = [.. CodeModel.DependencyKinds, "accesses", "registers"];

    static readonly Dictionary<string, (string Out, string In)> Verbs = new()
    {
        ["calls"] = ("calls", "called by"), ["injects"] = ("injects", "injected into"), ["implements"] = ("implements", "implemented by"),
        ["inherits"] = ("inherits", "inherited by"), ["imports"] = ("imports", "imported by"), ["renders"] = ("renders", "rendered by"),
        ["routes-to"] = ("routes to", "routed to from"), ["persists"] = ("persists", "persisted by"), ["publishes"] = ("publishes", "published by"),
        ["subscribes"] = ("subscribes to", "subscribed to by"), ["http"] = ("calls over HTTP", "called over HTTP by"),
        ["creates"] = ("creates", "created by"), ["accesses"] = ("accesses", "accessed by"), ["registers"] = ("resolved (DI) to", "registered (DI) for"),
    };

    // One fact. Origin: detected (read from the code), inferred (a heuristic), human (written docs), ai-drafted (🤖).
    public record Line(string Text, string Origin, string? Location = null);
    public record Block(string Name, string Title, List<Line> Lines);
    public record Truncation(string Section, int Dropped);

    // What --for resolved to: Kind symbol/endpoint/file/module, the symbols in scope, and the one symbol it names (if any).
    public record Target(string Kind, string Label, List<Node> Scope, Node? Primary);
    public record Resolution(Target? Target, string? Error, List<string> Candidates);

    // Where the model came from and whether it still matches the code.
    public record Freshness(string ModelSource, string? Head, int ChangedFiles);

    public record Package(string Repository, string? Commit, Freshness Freshness, Target Target, int Budget, bool IncludeAi,
        int AiLeftOut, List<Block> Blocks, List<Truncation> Truncated)
    {
        public bool Stale => Freshness.ChangedFiles > 0 || (Commit is { } c && Freshness.Head is { } h && !h.StartsWith(c) && !c.StartsWith(h));
    }

    // --for, in order: exact id → fully qualified name (with or without the parameter list) → file or folder → `VERB /route`.
    // Never guesses: several matches is an error with the candidates, none is an error with the closest names.
    public static Resolution Resolve(CodeModel model, string text)
    {
        var query = text.Trim();
        var children = model.Edges.Where(e => e.Kind == "contains").ToLookup(e => e.From, e => e.To);
        var nodes = model.Nodes.ToDictionary(n => n.Id);
        var symbols = model.Nodes.Where(n => n.Kind is not ("external" or "config" or "package" or "project")).ToList();

        Target Symbol(Node n, string kind, string label)
        {
            var scope = new List<Node> { n };
            for (var i = 0; i < scope.Count; i++) scope.AddRange(children[scope[i].Id].Where(nodes.ContainsKey).Select(id => nodes[id]));
            return new(kind, label, scope, n);
        }
        Resolution? One(List<Node> matches, Func<Node, string> kind, Func<Node, string> label) => matches.DistinctBy(n => n.Id).OrderBy(n => n.Id, StringComparer.Ordinal).ToList() switch
        {
            [] => null,
            [var n] => new(Symbol(n, kind(n), label(n)), null, []),
            var many => new(null, $"{query} is ambiguous: {many.Count} symbols match", [.. many.Select(n => n.Id)]),
        };

        if (nodes.TryGetValue(query, out var exact)) return new(Symbol(exact, Kind(exact), Label(exact)), null, []);
        if (One([.. symbols.Where(n => Generator.Display(n) == query)], Kind, Label) is { } byName) return byName;
        if (One([.. symbols.Where(n => Generator.Display(n).Split('(')[0] == query)], Kind, Label) is { } byPrefix) return byPrefix;

        var path = query.Replace('\\', '/').Trim('/');
        if (path.StartsWith("./")) path = path[2..];
        bool InScope(Node n) => n.Kind is not ("external" or "config");
        if (model.Nodes.Where(n => InScope(n) && n.File == path).ToList() is { Count: > 0 } inFile)
            return new(new("file", path, Ordered(inFile), null), null, []);
        if (model.Nodes.Where(n => InScope(n) && (Generator.Folder(n.File) == path || Generator.Folder(n.File).StartsWith(path + "/"))).ToList() is { Count: > 0 } inFolder)
            return new(new("module", path, Ordered(inFolder), null), null, []);

        if (Regex.Match(query, @"^([A-Za-z]+)\s+(\S+)$") is { Success: true } m)
        {
            var label = $"{m.Groups[1].Value.ToUpperInvariant()} /{m.Groups[2].Value.TrimStart('/')}";
            if (One([.. symbols.Where(n => n.Tags?.Contains("endpoint") == true && n.Tags.Count > 1 && Generator.EndpointLabel(n) == label)], _ => "endpoint", _ => label) is { } endpoint)
                return endpoint;
        }

        var q = query.ToLowerInvariant();
        var candidates = symbols.Where(n => Generator.Display(n).Contains(q, StringComparison.OrdinalIgnoreCase) || n.File.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (n.Tags?.Contains("endpoint") == true && n.Tags.Count > 1 && Generator.EndpointLabel(n).Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Select(n => n.Tags?.Contains("endpoint") == true && n.Tags.Count > 1 && !n.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ? Generator.EndpointLabel(n) : n.Id)
            .Distinct().OrderBy(c => c.Length).ThenBy(c => c, StringComparer.Ordinal).Take(10).ToList();
        return new(null, $"no symbol, file, folder or endpoint matches {query}", candidates);

        static string Kind(Node n) => n.Tags?.Contains("endpoint") == true ? "endpoint" : "symbol";
        static string Label(Node n) => n.Tags?.Contains("endpoint") == true && n.Tags.Count > 1 ? Generator.EndpointLabel(n) : Generator.Display(n);
        static List<Node> Ordered(IEnumerable<Node> ns) => [.. ns.OrderBy(n => n.File, StringComparer.Ordinal).ThenBy(n => n.Line).ThenBy(n => n.Id, StringComparer.Ordinal)];
    }

    // The facts about a target, by priority: what it is, its neighbours, hop-2 neighbours, flows and configuration,
    // tests and findings, and the files to read first. Budgeting (Fit) cuts from the end.
    public static List<Block> Blocks(ContextBuilder graph, Config config, Target target, int hops, bool includeAi,
        List<DocumentationItem> findings, ArchitectureResult arch, Dictionary<string, AiProse.Draft> drafts)
    {
        var nodes = graph.Nodes;
        var scope = target.Scope.Select(n => n.Id).ToHashSet();
        var findingOf = findings.ToDictionary(f => f.Node.Id);
        var parent = graph.Model.Edges.Where(e => e.Kind == "contains").GroupBy(e => e.To).ToDictionary(g => g.Key, g => g.First().From);
        // A member's own type contributes what is declared on the type (constructor injections, external systems).
        var owner = target.Primary is { } p && parent.TryGetValue(p.Id, out var o) && nodes.ContainsKey(o) ? nodes[o] : null;
        var origins = scope.Append(owner?.Id).OfType<string>().ToHashSet();
        string Loc(Node n) => CodeModel.Location(n);
        static string Origin(Origin o) => o switch { global::Origin.Written => "human", global::Origin.Fact => "detected", global::Origin.Inferred => "inferred", _ => "ai-drafted" };
        static string Flat(string s) => Regex.Replace(s, @"\s+", " ").Trim();

        // A file's or folder's own symbols: those not inside another symbol of the scope (a TS file module's functions count).
        var tops = target.Primary is { } primary ? [primary] : target.Scope.Where(x => x.Kind is not ("property" or "event" or "constructor")
            && (!parent.TryGetValue(x.Id, out var up) || !scope.Contains(up) || nodes[up].Kind == "module")).ToList();

        // 1. The target itself.
        var self = new List<Line>();
        if (target.Primary is { } n)
        {
            var layer = Layer(config, n.File);
            self.Add(new($"{n.Kind} `{Generator.Display(n)}`" + (n.Visibility is { } v ? $", {v}" : "") + (layer is null ? "" : $", layer {layer}"), "detected", Loc(n)));
            if (n.Tags?.Contains("endpoint") == true && n.Tags.Count > 1) self.Add(new($"route `{Generator.EndpointLabel(n)}`" + (n.Tags.Contains("authorize") ? ", requires authorization" : ""), "detected"));
            if (owner is not null) self.Add(new($"member of {owner.Kind} `{Generator.Display(owner)}`", "detected", Loc(owner)));
            if (n.Parameters is { Count: > 0 }) self.Add(new($"parameters: {string.Join(", ", n.Parameters.Select(x => $"`{x}`"))}", "detected"));
            if (n.Returns is not null) self.Add(new($"returns `{n.Returns}`", "detected"));
            if (n.Responses is { Count: > 0 }) self.Add(new($"responses: {string.Join(", ", n.Responses)}", "detected"));
            if (n.Throws is { Count: > 0 }) self.Add(new($"throws {string.Join(", ", n.Throws.Select(x => $"`{x}`"))}", "detected"));
            if (n.Events is { Count: > 0 }) self.Add(new($"emits {string.Join(", ", n.Events.Select(x => $"`{x}`"))}", "detected"));
            if (n.Complexity is { } c) self.Add(new($"cyclomatic complexity {c}", "detected"));
            if (findingOf.TryGetValue(n.Id, out var f))
            {
                foreach (var (name, s) in f.Sections.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    if (s.Origin != global::Origin.Ai || includeAi) self.Add(new($"{name.Replace('_', ' ')}: {Flat(s.Text)}", Origin(s.Origin)));
                if (f.Status != Status.Documented) self.Add(new($"documentation {f.Status.ToString().ToLowerInvariant()}, missing {string.Join(", ", f.Missing)} ({f.Level.ToString().ToLowerInvariant()} need: {string.Join("; ", f.Reasons)})", "detected"));
            }
            else if (ContextBuilder.WrittenSummary(n) is { } summary) self.Add(new($"summary: {Flat(summary)}", "human"));
            if (includeAi && drafts.TryGetValue(n.Id, out var d) && d.Text.Length > 0 && findingOf.GetValueOrDefault(n.Id)?.Sections.ContainsKey("summary") != true)
                self.Add(new($"summary 🤖: {Flat(d.Text)}", "ai-drafted"));
            foreach (var m in target.Scope.Skip(1).Where(m => m.Kind is not ("property" or "event")).OrderBy(m => m.Line).ThenBy(m => m.Id, StringComparer.Ordinal))
                self.Add(new($"member {m.Kind} `{Generator.Display(m)}`", "detected", Loc(m)));
        }
        else
        {
            var fileCount = target.Scope.Select(x => x.File).Distinct().Count();
            var layers = target.Scope.Select(x => Layer(config, x.File)).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();
            self.Add(new($"{target.Kind} `{target.Label}`: {fileCount} file{(fileCount == 1 ? "" : "s")}, {target.Scope.Count} symbols" + (layers.Count > 0 ? $", layer {string.Join(", ", layers)}" : ""), "detected"));
            foreach (var t in tops)
                self.Add(new($"{t.Kind} `{Generator.Display(t)}`" + (ContextBuilder.WrittenSummary(t) is { } s ? $": {Flat(s)}" : ""), ContextBuilder.WrittenSummary(t) is null ? "detected" : "human", Loc(t)));
        }

        // 2. Neighbours one hop away, and 3. further hops (signature only).
        var hop1 = new List<Line>();
        var reached = new HashSet<string>(origins);
        var frontier = new List<string>();
        var edges = origins.SelectMany(id => graph.Out(id, NeighbourKinds).Where(e => id == owner?.Id ? e.Kind == "injects" : true).Select(e => (Out: true, e.Kind, Other: e.To)))
            .Concat(scope.SelectMany(id => graph.In(id, NeighbourKinds).Select(e => (Out: false, e.Kind, Other: e.From))))
            .Where(x => !scope.Contains(x.Other) && x.Other != owner?.Id).Distinct()
            .OrderBy(x => !x.Out).ThenBy(x => Array.IndexOf(NeighbourKinds, x.Kind)).ThenBy(x => graph.Name(x.Other), StringComparer.Ordinal).ToList();
        // One line per neighbour and direction, every edge kind between them named: `injects, accesses X`.
        foreach (var g in edges.GroupBy(x => (x.Out, x.Other)))
        {
            var verbs = g.Select(x => Verbs.TryGetValue(x.Kind, out var vs) ? x.Out ? vs.Out : vs.In : x.Kind);
            hop1.Add(new($"{string.Join(", ", verbs)} `{graph.Name(g.Key.Other)}`", "detected", nodes.TryGetValue(g.Key.Other, out var x) ? Loc(x) : null));
            if (nodes.ContainsKey(g.Key.Other) && reached.Add(g.Key.Other)) frontier.Add(g.Key.Other);
        }
        var further = new List<Line>();
        for (var hop = 2; hop <= hops && frontier.Count > 0; hop++)
        {
            var next = new List<(string Id, string Via)>();
            foreach (var id in frontier.Order(StringComparer.Ordinal))
                foreach (var e in graph.Out(id, NeighbourKinds).Select(e => e.To).Concat(graph.In(id, NeighbourKinds).Select(e => e.From))
                    .Where(x => nodes.TryGetValue(x, out var xn) && xn.Kind is not ("external" or "config")).Distinct().Order(StringComparer.Ordinal))
                    if (reached.Add(e)) next.Add((e, id));
            foreach (var (id, via) in next.OrderBy(x => x.Id, StringComparer.Ordinal))
                further.Add(new($"`{graph.Name(id)}` (hop {hop}, via `{graph.Name(via)}`)", "detected", Loc(nodes[id])));
            frontier = [.. next.Select(x => x.Id)];
        }

        // 4. Request flows through the target, external systems, configuration keys (names only, never values).
        var flows = new List<Line>();
        var externals = new Dictionary<string, Node>();
        foreach (var (entry, chain, reachedExternals) in Generator.FlowChains(graph.Model, config, scope))
        {
            flows.Add(new($"flow `{chain}`", chain.Contains("(inferred)") ? "inferred" : "detected", Loc(entry)));
            foreach (var x in reachedExternals) externals.TryAdd(x.Id, x);
        }
        foreach (var e in origins.SelectMany(id => graph.Out(id, "connects")))
            if (nodes.TryGetValue(e.To, out var x)) externals.TryAdd(x.Id, x);
        foreach (var x in externals.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
            flows.Add(new($"external system {x.Name} ({Externals.Category(x)})", Externals.Certainty(x) == "detected" ? "detected" : "inferred"));
        foreach (var e in origins.SelectMany(id => graph.Out(id, "reads", "binds")).DistinctBy(e => (e.Kind, e.To)).OrderBy(e => e.To, StringComparer.Ordinal))
            if (nodes.TryGetValue(e.To, out var key)) flows.Add(new($"configuration key `{key.Name}` ({(e.Kind == "binds" ? "bound" : "read")})", "detected"));

        // 5. Linked tests, documentation gaps and architecture findings around the target.
        var quality = new List<Line>();
        var tests = new TestLinks(graph.Model);
        foreach (var t in target.Scope.SelectMany(x => tests.Of(x.Id)).DistinctBy(t => t.Test).OrderBy(t => t.Test, StringComparer.Ordinal))
            quality.Add(new($"test `{TestLinks.Name(t.Test)}`" + (t.Via is null ? "" : $" (via `{graph.Name(t.Via)}`)"), "detected"));
        var around = scope.Concat(edges.Select(x => x.Other)).ToHashSet();
        foreach (var f in findings.Where(f => around.Contains(f.Node.Id) && f.Status != Status.Documented)
            .OrderBy(f => !scope.Contains(f.Node.Id)).ThenByDescending(f => f.Level).ThenBy(f => f.Node.Id, StringComparer.Ordinal))
            quality.Add(new($"documentation gap ({f.Level.ToString().ToLowerInvariant()}{(Analyzer.IsCritical(f) ? ", critical" : "")}): `{Generator.Display(f.Node)}` missing {string.Join(", ", f.Missing)}", "detected", Loc(f.Node)));
        var files = target.Scope.Select(x => x.File).Append(owner?.File).OfType<string>().ToHashSet();
        foreach (var v in arch.Violations.Where(v => files.Contains(v.FromFile) || files.Contains(v.To))
            .OrderBy(v => v.Rule, StringComparer.Ordinal).ThenBy(v => v.FromFile, StringComparer.Ordinal).ThenBy(v => v.Example, StringComparer.Ordinal))
            quality.Add(new($"{v.Rule} ({v.Severity.ToString().ToLowerInvariant()}): {v.Example}", v.Basis == global::Origin.Inferred ? "inferred" : "detected", v.FromFile));
        var folders = files.Select(Generator.Folder).ToHashSet();
        foreach (var c in arch.Cycles.Where(c => c.Any(folders.Contains)))
            quality.Add(new($"ARCH-003 module cycle: {string.Join(" ↔ ", c)}", "detected"));

        // 6. Where to start reading: the target, its type, and the code on the other side of its direct edges.
        var read = tops.Append(owner).OfType<Node>()
            .Concat(edges.Select(x => nodes.GetValueOrDefault(x.Other)).OfType<Node>().Where(x => x.Kind is not ("external" or "config")))
            .DistinctBy(x => x.Id).Select(x => new Line($"`{x.Name}`", "detected", Loc(x))).ToList();

        return
        [
            new("target", "Target", self),
            new("neighbours", "Neighbours", hop1),
            new("further", "Further neighbours (signature only)", further),
            new("flows", "Endpoints, flows, external systems, configuration", flows),
            new("quality", $"Tests, documentation gaps, architecture findings ({TestLinks.Note})", quality),
            new("read", "Read before you change it", read),
        ];
    }

    // The layer a file falls in (first matching glob), as the architecture check assigns it.
    static string? Layer(Config config, string file) => config.Architecture.Layers.FirstOrDefault(kv => kv.Value.Any(glob =>
        System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(glob, file))).Key;

    // Drops lines from the end of the lowest-priority sections until the rendered package fits the budget. The header
    // (with the truncation notice) is never cut, so a budget smaller than the header yields the header alone.
    public static string Render(Package package, bool json)
    {
        var budget = package.Budget * CharsPerToken;
        var blocks = package.Blocks.Select(b => b with { Lines = [.. b.Lines] }).ToList();
        var dropped = blocks.ToDictionary(b => b.Name, _ => 0);
        int Cost(Line l) => json ? JsonSerializer.Serialize(l, Compact).Length + 1 : l.Text.Length + (l.Location?.Length + 3 ?? 0) + l.Origin.Length + 6;
        while (true)
        {
            var truncated = blocks.Where(b => dropped[b.Name] > 0).Select(b => new Truncation(b.Title, dropped[b.Name])).ToList();
            var current = package with { Blocks = blocks, Truncated = truncated };
            var text = json ? Json(current) : Markdown(current);
            if (text.Length <= budget || blocks.All(b => b.Lines.Count == 0)) return text;
            // Cut what the overshoot needs in one go, then render again: the truncation notice itself takes room too.
            for (var over = text.Length - budget; over > 0 && blocks.LastOrDefault(b => b.Lines.Count > 0) is { } last;)
            {
                over -= Cost(last.Lines[^1]);
                last.Lines.RemoveAt(last.Lines.Count - 1);
                dropped[last.Name]++;
            }
        }
    }

    static readonly string Provenance = "detected = read from the code; inferred = heuristic, confirm in the code; human = written docs, may be outdated; ai-drafted = 🤖 draft, not a fact";

    static string Markdown(Package p)
    {
        var sb = new StringBuilder();
        sb.Append($"# Context: {p.Target.Label}\n\n");
        sb.Append($"- target: {p.Target.Kind} `{p.Target.Label}`\n");
        sb.Append($"- repository: `{p.Repository}`, model at commit `{p.Commit ?? "unknown"}` from {p.Freshness.ModelSource}\n");
        foreach (var w in Warnings(p)) sb.Append($"- ⚠ {w}\n");
        sb.Append($"- budget: {p.Budget} tokens, estimated as characters / {CharsPerToken}\n");
        sb.Append($"- provenance: {Provenance}\n");
        sb.Append(p.IncludeAi ? "- AI drafts: included, marked ai-drafted\n" : p.AiLeftOut > 0 ? $"- AI drafts: {p.AiLeftOut} left out (--include-ai to see them)\n" : "");
        if (p.Truncated.Count > 0)
            sb.Append($"- truncated to fit the budget: {string.Join(", ", p.Truncated.Select(t => $"{t.Dropped} line{(t.Dropped == 1 ? "" : "s")} of {t.Section.Split(" (")[0]}"))}\n");
        sb.Append("- this is a map, not the code: read the code before you change it\n");
        foreach (var b in p.Blocks.Where(b => b.Lines.Count > 0))
        {
            sb.Append($"\n## {b.Title}\n\n");
            foreach (var l in b.Lines) sb.Append($"- {l.Text}{(l.Location is null ? "" : $" — {l.Location}")} [{l.Origin}]\n");
        }
        return sb.ToString();
    }

    static IEnumerable<string> Warnings(Package p)
    {
        if (p.Commit is { } c && p.Freshness.Head is { } h && !h.StartsWith(c) && !c.StartsWith(h))
            yield return $"stale: HEAD is `{h}`, the model was scanned at `{c}`; run `docwizz generate .` to refresh it";
        if (p.Freshness.ChangedFiles > 0)
            yield return $"stale: {p.Freshness.ChangedFiles} source file{(p.Freshness.ChangedFiles == 1 ? "" : "s")} changed after the model was written; run `docwizz generate .` to refresh it";
    }

    static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // The same package as data, compact (it is for tools, and whitespace costs tokens): the basis of the MCP server.
    public static string Json(Package p) => JsonSerializer.Serialize(new
    {
        target = new { kind = p.Target.Kind, label = p.Target.Label, id = p.Target.Primary?.Id },
        repository = p.Repository,
        commit = p.Commit,
        head = p.Freshness.Head,
        model = p.Freshness.ModelSource,
        stale = p.Stale,
        warnings = Warnings(p).ToList(),
        budget = new { tokens = p.Budget, estimate = $"characters / {CharsPerToken}" },
        provenance = Provenance,
        aiDrafts = p.IncludeAi ? "included" : p.AiLeftOut > 0 ? $"{p.AiLeftOut} left out" : "none",
        truncated = p.Truncated.Select(t => new { section = t.Section.Split(" (")[0], dropped = t.Dropped }),
        sections = p.Blocks.Where(b => b.Lines.Count > 0).Select(b => new { name = b.Name, title = b.Title, lines = b.Lines }),
    }, Compact);
}
