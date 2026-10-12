// Deterministic impact of changes between two explicit evidence catalog snapshots.
// This compares declaration hashes, not repository source-file contents.
internal static class ProjectEvidenceImpact
{
    internal sealed record ChangedEvidence(string Id, string Change, string[] RelatedIds);
    internal sealed record Report(IReadOnlyList<ChangedEvidence> Evidence, IReadOnlyList<EvidenceLink> AddedLinks, IReadOnlyList<EvidenceLink> RemovedLinks);

    public static Report Compare(ProjectEvidenceCatalog before, ProjectEvidenceCatalog after)
    {
        var oldItems = before.Items.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var newItems = after.Items.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var oldLinks = before.Links.ToHashSet();
        var newLinks = after.Links.ToHashSet();
        var changes = oldItems.Keys.Union(newItems.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Where(id => !oldItems.TryGetValue(id, out var a) || !newItems.TryGetValue(id, out var z) || a.Hash != z.Hash)
            .Select(id =>
            {
                var change = !oldItems.ContainsKey(id) ? "added" : !newItems.ContainsKey(id) ? "removed" : "changed";
                var related = before.Links.Concat(after.Links)
                    .Where(link => link.From == id || link.To == id)
                    .Select(link => link.From == id ? link.To : link.From)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                return new ChangedEvidence(id, change, related);
            }).ToArray();
        static EvidenceLink[] Ordered(IEnumerable<EvidenceLink> links) =>
            links.OrderBy(x => x.From, StringComparer.Ordinal).ThenBy(x => x.To, StringComparer.Ordinal).ToArray();
        return new(changes, Ordered(newLinks.Except(oldLinks)), Ordered(oldLinks.Except(newLinks)));
    }
}
