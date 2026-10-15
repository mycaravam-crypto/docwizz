using System.Text;

// Route pages: one per frontend path, the entry point into what the page shows and the code behind it — its screenshot,
// components and the flow down to the backend endpoints. What the scan could not resolve is listed as a gap, never guessed.
partial class Generator
{
    static string RoutePath(Node route) => route.Route ?? route.Name;

    // The page of a path; named like its screenshot, so /a-b and /a/b stay apart.
    internal static string RoutePage(string path) => $"routes/{Path.GetFileNameWithoutExtension(ScreenshotCapture.FileName(path))}.md";

    // Each path's gaps, as its page lists them: for the quality page.
    readonly Dictionary<string, List<string>> routeGaps = new(StringComparer.Ordinal);

    // Where route, requirement and verification links are missing, each with its page: the quality page's last section.
    string TraceabilityGaps()
    {
        if (routeGaps.Count == 0) foreach (var _ in RoutePages()) { }
        var sb = new StringBuilder("\n## Traceability gaps\n\n");
        var open = routeGaps.Where(g => g.Value.Count > 0).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        sb.AppendLine(routeGaps.Count == 0 ? "No frontend routes.\n" : open.Count == 0 ? "Every route resolves to code, endpoints and work items.\n"
            : $"| Route | Gap |\n|---|---|\n" + string.Join("\n", open.SelectMany(g => g.Value.Select(x => $"| [`{g.Key}`]({RoutePage(g.Key)}) | {x} |"))) + "\n");
        if (Requirements is not { } index)
        {
            sb.AppendLine("No requirement history imported (`docwizz requirements import`): work items and acceptance criteria are unknown.");
            return sb.ToString();
        }
        foreach (var w in RequirementResolver.Coverage(index, Head)) sb.AppendLine($"⚠ {Esc(w)}\n");
        var linked = RequirementLinks().Select(l => l.Requirement.Id).ToHashSet();
        var unlinked = index.WorkItems.Where(w => !linked.Contains(w.Id)).OrderBy(w => w.Id).ToList();
        if (unlinked.Count > 0)
            sb.AppendLine($"Work items linked to no current code ({unlinked.Count}): " + string.Join(", ", unlinked.Select(w => WorkItemLink(w.Id, ""))) + "\n");
        var criteria = index.WorkItems.Sum(w => w.Criteria.Count);
        if (criteria > 0)
            sb.AppendLine($"Acceptance criteria: {criteria} in {index.WorkItems.Count(w => w.Criteria.Count > 0)} work items, all **unverified**: no test results are imported for them.");
        return sb.ToString();
    }

    IEnumerable<(string Page, string Body)> RoutePages() =>
        model.Nodes.Where(n => n.Kind == "route").GroupBy(RoutePath).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (RoutePage(g.Key), RoutePageBody(g.Key, g.ToList())));

    string RoutePageBody(string path, List<Node> routes)
    {
        var sb = new StringBuilder($"# Route `{path}`\n\n[Frontend](../frontend.md) / route\n\n");
        var components = routes.SelectMany(r => model.Edges.Where(e => e.Kind == "routes-to" && e.From == r.Id).Select(e => e.To))
            .Where(nodes.ContainsKey).Distinct().Select(id => nodes[id]).ToList();
        var gaps = new List<string>();
        sb.AppendLine("Component: " + (components.Count == 0 ? "—" : string.Join(", ", components.Select(UnitLink))) + "\n");
        if (components.Count == 0) gaps.Add("No component found for this path (dynamic or lazy route?).");

        if (Screenshots.TryGetValue(path, out var image) && File.Exists(Path.Combine(outDir, image)))
            sb.AppendLine($"![Screenshot of {path}](../{image})\n\n_The screenshot shows the page as captured; it does not prove the page behaves correctly._\n");

        var traces = routes.Select(r => flowGraph.TraceOf(r.Id)).Where(t => t.Layers.Count > 0).ToList();
        var units = traces.SelectMany(t => t.Layers.SelectMany(l => l)).Distinct().ToList();
        if (traces.Count > 0)
        {
            sb.AppendLine("## Flow\n\nFrom the page to the backend, hop by hop (calls, rendering, HTTP); see [API](../api.md) for what the endpoints do.\n");
            foreach (var t in traces)
                for (var i = 0; i < t.Layers.Count; i++)
                    sb.AppendLine($"{i + 1}. " + string.Join(", ", t.Layers[i].Select(UnitLink)));
            sb.AppendLine();
        }
        var endpoints = units.Where(u => nodes.TryGetValue(u, out var n) && n.Tags?.Contains("endpoint") == true).Select(u => nodes[u]).ToList();
        if (endpoints.Count > 0)
        {
            sb.AppendLine("## Endpoints\n\n| Endpoint | Handler | Summary |\n|---|---|---|");
            foreach (var e in endpoints.OrderBy(e => e.Route?.TrimStart('/')).ThenBy(e => e.Tags![1]))
                sb.AppendLine($"| `{Naming.EndpointLabel(e)}` | {SourceLink(e.File, e.Line, CodeModel.Location(e), sub: "routes")} | {Esc(Summary(e) ?? "—")} |");
            sb.AppendLine();
        }
        else if (components.Count > 0) gaps.Add("Reaches no backend endpoint.");
        foreach (var u in units.Where(u => u.StartsWith("http:")).Order(StringComparer.Ordinal))
            gaps.Add($"Calls `{Esc(u[5..])}`, but no backend endpoint matches it.");

        sb.AppendLine(RouteRequirements(path, gaps));
        routeGaps[path] = gaps;
        sb.AppendLine("## Gaps\n");
        sb.AppendLine(gaps.Count == 0 ? "None found.\n" : string.Join("\n", gaps.Select(g => $"- {g}")) + "\n");
        return sb.ToString();
    }

    // A flow unit linked to its module page from a page one folder down; an unmatched HTTP call stays plain text.
    string UnitLink(string id) => nodes.TryGetValue(id, out var n) ? UnitLink(n) : Esc(flowGraph.UnitLabel(id));
    string UnitLink(Node n) => n.Kind == "external" ? Esc(flowGraph.UnitLabel(n.Id))
        : $"[{Text(flowGraph.UnitLabel(n.Id))}](../modules/{Naming.Slug(Naming.Folder(n.File))}.md#{Anchor(Top(n.Id))})";
}
