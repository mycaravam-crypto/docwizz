using System.Text;
using System.Text.Json;

// Optional, offline release-history input. GitWizz owns discovery; DocWizz only renders its v2 export.
static class VersionHistoryPage
{
    internal sealed record Data(string Markdown, List<(string Version, string Status)> Versions);

    /// <summary>Reads an optional GitWizz v2 export and renders a release-history page without accessing Git or the network.</summary>
    public static Data? Load(string root)
    {
        var path = Path.Join(root, "docs", "architecture", "versions.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var json = doc.RootElement;
            if (json.ValueKind != JsonValueKind.Object ||
                Text(json, "schema") != "gitwizz.version-history/v2" ||
                !json.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("expected gitwizz.version-history/v2 with versions array");

            var output = new StringBuilder("# Version history\n\n");
            output.AppendLine("Source: `docs/architecture/versions.json` (GitWizz release history; not document revisions).\n");
            if (Flag(json, "shallow")) output.AppendLine("> Warning: shallow Git clone; history may be incomplete.\n");
            Warnings(output, json);
            var listed = new List<(string Version, string Status)>();
            foreach (var v in versions.EnumerateArray())
            {
                var version = Required(v, "version");
                var status = Required(v, "status");
                if (status is not ("released" or "unreleased"))
                    throw new ArgumentException($"unknown status for {version}: {status}");
                var sha = Required(v, "commitSha");
                var date = Required(v, "committedAt");
                var tagged = Text(v, "taggedAt");
                listed.Add((version, status));
                output.AppendLine($"## {Escape(version)} ({(status == "released" ? "released" : "unreleased")})\n");
                output.Append($"Date: `{Escape((tagged ?? date).Split('T')[0])}` · Commit: `{Escape(sha)}`");
                if (v.TryGetProperty("bases", out var bases) && bases.ValueKind == JsonValueKind.Array)
                {
                    var names = bases.EnumerateArray().Select(b => Escape(Required(b, "version"))).ToList();
                    if (names.Count > 0) output.Append(" · Based on: " + string.Join(", ", names));
                }
                output.AppendLine().AppendLine();
                if (Flag(v, "truncated")) output.AppendLine("> Warning: changes were truncated by GitWizz.\n");
                Warnings(output, v);

                // PRs own their commits. List only changes not attributed to a PR to avoid duplicates.
                var changes = RequiredArray(v, "changes");
                var prs = v.TryGetProperty("pullRequests", out var prArray) && prArray.ValueKind == JsonValueKind.Array
                    ? prArray.EnumerateArray().ToList() : [];
                foreach (var category in new[] { "breaking", "feature", "fix", "performance", "other" })
                {
                    var groupPrs = prs.Where(p => Matches(category, Text(p, "category"), Flag(p, "breaking"))).ToList();
                    var direct = changes.Where(c => Text(c, "pullRequest") is null && Text(c, "kind") != "merge" &&
                        Matches(category, Text(c, "category"), Flag(c, "breaking"))).ToList();
                    if (groupPrs.Count == 0 && direct.Count == 0) continue;
                    var label = category switch
                    {
                        "breaking" => "Breaking changes", "feature" => "Features",
                        "fix" => "Fixes", "performance" => "Performance", _ => "Other changes"
                    };
                    output.AppendLine("### " + label + "\n");
                    foreach (var pr in groupPrs)
                        output.AppendLine($"- {Escape(Required(pr, "description"))} ({Escape(Required(pr, "id"))})");
                    foreach (var c in direct)
                        output.AppendLine($"- {Escape(Required(c, "description"))} (`{Escape(Required(c, "sha")[..Math.Min(8, Required(c, "sha").Length)])}`)");
                    output.AppendLine();
                }
            }
            if (listed.Count == 0) output.AppendLine("No releases or release branches in the export.\n");
            return new Data(output.ToString(), listed);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new ArgumentException($"Invalid GitWizz version history at {path}: {e.Message}", e);
        }
    }

    static bool Matches(string section, string? category, bool breaking)
    {
        if (section == "breaking") return breaking;
        if (breaking) return false;
        if (section == "other") return category is not ("feature" or "fix" or "performance");
        return section == category;
    }

    static JsonElement.ArrayEnumerator RequiredArray(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"missing array: {name}");
        return value.EnumerateArray();
    }

    static string Required(JsonElement e, string name) =>
        Text(e, name) is { Length: > 0 } value ? value : throw new ArgumentException($"missing string: {name}");

    static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static bool Flag(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    static void Warnings(StringBuilder output, JsonElement e)
    {
        if (!e.TryGetProperty("warnings", out var warnings) || warnings.ValueKind != JsonValueKind.Array) return;
        foreach (var message in warnings.EnumerateArray()
            .Where(w => w.ValueKind == JsonValueKind.String)
            .Select(w => w.GetString()).OfType<string>())
            output.AppendLine("> Warning: " + Escape(message) + "\n");
    }

    // Treat all commit messages and titles as untrusted text (Markdown and HTML are disabled in HTML rendering).
    static string Escape(string value) => value.Replace("\\", "\\\\").Replace("`", "\\`")
        .Replace("*", "\\*").Replace("_", "\\_").Replace("[", "\\[")
        .Replace("]", "\\]").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\r", " ").Replace("\n", " ");
}
