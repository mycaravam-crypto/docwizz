using System.Text;
using System.Text.Json;

// Area-scoped inventory and the independent evidence overlays from imported history and ADO test snapshots.
// Neither a provider link nor a test-code reference establishes implementation, verification or test execution.
static partial class RequirementsCatalog
{
    /// <summary>Load the previously validated local catalog for the normal documentation generator, if present.</summary>
    public static JsonElement? LoadFromRepository(string root)
    {
        var file = Path.Join(Path.GetFullPath(root), "docs", ".docwizz", "workitem-catalog.json");
        if (!File.Exists(file)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        Validate(document.RootElement);
        return document.RootElement.Clone();
    }

    // Require the same ADO server/collection and project; numeric work-item IDs alone are not globally unique.
    static bool SameOrigin(JsonElement catalog, RequirementIndex.Index history)
    {
        if (history.Inputs.Provider != "azure-devops"
            || history.Inputs.Repository is not { } repository
            || !Uri.TryCreate(repository, UriKind.Absolute, out var repo)
            || !Uri.TryCreate(catalog.GetProperty("source").GetString(), UriKind.Absolute, out var server)
            || repo.Scheme != server.Scheme || repo.Host != server.Host || repo.Port != server.Port
            || repo.UserInfo.Length > 0 || server.UserInfo.Length > 0)
            return false;
        var prefix = server.AbsolutePath.TrimEnd('/');
        var path = repo.AbsolutePath;
        if (!path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return false;
        var after = Uri.UnescapeDataString(path[(prefix.Length + 1)..]).Split('/', 2)[0];
        return after.Equals(catalog.GetProperty("project").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    sealed record CodeEvidence(string Symbol, string Status, bool ChangedSince, List<string> Commits,
        List<string> Tests);
    sealed record CaseEvidence(int Id, string Title);

    /// <summary>Generate deterministic requirement pages with separately labelled history, code-test and ADO test-case evidence.</summary>
    public static IEnumerable<(string Page, string Body)> Pages(JsonElement catalog, RequirementIndex.Index? history,
        string? head, CodeModel model, IReadOnlyList<AdoTestSnapshot.ImportData> snapshots)
    {
        Validate(catalog);
        var items = catalog.GetProperty("items").EnumerateArray().OrderBy(i => i.GetProperty("id").GetInt32()).ToArray();
        var itemIds = items.Select(i => i.GetProperty("id").GetInt32()).ToHashSet();
        var matchingHistory = history is not null && SameOrigin(catalog, history);
        var coverage = matchingHistory ? RequirementResolver.Coverage(history!, head).ToArray() : [];
        var historyUnavailable = history is not null && !matchingHistory;
        var incompleteHistory = coverage.Length > 0 ||
            (history?.Warnings.Count > 0 && matchingHistory) ||
            (matchingHistory && history!.Commits.Any(c => c.Mapping == "missing" || c.Linkage is "unavailable" or "denied" or "unknown"));
        var nodes = model.Nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var linked = new Dictionary<int, List<CodeEvidence>>();
        if (matchingHistory)
        {
            var testLinks = new TestLinks(model);
            foreach (var symbol in history!.Commits.SelectMany(c => c.Touches).Select(t => t.Symbol)
                         .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (!nodes.TryGetValue(symbol, out var node)) continue;
                var resolved = RequirementResolver.For(history, symbol, node, head, nodes.ContainsKey);
                foreach (var r in resolved.Requirements.Where(r => !r.Id.IsGithub && itemIds.Contains(r.Id.Number)))
                {
                    if (!linked.TryGetValue(r.Id.Number, out var group)) linked[r.Id.Number] = group = [];
                    group.Add(new CodeEvidence(symbol, r.Status, r.ChangedSince,
                        r.Evidence.Select(e => e.Commit).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                        testLinks.Of(symbol).Select(t => TestLinks.Short(t.Test)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()));
                }
            }
        }

        // ADO test-case IDs are explicit work-item relationships from a separate, offline test snapshot.
        // A snapshot does not supply the ADO server, so evidence is scoped by the project AND the exact work-item ID.
        var testCases = new Dictionary<int, List<CaseEvidence>>();
        var assessed = new HashSet<int>();
        var testsAvailable = false;
        foreach (var snapshot in snapshots.Where(s =>
                     s.Project.Equals(catalog.GetProperty("project").GetString(), StringComparison.OrdinalIgnoreCase)))
        {
            testsAvailable = true;
            using var document = JsonDocument.Parse(snapshot.Content);
            var testNames = document.RootElement.GetProperty("testCases").EnumerateArray()
                .ToDictionary(t => t.GetProperty("id").GetInt32(),
                    t => t.GetProperty("title").GetString() ?? "");
            foreach (var requirement in document.RootElement.GetProperty("requirements").EnumerateArray())
            {
                var id = requirement.GetProperty("id").GetInt32();
                if (!itemIds.Contains(id)) continue;
                assessed.Add(id);
                if (!testCases.TryGetValue(id, out var cases)) testCases[id] = cases = [];
                foreach (var testCaseId in requirement.GetProperty("testCaseIds").EnumerateArray().Select(t => t.GetInt32()))
                    if (testNames.TryGetValue(testCaseId, out var name) && cases.All(c => c.Id != testCaseId))
                        cases.Add(new(testCaseId, name));
            }
        }

        string CodeStatus(int id) => !matchingHistory
            ? historyUnavailable ? "unavailable (different history origin)" : "not-assessed (no history imported)"
            : linked.TryGetValue(id, out var code) && code.Count > 0
                ? code.Any(c => c.Status == "confirmed") ? "confirmed link" : "candidate link"
                : incompleteHistory ? "unavailable (history incomplete)" : "not-linked (imported history)";
        string CaseStatus(int id) => !testsAvailable ? "not-assessed (no ADO test snapshot)"
            : !assessed.Contains(id) ? "unavailable (not in snapshot)"
            : testCases.GetValueOrDefault(id)?.Count > 0 ? "linked (ADO cases)" : "not-linked (ADO cases)";
        string CodeTestStatus(int id) => !linked.TryGetValue(id, out var code) || code.Count == 0
            ? "not-assessed (no linked code)" : code.Any(c => c.Tests.Count > 0)
                ? "referenced by test code" : "no structural test-code link";

        var index = new StringBuilder("# Area requirements catalog\n\n");
        index.AppendLine($"Project: {Escape(catalog.GetProperty("project").GetString())}  ");
        index.AppendLine($"Area: {Escape(catalog.GetProperty("area").GetString())}\n");
        index.AppendLine("AreaPath selects organizational scope. Parent/child columns represent explicit ADO hierarchy links, not a hierarchy inferred from AreaPath.\n");
        index.AppendLine("A confirmed link means a provider-linked commit touched code; a candidate link comes from weaker history evidence. Neither proves implementation. Test-case links are not test execution, acceptance or coverage proof.\n");
        foreach (var warning in coverage) index.AppendLine($"Warning: {Escape(warning)}\n");
        if (historyUnavailable) index.AppendLine("Warning: the imported history is from another ADO provider, server or project; it was not joined.\n");
        if (incompleteHistory) index.AppendLine("Warning: historical evidence is incomplete; the table must not be used as a coverage percentage.\n");
        index.AppendLine("| ID | Type | Parent | Title | State | Area | Code link | ADO test cases |");
        index.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var item in items)
        {
            var id = item.GetProperty("id").GetInt32();
            index.AppendLine($"| [{id}](items/{id}.md) | {Val(item, "type")} | {Parent(item)} | {Val(item, "title")} | {Val(item, "state")} | {Val(item, "area")} | {Escape(CodeStatus(id))} | {Escape(CaseStatus(id))} |");
        }
        yield return ("requirements/area-catalog.md", index.ToString());

        foreach (var item in items)
        {
            var id = item.GetProperty("id").GetInt32();
            var page = new StringBuilder($"# WI-{id}: {Val(item, "title")}\n\n[Area catalog](../area-catalog.md) / work item\n\n");
            page.AppendLine($"- Type: {Val(item, "type")}");
            page.AppendLine($"- State: {Val(item, "state")}");
            page.AppendLine($"- AreaPath: {Val(item, "area")}");
            page.AppendLine($"- Iteration: {Val(item, "iteration")}");
            page.AppendLine($"- Parent: {Parent(item)} (explicit ADO link)\n");
            var children = item.GetProperty("relations").EnumerateArray()
                .Where(e => e.GetProperty("kind").GetString() == "child")
                .Select(e => e.GetProperty("id").GetInt32()).Order().ToArray();
            if (children.Length > 0)
                page.AppendLine("Children: " + string.Join(", ", children.Select(child =>
                    itemIds.Contains(child) ? $"[WI-{child}]({child}.md)" : $"WI-{child} (outside the exported area)")) + "\n");
            page.AppendLine("## Description\n");
            page.AppendLine(Multiline(item.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String
                ? description.GetString() : null) + "\n");
            page.AppendLine("## Acceptance criteria\n");
            if (item.TryGetProperty("criteria", out var criteria) && criteria.ValueKind == JsonValueKind.Array)
            {
                var n = 0;
                foreach (var criterion in criteria.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String))
                {
                    var lines = (criterion.GetString() ?? "").Replace("\r\n", "\n").Split('\n');
                    page.AppendLine($"{++n}. {Escape(lines[0])}");
                    foreach (var line in lines.Skip(1)) page.AppendLine("   " + Escape(line));
                }
                if (n == 0) page.AppendLine("No criteria exported.");
            }
            else page.AppendLine("No criteria exported.");
            page.AppendLine("\n## Implementation history\n");
            page.AppendLine($"Status: **{Escape(CodeStatus(id))}**\n");
            if (linked.TryGetValue(id, out var code) && code.Count > 0)
            {
                page.AppendLine("| Source symbol | Historical link | Changed since | Commits |\n|---|---|---|---|");
                foreach (var evidence in code.OrderBy(e => e.Symbol, StringComparer.Ordinal))
                    page.AppendLine($"| {Escape(evidence.Symbol)} | {Escape(evidence.Status)} | {(evidence.ChangedSince ? "yes" : "no")} | {string.Join(", ", evidence.Commits.Select(c => Escape(c[..Math.Min(7, c.Length)])))} |");
                page.AppendLine("\nA code link does not verify that the requirement is satisfied.\n");
            }
            if (matchingHistory && history!.WorkItems.Any(w => !w.Id.IsGithub && w.Id.Number == id))
                page.AppendLine($"Historical requirement details: [WI-{id}](../wi-{id}.md)\n");
            page.AppendLine("## Test traceability\n");
            page.AppendLine($"ADO test-case evidence: **{Escape(CaseStatus(id))}**  ");
            page.AppendLine($"Structural test-code references: **{Escape(CodeTestStatus(id))}**\n");
            if (testCases.TryGetValue(id, out var cases) && cases.Count > 0)
            {
                foreach (var testCase in cases.OrderBy(t => t.Id))
                    page.AppendLine($"- ADO test case #{testCase.Id}: {Escape(testCase.Title)}");
                page.AppendLine();
            }
            if (linked.TryGetValue(id, out var codeTests))
            {
                foreach (var test in codeTests.SelectMany(e => e.Tests).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                    page.AppendLine($"- Test-code reference: {Escape(test)}");
                page.AppendLine();
            }
            page.AppendLine("Test-case relationships and test-code references do not establish successful execution, coverage or acceptance.");
            yield return ($"requirements/items/{id}.md", page.ToString() + "\n");
        }
    }
}
