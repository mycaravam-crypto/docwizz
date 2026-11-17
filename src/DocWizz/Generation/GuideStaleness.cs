/// <summary>Flags guides captured from an older revision of their work item than the requirements export.</summary>
static class GuideStaleness
{
    public static IReadOnlyList<(JourneyEvidence.Guide Guide, int Current)> Stale(
        IReadOnlyList<JourneyEvidence.Guide> guides, RequirementIndex.Index? requirements)
    {
        if (requirements is null) return [];
        var current = requirements.WorkItems.Where(w => !w.Id.IsGithub && w.Revision is not null)
            .GroupBy(w => w.Id.Number).ToDictionary(g => g.Key, g => g.Max(w => w.Revision!.Value));
        return [.. guides.Where(g => current.TryGetValue(g.WorkItem, out var rev) && rev > g.WorkItemRevision)
            .Select(g => (g, current[g.WorkItem]))];
    }

    public static string Section(IReadOnlyList<JourneyEvidence.Guide> guides, RequirementIndex.Index? requirements)
    {
        var stale = Stale(guides, requirements);
        return stale.Count == 0 ? "" :
            "\n## Stale guides\n\nThe work item changed after capture; recapture before publishing.\n\n" +
            string.Join("\n", stale.Select(s => $"- [{s.Guide.Title}]({s.Guide.Id}.md): WI-{s.Guide.WorkItem} captured at revision {s.Guide.WorkItemRevision}, now {s.Current}")) + "\n";
    }
}
