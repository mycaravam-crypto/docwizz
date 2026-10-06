record DiffResult(
    List<Node> Added, List<Node> Removed, List<Node> Changed,
    List<string> Pages,
    List<string> DepsAdded, List<string> DepsRemoved,
    List<DocumentationItem> NewGaps, List<Violation> NewViolations, List<string> Decisions,
    List<StaleDoc> Stale, List<(DocumentationItem Item, QualityFlag Flag)> NewFlags);

// A symbol whose contract changed while its doc comment stayed exactly the same.
record StaleDoc(Node Before, Node After, List<string> Changes);

static class Diff
{
    // Kinds worth reporting; properties/modules churn with every edit and carry no docs of their own.
    static readonly string[] Reported = ["class", "record", "struct", "interface", "type", "enum", "method", "constructor",
        "endpoint", "component", "function", "store", "route", "procedure", "sql-function", "sql-view", "trigger", "table", "migration"];

    public static DiffResult Compare(CodeModel before, CodeModel after, Config config)
    {
        var old = before.Nodes.Where(n => Reported.Contains(n.Kind)).ToDictionary(n => n.Id);
        var now = after.Nodes.Where(n => Reported.Contains(n.Kind)).ToDictionary(n => n.Id);
        var added = now.Values.Where(n => !old.ContainsKey(n.Id)).ToList();
        var removed = old.Values.Where(n => !now.ContainsKey(n.Id)).ToList();
        var changed = now.Values.Where(n => old.TryGetValue(n.Id, out var o) && o.Hash != n.Hash).ToList();

        var depsBefore = ModuleDeps(before);
        var depsAfter = ModuleDeps(after);
        var depsAdded = depsAfter.Except(depsBefore).Order().ToList();
        var depsRemoved = depsBefore.Except(depsAfter).Order().ToList();

        // Gaps that weren't gaps before: new code, or code that changed enough to need (more) docs.
        var itemsBefore = Analyzer.Analyze(before, config);
        var itemsAfter = Analyzer.Analyze(after, config);
        var gapsBefore = itemsBefore.Where(f => f.Status != Status.Documented).Select(f => f.Node.Id).ToHashSet();
        var newGaps = itemsAfter.Where(f => f.Status != Status.Documented && !gapsBefore.Contains(f.Node.Id)).ToList();
        var flagsBefore = itemsBefore.SelectMany(f => (f.Flags ?? []).Select(q => (f.Node.Id, q))).ToHashSet();
        var newFlags = itemsAfter.SelectMany(f => (f.Flags ?? []).Select(q => (Item: f, Flag: q)))
            .Where(x => !flagsBefore.Contains((x.Item.Node.Id, x.Flag))).ToList();

        // Possibly stale docs: same doc comment, different contract. A changed signature changes a C# id, so an
        // overload removed and one added under the same name in the same type count as the same symbol.
        var stale = new List<StaleDoc>();
        foreach (var (o, n) in changed.Select(n => (old[n.Id], n)).Concat(SameName(removed, added)))
            if (o.Doc is not null && n.Doc is not null && DocBody(o.Doc) == DocBody(n.Doc) && ContractChanges(o, n) is { Count: > 0 } changes)
                stale.Add(new(o, n, changes));

        var archBefore = Architecture.Check(before, config);
        var archAfter = Architecture.Check(after, config);
        var violationsBefore = archBefore.Violations.Select(Key).ToHashSet();
        var newViolations = archAfter.Violations.Where(v => !violationsBefore.Contains(Key(v))).ToList();

        // ADR candidates: decisions the change implies but nobody wrote down — a new external system, a new layer dependency.
        var decisions = new List<string>();
        var externalsBefore = before.Nodes.Where(n => n.Kind == "external").Select(n => n.Id).ToHashSet();
        foreach (var ext in after.Nodes.Where(n => n.Kind == "external" && !externalsBefore.Contains(n.Id)).OrderBy(n => n.Name))
        {
            var users = after.Edges.Where(e => e.Kind == "connects" && e.To == ext.Id).Select(e => e.From[(e.From.IndexOf(':') + 1)..]).Distinct().Order().ToList();
            decisions.Add($"Adopt {ext.Name} ({Externals.Category(ext)}, {Externals.Certainty(ext)})" + (users.Count > 0 ? $" — used by {string.Join(", ", users)}" : ""));
        }
        var layersBefore = archBefore.LayerDependencies.Select(d => (d.From, d.To)).ToHashSet();
        foreach (var d in archAfter.LayerDependencies.Where(d => !layersBefore.Contains((d.From, d.To))))
            decisions.Add($"Let layer {d.From} depend on {(d.To == "http" ? "HTTP directly" : d.To)} ({(d.Allowed ? "allowed by the rules" : "not allowed by the rules: change them or the code")})");

        // Pages mirror Generator's layout: every symbol lives on its module page, plus the overview it appears in.
        var pages = new SortedSet<string>();
        foreach (var n in added.Concat(removed).Concat(changed))
        {
            pages.Add($"modules/{Generator.Slug(Generator.Folder(n.File))}.md");
            if (n.Kind == "endpoint" || n.Tags?.Contains("endpoint") == true) pages.Add("api.md");
            if (n.Kind is "component" or "route") pages.Add("frontend.md");
        }
        // Flows passing through a touched symbol: a changed service marks the endpoints and routes that reach it.
        pages.UnionWith(Generator.FlowPages(after, config, added.Concat(changed).Select(n => n.Id)));
        pages.UnionWith(Generator.FlowPages(before, config, removed.Select(n => n.Id)));
        if (added.Count + removed.Count > 0) pages.Add("index.md");
        if (depsAdded.Count + depsRemoved.Count + newViolations.Count > 0)
            pages.UnionWith(["architecture.md", "architecture-description.md", "views/components.md"]);
        if (added.Concat(removed).Any(n => n.Kind == "endpoint" || n.Tags?.Contains("endpoint") == true))
            pages.Add("architecture-description.md");
        if (newGaps.Count > 0 || pages.Count > 0) pages.Add("quality.md");

        return new(added, removed, changed, [.. pages], depsAdded, depsRemoved, newGaps, newViolations, decisions, stale, newFlags);
    }

