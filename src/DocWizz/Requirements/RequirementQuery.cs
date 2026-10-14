using System.Text.Json;
using System.Text.Json.Nodes;

// `docwizz requirements query <dir> <query.json>` (#173): gitwizz's requirement resolver protocol. gitwizz runs the command
// with the repository and a `gitwizz.requirement-query/v1` file naming a PR's changed symbols, and reads
// `gitwizz.requirement-input/v1` from stdout (gitwizz src/Gitwizz/RequirementContext.cs). The answer comes from the
// imported index through RequirementResolver, for the requested symbols only. Read-only: no network, no AI, no writes.
//   verified   a DocWizz `confirmed` link: a commit the provider links to the work item changed the symbol
//   candidate  everything else (a confirmed PR's link, a message mention)
// What can't be answered goes to `unavailable`, so gitwizz reports `partial` instead of reading an empty list as "none".
// `analyzedCommit` is where the imported history ends: gitwizz calls the answer stale when that isn't the target.
static class RequirementQuery
{
    public const string QuerySchema = "gitwizz.requirement-query/v1";
    public const string InputSchema = "gitwizz.requirement-input/v1";
    const int MaxLinks = 10, MaxEvidence = 5, MaxUnavailable = 20, MaxTitle = 200;

    public record Symbol(string Id, string? PreviousId);

    // The requested symbols, or why the query can't be read (the caller exits 1: gitwizz reports the context as failed).
    public static (List<Symbol> Symbols, string? Error) Read(string json)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException e) { return ([], $"the query is not JSON: {e.Message}"); }
        if (root.ValueKind != JsonValueKind.Object) return ([], "the query is not a JSON object");
        var schema = root.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (schema != QuerySchema) return ([], $"the query is not {QuerySchema} (schema: {schema ?? "missing"})");
        if (!root.TryGetProperty("symbols", out var list) || list.ValueKind != JsonValueKind.Array) return ([], "the query has no symbols array");
        var symbols = new List<Symbol>();
        foreach (var (e, i) in list.EnumerateArray().Select((e, i) => (e, i)))
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 })
                return ([], $"symbols[{i}].id is missing or not a string");
            var was = e.TryGetProperty("previousId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            symbols.Add(new(id.GetString()!, was));
        }
        return ([.. symbols.DistinctBy(x => x.Id)], null);
    }

    // The answer for the requested symbols. `index` null: none was imported. `node`: the symbol in the current model.
    public static JsonObject Answer(RequirementIndex.Index? index, List<Symbol> symbols, Func<string, Node?> node, string? head, bool scopeChanged)
    {
        var links = new JsonArray();
        var unavailable = new List<(string Source, string Reason)>();
        if (index is null)
            unavailable.Add(("docwizz", "no requirement index: run docwizz requirements import <dir> <history.json> <work-items> first"));
        else
        {
            if (scopeChanged) unavailable.Add(("docwizz", "the project scope changed since the index was imported; symbol ids may differ"));
            if (index.Inputs.Truncated) unavailable.Add(("history", "the history export was truncated: older commits are not covered"));
            if (index.Inputs.Shallow) unavailable.Add(("history", "the history was exported from a shallow clone: older commits are not covered"));
            var known = index.Commits.SelectMany(c => c.Touches).Select(t => t.Symbol).ToHashSet();
            foreach (var s in symbols)
            {
                // A changed signature is a new id: its history is under the old one.
                var id = !known.Contains(s.Id) && s.PreviousId is { } was && known.Contains(was) ? was : s.Id;
                var current = node(s.Id);
                var context = RequirementResolver.For(index, id, current, head, x => node(x) is not null);
                // Not Outcome == "unavailable": a symbol missing from the current code (one a PR adds or renames) is
                // `not-current` there, and its unknown links must not read as "none" either.
                if (context.Requirements.Count == 0 && !context.Complete)
                    unavailable.Add(("history", $"{s.Id}: the work-item links of some commits that changed it are unknown"));
                foreach (var r in context.Requirements.Where(r => r.Availability == "unavailable"))
                    unavailable.Add(("work-items", $"AB#{r.Id} is linked to {s.Id} but was not exported"));
                foreach (var r in context.Requirements.Take(MaxLinks))
                    links.Add(Link(s.Id, id, r, current is null));
                if (context.Requirements.Count > MaxLinks)
                    unavailable.Add(("docwizz", $"{s.Id}: {context.Requirements.Count - MaxLinks} more linked work items left out"));
            }
        }
        var distinct = unavailable.Distinct().ToList();
        var reasons = distinct.Take(MaxUnavailable).Select(u => (JsonNode)new JsonObject { ["source"] = u.Source, ["reason"] = u.Reason }).ToList();
        if (distinct.Count > MaxUnavailable)
            reasons.Add(new JsonObject { ["source"] = "docwizz", ["reason"] = $"{distinct.Count - MaxUnavailable} more unavailable sources left out" });
        return new JsonObject
        {
            ["schema"] = InputSchema,
            ["tool"] = $"docwizz {AppVersion.Number}",
            ["analyzedCommit"] = index?.Inputs.HeadSha,
            ["links"] = links,
            ["unavailable"] = new JsonArray([.. reasons]),
        };
    }

    // Work-item text is untrusted: the index keeps its line breaks, a link title is one bounded line.
    static string OneLine(string text, int max)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }

    static JsonObject Link(string symbol, string historyId, RequirementResolver.Requirement r, bool notCurrent)
    {
        var evidence = r.Evidence.Take(MaxEvidence).Select(e => (JsonNode)(e.Relation switch
        {
            "commit-workitem" => "linked commit",
            "commit-message" => "commit message names it",
            _ => $"commit of linked PR {e.PullRequest}{(e.Confidence is "exact" or null ? "" : $" ({e.Confidence})")}",
        } + $" {e.Commit[..7]} changed {e.File}:{e.Lines}" + (historyId != symbol ? $" (as {historyId})" : ""))).ToList();
        var link = new JsonObject
        {
            ["symbol"] = symbol,
            ["requirement"] = $"AB#{r.Id}",
            ["type"] = r.Status == "confirmed" ? "verified" : "candidate",
            ["source"] = r.Evidence.Any(e => e.Relation == "commit-workitem") ? "commit-link"
                : r.Evidence.Any(e => e.Relation == "pr-workitem") ? "pr-link" : "commit-message",
            // The code as the history saw it: unknown when the symbol isn't in the current code to compare with.
            ["freshness"] = notCurrent ? "unknown" : r.ChangedSince ? "stale" : "current",
            ["evidence"] = new JsonArray([.. evidence]),
        };
        if (r.WorkItem?.Title is { } title) link["title"] = OneLine(title, MaxTitle);
        if (r.WorkItem?.Revision is { } revision) link["revision"] = revision;
        return link;
    }
}
