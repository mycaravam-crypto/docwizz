record DiffResult(
    List<Node> Added, List<Node> Removed, List<Node> Changed,
    List<string> Pages,
    List<string> DepsAdded, List<string> DepsRemoved,
    List<DocumentationItem> NewGaps, List<Violation> NewViolations, List<string> Decisions);

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
        var gapsBefore = Analyzer.Analyze(before, config).Where(f => f.Status != Status.Documented).Select(f => f.Node.Id).ToHashSet();
        var newGaps = Analyzer.Analyze(after, config).Where(f => f.Status != Status.Documented && !gapsBefore.Contains(f.Node.Id)).ToList();

        var archBefore = Architecture.Check(before, config.Architecture);
        var archAfter = Architecture.Check(after, config.Architecture);
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

        return new(added, removed, changed, [.. pages], depsAdded, depsRemoved, newGaps, newViolations, decisions);
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
        o.WriteLine();
        o.WriteLine($"Introduced: {d.NewGaps.Count(Analyzer.IsCritical)} critical, " +
                    $"{d.NewGaps.Count(f => !Analyzer.IsCritical(f))} other documentation gaps, {d.NewViolations.Count} architecture violations");
        foreach (var f in d.NewGaps.OrderByDescending(f => f.Level))
            o.WriteLine($"  {f.Level,-6} {f.Node.File}:{f.Node.Line}  {Generator.Display(f.Node)}  missing: {string.Join(", ", f.Missing)}");
        foreach (var v in d.NewViolations)
            o.WriteLine($"  {v.Rule}  {v.FromLayer} → {v.ToLayer}  {v.FromFile} → {v.To}  [{v.Severity.ToString().ToLowerInvariant()}]");

        void Section(string title, List<Node> ns, string sign)
        {
            if (ns.Count == 0) return;
            o.WriteLine($"{title} ({ns.Count})");
            foreach (var n in ns.OrderBy(n => n.File).ThenBy(n => n.Line)) o.WriteLine($"  {sign} {Generator.Display(n)}");
            o.WriteLine();
        }
    }
}
