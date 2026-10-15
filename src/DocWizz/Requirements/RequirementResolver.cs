using System.Text.Json;

// `docwizz requirements show`: from the index (RequirementIndex) to the requirements of one current code symbol, or the
// symbols of one work item. Deterministic and offline: the index file and the current code model, nothing else.
//   confirmed  a commit the provider links to the work item itself (verified commit link) changed the symbol
//   candidate  only a confirmed PR's link (also through an ambiguous PR association), or a mention in the message of a
//              commit that changed it, names the work item
// Neither a PR link nor a message mention is ever promoted to confirmed: a PR touching a symbol is no proof the symbol
// implements all its work items, and a message naming one is nobody's verified link.
// Every link says whether the symbol changed after the newest commit behind it (changedSince): a historical requirement
// describes the code as it was then, not necessarily as it is now.
static class RequirementResolver
{
    public const string Schema = "docwizz.requirement-context/v1";

    // linked: at least one requirement. no-link: every commit that changed the symbol has a verified, empty linkage.
    // unavailable: no requirement, and some linkage couldn't be read (unavailable, denied, unknown) or a commit is missing.
    // not-in-history: no exported commit changed it. not-current: the index knows the id, the current code doesn't.
    public static readonly string[] Outcomes = ["linked", "no-link", "unavailable", "not-in-history", "not-current"];

    public record Context(string Schema, Symbol Symbol, string Outcome, bool Complete, List<Requirement> Requirements,
        List<CommitTouch> Commits, List<string> FileLevel, IndexInfo Index, List<string> Warnings);
    public record Symbol(string Id, string? Location, string? Hash);
    public record Requirement(RequirementId Id, string Status, bool ChangedSince, string Availability, RequirementIndex.WorkItem? WorkItem,
        List<Evidence> Evidence);
    // Symbol: for `renamed-from`, the old id whose evidence this is.
    public record Evidence(string Relation, string Commit, int? PullRequest, string? Confidence, string File, string Lines, string SymbolLines,
        string? Symbol = null);
    public record CommitTouch(string Commit, string Linkage, string Lines, List<int> PullRequests);
    public record IndexInfo(string Fingerprint, string? HistoryHead, string? Head, bool CoversHead);

    public record Reverse(string Schema, RequirementId WorkItem, string Availability, RequirementIndex.WorkItem? Item, List<ReverseSymbol> Symbols,
        IndexInfo Index, List<string> Warnings);
    public record ReverseSymbol(string Id, string Status, bool Current, string? Location, bool ChangedSince, List<Evidence> Evidence);

