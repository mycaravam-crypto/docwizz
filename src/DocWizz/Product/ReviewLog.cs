using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

// A human-maintained record of reviews of one file: who set which state on which date, for the exact content they
// reviewed (its SHA-256). docwizz only reads it; nothing docwizz generates can write or imply a review. Subject-agnostic:
// the caller names the states it accepts (ProductCheck: V-Modell XT product states, #93).
internal sealed record Review(string State, string By, DateOnly Date, string Sha256, string? Note);

internal sealed record ReviewLog(IReadOnlyList<Review> Reviews)
{
    // Strict: an unknown key, a state outside `states`, a missing field or an out-of-order date throws, so a half-written
    // record never counts as a review.
    public static ReviewLog Parse(TextReader reader, IReadOnlySet<string> states)
    {
        var stream = new YamlStream();
        try { stream.Load(reader); }
        catch (YamlDotNet.Core.YamlException ex) { throw new ArgumentException($"invalid review YAML: {ex.Message}", ex); }
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new ArgumentException("review log must contain exactly one mapping");
        ProjectEvidenceCatalog.Keys(root, ["schema", "reviews"], "log");
        if (ProjectEvidenceCatalog.Value(root, "schema", "log") != "1" || ProjectEvidenceCatalog.Node(root, "schema") is not YamlScalarNode { Style: YamlDotNet.Core.ScalarStyle.Plain })
            throw new ArgumentException("log.schema must be unquoted 1");
        if (ProjectEvidenceCatalog.Node(root, "reviews") is not YamlSequenceNode entries)
            throw new ArgumentException("log.reviews must be a list");
        var reviews = new List<Review>();
        foreach (var (entry, i) in entries.Children.Select((x, i) => (x, i)))
        {
            var at = $"reviews[{i}]";
            if (entry is not YamlMappingNode item) throw new ArgumentException($"{at} must be a mapping");
            ProjectEvidenceCatalog.Keys(item, ["state", "by", "date", "sha256", "note"], at);
            var state = ProjectEvidenceCatalog.Value(item, "state", at);
            var by = ProjectEvidenceCatalog.Value(item, "by", at);
            var note = ProjectEvidenceCatalog.Node(item, "note") is null ? null : ProjectEvidenceCatalog.Value(item, "note", at);
            if (!states.Contains(state)) throw new ArgumentException($"{at}.state must be one of {string.Join(", ", states.Order(StringComparer.Ordinal))}");
            if (new[] { by, note ?? "" }.Any(x => x.Contains('\n') || x.Contains('\r'))) throw new ArgumentException($"{at} values must be single-line");
            if (!DateOnly.TryParseExact(ProjectEvidenceCatalog.Value(item, "date", at), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new ArgumentException($"{at}.date must be a date like 2026-10-14");
            // An append-only history: a later entry can't predate an earlier one.
            if (reviews.Count > 0 && date < reviews[^1].Date) throw new ArgumentException($"{at}.date is before the previous review's");
            var sha = ProjectEvidenceCatalog.Value(item, "sha256", at);
            if (!Regex.IsMatch(sha, "^[a-f0-9]{64}$")) throw new ArgumentException($"{at}.sha256 must be a lowercase SHA-256 hex digest");
            reviews.Add(new(state, by, date, sha, note));
        }
        return new(reviews);
    }

    // The reviewed content's identity: the file's bytes, exactly as written.
    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
