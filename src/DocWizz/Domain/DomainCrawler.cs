using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// A bounded, read-only first pass towards domain architecture discovery: traverse observed code edges and
// attach historical requirements as separately labelled evidence. No AI interpretation and no ADO calls.
static class DomainCrawler
{
    public const string Schema = "docwizz.domain-discovery/v1";
    public const int DefaultDepth = 3;
    public const int MaxDepth = 8;
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;
    const int MaxRequirementsPerSymbol = 10;

    // Keep traversal directed. Reverse lookup is used only for interface dispatch (implements); following all
    // callers or all consumers of a repository/service quickly swallows an unrelated application. `contains` is
    // followed from seed symbols only.
    static readonly HashSet<string> Relations =
    [
        "calls", "injects", "accesses", "creates", "implements", "registers", "inherits",
        "persists", "publishes", "subscribes", "http", "renders", "routes-to", "connects",
        "contains"
    ];

    static readonly HashSet<string> Containers = ["class", "struct", "record", "interface", "module"];

    public record Seed(string Kind, string Value, List<string> Symbols);
    public record ModelVersion(string? Commit, string? Head, bool Stale);
    public record IndexVersion(string Status, string? Fingerprint, bool? CoversHead);
    public record Symbol(string Id, string Kind, string Name, string Location, int Depth, List<string> Tags);
    public record Relation(string From, string To, string Kind, string Certainty, string? Label);
    public record Evidence(string Relation, string Commit, int? PullRequest, string? Confidence, string File, string Lines);
    public record Requirement(string Symbol, int Id, string Status, bool ChangedSince, string Availability,
        string? Title, int? Revision, bool? IsCurrent, List<string> AcceptanceCriteria, List<Evidence> Evidence,
        List<string> Checks);
    // A work-item title is a candidate term, NOT a verified domain concept or bounded context.
    public record CandidateTerm(int WorkItem, string Label, List<string> Symbols, string Classification);
    public record Report(string Schema, Seed Seed, ModelVersion Model, IndexVersion RequirementsIndex,
        int MaxDepth, int MaxNodes, bool Truncated, List<Symbol> Symbols, List<Relation> Relations,
        List<Requirement> Requirements, List<CandidateTerm> CandidateTerms, List<string> Warnings);

