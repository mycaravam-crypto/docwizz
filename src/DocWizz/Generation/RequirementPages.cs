using System.Text;
using System.Text.RegularExpressions;

// Requirement pages from the imported index (docwizz requirements import): one per work item, with its criteria, the
// routes and code it is linked to and the commits and PRs behind each link, plus the routes' and modules' way back.
// Offline and deterministic: the index file and the current model. A link is history (a commit or PR that changed the
// code names the work item), never proof that the code fulfils it.
partial class Generator
{
    // The imported index; null: none imported, and the pages say so.
    public RequirementIndex.Index? Requirements { get; init; }
    // HEAD, to tell whether the imported history still covers the code.
    public string? Head { get; init; }

    record RequirementLink(Node Symbol, RequirementResolver.Requirement Requirement);
    List<RequirementLink>? requirementLinks;
    Dictionary<string, HashSet<string>>? routeUnits;

    // Every current symbol's links, resolved once.
    List<RequirementLink> RequirementLinks() => requirementLinks ??= Requirements is not { } index ? []
        : [.. index.Commits.SelectMany(c => c.Touches).Select(t => t.Symbol).Distinct().Order(StringComparer.Ordinal)
            .Where(nodes.ContainsKey)
            .SelectMany(id => RequirementResolver.For(index, id, nodes[id], Head, nodes.ContainsKey).Requirements.Select(r => new RequirementLink(nodes[id], r)))];

    // The units a route's page shows: its components and everything its flow reaches.
    Dictionary<string, HashSet<string>> RouteUnits() => routeUnits ??= model.Nodes.Where(n => n.Kind == "route").GroupBy(RoutePath)
        .ToDictionary(g => g.Key, g => g.SelectMany(r => model.Edges.Where(e => e.Kind == "routes-to" && e.From == r.Id).Select(e => e.To)
            .Concat(flowGraph.TraceOf(r.Id).Layers.SelectMany(l => l))).ToHashSet(StringComparer.Ordinal));

    internal static string RequirementPage(RequirementId id) =>
        $"requirements/{(id.IsGithub ? Regex.Replace(id.ToString().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') : $"wi-{id.Number}")}.md";

    string WorkItemLink(RequirementId id, string up = "../") => $"[{id.Display}]({up}{RequirementPage(id)})";

    static string LinkNote =>
        "_Confirmed: a commit linked to the work item changed the code; candidate: only a PR or a commit message names it. " +
        "Neither proves the code fulfils the work item._\n";

    // The Requirements section of a route page, and the gap when nothing is linked.
    string RouteRequirements(string path, List<string> gaps)
    {
        if (Requirements is null) return "## Requirements\n\nNo requirement history imported (`docwizz requirements import`).\n";
        var units = RouteUnits()[path];
        var linked = RequirementLinks().Where(l => units.Contains(flowGraph.Unit(l.Symbol.Id))).GroupBy(l => l.Requirement.Id).OrderBy(g => g.Key).ToList();
        if (linked.Count == 0)
        {
            gaps.Add("No work item is linked to the code behind this route.");
            return "## Requirements\n\nNone linked.\n";
        }
        var sb = new StringBuilder("## Requirements\n\n| Work item | Title | Link | Via | Code changed since |\n|---|---|---|---|---|\n");
        foreach (var g in linked)
        {
            var item = g.First().Requirement.WorkItem;
            var via = g.Select(l => flowGraph.UnitLabel(flowGraph.Unit(l.Symbol.Id))).Distinct().Order(StringComparer.Ordinal).ToList();
            sb.AppendLine($"| {WorkItemLink(g.Key)} | {Untrusted(item?.Title ?? "content unavailable")} | {LinkStatus(g)} | " +
                $"{Esc(string.Join(", ", via.Take(MaxListed)) + (via.Count > MaxListed ? $", +{via.Count - MaxListed} more" : ""))} | {(g.Any(l => l.Requirement.ChangedSince) ? "yes" : "—")} |");
        }
        return sb.Append('\n').Append(LinkNote).ToString();
    }

    // Exported work-item text, untrusted: one line, no HTML and no Markdown links or images (a javascript: link stays text).
    static string Untrusted(string s) => Esc(s).Replace("[", "\\[").Replace("]", "\\]");

    static string LinkStatus(IEnumerable<RequirementLink> links) => links.Any(l => l.Requirement.Status == "confirmed") ? "confirmed" : "candidate";

    IEnumerable<(string Page, string Body)> RequirementPages()
    {
        if (Requirements is not { } index) yield break;
        var byItem = RequirementLinks().ToLookup(l => l.Requirement.Id);
        var ids = index.WorkItems.Select(w => w.Id).Concat(byItem.Select(g => g.Key)).Distinct().Order().ToList();
        var items = index.WorkItems.ToDictionary(w => w.Id);
        var routesOf = ids.ToDictionary(id => id, id => RouteUnits().Where(r => byItem[id].Any(l => r.Value.Contains(flowGraph.Unit(l.Symbol.Id))))
            .Select(r => r.Key).Order(StringComparer.Ordinal).ToList());
        var coverage = RequirementResolver.Coverage(index, Head).ToList();

        var sb = new StringBuilder("# Requirements\n\n");
        sb.AppendLine($"Work items from the imported history{(index.Inputs.Provider is { } p ? $" ({p}" + (index.Inputs.Repository is { } r ? $", {r})" : ")") : "")}" +
            $"{(index.Inputs.HeadSha is { } h ? $" up to commit `{h[..Math.Min(7, h.Length)]}`" : "")}, and the routes and code they are linked to.\n");
        foreach (var w in coverage) sb.AppendLine($"⚠ {Esc(w)}\n");
        sb.AppendLine("| Work item | Type | State | Title | Criteria | Code | Routes |\n|---|---|---|---|---|---|---|");
        foreach (var id in ids)
        {
            var w = items.GetValueOrDefault(id);
            sb.AppendLine($"| {WorkItemLink(id, "")} | {Untrusted(w?.Type ?? "—")} | {Untrusted(w?.State ?? "—")} | {Untrusted(w?.Title ?? "content unavailable")} | " +
                $"{w?.Criteria.Count ?? 0} | {byItem[id].Select(l => l.Symbol.Id).Distinct().Count()} | {routesOf[id].Count} |");
        }
        sb.Append('\n').Append(LinkNote);
        yield return ("requirements/index.md", sb.ToString());

