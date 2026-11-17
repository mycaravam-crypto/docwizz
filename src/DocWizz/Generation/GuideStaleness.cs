using System.Security.Cryptography;
using System.Text;

/// <summary>Flags guides whose work item or route code changed after they were captured.</summary>
static class GuideStaleness
{
    public static IReadOnlyList<(JourneyEvidence.Guide Guide, int Current)> Stale(
        IReadOnlyList<JourneyEvidence.Guide> guides, RequirementIndex.Index? requirements)
    {
        if (requirements is null) return [];
        var current = requirements.WorkItems.Where(w => !w.Id.IsGithub && w.Revision is not null)
            .GroupBy(w => w.Id.Number).ToDictionary(g => g.Key, g => g.Max(w => w.Revision!.Value));
        return [.. guides.Where(g => g.JourneyFile is null && current.TryGetValue(g.WorkItem, out var rev) && rev > g.WorkItemRevision)
            .Select(g => (g, current[g.WorkItem]))];
    }

    /// <summary>A hash of the source files behind a route (its components and what their flow reaches); null when there are none.</summary>
    public static string? Code(string root, CodeModel model, FlowGraph flow, string route)
    {
        var nodes = model.Nodes.ToDictionary(n => n.Id);
        var files = model.Nodes.Where(n => n.Kind == "route" && (n.Route ?? n.Name) == route)
            .SelectMany(r => model.Edges.Where(e => e.Kind == "routes-to" && e.From == r.Id).Select(e => e.To)
                .Concat(flow.TraceOf(r.Id).Layers.SelectMany(l => l)).Select(id => nodes.GetValueOrDefault(id)?.File).Append(r.File))
            .OfType<string>().Distinct().Order(StringComparer.Ordinal).Where(f => File.Exists(Path.Combine(root, f))).ToList();
        if (files.Count == 0) return null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Without carriage returns: a checkout with other line endings is the same code.
        foreach (var f in files) hash.AppendData(Encoding.UTF8.GetBytes(f + "\0" + File.ReadAllText(Path.Combine(root, f)).Replace("\r", "") + "\0"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>A captured guide whose route code is no longer what it was captured from.</summary>
    public static bool CodeChanged(JourneyEvidence.Guide guide, string root, CodeModel model, FlowGraph flow) =>
        guide.Code is { } code && code != Code(root, model, flow, guide.Route);

    public static string Section(IReadOnlyList<JourneyEvidence.Guide> guides, RequirementIndex.Index? requirements,
        Func<JourneyEvidence.Guide, bool>? codeChanged = null)
    {
        var lines = Stale(guides, requirements)
            .Select(s => $"- [{JourneyEvidence.Escape(s.Guide.Title)}]({s.Guide.Id}.md): WI-{s.Guide.WorkItem} captured at revision {s.Guide.WorkItemRevision}, now {s.Current}")
            .Concat(guides.Where(g => codeChanged?.Invoke(g) == true)
                .Select(g => $"- [{JourneyEvidence.Escape(g.Title)}]({g.Id}.md): the code behind `{g.Route.Replace("`", "")}` changed since the capture on {g.Captured}")).ToList();
        return lines.Count == 0 ? "" : "\n## Stale guides\n\nChanged after capture; recapture before publishing.\n\n" + string.Join("\n", lines) + "\n";
    }
}