    /// <summary>
    /// Discovers a bounded, deterministic subgraph around a code symbol or imported work item,
    /// retaining source-backed technical relations and explicitly qualified historical requirements.
    /// Does not infer business capabilities or contact external services.
    /// </summary>
    /// <param name="model">The scanned, language-neutral code model.</param>
    /// <param name="codeSeed">A single symbol or endpoint; null when starting from a work item.</param>
    /// <param name="workItem">An imported Azure DevOps ID; null when starting from code.</param>
    /// <param name="maxDepth">Maximum graph distance from the seed.</param>
    /// <param name="maxNodes">Maximum number of visited symbols.</param>
    /// <param name="index">Optional previously imported requirements index.</param>
    /// <param name="head">Current HEAD for freshness assessment, if known.</param>
    /// <param name="root">Repository root to read method bodies for the cross-checks; null skips them.</param>
    /// <returns>Read-only, versioned code and requirement evidence, including warnings and truncation.</returns>
    public static Report Discover(CodeModel model, string? codeSeed, int? workItem, int maxDepth, int maxNodes,
        RequirementIndex.Index? index = null, string? head = null, string? root = null)
    {
        if ((codeSeed is null) == (workItem is null))
            throw new ArgumentException("choose exactly one of --seed <symbol|endpoint> or --work-item <id>");
        if (maxDepth is < 0 or > MaxDepth)
            throw new ArgumentException($"--depth must be from 0 to {MaxDepth}");
        if (maxNodes is < 1 or > MaxLimit)
            throw new ArgumentException($"--limit must be from 1 to {MaxLimit}");
        if (workItem is <= 0) throw new ArgumentException("--work-item must be a positive number");

        var nodes = model.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        List<string> starts;
        Seed seed;
        if (workItem is { } id)
        {
            if (index is null)
                throw new ArgumentException("work-item seed requires the imported requirements index (docwizz requirements import)");
            var reverse = RequirementResolver.Symbols(index, id, model, head);
            if (reverse.Item is null && reverse.Symbols.Count == 0)
                throw new ArgumentException($"WI-{id} is not present in the imported requirements index");
            starts = [.. reverse.Symbols.Where(s => s.Current && nodes.ContainsKey(s.Id))
                .Select(s => s.Id).Distinct().Order(StringComparer.Ordinal)];
            seed = new("work-item", $"WI-{id}", starts);
        }
        else
        {
            var resolution = AgentContext.Resolve(model, codeSeed!);
            if (resolution.Target?.Primary is not { } n)
            {
                var hints = resolution.Candidates.Count == 0 ? "" : $"; candidates: {string.Join(", ", resolution.Candidates)}";
                throw new ArgumentException((resolution.Error ?? "the seed must resolve to a single symbol or endpoint") + hints);
            }
            starts = [n.Id];
            seed = new("symbol", codeSeed!, starts);
        }

        var warnings = new List<string>();
        var stale = model.Commit is { } c && head is { } h && !SameCommit(c, h);
        if (stale) warnings.Add("the code model was scanned at another commit; rescan before trusting the result");

        string indexStatus = index is null ? "unavailable" : "available";
        bool? coversHead = null;
        if (index is null) warnings.Add("no requirements index: technical graph only; run docwizz requirements import to add business evidence");
        else
        {
            coversHead = index.Inputs.HeadSha is { } ih && head is { } hh ? SameCommit(ih, hh) : null;
            if (coversHead == false) warnings.Add("the requirements index does not cover the current HEAD; historical evidence may be stale");
            if (index.Inputs.Truncated) warnings.Add("the history export was truncated: older commits are not covered");
            if (index.Inputs.Shallow) warnings.Add("the history export was from a shallow clone");
            warnings.AddRange(index.Warnings.Take(20));
            if (index.Warnings.Count > 20) warnings.Add("additional history warnings omitted");
        }

        if (starts.Count == 0) warnings.Add("the work item has no linked symbols in the current code model");
        var allowed = model.Edges.Where(e => Relations.Contains(e.Kind)
            && nodes.TryGetValue(e.From, out var from) && nodes.ContainsKey(e.To)
            && from.Kind is not ("external" or "package" or "project" or "config")
            && (e.Kind != "contains" || Containers.Contains(from.Kind))
            && nodes[e.To].Kind is not ("package" or "project" or "config")
            && nodes[e.To].Tags?.Contains("test") != true)
            .Distinct().OrderBy(e => e.From, StringComparer.Ordinal)
            .ThenBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ToList();
        var outgoing = allowed.ToLookup(e => e.From);
        var implementations = allowed.Where(e => e.Kind == "implements").ToLookup(e => e.To);

        // Best-first instead of plain BFS: behaviour paths (calls, injects, accesses, http, persists…) are admitted
        // before data construction, so the node budget reaches services and storage before DTOs. A node reached
        // through a deferred edge stays deferred, as does everything behind it. Ties: depth, then ordinal id.
        var visited = new Dictionary<string, int>(StringComparer.Ordinal);
        var frontier = new PriorityQueue<string, (int Tier, int Depth, string Id)>(Comparer<(int Tier, int Depth, string Id)>.Create(
            (a, b) => a.Tier != b.Tier ? a.Tier.CompareTo(b.Tier)
                : a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth)
                : string.CompareOrdinal(a.Id, b.Id)));
        foreach (var startId in starts) frontier.Enqueue(startId, (0, 0, startId));

