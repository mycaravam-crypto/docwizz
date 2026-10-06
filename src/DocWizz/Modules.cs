using System.Text;

// Module pages: a folder of code explained as a unit — its role, what it offers and touches, the flows through it and
// what deserves attention — before the per-component reference. Sections appear only when the code has something to say.
partial class Generator
{
    const int MaxListed = 8;
    const int HighComplexity = 15;
    const int HighFanOut = 10;

    readonly Dictionary<string, FlowTrace> traces = [];
    // Frontend routes stop at the endpoints they reach; api.md continues from there.
    FlowTrace TraceOf(string id) => traces.TryGetValue(id, out var t) ? t
        : traces[id] = nodes[id].Kind == "route" ? Trace(id, stop: n => n.Tags?.Contains("endpoint") == true) : Trace(id);

    string Module(string folder, List<Node> members)
    {
        var sb = new StringBuilder($"# {folder}\n\n");
        var tops = members.Where(n => TopKinds.Contains(n.Kind) && Top(n.Id) == n.Id).OrderBy(n => n.Name).ToList();
        var topIds = tops.Select(t => t.Id).ToHashSet();
        bool Here(string id) => nodes.TryGetValue(id, out var n) && n.Kind != "external" && Folder(n.File) == folder;
        var endpoints = model.Nodes.Where(n => n.Tags?.Contains("endpoint") == true && Folder(n.File) == folder)
            .OrderBy(n => n.Route?.TrimStart('/')).ThenBy(n => n.Tags![1]).ToList();

        // Role: facts about what kind of code lives here.
        var role = new List<string>();
        if (Layer(folder) is { } layer) role.Add($"Layer **{layer}**");
        if (members.FirstOrDefault() is { } any && global::Projects.Of(Projects, any.File) is { } project) role.Add($"project `{project.Name}`");
        var blocks = Roles.Select(r => (r.Label, Count: tops.Concat(endpoints).Count(n => n.Kind == r.Match || n.Tags?.Contains(r.Match) == true)))
            .Where(r => r.Count > 0).Select(r => $"{r.Label}: {r.Count}").ToList();
        if (blocks.Count > 0) role.Add(string.Join(", ", blocks));
        if (role.Count > 0) sb.AppendLine(string.Join(" · ", role) + "\n");

        // AI-drafted overview: what the module is for and how it is used, each sentence from the facts it cites.
        if (overviews?.GetValueOrDefault(folder)?.Sections is { } overview)
        {
            sb.AppendLine("## Overview 🤖\n");
            foreach (var (name, title) in new[] { ("summary", ""), ("responsibilities", "Responsibilities"), ("usage", "Usage") })
                if (overview.GetValueOrDefault(name) is { Count: > 0 } sentences)
                    sb.AppendLine((title.Length > 0 ? $"**{title}.** " : "") + string.Join(" ", sentences.Select(x => x.Text)) + "\n");
            sb.AppendLine("_AI draft from the facts on this page; review it, then write the module's documentation yourself._\n");
        }

        // Key components: the ones the rest of the code relies on most.
        var deps = Dependencies().Select(d => (From: Top(d.From), To: Top(d.To)))
            .Where(d => d.From != d.To && nodes.ContainsKey(d.To) && (topIds.Contains(d.From) || topIds.Contains(d.To)))
            .Distinct().ToList();
        var usedByCount = tops.ToDictionary(t => t.Id, t => deps.Count(d => d.To == t.Id));
        var key = tops.Where(t => t.Kind != "module").OrderByDescending(t => usedByCount[t.Id]).ThenBy(t => t.Name).Take(MaxListed).ToList();
        if (key.Count > 1)
        {
            sb.AppendLine("## Key components\n\n| Component | Kind | Used by | Summary |\n|---|---|---|---|");
            foreach (var t in key)
                sb.AppendLine($"| [{t.Name}](#{Anchor(t.Id)}) | {string.Join(", ", (t.Tags ?? []).Where(x => Roles.Any(r => r.Match == x)).DefaultIfEmpty(t.Kind))} | " +
                    $"{usedByCount[t.Id]} | {Esc(Summary(t) ?? "")} |");
            sb.AppendLine();
        }

        if (endpoints.Count > 0)
        {
            sb.AppendLine("## API\n\n| Endpoint | Summary | Authorization |\n|---|---|---|");
            foreach (var e in endpoints)
                sb.AppendLine($"| `{EndpointLabel(e)}` | {Esc(Summary(e) ?? "—")} | " +
                    $"{(e.Tags!.Contains("anonymous") ? "anonymous" : e.Tags.Contains("authorize") ? "required" : "—")} |");
            sb.AppendLine("\nContracts and callers: [API](../api.md).\n");
        }

        // Data: contexts defined here, and code here that uses a context.
        var contexts = model.Nodes.Where(n => n.Tags?.Contains("dbcontext") == true).ToList();
        var data = new List<string>();
        foreach (var ctx in contexts.Where(c => topIds.Contains(c.Id)))
        {
            var entities = model.Edges.Where(e => e.Kind == "persists" && e.From == ctx.Id).Select(e => nodes.GetValueOrDefault(e.To)?.Name).OfType<string>().Order().ToList();
            var dbs = DatabasesOf(ctx).Select(ExternalLabel).ToList();
            data.Add($"- `{ctx.Name}` stores {(entities.Count > 0 ? string.Join(", ", entities) : "no mapped entities")}" +
                (dbs.Count > 0 ? $" in {string.Join(", ", dbs)}" : ""));
        }
        var access = model.Edges.Where(e => e.Kind is "accesses" or "injects" && Here(e.From) && contexts.Any(c => c.Id == e.To) && Unit(e.From) != e.To)
            .Select(e => (User: UnitLabel(Unit(e.From)), Ctx: nodes[e.To].Name)).Distinct().OrderBy(x => x.User).ToList();
        data.AddRange(access.GroupBy(a => a.Ctx).Select(g => $"- {Users(g.Select(a => a.User))} `{g.Key}`"));
        if (data.Count > 0) sb.AppendLine("## Data and persistence\n\n" + string.Join("\n", data) + "\n");

        var external = model.Edges.Where(e => e.Kind == "connects" && Here(e.From) && nodes.ContainsKey(e.To))
            .Select(e => (User: UnitLabel(Unit(e.From)), Ext: nodes[e.To])).Distinct().OrderBy(x => x.Ext.Name).ToList();
        if (external.Count > 0)
            sb.AppendLine("## External systems\n\n" + string.Join("\n", external.GroupBy(x => x.Ext).Select(g =>
                $"- {g.Key.Name} ({Externals.Category(g.Key)}, {Externals.Certainty(g.Key)}) — used by {string.Join(", ", g.Select(x => $"`{x.User}`").Distinct())}")) +
                "\n\nSee [system context](../views/context.md).\n");

        var settings = model.Edges.Where(e => e.Kind is "reads" or "binds" && Here(e.From) && nodes.ContainsKey(e.To)).Select(e => nodes[e.To]).Distinct()
            .OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (settings.Count > 0)
            sb.AppendLine("## Configuration\n\n| Key | Read by | Status |\n|---|---|---|\n" + string.Join("\n", settings.Select(k =>
                $"| `{k.Name}` | {ConfigReaderList(k)} | {ConfigStatus(k)} |")) + "\n\nAll keys: [deployment](../views/deployment.md#configuration).\n");

        // Flows: every route and endpoint whose path starts or passes here.
        var flows = model.Nodes.Where(n => n.Kind == "route").OrderBy(n => n.Route)
            .Concat(model.Nodes.Where(n => n.Tags?.Contains("endpoint") == true).OrderBy(n => n.Route?.TrimStart('/')).ThenBy(n => n.Tags![1]))
            .Select(n => (Start: n, Trace: TraceOf(n.Id)))
            .Where(f => f.Trace.Layers.Count > 0 && (Here(f.Start.Id) || f.Trace.Layers.SelectMany(l => l).Any(Here))).ToList();
        if (flows.Count > 0)
        {
            sb.AppendLine("## Flows through this module\n");
            foreach (var (n, t) in flows.Take(MaxListed))
            {
                var handler = parent.TryGetValue(n.Id, out var h) ? nodes[h].Name : null;
                var head = n.Kind == "route" ? $"`{n.Route}`" : $"`{EndpointLabel(n)}`:" + (handler is null ? "" : $" {handler}");
                var sep = n.Kind == "route" || handler is not null ? " → " : " ";
                sb.AppendLine($"- {head}{sep}{Esc(Chain(t))}");
            }
            if (flows.Count > MaxListed) sb.AppendLine($"- … {flows.Count - MaxListed} more in [API](../api.md#flows) and [frontend](../frontend.md#flows)");
            sb.AppendLine();
        }

        var gaps = findings.Where(f => Folder(f.Node.File) == folder && f.Status != Status.Documented)
            .OrderByDescending(f => f.Level).ThenBy(f => f.Node.Line).ToList();
        if (gaps.Count > 0)
        {
            sb.AppendLine($"## Documentation gaps\n\n{gaps.Count} items need documentation ({gaps.Count(Analyzer.IsCritical)} critical):\n");
            foreach (var f in gaps.Take(MaxListed))
                sb.AppendLine($"- {f.Level.ToString().ToLowerInvariant()}: `{Esc(ShortName(f.Node))}` " +
                    $"— missing {string.Join(", ", f.Missing)} ({Esc(string.Join("; ", f.Reasons))})");
            if (gaps.Count > MaxListed) sb.AppendLine($"- … see [quality](../quality.md)");
            sb.AppendLine();
        }

        var observations = Observations(folder, tops, deps).ToList();
        if (observations.Count > 0) sb.AppendLine("## Architecture observations\n\n" + string.Join("\n", observations.Select(o => $"- {o}")) + "\n");

        string ModuleOf(string id) => Folder(nodes[id].File);
        var uses = deps.Where(d => topIds.Contains(d.From) && !topIds.Contains(d.To)).Select(d => ModuleOf(d.To)).Distinct().Order().ToList();
        var usedBy = deps.Where(d => topIds.Contains(d.To) && !topIds.Contains(d.From)).Select(d => ModuleOf(d.From)).Distinct().Order().ToList();
        var outgoing = deps.Where(d => topIds.Contains(d.From)).Select(d => (d.From, d.To, 0)).ToList();
        if (uses.Count + usedBy.Count + outgoing.Count > 0)
        {
            sb.AppendLine("## Dependencies\n");
            if (uses.Count > 0) sb.AppendLine("Depends on: " + string.Join(", ", uses.Select(m => $"[{m}]({Slug(m)}.md)")) + "\n");
            if (usedBy.Count > 0) sb.AppendLine("Used by: " + string.Join(", ", usedBy.Select(m => $"[{m}]({Slug(m)}.md)")) + "\n");
            if (outgoing.Count is > 0 and <= MaxDiagramEdges) sb.AppendLine(Mermaid("graph LR", outgoing, id => nodes[id].Name));
        }

        if (tops.Count > 0) sb.AppendLine("## Components\n");
        foreach (var t in tops)
        {
            sb.AppendLine($"### {t.Name}\n");
            sb.AppendLine($"_{t.Kind}_ · {SourceLink(t.File, t.Line, Path.GetFileName(t.File), sub: "modules")}{Badge(t)}\n");
            if (Summary(t) is { } s) sb.AppendLine(s + "\n");
            if (t.Kind == "component" && t.Params > 0) sb.AppendLine($"Props: {string.Join(", ", t.Parameters ?? [$"{t.Params}"])}\n");
            if (t.Events is { Count: > 0 }) sb.AppendLine($"Emits: {string.Join(", ", t.Events)}\n");
            foreach (var (name, section) in Derived(t))
                sb.AppendLine($"- **{name}** _({(section.Origin == Origin.Ai ? "🤖 draft" : section.Origin.ToString().ToLowerInvariant())})_: {Esc(section.Text)}");
            if (Derived(t).Any()) sb.AppendLine();
            if (Backlinks(t) is { Length: > 0 } back) sb.AppendLine(back + "\n");
            if (findingOf.TryGetValue(t.Id, out var item))
                sb.AppendLine("_Evidence:_ " + string.Join(", ", item.Sources.Select(nodes.GetValueOrDefault).OfType<Node>()
                    .Select(n => SourceLink(n.File, n.Line, CodeModel.Location(n), sub: "modules"))) + "\n");

            var ms = children[t.Id].Select(nodes.GetValueOrDefault).OfType<Node>()
                .Where(m => m.Kind is "method" or "function" or "endpoint" && m.Visibility is "public" or "protected" or "internal" or null)
                .OrderBy(m => m.Line).ToList();
            if (ms.Count == 0) continue;
            sb.AppendLine("| Member | Summary | Derived from code | Complexity |\n|---|---|---|---|");
            foreach (var m in ms)
                sb.AppendLine($"| {SourceLink(m.File, m.Line, $"`{Esc(MemberName(m, t))}`", sub: "modules")}{Badge(m)} | {Esc(Summary(m) ?? "")} | " +
                    $"{Esc(string.Join("; ", Derived(m).Select(d => $"{d.Name}{d.Section.Origin switch { Origin.Inferred => " (inferred)", Origin.Ai => " 🤖", _ => "" }}: {d.Section.Text}")))} | {m.Complexity} |");
            sb.AppendLine();
        }
        var files = members.Select(m => m.File).Distinct().Order().ToList();
        sb.AppendLine($"---\n_Generated from {members.Count} symbols in {string.Join(", ", files.Select(f => SourceLink(f, 0, f, sub: "modules")))} " +
            $"(commit `{model.Commit ?? "unknown"}`, profile `{config.Profile}`). Sections marked _fact_ are read from the code, " +
            "_inferred_ come from heuristics, 🤖 marks AI drafts._");
        return sb.ToString();
    }

