using System.Security.Cryptography;
using System.Text;

/// <summary>Flags guides whose requirement or route code changed after they were captured.</summary>
static class GuideStaleness
{
    /// <summary>Guides whose requirement has a newer version in the index than the one they were captured from:
    /// a higher ADO revision, or a later GitHub updatedAt.</summary>
    /// <returns>Each stale guide with the version it was captured from and the current one.</returns>
    public static IReadOnlyList<(JourneyEvidence.Guide Guide, string Was, string Now)> Stale(
        IReadOnlyList<JourneyEvidence.Guide> guides, RequirementIndex.Index? requirements)
    {
        if (requirements is null) return [];
        var current = requirements.WorkItems.GroupBy(w => w.Id).ToDictionary(g => g.Key, g => g.First());
        var stale = new List<(JourneyEvidence.Guide, string, string)>();
        foreach (var g in guides)
        {
            if (g.Requirement is not { } id || !current.TryGetValue(id, out var w)) continue;
            if (!id.IsGithub && g.WorkItemRevision > 0 && w.Revision is { } rev && rev > g.WorkItemRevision)
                stale.Add((g, $"revision {g.WorkItemRevision}", $"revision {rev}"));
            else if (id.IsGithub && g.Changed is { } was && w.ChangedDate is { } now && Later(now, was))
                stale.Add((g, $"update {was}", $"update {now}"));
        }
        return stale;
    }

    // GitHub updatedAt values may carry different offsets; text that is no timestamp counts as changed when it differs.
    static bool Later(string now, string was) =>
        DateTimeOffset.TryParse(now, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var n)
        && DateTimeOffset.TryParse(was, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var w)
            ? n > w : now != was;

    /// <summary>A freshly captured guide with its requirement's current version, to compare later generations against.</summary>
    public static JourneyEvidence.Guide Versioned(JourneyEvidence.Guide guide, RequirementIndex.Index? requirements) =>
        guide.Requirement is { } id && requirements?.WorkItems.FirstOrDefault(w => w.Id == id) is { } w
            ? guide with { WorkItemRevision = id.IsGithub ? 0 : w.Revision ?? 0, Changed = id.IsGithub ? w.ChangedDate : null }
            : guide;

    /// <summary>Guides that name a requirement whose version was unknown at capture, so a change to it cannot be detected.</summary>
    public static IReadOnlyList<JourneyEvidence.Guide> Untracked(IReadOnlyList<JourneyEvidence.Guide> guides) =>
        [.. guides.Where(g => g.Requirement is { } id && (id.IsGithub ? g.Changed is null : g.WorkItemRevision <= 0))];

    /// <summary>Why a guide is stale, for its page; null when it is current.</summary>
    public static string? Reason(JourneyEvidence.Guide guide, RequirementIndex.Index? requirements, bool codeChanged) =>
        Stale([guide], requirements) is [var s]
            ? $"{guide.Requirement!.Value.Display} changed since the capture ({s.Was}, now {s.Now})"
            : codeChanged ? $"the code behind `{guide.Route.Replace("`", "")}` changed since the capture" : null;

    /// <summary>A hash of the source files behind a route (its components and what their flow reaches); null when there are none.</summary>
    public static string? Code(string root, CodeModel model, FlowGraph flow, string route)
    {
        var nodes = model.Nodes.ToDictionary(n => n.Id);
        var files = model.Nodes.Where(n => n.Kind == "route" && (n.Route ?? n.Name) == route)
            .SelectMany(r => model.Edges.Where(e => e.Kind == "routes-to" && e.From == r.Id).Select(e => e.To)
                .Concat(flow.TraceOf(r.Id).Layers.SelectMany(l => l)).Select(id => nodes.GetValueOrDefault(id)?.File).Append(r.File))
            .OfType<string>().Distinct().Order(StringComparer.Ordinal).Where(f => File.Exists(Path.Join(root, f))).ToList();
        if (files.Count == 0) return null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Without carriage returns: a checkout with other line endings is the same code.
        foreach (var f in files) hash.AppendData(Encoding.UTF8.GetBytes(f + "\0" + File.ReadAllText(Path.Join(root, f)).Replace("\r", "") + "\0"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>A captured guide whose route code is no longer what it was captured from.</summary>
    public static bool CodeChanged(JourneyEvidence.Guide guide, string root, CodeModel model, FlowGraph flow) =>
        guide.Code is { } code && code != Code(root, model, flow, guide.Route);

    public static string Section(IReadOnlyList<JourneyEvidence.Guide> guides, RequirementIndex.Index? requirements,
        Func<JourneyEvidence.Guide, bool>? codeChanged = null)
    {
        var lines = Stale(guides, requirements)
            .Select(s => $"- [{JourneyEvidence.Escape(s.Guide.Title)}]({s.Guide.Id}.md): {s.Guide.Requirement!.Value.Display} captured at {s.Was}, now {s.Now}")
            .Concat(guides.Where(g => codeChanged?.Invoke(g) == true)
                .Select(g => $"- [{JourneyEvidence.Escape(g.Title)}]({g.Id}.md): the code behind `{g.Route.Replace("`", "")}` changed since the capture on {g.Captured}")).ToList();
        var untracked = Untracked(guides)
            .Select(g => $"- [{JourneyEvidence.Escape(g.Title)}]({g.Id}.md): {g.Requirement!.Value.Display}").ToList();
        return (lines.Count == 0 ? "" : "\n## Stale guides\n\nChanged after capture; recapture before publishing. The manual leaves them out until then.\n\n" + string.Join("\n", lines) + "\n")
            + (untracked.Count == 0 ? "" : "\n## Requirement not tracked\n\nThese guides name a requirement that was not imported when they were captured, so a change to it " +
                "cannot be detected. Run `docwizz requirements import`, then capture again.\n\n" + string.Join("\n", untracked) + "\n");
    }
}