        var truncated = false;
        while (frontier.TryDequeue(out var current, out var at))
        {
            if (visited.ContainsKey(current)) continue;
            if (visited.Count >= maxNodes) { truncated = true; break; }
            visited.Add(current, at.Depth);
            if (at.Depth >= maxDepth) continue;
            // Type -> member only from a seed: a class reached through a field would otherwise pull in its whole API
            // (every sibling method of the one actually called) and their requirements.
            var following = outgoing[current].Where(e => e.Kind != "contains" || at.Depth == 0)
                .Select(e => (e.To, Deferred: Deferred(e, nodes[e.To])))
                .Concat(implementations[current].Select(e => (To: e.From, Deferred: false)));
            foreach (var (next, deferred) in following)
                if (!visited.ContainsKey(next))
                    frontier.Enqueue(next, (deferred ? 1 : at.Tier, at.Depth + 1, next));
        }
        if (truncated) warnings.Add($"node limit {maxNodes} reached: this is only a partial traversal");

        var symbols = visited.OrderBy(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x =>
            {
                var n = nodes[x.Key];
                return new Symbol(n.Id, n.Kind, n.Name, CodeModel.Location(n), x.Value,
                    [.. (n.Tags ?? []).Order(StringComparer.Ordinal)]);
            }).ToList();