    // `Type.Member(List<Node>, string)`: owner and member without namespaces.
    string ShortName(Node n) => System.Text.RegularExpressions.Regex.Replace(
        parent.TryGetValue(n.Id, out var o) ? $"{nodes[o].Name}.{MemberName(n, nodes[o])}" : n.Kind == "endpoint" ? n.Name : Display(n), @"\b(?:\w+\.)+(?=\w)", m => m.Index == 0 ? m.Value : "");

    // What deserves a closer look here, each with the reason — rule violations, cycles, complexity, coupling, and
    // presentation code that reaches the database directly.
    // Unit → the flows (routes, endpoints) that reach it.
    ILookup<string, Node>? reachedBy;

    // Where a component turns up elsewhere: the flows that reach it and the configuration keys it (or a member) reads.
    string Backlinks(Node t)
    {
        reachedBy ??= model.Nodes.Where(n => n.Kind == "route" || n.Tags?.Contains("endpoint") == true)
            .SelectMany(n => TraceOf(n.Id).Layers.SelectMany(l => l).Select(u => (Unit: u, Start: n))).Distinct()
            .ToLookup(x => x.Unit, x => x.Start);
        var parts = new List<string>();
        var starts = reachedBy[t.Id].Where(s => s.Id != t.Id && Top(s.Id) != t.Id).OrderBy(s => s.Kind == "route").ThenBy(s => s.Route?.TrimStart('/')).ToList();
        if (starts.Count > 0)
            parts.Add("_Reached from:_ " + string.Join(", ", starts.Take(MaxListed).Select(s => s.Kind == "route"
                ? $"[`{s.Route}`](../frontend.md#flows)" : $"[`{EndpointLabel(s)}`](../api.md#flows)")) + (starts.Count > MaxListed ? $", +{starts.Count - MaxListed} more" : ""));
        var keys = model.Edges.Where(e => e.Kind is "reads" or "binds" && (e.From == t.Id || Top(e.From) == t.Id) && nodes.ContainsKey(e.To))
            .Select(e => nodes[e.To].Name).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (keys.Count > 0) parts.Add("_Configuration:_ " + string.Join(", ", keys.Select(k => $"[`{k}`](../views/deployment.md#configuration)")));
        return string.Join(" · ", parts);
    }