        foreach (var id in ids) yield return (RequirementPage(id), RequirementPageBody(id, items.GetValueOrDefault(id), [.. byItem[id]], routesOf[id], coverage));
    }

    string RequirementPageBody(RequirementId id, RequirementIndex.WorkItem? w, List<RequirementLink> links, List<string> routes, List<string> coverage)
    {
        var sb = new StringBuilder($"# {id.Display}: {Untrusted(w?.Title ?? "content unavailable")}\n\n[Requirements](index.md) / work item\n\n");
        if (w is null) sb.AppendLine("Linked in the history, but its content was not exported.\n");
        else
        {
            var facts = new List<string>();
            if (w.Type is { } type) facts.Add(Untrusted(type));
            if (w.State is { } state) facts.Add(Untrusted(state));
            if (w.Revision is { } rev) facts.Add($"revision {rev}" + (w.IsCurrent == false ? $" (current: {w.CurrentRevision}; the export is stale)" : ""));
            else if (w.ChangedDate is { } changed) facts.Add($"exported update {Untrusted(changed)}");
            if (w.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http") facts.Add($"[source]({u.AbsoluteUri})");
            if (facts.Count > 0) sb.AppendLine(string.Join(" · ", facts) + "\n");
            if (w.Description is { Length: > 0 } description) sb.AppendLine(string.Join("\n", description.Replace("\r", "").Split('\n').Select(l => $"> {Untrusted(l)}")) + "\n");
            sb.AppendLine("## Acceptance criteria\n");
            sb.AppendLine(w.Criteria.Count == 0 ? "None exported.\n"
                : string.Join("\n", w.Criteria.Select((c, i) => $"- **AC{i + 1}** {Untrusted(c)}")) + $"\n\n_AC numbers follow the order in the export{(w.Revision is { } r ? $" (revision {r})" : "")}._\n");
        }
        foreach (var warning in coverage) sb.AppendLine($"⚠ {Esc(warning)}\n");

        sb.AppendLine("## Routes\n");
        sb.AppendLine(routes.Count == 0 ? "None linked.\n" : string.Join("\n", routes.Select(r => $"- [`{r}`](../{RoutePage(r)})")) + "\n");

        sb.AppendLine("## Code\n");
        if (links.Count == 0) sb.AppendLine("None linked in the current code.\n");
        else
        {
            sb.AppendLine("| Symbol | Link | Code changed since | Commits | PRs |\n|---|---|---|---|---|");
            foreach (var l in links.OrderBy(l => l.Requirement.Status == "confirmed" ? 0 : 1).ThenBy(l => l.Symbol.Id, StringComparer.Ordinal))
            {
                var e = l.Requirement.Evidence;
                sb.AppendLine($"| {SourceLink(l.Symbol.File, l.Symbol.Line, Code(Naming.Display(l.Symbol)), sub: "requirements")} | {l.Requirement.Status} | " +
                    $"{(l.Requirement.ChangedSince ? "yes" : "—")} | {string.Join(", ", e.Select(x => $"`{x.Commit[..Math.Min(7, x.Commit.Length)]}`").Distinct())} | " +
                    $"{string.Join(", ", e.Select(x => x.PullRequest).OfType<int>().Distinct().Order().Select(p => $"#{p}"))} |");
            }
            sb.Append('\n').Append(LinkNote);
        }
        var prs = links.SelectMany(l => l.Requirement.Evidence).Select(x => x.PullRequest).OfType<int>().Distinct().Order().ToList();
        var titles = Requirements!.PullRequests.ToDictionary(p => p.Id, p => p.Title);
        if (prs.Count > 0)
            sb.AppendLine("\n## Pull requests\n\n" + string.Join("\n", prs.Select(p => $"- #{p}" + (titles.GetValueOrDefault(p) is { } t ? $" {Untrusted(t)}" : ""))) + "\n");
        return sb.ToString();
    }

    // The work items a top-level component (or a member) is linked to, for its module page.
    string? WorkItemBacklinks(Node t)
    {
        var linked = RequirementLinks().Where(l => Top(l.Symbol.Id) == t.Id).GroupBy(l => l.Requirement.Id).OrderBy(g => g.Key).ToList();
        return linked.Count == 0 ? null : "_Work items:_ " + string.Join(", ", linked.Take(MaxListed).Select(g => $"{WorkItemLink(g.Key)} ({LinkStatus(g)})"))
            + (linked.Count > MaxListed ? $", +{linked.Count - MaxListed} more" : "") + " — history, not proof";
    }
}
