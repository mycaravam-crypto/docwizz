using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

static partial class RequirementIndex
{
    // Raw, offline gh issue view --json number,url,title,body,labels,state,updatedAt output. The URL identifies
    // both the repository and object type; a PR or a same-number issue from another repository is never substituted.
    static bool IsGithubIssue(JsonElement item) => item.ValueKind == JsonValueKind.Object
        && !item.TryGetProperty("schema", out _) && item.TryGetProperty("number", out _);

    static WorkItem NormalizeGithub(JsonElement item, string digest, string name)
    {
        string Field(string field) => item.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()! : throw new ArgumentException($"{name}: GitHub issue requires '{field}'; export number,url,title,body,labels,state,updatedAt with gh issue view --json");
        var identity = RequirementId.FromIssueUrl(Field("url"));
        if (item.GetProperty("number").ValueKind != JsonValueKind.Number || !item.GetProperty("number").TryGetInt32(out var number) || number != identity.Number)
            throw new ArgumentException($"{name}: GitHub issue number does not match its URL");
        var state = Field("state");
        if (state is not ("OPEN" or "CLOSED")) throw new ArgumentException($"{name}: GitHub issue state must be OPEN or CLOSED");
        var timestamp = Field("updatedAt");
        if (!Regex.IsMatch(timestamp, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$")
            || !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var updated))
            throw new ArgumentException($"{name}: GitHub updatedAt must be a timestamp");
        var body = Field("body");
        var warnings = new List<string>();
        if (body.Length > MaxDescription) warnings.Add($"description cut to {MaxDescription} characters");
        if (!item.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"{name}: GitHub issue requires a labels array");
        var names = labels.EnumerateArray().Select(l => l.ValueKind == JsonValueKind.Object
                && l.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? Clean(n.GetString(), 100, null)
                : throw new ArgumentException($"{name}: GitHub labels must have a name"))
            .OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();
        if (names.Count > 20) { warnings.Add("labels cut to 20"); names = names[..20]; }
        var criteria = GithubCriteria(body).Select(c => Clean(c, MaxCriterion, null)).OfType<string>().ToList();
        if (criteria.Count > MaxCriteria) { warnings.Add($"acceptance criteria cut to {MaxCriteria}"); criteria = criteria[..MaxCriteria]; }
        return new(identity, null, null, null, "Issue", state, Clean(Field("title"), MaxTitle, null), Field("url"),
            updated.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), "body", Clean(body, MaxDescription, null),
            criteria.Count == 0 ? null : "body:acceptance-criteria", criteria, digest, warnings, names);
    }

    // Only an explicitly named section contributes criteria. Other prose, checklists and code blocks remain body
    // text. These are author statements, never inferred requirements or executable instructions.
    static List<string> GithubCriteria(string body)
    {
        var active = false;
        var fenced = false;
        var result = new List<string>();
        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
        {
            if (Regex.IsMatch(line, @"^\s*(```|~~~)")) { fenced = !fenced; continue; }
            if (fenced) continue;
            if (Regex.IsMatch(line, @"^\s*#{1,6}\s+"))
            {
                active = Regex.IsMatch(line, @"^\s*#{1,6}\s+acceptance(?:\s+criteria)?\s*:?\s*#*\s*$", RegexOptions.IgnoreCase);
                continue;
            }
            if (active && Regex.Match(line, @"^\s*(?:[-*+]|\d+[.)])\s+(?:\[[ xX]\]\s+)?(.+)$") is { Success: true } match)
                result.Add(match.Groups[1].Value.Trim());
        }
        return [.. result.Distinct()];
    }
}