    // The requirements of the symbol with this id. `node` is the symbol in the current model (null: not current).
    // `exists`: whether an id is in the current model; with it, the links of a symbol renamed to this one are carried over.
    public static Context For(RequirementIndex.Index index, string id, Node? node, string? head, Func<string, bool>? exists = null)
    {
        var warnings = new List<string>(Coverage(index, head));
        var touched = index.Commits.Where(c => c.Touches.Any(t => t.Symbol == id)).ToList();
        var prs = index.PullRequests.ToDictionary(p => p.Id);
        var evidence = new List<(RequirementId WorkItem, Evidence Evidence, RequirementIndex.Touch Touch, int Order)>();
        var incomplete = false;
        foreach (var (c, order) in index.Commits.Select((c, i) => (c, i)).Where(x => touched.Contains(x.c)))
        {
            foreach (var t in c.Touches.Where(t => t.Symbol == id))
            {
                foreach (var wi in c.WorkItems) evidence.Add((wi, new("commit-workitem", c.Sha, null, null, t.File, t.Lines, t.SymbolLines), t, order));
                foreach (var wi in c.Mentions.Except(c.WorkItems)) evidence.Add((wi, new("commit-message", c.Sha, null, null, t.File, t.Lines, t.SymbolLines), t, order));
                foreach (var pr in c.PullRequests)
                    if (prs.TryGetValue(pr.Id, out var p) && p.Status == "confirmed")
                        foreach (var wi in p.WorkItems) evidence.Add((wi, new("pr-workitem", c.Sha, pr.Id, pr.Confidence, t.File, t.Lines, t.SymbolLines), t, order));
            }
            if (c.Linkage != "verified")
            {
                incomplete = true;
                warnings.Add($"commit {c.Sha[..7]} changed it, but its work-item links are {c.Linkage}");
            }
            foreach (var pr in c.PullRequests)
                if (!prs.TryGetValue(pr.Id, out var p) || p.Status != "confirmed" || p.Linkage != "verified")
                {
                    incomplete = true;
                    warnings.Add($"PR {pr.Id} (commit {c.Sha[..7]}): " + (p is null ? "not in the export"
                        : p.Status != "confirmed" ? $"{p.Status}, its work items are not used" : $"work-item links are {p.Linkage}"));
                }
        }
        if (node is not null && exists is not null && RenamedFrom(index, id, exists) is { } was)
            foreach (var r in For(index, was.Old, null, head).Requirements)
                foreach (var e in r.Evidence)
                    evidence.Add((r.Id, new("renamed-from", e.Commit, e.PullRequest, e.Confidence, e.File, e.Lines, e.SymbolLines, was.Old), was.Touch, was.Order));
        // Commits missing locally may have changed it; nobody can tell.
        if (index.Commits.Any(c => c.Mapping == "missing"))
        {
            incomplete = true;
            warnings.Add($"{index.Commits.Count(c => c.Mapping == "missing")} exported commit(s) are not in the local repository: what they changed is unknown");
        }

        var items = index.WorkItems.ToDictionary(w => w.Id);
        var requirements = evidence.GroupBy(e => e.WorkItem).Select(g =>
        {
            var direct = g.Any(e => e.Evidence.Relation == "commit-workitem");
            var newest = g.MaxBy(e => e.Order);
            var known = items.GetValueOrDefault(g.Key);
            return new Requirement(g.Key, direct ? "confirmed" : "candidate", node?.Hash is { } h && newest.Touch.Hash != h,
                known is null ? "unavailable" : "available", known,
                [.. g.Select(e => e.Evidence).Distinct().OrderBy(e => e.Relation, StringComparer.Ordinal).ThenBy(e => e.Commit, StringComparer.Ordinal)
                    .ThenBy(e => e.PullRequest).ThenBy(e => e.Lines, StringComparer.Ordinal)]);
        }).OrderBy(r => r.Status == "confirmed" ? 0 : 1).ThenBy(r => r.Id).ToList();
        foreach (var r in requirements.Where(r => r.Availability == "unavailable"))
            warnings.Add($"{r.Id.Display} is linked but was not exported: its content is unavailable");

        var outcome = node is null ? "not-current"
            : requirements.Count > 0 ? "linked"
            : touched.Count == 0 ? (index.Commits.Any(c => c.Mapping == "missing") ? "unavailable" : "not-in-history")
            : incomplete ? "unavailable" : "no-link";
        var file = node?.File ?? touched.SelectMany(c => c.Touches).FirstOrDefault(t => t.Symbol == id)?.File;
        var fileLevel = file is null ? [] : index.Commits.Where(c => c.FileOnly.Any(f => f.File == file) && !touched.Contains(c))
            .Select(c => c.Sha).ToList();
        return new(index.Schema == RequirementIndex.GithubSchema ? "docwizz.requirement-context/v2" : Schema, new(id, node is null ? null : CodeModel.Location(node), node?.Hash), outcome, !incomplete, requirements,
            [.. touched.SelectMany(c => c.Touches.Where(t => t.Symbol == id).Select(t => new CommitTouch(c.Sha, c.Linkage, t.Lines, [.. c.PullRequests.Select(p => p.Id)])))],
            fileLevel, Info(index, head), [.. warnings.Distinct()]);
    }

    // The symbol this one was renamed from (#187): one that is gone from the current code, of the same type with the same
    // parameters, whose last change sat in the same file at the line where this one starts in the commit that first
    // changed it. Its links come over as `renamed-from` evidence, so they are candidates: the position says rename, the
    // provider never linked the new name.
    // ponytail: one hop, same file; follow chains and moves (another file or type) when real histories need it.
    static (string Old, RequirementIndex.Touch Touch, int Order)? RenamedFrom(RequirementIndex.Index index, string id, Func<string, bool> exists)
    {
        var (owner, name, args) = Parts(id);
        var first = index.Commits.Select((c, i) => (Touch: c.Touches.FirstOrDefault(t => t.Symbol == id), Order: i)).FirstOrDefault(x => x.Touch is not null);
        if (first.Touch is null) return null;
        var start = first.Touch.SymbolLines.Split('-')[0];
        var last = new Dictionary<string, (RequirementIndex.Touch Touch, int Order)>(StringComparer.Ordinal);
        foreach (var (c, i) in index.Commits.Select((c, i) => (c, i)))
            foreach (var t in c.Touches.Where(t => t.Symbol != id && t.Kind == first.Touch.Kind && Parts(t.Symbol) is var p
                && p.Owner == owner && p.Args == args && p.Name != name))
                last[t.Symbol] = (t, i);
        var old = last.Where(x => x.Value.Order < first.Order && x.Value.Touch.File == first.Touch.File
            && x.Value.Touch.SymbolLines.Split('-')[0] == start && !exists(x.Key)).Select(x => x.Key).ToList();
        return old is [var only] ? (only, first.Touch, first.Order) : null;
    }