    static IEnumerable<(Node, Node)> SameName(List<Node> removed, List<Node> added)
    {
        static string Key(Node n) => n.Id.Split('(')[0];
        var gone = removed.GroupBy(Key).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        return added.GroupBy(Key).Where(g => g.Count() == 1 && gone.ContainsKey(g.Key)).Select(g => (gone[g.Key], g.Single()));
    }

    // Roslyn's doc XML names the member (<member name="M:T.M(System.String)">), which changes with the signature.
    static string DocBody(string doc)
    {
        try { return System.Text.RegularExpressions.Regex.Replace(string.Concat(System.Xml.Linq.XElement.Parse(doc).Nodes()), @"\s+", " ").Trim(); }
        catch { return doc; }
    }

    static List<string> ContractChanges(Node o, Node n)
    {
        var changes = new List<string>();
        if (!(o.Parameters ?? []).SequenceEqual(n.Parameters ?? [])) changes.Add("parameters");
        if (o.Returns != n.Returns) changes.Add("return type");
        if (!(o.Throws ?? []).Order().SequenceEqual((n.Throws ?? []).Order())) changes.Add("exceptions");
        if (o.Route != n.Route || o.Tags?.ElementAtOrDefault(1) != n.Tags?.ElementAtOrDefault(1) && n.Tags?.Contains("endpoint") == true)
            changes.Add("route");
        return changes;
    }

    static string Key(Violation v) => $"{v.Rule}|{v.FromFile}|{v.To}";

    static HashSet<string> ModuleDeps(CodeModel m)
    {
        var files = m.Nodes.ToDictionary(n => n.Id, n => Generator.Folder(n.File));
        return m.Edges.Where(e => CodeModel.DependencyKinds.Contains(e.Kind) && files.ContainsKey(e.From) && files.ContainsKey(e.To))
            .Select(e => (From: files[e.From], To: files[e.To])).Where(d => d.From != d.To)
            .Select(d => $"{d.From} → {d.To}").ToHashSet();
    }

    public static void Report(DiffResult d, string baseline, TextWriter o)
    {
        o.WriteLine($"Documentation impact (vs {baseline})");
        o.WriteLine();
        Section("Changed", d.Changed, "~");
        Section("Added", d.Added, "+");
        Section("Removed", d.Removed, "-");

        o.WriteLine($"Affected documentation ({d.Pages.Count})");
        foreach (var p in d.Pages) o.WriteLine($"  ✓ {p}");
        if (d.DepsAdded.Count + d.DepsRemoved.Count > 0)
        {
            o.WriteLine();
            o.WriteLine("Architecture change: module dependencies");
            foreach (var x in d.DepsAdded) o.WriteLine($"  + {x}");
            foreach (var x in d.DepsRemoved) o.WriteLine($"  - {x}");
        }
        if (d.Decisions.Count > 0)
        {
            o.WriteLine();
            o.WriteLine($"ADR candidates ({d.Decisions.Count}) — decisions this change implies; record them in docs/architecture/decisions/");
            foreach (var x in d.Decisions) o.WriteLine($"  ? {x}");
        }
        if (d.Stale.Count > 0)
        {
            o.WriteLine();
            o.WriteLine($"Possibly stale docs ({d.Stale.Count}) — the contract changed, the doc comment didn't");
            foreach (var s in d.Stale.OrderBy(s => s.After.File).ThenBy(s => s.After.Line))
                o.WriteLine($"  ! {s.After.File}:{s.After.Line}  {Generator.Display(s.After)}  changed: {string.Join(", ", s.Changes)}");
        }
        o.WriteLine();
        o.WriteLine($"Introduced: {d.NewGaps.Count(Analyzer.IsCritical)} critical, " +
                    $"{d.NewGaps.Count(f => !Analyzer.IsCritical(f))} other documentation gaps, {d.NewViolations.Count} architecture violations, " +
                    $"{d.NewFlags.Count} doc quality flags");
        foreach (var f in d.NewGaps.OrderByDescending(f => f.Level))
            o.WriteLine($"  {f.Level,-6} {f.Node.File}:{f.Node.Line}  {Generator.Display(f.Node)}  missing: {string.Join(", ", f.Missing)}");
        foreach (var v in d.NewViolations)
            o.WriteLine($"  {v.Rule}  {v.FromLayer} → {v.ToLayer}  {v.FromFile} → {v.To}  [{v.Severity.ToString().ToLowerInvariant()}]");
        foreach (var (f, q) in d.NewFlags)
            o.WriteLine($"  {q.Rule}{(q.Origin == Origin.Inferred ? " (inferred)" : "")}  {f.Node.File}:{f.Node.Line}  {Generator.Display(f.Node)}  {q.Detail}");

        void Section(string title, List<Node> ns, string sign)
        {
            if (ns.Count == 0) return;
            o.WriteLine($"{title} ({ns.Count})");
            foreach (var n in ns.OrderBy(n => n.File).ThenBy(n => n.Line)) o.WriteLine($"  {sign} {Generator.Display(n)}");
            o.WriteLine();
        }
    }
}
