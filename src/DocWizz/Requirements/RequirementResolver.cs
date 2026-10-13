using System.Text.Json;

// `docwizz requirements show`: from the index (RequirementIndex) to the requirements of one current code symbol, or the
// symbols of one work item. Deterministic and offline: the index file and the current code model, nothing else.
//   confirmed  a commit with a direct commit↔work-item link changed the symbol
//   candidate  only a completed PR's link (or an ambiguous PR association) covers a commit that changed it
// A PR link is never promoted to confirmed: a PR touching a symbol is no proof the symbol implements all its work items.
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
    public record Requirement(int Id, string Status, bool ChangedSince, string Availability, RequirementIndex.WorkItem? WorkItem,
        List<Evidence> Evidence);
    public record Evidence(string Relation, string Commit, int? PullRequest, string? Confidence, string File, string Lines, string SymbolLines);
    public record CommitTouch(string Commit, string Linkage, string Lines, List<int> PullRequests);
    public record IndexInfo(string Fingerprint, string? HistoryHead, string? Head, bool CoversHead);

    public record Reverse(string Schema, int WorkItem, string Availability, RequirementIndex.WorkItem? Item, List<ReverseSymbol> Symbols,
        IndexInfo Index, List<string> Warnings);
    public record ReverseSymbol(string Id, string Status, bool Current, string? Location, bool ChangedSince, List<Evidence> Evidence);

    // The requirements of the symbol with this id. `node` is the symbol in the current model (null: not current).
    public static Context For(RequirementIndex.Index index, string id, Node? node, string? head)
    {
        var warnings = new List<string>(Coverage(index, head));
        var touched = index.Commits.Where(c => c.Touches.Any(t => t.Symbol == id)).ToList();
        var prs = index.PullRequests.ToDictionary(p => p.Id);
        var evidence = new List<(int WorkItem, Evidence Evidence, RequirementIndex.Touch Touch, int Order)>();
        var incomplete = false;
        foreach (var (c, order) in index.Commits.Select((c, i) => (c, i)).Where(x => touched.Contains(x.c)))
        {
            foreach (var t in c.Touches.Where(t => t.Symbol == id))
            {
                foreach (var wi in c.WorkItems) evidence.Add((wi, new("commit-workitem", c.Sha, null, null, t.File, t.Lines, t.SymbolLines), t, order));
                foreach (var pr in c.PullRequests)
                    if (prs.TryGetValue(pr.Id, out var p))
                        foreach (var wi in p.WorkItems) evidence.Add((wi, new("pr-workitem", c.Sha, pr.Id, pr.Confidence, t.File, t.Lines, t.SymbolLines), t, order));
            }
            if (c.Linkage != "verified")
            {
                incomplete = true;
                warnings.Add($"commit {c.Sha[..7]} changed it, but its work-item links are {c.Linkage}");
            }
            foreach (var pr in c.PullRequests)
                if (!prs.TryGetValue(pr.Id, out var p) || p.Linkage != "verified")
                {
                    incomplete = true;
                    warnings.Add($"PR {pr.Id} (commit {c.Sha[..7]}): work-item links are {(p is null ? "not in the export" : p.Linkage)}");
                }
        }
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
            warnings.Add($"WI-{r.Id} is linked but was not exported: its content is unavailable");

        var outcome = node is null ? "not-current"
            : requirements.Count > 0 ? "linked"
            : touched.Count == 0 ? (index.Commits.Any(c => c.Mapping == "missing") ? "unavailable" : "not-in-history")
            : incomplete ? "unavailable" : "no-link";
        var file = node?.File ?? touched.SelectMany(c => c.Touches).FirstOrDefault(t => t.Symbol == id)?.File;
        var fileLevel = file is null ? [] : index.Commits.Where(c => c.FileOnly.Any(f => f.File == file) && !touched.Contains(c))
            .Select(c => c.Sha).ToList();
        return new(Schema, new(id, node is null ? null : CodeModel.Location(node), node?.Hash), outcome, !incomplete, requirements,
            [.. touched.SelectMany(c => c.Touches.Where(t => t.Symbol == id).Select(t => new CommitTouch(c.Sha, c.Linkage, t.Lines, [.. c.PullRequests.Select(p => p.Id)])))],
            fileLevel, Info(index, head), [.. warnings.Distinct()]);
    }

    // The symbols one work item is linked to, current ones first.
    public static Reverse Symbols(RequirementIndex.Index index, int workItem, CodeModel model, string? head)
    {
        var nodes = model.Nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
        var ids = index.Commits.SelectMany(c => c.Touches).Select(t => t.Symbol).Distinct().Order(StringComparer.Ordinal);
        var symbols = ids.Select(id => (Id: id, Context: For(index, id, nodes.GetValueOrDefault(id), head)))
            .Select(x => (x.Id, x.Context, Link: x.Context.Requirements.FirstOrDefault(r => r.Id == workItem)))
            .Where(x => x.Link is not null)
            .Select(x => new ReverseSymbol(x.Id, x.Link!.Status, nodes.ContainsKey(x.Id), x.Context.Symbol.Location, x.Link.ChangedSince, x.Link.Evidence))
            .OrderBy(s => s.Current ? 0 : 1).ThenBy(s => s.Status == "confirmed" ? 0 : 1).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        var item = index.WorkItems.FirstOrDefault(w => w.Id == workItem);
        return new(Schema, workItem, item is null ? "unavailable" : "available", item, symbols, Info(index, head), [.. Coverage(index, head)]);
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
            sb.Append($"\nWI-{r.Id} [{r.Status}{(r.ChangedSince ? ", code changed since" : "")}]");
            sb.Append(r.WorkItem is { } w ? $" {w.Type} {w.State}: {w.Title} (revision {w.Revision}{(w.IsCurrent == false ? $", current {w.CurrentRevision}" : "")})\n" : " content unavailable\n");
            foreach (var crit in r.WorkItem?.Criteria ?? []) sb.Append($"  - {crit.Replace("\n", " ")}\n");
            foreach (var e in r.Evidence)
                sb.Append($"  {e.Relation} via {e.Commit[..7]}{(e.PullRequest is { } p ? $" (PR {p}{(e.Confidence == "ambiguous" ? ", ambiguous" : "")})" : "")}: {e.File}:{e.Lines}\n");
        }
        if (c.FileLevel.Count > 0) sb.Append($"\n{c.FileLevel.Count} commit(s) changed its file without overlapping it (not asserted)\n");
        foreach (var w in c.Warnings) sb.Append($"⚠ {w}\n");
        return sb.ToString();
    }
}