    // `cs:Shop.Orders.OrderService.Place(int, decimal)` -> (`cs:Shop.Orders.OrderService`, `Place`, `(int, decimal)`).
    static (string Owner, string Name, string Args) Parts(string id)
    {
        var paren = id.IndexOf('(');
        var path = paren < 0 ? id : id[..paren];
        var dot = path.LastIndexOf('.');
        return (dot < 0 ? "" : path[..dot], path[(dot + 1)..], paren < 0 ? "" : id[paren..]);
    }

    // The symbols one work item is linked to, current ones first.
    public static Reverse Symbols(RequirementIndex.Index index, RequirementId workItem, CodeModel model, string? head)
    {
        var nodes = model.Nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
        var ids = index.Commits.SelectMany(c => c.Touches).Select(t => t.Symbol).Distinct().Order(StringComparer.Ordinal);
        var symbols = ids.Select(id => (Id: id, Context: For(index, id, nodes.GetValueOrDefault(id), head, nodes.ContainsKey)))
            .Select(x => (x.Id, x.Context, Link: x.Context.Requirements.FirstOrDefault(r => r.Id == workItem)))
            .Where(x => x.Link is not null)
            .Select(x => new ReverseSymbol(x.Id, x.Link!.Status, nodes.ContainsKey(x.Id), x.Context.Symbol.Location, x.Link.ChangedSince, x.Link.Evidence))
            .OrderBy(s => s.Current ? 0 : 1).ThenBy(s => s.Status == "confirmed" ? 0 : 1).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        var item = index.WorkItems.FirstOrDefault(w => w.Id == workItem);
        return new(index.Schema == RequirementIndex.GithubSchema ? "docwizz.requirement-context/v2" : Schema, workItem, item is null ? "unavailable" : "available", item, symbols, Info(index, head), [.. Coverage(index, head)]);
    }

    static IndexInfo Info(RequirementIndex.Index index, string? head) =>
        new(index.Fingerprint, index.Inputs.HeadSha, head, Covers(index, head));

    static bool Covers(RequirementIndex.Index index, string? head) =>
        head is null || index.Inputs.HeadSha is not { } h || h.StartsWith(head, StringComparison.Ordinal) || head.StartsWith(h, StringComparison.Ordinal);

    static IEnumerable<string> Coverage(RequirementIndex.Index index, string? head)
    {
        if (!Covers(index, head) && index.Inputs.HeadSha is { } end && head is not null)
            yield return $"the history ends at {end[..Math.Min(7, end.Length)]}, HEAD is {head[..Math.Min(7, head.Length)]}: later commits are not covered";
        if (index.Inputs.Truncated) yield return "the history export was truncated: older commits are not covered";
        if (index.Inputs.Shallow) yield return "the history was exported from a shallow clone: older commits are not covered";
    }

    public static string Json(object value) => JsonSerializer.Serialize(value, RequirementIndex.Write).Replace("\r\n", "\n") + "\n";

    // The same answer for people.
    public static string Text(Context c)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"{c.Symbol.Id}{(c.Symbol.Location is { } l ? $" — {l}" : "")}\n");
        sb.Append($"outcome: {c.Outcome}{(c.Complete ? "" : " (linkage incomplete)")}\n");
        foreach (var r in c.Requirements)
        {
            sb.Append($"\n{r.Id.Display} [{r.Status}{(r.ChangedSince ? ", code changed since" : "")}]");
            sb.Append(r.WorkItem is { } w ? w.Id.IsGithub ? $" {w.Type} {w.State}: {w.Title} (exported update {w.ChangedDate}; current freshness unknown)\n"
                : $" {w.Type} {w.State}: {w.Title} (revision {w.Revision}{(w.IsCurrent == false ? $", current {w.CurrentRevision}" : "")})\n" : " content unavailable\n");
            if (r.WorkItem is { Id.IsGithub: true, Description: { } body }) sb.Append($"  {body.Replace("\n", " ")}\n");
            foreach (var crit in r.WorkItem?.Criteria ?? []) sb.Append($"  - {crit.Replace("\n", " ")}\n");
            foreach (var e in r.Evidence)
                sb.Append($"  {e.Relation}{(e.Symbol is { } old ? $" {old}" : "")} via {e.Commit[..7]}{(e.PullRequest is { } p ? $" (PR {p}{(e.Confidence is "ambiguous" or "inferred" ? $", {e.Confidence}" : "")})" : "")}: {e.File}:{e.Lines}\n");
        }
        if (c.FileLevel.Count > 0) sb.Append($"\n{c.FileLevel.Count} commit(s) changed its file without overlapping it (not asserted)\n");
        foreach (var w in c.Warnings) sb.Append($"⚠ {w}\n");
        return sb.ToString();
    }
}
