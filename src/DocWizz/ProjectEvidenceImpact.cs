// Deterministic impact of changes between two explicit evidence catalog snapshots.
// This compares declaration hashes, not repository source-file contents.
internal static class ProjectEvidenceImpact
{
    // Fields: the declaration fields that differ for a `changed` record (empty for added/removed).
    // AffectedTests: test records reachable from this record along explicit links (requirement → architecture → test),
    // in either snapshot. Following declared, typed links is not inference; nothing beyond them is assumed.
    internal sealed record ChangedEvidence(string Id, string Change, string[] Fields, string[] RelatedIds, string[] AffectedTests);
    internal sealed record Report(IReadOnlyList<ChangedEvidence> Evidence, IReadOnlyList<EvidenceLink> AddedLinks, IReadOnlyList<EvidenceLink> RemovedLinks);

    public static Report Compare(ProjectEvidenceCatalog before, ProjectEvidenceCatalog after)
    {
        var oldItems = before.Items.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var newItems = after.Items.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var oldLinks = before.Links.ToHashSet();
        var newLinks = after.Links.ToHashSet();
        var allLinks = oldLinks.Union(newLinks).ToList();
        var outgoing = allLinks.ToLookup(x => x.From, x => x.To, StringComparer.Ordinal);
        var incoming = allLinks.ToLookup(x => x.To, x => x.From, StringComparer.Ordinal);
        string? Kind(string id) => newItems.GetValueOrDefault(id)?.Kind ?? oldItems.GetValueOrDefault(id)?.Kind;

        string[] Tests(string id)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { id };
            var queue = new Queue<string>([id]);
            var tests = new List<string>();
            while (queue.TryDequeue(out var current))
                foreach (var next in outgoing[current])
                    if (seen.Add(next))
                    {
                        if (Kind(next) == "test") tests.Add(next);
                        queue.Enqueue(next);
                    }
            return tests.Order(StringComparer.Ordinal).ToArray();
        }

        var changes = oldItems.Keys.Union(newItems.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Where(id => !oldItems.TryGetValue(id, out var a) || !newItems.TryGetValue(id, out var z) || a.Hash != z.Hash)
            .Select(id =>
            {
                oldItems.TryGetValue(id, out var a);
                newItems.TryGetValue(id, out var z);
                var change = a is null ? "added" : z is null ? "removed" : "changed";
                string[] fields = a is null || z is null ? [] :
                    new[] { ("kind", a.Kind, z.Kind), ("text", a.Text, z.Text), ("source", a.Source, z.Source), ("version", a.Version, z.Version) }
                        .Where(f => !string.Equals(f.Item2, f.Item3, StringComparison.Ordinal)).Select(f => f.Item1).ToArray();
                var related = outgoing[id].Concat(incoming[id]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                return new ChangedEvidence(id, change, fields, related, Tests(id));
            }).ToArray();
        static EvidenceLink[] Ordered(IEnumerable<EvidenceLink> links) =>
            links.OrderBy(x => x.From, StringComparer.Ordinal).ThenBy(x => x.To, StringComparer.Ordinal).ToArray();
        return new(changes, Ordered(newLinks.Except(oldLinks)), Ordered(oldLinks.Except(newLinks)));
    }
}
