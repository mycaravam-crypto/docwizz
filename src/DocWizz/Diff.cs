record DiffResult(
    List<Node> Added, List<Node> Removed, List<Node> Changed,
    List<string> Pages,
    List<string> DepsAdded, List<string> DepsRemoved,
    List<DocumentationItem> NewGaps, List<Violation> NewViolations);

static class Diff
{
    // Kinds worth reporting; properties/modules churn with every edit and carry no docs of their own.
    static readonly string[] Reported = ["class", "record", "struct", "interface", "enum", "method", "constructor",
        "endpoint", "component", "function", "store", "route"];

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

        var violationsBefore = Architecture.Check(before, config.Architecture).Violations.Select(Key).ToHashSet();
        var newViolations = Architecture.Check(after, config.Architecture).Violations.Where(v => !violationsBefore.Contains(Key(v))).ToList();

        // Pages mirror Generator's layout: every symbol lives on its module page, plus the overview it appears in.
        var pages = new SortedSet<string>();
        foreach (var n in added.Concat(removed).Concat(changed))
        {
            pages.Add($"modules/{Generator.Slug(Generator.Folder(n.File))}.md");
            if (n.Kind == "endpoint" || n.Tags?.Contains("endpoint") == true) pages.Add("api.md");
            if (n.Kind is "component" or "route") pages.Add("frontend.md");
        }
        if (added.Count + removed.Count > 0) pages.Add("index.md");
        if (depsAdded.Count + depsRemoved.Count + newViolations.Count > 0)
            pages.UnionWith(["architecture.md", "architecture-description.md", "views/components.md"]);
        if (added.Concat(removed).Any(n => n.Kind == "endpoint" || n.Tags?.Contains("endpoint") == true))
            pages.Add("architecture-description.md");
        if (newGaps.Count > 0 || pages.Count > 0) pages.Add("quality.md");

        return new(added, removed, changed, [.. pages], depsAdded, depsRemoved, newGaps, newViolations);
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
        o.WriteLine();
        o.WriteLine($"Introduced: {d.NewGaps.Count(Analyzer.IsCritical)} critical, " +
                    $"{d.NewGaps.Count(f => !Analyzer.IsCritical(f))} other documentation gaps, {d.NewViolations.Count} architecture violations");
        foreach (var f in d.NewGaps.OrderByDescending(f => f.Level))
            o.WriteLine($"  {f.Level,-6} {f.Node.File}:{f.Node.Line}  {Generator.Display(f.Node)}  missing: {string.Join(", ", f.Missing)}");
        foreach (var v in d.NewViolations)
            o.WriteLine($"  {v.Rule}  {v.FromLayer} → {v.ToLayer}  {v.FromFile} → {v.To}");

        void Section(string title, List<Node> ns, string sign)
        {
            if (ns.Count == 0) return;
            o.WriteLine($"{title} ({ns.Count})");
            foreach (var n in ns.OrderBy(n => n.File).ThenBy(n => n.Line)) o.WriteLine($"  {sign} {Generator.Display(n)}");
            o.WriteLine();
        }
    }
}