    // "`A` uses" / "`A`, `B` use"
    static string Users(IEnumerable<string> users) => users.ToList() is var u && u.Count == 1 ? $"`{u[0]}` uses" : $"{string.Join(", ", u.Select(x => $"`{x}`"))} use";

    IEnumerable<string> Observations(string folder, List<Node> tops, List<(string From, string To)> deps)
    {
        foreach (var v in arch.Violations.Where(v => Folder(v.FromFile) == folder || Folder(v.To) == folder).OrderByDescending(v => v.Severity))
            yield return (v.Concern is not null ? $"{v.Rule} (security, {v.Severity.ToString().ToLowerInvariant()}): {Esc(v.Example)} — see [security](../architecture-description.md#security) "
                : v.Custom ? $"{v.Rule} (custom, {v.Severity.ToString().ToLowerInvariant()}): {Esc(v.Example)} — see [rules](../architecture.md#custom-rules) "
                    : $"{v.Rule} ({v.Severity.ToString().ToLowerInvariant()}): {v.FromLayer} → {v.ToLayer} is not allowed — {Esc(v.Example)} ") +
                $"({SourceLink(v.FromFile, 0, Path.GetFileName(v.FromFile), sub: "modules")})";
        foreach (var c in arch.Cycles.Where(c => c.Contains(folder)))
            yield return $"ARCH-003: part of a dependency cycle {string.Join(" ↔ ", c.Select(m => $"[{m}]({Slug(m)}.md)"))} — these modules can't be understood or changed independently";
        var complex = model.Nodes.Where(n => Folder(n.File) == folder && n.Complexity >= HighComplexity && n.Kind != "component")
            .OrderByDescending(n => n.Complexity).ToList();
        if (complex.Count > 0)
            yield return $"{complex.Count} {(complex.Count == 1 ? "member has" : "members have")} cyclomatic complexity ≥ {HighComplexity} (many paths to test and explain): " +
                string.Join(", ", complex.Take(5).Select(n => $"`{Esc(ShortName(n))}` ({n.Complexity})")) + (complex.Count > 5 ? ", … see [quality](../quality.md)" : "");
        foreach (var t in tops)
            if (deps.Where(d => d.From == t.Id).Select(d => d.To).Distinct().Count() is var fanOut and >= HighFanOut)
                yield return $"`{t.Name}` depends on {fanOut} other components — high coupling, possibly several responsibilities";
        if (Layer(folder) is "ui" or "api" or "state" or "client")
            foreach (var (user, ctx) in model.Edges.Where(e => e.Kind is "accesses" or "injects" && nodes.TryGetValue(e.From, out var f) && Folder(f.File) == folder
                    && nodes.GetValueOrDefault(e.To)?.Tags?.Contains("dbcontext") == true)
                .Select(e => (User: UnitLabel(Unit(e.From)), Ctx: nodes[e.To].Name)).Distinct().OrderBy(x => x.User).GroupBy(x => x.Ctx).Select(g => (g.Select(x => x.User), g.Key)))
                yield return $"{Users(user)} `{ctx}` directly from the {Layer(folder)} layer — data access bypasses the application layer";
    }
}