        // Edges retain their ORIGINAL direction even when the walker followed an implements edge backwards.
        var possible = allowed.Where(e => visited.ContainsKey(e.From) && visited.ContainsKey(e.To)).ToList();
        var maxRelations = maxNodes * 12;
        if (possible.Count > maxRelations)
        {
            warnings.Add($"edge limit {maxRelations} reached: relations truncated");
            truncated = true;
        }
        var relations = possible.Take(maxRelations)
            .Select(e => new Relation(e.From, e.To, e.Kind,
                e.Label?.Contains("inferred", StringComparison.OrdinalIgnoreCase) == true ? "inferred" : "detected",
                e.Label))
            .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.To, StringComparer.Ordinal).ToList();

        var requirements = new List<Requirement>();
        // Every current method a work item is linked to, once per work item: the cross-checks compare it with its siblings.
        var linkedTo = new Dictionary<int, List<Node>>();
        List<Node> Linked(int id) => linkedTo.TryGetValue(id, out var l) ? l : linkedTo[id] =
            [.. RequirementResolver.Symbols(index!, id, model, head).Symbols.Where(x => x.Current && nodes.ContainsKey(x.Id)).Select(x => nodes[x.Id])];
        if (index is not null)
            foreach (var s in symbols.Where(s => nodes[s.Id].Kind is not ("external" or "config")))
            {
                var context = RequirementResolver.For(index, s.Id, nodes[s.Id], head);
                if (!context.Complete) warnings.Add($"historical linkage for {s.Id} is incomplete");
                if (context.Requirements.Count > MaxRequirementsPerSymbol)
                    warnings.Add($"{s.Id}: only {MaxRequirementsPerSymbol} of {context.Requirements.Count} requirements shown");
                foreach (var r in context.Requirements.Take(MaxRequirementsPerSymbol))
                {
                    requirements.Add(new(s.Id, r.Id, r.Status, r.ChangedSince, r.Availability,
                        r.WorkItem?.Title, r.WorkItem?.Revision, r.WorkItem?.IsCurrent,
                        [.. (r.WorkItem?.Criteria ?? []).Take(3)],
                        [.. r.Evidence.Select(e => new Evidence(e.Relation, e.Commit, e.PullRequest,
                            e.Confidence, e.File, e.Lines))],
                        root is null || r.WorkItem is null ? [] : Checks(r.WorkItem, nodes[s.Id], context.Requirements, model, root,
                            [.. Linked(r.Id).Where(n => n.Id != s.Id)])));
                }
            }

        var concepts = requirements.Where(r => !string.IsNullOrWhiteSpace(r.Title))
            .GroupBy(r => r.Id).OrderBy(g => g.Key)
            .Select(g => new CandidateTerm(g.Key, g.First().Title!,
                [.. g.Select(r => r.Symbol).Distinct().Order(StringComparer.Ordinal)], "historical-requirement-title"))
            .ToList();

        return new(Schema, seed, new(model.Commit, head, stale),
            new(indexStatus, index?.Fingerprint, coversHead), maxDepth, maxNodes, truncated, symbols, relations,
            requirements, concepts, [.. warnings.Distinct()]);
    }

    // Cross-checks between a work item's text and the linked method's code (#187). A hit moves the link out of As-intended
    // with its reason; none claims the code is wrong, a reviewer decides. Only regexes run over the untrusted text.
    //   numbers   the work item states numbers, the code uses others and none of them (14 days vs. `> 30`)
    //   name      the work item names a method of the same class that this one neither calls nor shares the work item with
    //   sibling   another method linked to the work item has its values (TRK-, 500), this one has none (an unrelated edit)
    static List<string> Checks(RequirementIndex.WorkItem item, Node method, List<RequirementResolver.Requirement> all,
        CodeModel model, string root, List<Node> siblings)
    {
        var checks = new List<string>();
        if (method.Kind is not ("method" or "constructor") || Body(root, method) is not { } body) return checks;
        var text = Text(item);
        var stated = Stated(text);
        var tokens = Regex.Matches(text, @"\b[A-Z]{2,}(?:\d+\b|-)").Select(m => m.Value).ToHashSet();
        bool Has(string code) => Numbers(code).Any(stated.Contains) || tokens.Any(code.Contains);

        var used = Numbers(body).Where(n => n is not (0 or 1)).ToHashSet();
        var explained = all.Where(r => r.WorkItem is not null)
            .SelectMany(r => Stated(Text(r.WorkItem!))).ToHashSet();
        if (stated.Count > 0 && used.Count > 0 && !used.Any(stated.Contains) && !used.All(explained.Contains))
            checks.Add($"the work item states {string.Join(", ", Numbers(text).Distinct())}, the code uses {string.Join(", ", used)}");

        var calls = model.Edges.Where(e => e.From == method.Id && e.Kind == "calls").Select(e => e.To).ToHashSet();
        var shared = siblings.Select(n => n.Id).ToHashSet();
        var owner = model.Edges.FirstOrDefault(e => e.Kind == "contains" && e.To == method.Id)?.From;
        foreach (var m in model.Edges.Where(e => owner is not null && e.Kind == "contains" && e.From == owner && e.To != method.Id)
            .Select(e => e.To).Distinct().Order(StringComparer.Ordinal))
        {
            var name = m[(m.IndexOf(':') + 1)..].Split('(')[0].Split('.').Last();
            var words = Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ");
            if (!calls.Contains(m) && !shared.Contains(m) && Regex.IsMatch(text, $@"\b{Regex.Escape(words)}\b", RegexOptions.IgnoreCase))
                checks.Add($"the work item mentions {Generator.Code(name)}, which this method doesn't call");
        }

        if (!Has(body) && siblings.FirstOrDefault(n => Body(root, n) is { } b && Has(b)) is { } other)
            checks.Add($"{Generator.Code(Label(other.Id))}, also linked to the work item, has its values; this method has none");
        return checks;
    }

    static string? Body(string root, Node n)
    {
        var path = Path.Combine(root, n.File);
        if (!File.Exists(path)) return null;
        var lines = File.ReadAllLines(path);
        var (from, to) = (Math.Max(1, n.Line), Math.Min(lines.Length, n.EndLine ?? n.Line));
        return from > to ? null : string.Join('\n', lines[(from - 1)..to]);
    }

    static string Text(RequirementIndex.WorkItem item) => string.Join('\n', [item.Title ?? "", .. item.Criteria]);

    // Numbers as written, except a digit inside a name (SAVE20, x1).
    static IEnumerable<decimal> Numbers(string s) => Regex.Matches(s, @"(?<![\w.])\d+(?:\.\d+)?")
        .Select(m => decimal.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture));

    // A work item's numbers, and each as a fraction: "19 percent" and "5%" are 0.19 and 0.05 in code.
    static HashSet<decimal> Stated(string text) => [.. Numbers(text).SelectMany(n => new[] { n, n / 100 })];

    static bool Deferred(Edge e, Node to) => e.Kind == "creates" || to.Kind is "record" or "struct" or "enum";

    static bool SameCommit(string a, string b) =>
        a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Json(Report report) => JsonSerializer.Serialize(report, JsonOptions) + "\n";

    // ponytail: Mermaid gets unreadable beyond ~80 nodes; the tables below stay complete, a clustered view is #188.
    const int MaxGraphNodes = 80;

    /// <summary>Reviewable Markdown in three parts: As-is (code facts), As-intended (confirmed, current requirements the
    /// cross-checks don't question) and Unclear (every other link with its reason, and reached methods without one).
    /// Deterministic; no contradiction is claimed.</summary>
    /// <param name="links">Path from the Markdown file to the repository root; null (stdout) writes locations as code.</param>
    public static string Markdown(Report report, string? links = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Domain discovery: {report.Seed.Kind} {Generator.Code(report.Seed.Value)}\n");
        sb.AppendLine($"> Offline walk of the code model{(report.Model.Commit is { } c ? $" at `{c[..Math.Min(12, c.Length)]}`" : "")}, " +
            $"depth ≤ {report.MaxDepth}, ≤ {report.MaxNodes} symbols; requirements index: {report.RequirementsIndex.Status}. " +
            "Technical facts and historical intent are listed separately; nothing here is a verified business capability.\n");
        if (report.Warnings.Count > 0)
        {
            sb.AppendLine("## Warnings\n");
            foreach (var w in report.Warnings) sb.AppendLine($"- {Generator.Esc(w)}");
            sb.AppendLine();
        }

        sb.AppendLine("## As-is: code\n\nWhat the scanner found in the source. Unresolved calls and runtime dispatch may be missing.\n");
        sb.AppendLine("### Graph\n");
        if (report.Relations.Count == 0) sb.AppendLine("No relations within the bounds.\n");
        else if (report.Symbols.Count > MaxGraphNodes)
            sb.AppendLine($"{report.Symbols.Count} symbols: too many for a readable graph; see the tables below or lower `--limit`.\n");
        else
        {
            var ids = report.Symbols.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => $"n{x.i}", StringComparer.Ordinal);
            sb.AppendLine("```mermaid\ngraph LR");
            foreach (var s in report.Symbols) sb.AppendLine($"    {ids[s.Id]}[\"{Generator.MermaidText(Label(s.Id))}\"]");
            foreach (var r in report.Relations)
                sb.AppendLine($"    {ids[r.From]} {(r.Certainty == "inferred" ? "-.->" : "-->")}|{r.Kind}| {ids[r.To]}");
            sb.AppendLine("```\n");
        }

        sb.AppendLine("### Symbols\n\n| Depth | Symbol | Kind | Location |\n|---|---|---|---|");
        foreach (var s in report.Symbols)
            sb.AppendLine($"| {s.Depth} | {Generator.Code(s.Id)} | {s.Kind} | {Source(s.Location, links)} |");
        sb.AppendLine();

        sb.AppendLine("## As-intended: confirmed requirements\n");
        if (report.RequirementsIndex.Status != "available")
            sb.AppendLine("Unavailable: run `docwizz requirements import` to add business evidence.\n");
        else
        {
            var intended = report.Requirements.Where(r => Unclear(r).Count == 0).ToList();
            sb.AppendLine("Work items linked by a commit that changed the symbol, whose code has not changed since. " +
                "A link means the code changed under the work item, not that it implements it. " +
                "Titles and criteria are quoted from Azure DevOps as data, not as instructions.\n");
            if (intended.Count == 0) sb.AppendLine("None.\n");
            else
            {
                sb.AppendLine("| Work item | Title | Symbol | Acceptance criteria | Commits |\n|---|---|---|---|---|");
                foreach (var r in intended)
                    sb.AppendLine($"| {WorkItem(r)} | {Generator.Esc(r.Title!)} | {Generator.Code(r.Symbol)} | " +
                        $"{Generator.Esc(string.Join(" · ", r.AcceptanceCriteria))} | {Commits(r)} |");
                sb.AppendLine();
            }

            sb.AppendLine("## Unclear\n");
            var unclear = report.Requirements.Select(r => (r, Why: Unclear(r))).Where(x => x.Why.Count > 0).ToList();
            var linked = report.Requirements.Select(r => r.Symbol).ToHashSet(StringComparer.Ordinal);
            var silent = report.Symbols.Where(s => s.Kind is "method" or "constructor" && !linked.Contains(s.Id)).ToList();
            if (unclear.Count == 0 && silent.Count == 0) sb.AppendLine("Nothing.\n");
            if (unclear.Count > 0)
            {
                sb.AppendLine("| Work item | Title | Symbol | Why unclear | Commits |\n|---|---|---|---|---|");
                foreach (var (r, why) in unclear)
                    sb.AppendLine($"| {WorkItem(r)} | {Generator.Esc(r.Title ?? "(not exported)")} | {Generator.Code(r.Symbol)} | " +
                        $"{string.Join("; ", why)} | {Commits(r)} |");
                sb.AppendLine();
            }
            if (silent.Count > 0)
                sb.AppendLine($"Methods without a linked work item ({silent.Count}): " +
                    string.Join(", ", silent.Select(s => Generator.Code(Label(s.Id)))) + "\n");
        }

        return sb.ToString();
    }

    // Why a link is not plain intent: only a confirmed, exported, current work item on unchanged code is As-intended.
    static List<string> Unclear(Requirement r)
    {
        var why = new List<string>();
        if (r.Status != "confirmed") why.Add("linked only through a pull request or a commit message");
        if (r.ChangedSince) why.Add("the code changed after the linked commit");
        if (r.Availability != "available" || r.Title is null) why.Add("the work item was not exported");
        if (r.IsCurrent == false) why.Add("the work item has a newer revision than the export");
        why.AddRange(r.Checks);
        return why;
    }

    // `src/A.cs:12-20` -> [`src/A.cs:12-20`](../src/A.cs#L12-L20): GitHub's line anchors, a plain file link elsewhere.
    static string Source(string location, string? links)
    {
        if (links is null) return Generator.Code(location);
        var colon = location.LastIndexOf(':');
        var (file, lines) = colon > 0 ? (location[..colon], location[(colon + 1)..]) : (location, "");
        var anchor = lines.Length == 0 ? "" : "#L" + lines.Replace("-", "-L");
        var target = (links == "." ? "" : links + "/") + file;
        return $"[{Generator.Code(location)}]({target.Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29")}{anchor})";
    }

    static string WorkItem(Requirement r) => $"WI-{r.Id}" + (r.Revision is { } rev ? $" rev {rev}" : "");

    static string Commits(Requirement r) => string.Join(", ", r.Evidence
        .Select(e => $"`{e.Commit[..Math.Min(8, e.Commit.Length)]}`" + (e.PullRequest is { } pr ? $" PR {pr}" : "")).Distinct());

    // `cs:Shop.Api.OrderController.Place(int)` -> `OrderController.Place`: bare member names collide in a graph.
    static string Label(string id) =>
        string.Join('.', id[(id.IndexOf(':') + 1)..].Split('(')[0].Split('.').TakeLast(2));

    public static string Text(Report report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Domain discovery: {report.Seed.Kind} {report.Seed.Value}");
        sb.AppendLine($"Technical symbols: {report.Symbols.Count}, relations: {report.Relations.Count}, linked requirements: {report.Requirements.Count}");
        sb.AppendLine($"Requirements index: {report.RequirementsIndex.Status}" + (report.Truncated ? " (truncated)" : ""));
        foreach (var n in report.Symbols)
            sb.AppendLine($"  [{n.Depth}] {n.Id} ({n.Kind}, {n.Location})");
        foreach (var r in report.Requirements)
            sb.AppendLine($"  WI-{r.Id} {r.Status} -> {r.Symbol}" + (r.ChangedSince ? " (code changed since)" : "") +
                (r.Title is null ? "" : $": {r.Title}"));
        foreach (var w in report.Warnings) sb.AppendLine($"  warning: {w}");
        return sb.ToString();
    }
}
