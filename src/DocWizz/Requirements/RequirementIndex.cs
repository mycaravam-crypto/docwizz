using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// `docwizz requirements import`: a local, versioned index of which historical commits changed which code symbols, and
// which work items those commits and their pull requests were linked to. Inputs are files other tools exported:
// `gitwizz.history/v2` (gitwizz history export: commits, line ranges, PR attribution, links with their source; the
// producer's contract is gitwizz docs/history/README.md), `ado.requirement-context/v1` work items, and gh issue JSON.
// The index holds facts only (what each commit touched, what each link source said); whether a requirement is
// `confirmed` or a `candidate` for a symbol is decided at query time (RequirementResolver). It is separate from the
// CodeModel (no external text mixed into static facts) and from ProjectEvidenceCatalog (explicit, human-maintained links:
// nothing here is ever written there). No network: every input is a file.
static partial class RequirementIndex
{
    public const string Schema = "docwizz.requirement-index/v2";
    public const string GithubSchema = "docwizz.requirement-index/v3";
    public const string HistorySchema = "gitwizz.history/v2";
    public const string WorkItemSchema = "ado.requirement-context/v1";
    public const string DefaultFile = "docs/.docwizz/requirements.json";

    // Linkage as the exporter reports it: `verified` (looked up; an empty list really means none), `unavailable` (the
    // provider couldn't answer), `denied` (no permission), `unknown` (never looked up). Only verified counts as an answer.
    public static readonly string[] Linkages = ["verified", "unavailable", "denied", "unknown"];

    // The contract's enumerations. A value outside them is a newer contract, never guessed at (it could read as verified).
    static readonly string[] Providers = ["local", "github", "azure-devops"];
    static readonly string[] CommitKinds = ["commit", "merge"];
    static readonly string[] FileStatuses = ["added", "modified", "deleted", "renamed", "copied"];
    static readonly string[] Granularities = ["lines", "file"];
    static readonly string[] Resolutions = ["exact", "inferred", "ambiguous", "unknown"];
    static readonly string[] PrStatuses = ["confirmed", "unconfirmed", "local"];
    static readonly string[] Strategies = ["merge", "squash", "unknown"];
    static readonly string[] MessageKinds = ["ab", "closes", "mention"];

    // Bounds on work-item text kept in the index: it is untrusted, and only a bounded slice is ever handed to a model.
    const int MaxTitle = 300, MaxDescription = 2000, MaxCriterion = 500, MaxCriteria = 50;

    public record Index(string Schema, string Fingerprint, Inputs Inputs, List<Commit> Commits, List<PullRequest> PullRequests,
        List<WorkItem> WorkItems, List<string> Warnings);

    // What the index was built from: digests, never paths, so the same inputs give the same bytes anywhere. Provider and
    // range as the export states them; Shallow: the clone had a boundary, so older commits may be missing.
    public record Inputs(string HistorySchema, string HistoryDigest, string? Provider, string? Repository, string? Base, string? BaseSha,
        string? HeadSha, bool Truncated, bool Shallow, string? Scope, List<WorkItemInput> WorkItems);
    public record WorkItemInput(RequirementId Id, int? Revision, string Digest);

    // One exported commit: its linkage, its PR associations and what it touched. Linkage and WorkItems: what the provider
    // links to the commit itself (only `verified` is an answer). Mentions: work items its message names, which nobody
    // verified (candidates at most). Mapping: `mapped` (scanned as of the commit), `missing` (not in the local
    // repository: its touches are unknown), `no-source` (changed no scanned file, or a merge, whose changes are its parents').
    public record Commit(string Sha, string Linkage, List<RequirementId> WorkItems, List<RequirementId> Mentions, List<PullRequestRef> PullRequests,
        string Mapping, List<Touch> Touches, List<FileTouch> FileOnly);
    // Confidence: the export's resolution of the commit's PR: exact (the provider confirms it), inferred (only messages
    // say so), ambiguous (one of several candidates).
    public record PullRequestRef(int Id, string Confidence);

    // A symbol a hunk overlapped, as of the commit: its id and body hash then, the hunk's lines and the symbol's lines.
    public record Touch(string Symbol, string Kind, string? Hash, string File, string Lines, string SymbolLines);
    // A hunk that overlapped no symbol (usings, a deleted file), or a file-granularity change (binary, rename without
    // content change): evidence about the file, never about a symbol.
    public record FileTouch(string File, string Lines);

    // Status: confirmed (the provider says it completed with this merge commit), unconfirmed or local. Only a confirmed
    // PR's work items are used; Linkage is the export's workItemsStatus.
    public record PullRequest(int Id, string? Title, string? MergeStrategy, string Status, string Linkage, List<RequirementId> WorkItems);

    // Cleaned, bounded exported text. ADO revisions retain their meaning; GitHub uses ChangedDate (updatedAt) and
    // leaves revisions/currentness unknown. The exported content digest participates in the index fingerprint.
    public record WorkItem(RequirementId Id, int? Revision, int? CurrentRevision, bool? IsCurrent, string? Type, string? State,
        string? Title, string? Url, string? ChangedDate, string? DescriptionField, string? Description, string? CriteriaField,
        List<string> Criteria, string Digest, List<string> Warnings,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        List<string>? Labels = null)
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? Provider => Id.IsGithub ? "github" : null;
    }

    // ---- inputs ----

    // A change as the index builder needs it: path relative to the scanned directory, and its line ranges (none for a
    // file-granularity change, which is never mapped to a symbol).
    public record Hunk(int OldStart, int OldLines, int NewStart, int NewLines);
    record Change(string Path, bool Deleted, bool FileLevel, List<Hunk> Hunks);

    // A commit of the export, read and checked: what Build needs from it.
    record HistoryCommit(string Sha, string Kind, List<(string Path, string Status, string Granularity, List<Hunk> Ranges)> Files,
        string Linkage, List<RequirementId> WorkItems, List<RequirementId> Mentions, List<PullRequestRef> PullRequests);

    static readonly JsonSerializerOptions Read = new(JsonSerializerDefaults.Web) { AllowTrailingCommas = false };

    public static readonly JsonSerializerOptions Write = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    static string SchemaOf(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "(none)";

    // The work-item files: one file, or every *.json in a folder (ordinal order); a file holds one object or an array.
    public static List<(JsonElement Item, string Digest, string Name)> ReadWorkItems(string? path)
    {
        if (path is null) return [];
        var files = Directory.Exists(path) ? Directory.GetFiles(path, "*.json").Order(StringComparer.Ordinal).ToList()
            : File.Exists(path) ? [path] : throw new ArgumentException($"{path}: no such file or folder");
        var items = new List<(JsonElement, string, string)>();
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            JsonElement root;
            try { root = JsonDocument.Parse(bytes).RootElement.Clone(); }
            catch (JsonException e) { throw new ArgumentException($"{file}: not JSON ({e.Message})"); }
            var list = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
            foreach (var (item, i) in list.Select((x, i) => (x, i)))
            {
                if (SchemaOf(item) != WorkItemSchema && !IsGithubIssue(item))
                    throw new ArgumentException($"{file}: unknown schema {SchemaOf(item)}; expected {WorkItemSchema} or a gh issue view JSON export");
                // An item's own digest: its canonical JSON, so an array file changing one item changes only that one.
                items.Add((item, Digest(Encoding.UTF8.GetBytes(item.GetRawText())), list.Count == 1 ? Path.GetFileName(file) : $"{Path.GetFileName(file)}[{i}]"));
            }
        }
        return items;
    }

    // ---- reading gitwizz.history/v2 ----
    // Every property the contract lists is required; enumerations are closed. Anything else fails with the path of the
    // offending value, so a newer or hand-edited export never imports as a plausible but wrong index.

    static JsonElement Prop(JsonElement o, string name, string where) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) ? v : throw new ArgumentException($"history: {where}: missing '{name}'");

    static string Text(JsonElement o, string name, string where) =>
        Prop(o, name, where) is { ValueKind: JsonValueKind.String } v ? v.GetString()! : throw new ArgumentException($"history: {where}.{name}: expected a string");

    static string? TextOrNull(JsonElement o, string name, string where) => Prop(o, name, where) switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { ValueKind: JsonValueKind.Null } => null,
        _ => throw new ArgumentException($"history: {where}.{name}: expected a string or null"),
    };

    static bool Flag(JsonElement o, string name, string where) =>
        Prop(o, name, where) is { ValueKind: JsonValueKind.True or JsonValueKind.False } v ? v.GetBoolean() : throw new ArgumentException($"history: {where}.{name}: expected true or false");

    static List<JsonElement> List(JsonElement o, string name, string where) =>
        Prop(o, name, where) is { ValueKind: JsonValueKind.Array } v ? [.. v.EnumerateArray()] : throw new ArgumentException($"history: {where}.{name}: expected a list");

    static string OneOf(string value, string[] allowed, string where) =>
        allowed.Contains(value) ? value : throw new ArgumentException($"history: {where}: '{value}' is not one of {string.Join(", ", allowed)} (a newer gitwizz.history contract?)");

    static int Number(JsonElement o, string name, string where) =>
        Prop(o, name, where) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var i) && i >= 0 ? i : throw new ArgumentException($"history: {where}.{name}: expected a non-negative whole number");

    static readonly Regex PrId = new(@"^#([1-9][0-9]{0,9})$");
    static readonly Regex AzureBoards = new(@"^AB#([1-9][0-9]{0,9})$");

    static int PullRequestId(string id, string where) =>
        PrId.Match(id) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? n : throw new ArgumentException($"history: {where}: pull request id '{id}' is not #<number>");

    // Links retain provider identity. GitHub message references are local to the history repository; provider links
    // additionally need the issue URL, since a PR can close an issue in another repository. Local #n is ambiguous.
    static List<RequirementId> Links(List<JsonElement> links, string source, string[] kinds, string where, List<string> warnings, string? githubRepository, Action? unresolved = null)
    {
        var ids = new List<RequirementId>();
        foreach (var (l, i) in links.Select((l, i) => (l, i)))
        {
            var at = $"{where}[{i}]";
            var item = Text(l, "workItem", at);
            if (Text(l, "source", at) != source)
                throw new ArgumentException($"history: {at}.source: '{Text(l, "source", at)}' where only '{source}' links belong (a message mention is never a provider link)");
            OneOf(Text(l, "kind", at), kinds, $"{at}.kind");
            Text(l, "evidence", at);
            if (AzureBoards.Match(item) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var id)) ids.Add(id);
            else if (PrId.Match(item) is { Success: true } github && int.TryParse(github.Groups[1].Value, out id))
            {
                if (githubRepository is null)
                {
                    warnings.Add($"{item} ({where}): no GitHub repository scope; not indexed");
                    unresolved?.Invoke();
                    continue;
                }
                var identity = new RequirementId(id, githubRepository);
                if (l.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String)
                {
                    var resolved = RequirementId.FromIssueUrl(uri.GetString()!);
                    if (resolved.Number != id || resolved.Repository != githubRepository)
                        throw new ArgumentException($"history: {at}: issue URL does not match {githubRepository}#{id}; cross-repository links are not supported");
                    identity = resolved;
                }
                else if (source == "provider")
                {
                    warnings.Add($"{item} ({where}): provider issue link has no repository-qualified URL; export again with a GitWizz version that preserves issue URLs; not indexed");
                    unresolved?.Invoke();
                    continue;
                }
                ids.Add(identity);
            }
            else throw new ArgumentException($"history: {at}.workItem: '{item}' is neither AB#<number> nor #<number>");
        }
        return [.. ids.Distinct().Order()];
    }

    static List<HistoryCommit> Commits(JsonElement root, List<string> warnings, string? githubRepository)
    {
        var commits = new List<HistoryCommit>();
        var seen = new HashSet<string>();
        foreach (var (c, i) in List(root, "commits", "history").Select((c, i) => (c, i)))
        {
            var where = $"commits[{i}]";
            var sha = Text(c, "sha", where);
            if (!Regex.IsMatch(sha, "^[0-9a-f]{40}$")) throw new ArgumentException($"history: {where}: commit {sha} is not a full 40-character SHA");
            if (!seen.Add(sha)) throw new ArgumentException($"history: commit {sha} is listed twice");
            where = $"commit {sha[..7]}";
            var kind = OneOf(Text(c, "kind", where), CommitKinds, $"{where}.kind");

            var files = new List<(string, string, string, List<Hunk>)>();
            foreach (var (f, j) in List(c, "files", where).Select((f, j) => (f, j)))
            {
                var at = $"{where}.files[{j}]";
                var path = Text(f, "path", at);
                var status = OneOf(Text(f, "status", at), FileStatuses, $"{at}.status");
                var granularity = OneOf(Text(f, "granularity", at), Granularities, $"{at}.granularity");
                var ranges = List(f, "ranges", at).Select((r, k) => new Hunk(Number(r, "oldStart", $"{at}.ranges[{k}]"), Number(r, "oldCount", $"{at}.ranges[{k}]"),
                    Number(r, "newStart", $"{at}.ranges[{k}]"), Number(r, "newCount", $"{at}.ranges[{k}]"))).ToList();
                if (granularity == "file" && ranges.Count > 0) throw new ArgumentException($"history: {at}: file granularity with line ranges");
                files.Add((path, status, granularity, ranges));
            }

            var mentions = Links(List(c, "workItems", where), "commit-message", MessageKinds, $"{where}.workItems", warnings, githubRepository);

            var provider = Prop(c, "providerLinks", where);
            var linkage = OneOf(Text(provider, "status", $"{where}.providerLinks"), Linkages, $"{where}.providerLinks.status");
            var items = Prop(provider, "items", $"{where}.providerLinks");
            List<RequirementId> linked = [];
            var unresolvedLinks = false;
            if (linkage == "verified")
            {
                if (items.ValueKind != JsonValueKind.Array) throw new ArgumentException($"history: {where}.providerLinks: verified without a list of items");
                linked = Links([.. items.EnumerateArray()], "provider", ["commit-link"], $"{where}.providerLinks.items", warnings, githubRepository, () => unresolvedLinks = true);
            }
            else if (items.ValueKind != JsonValueKind.Null)
                throw new ArgumentException($"history: {where}.providerLinks: items must be null when the status is {linkage} (only verified is an answer)");

            if (unresolvedLinks) linkage = "unknown"; // unresolved identity is never a verified empty answer
            var pr = Prop(c, "pullRequest", where);
            var resolution = OneOf(Text(pr, "resolution", $"{where}.pullRequest"), Resolutions, $"{where}.pullRequest.resolution");
            var prId = TextOrNull(pr, "id", $"{where}.pullRequest");
            var candidates = List(pr, "candidates", $"{where}.pullRequest").Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : throw new ArgumentException($"history: {where}.pullRequest.candidates: expected strings")).ToList();
            List<PullRequestRef> refs = resolution switch
            {
                "exact" or "inferred" when prId is not null => [new(PullRequestId(prId, $"{where}.pullRequest"), resolution)],
                "ambiguous" when candidates.Count > 1 => [.. candidates.Select(x => new PullRequestRef(PullRequestId(x, $"{where}.pullRequest.candidates"), "ambiguous"))],
                "unknown" => [],
                _ => throw new ArgumentException($"history: {where}.pullRequest: resolution {resolution} with id {prId ?? "null"} and {candidates.Count} candidate(s)"),
            };
            commits.Add(new(sha, kind, files, linkage, linked, mentions, [.. refs.DistinctBy(r => r.Id).OrderBy(r => r.Id)]));
        }
        return commits;
    }

    static List<PullRequest> PullRequests(JsonElement root, List<string> warnings, string? githubRepository)
    {
        var prs = new List<PullRequest>();
        foreach (var (p, i) in List(root, "pullRequests", "history").Select((p, i) => (p, i)))
        {
            var where = $"pullRequests[{i}]";
            var id = PullRequestId(Text(p, "id", where), where);
            where = $"PR #{id}";
            var status = OneOf(Text(p, "status", where), PrStatuses, $"{where}.status");
            var strategy = OneOf(Text(p, "strategy", where), Strategies, $"{where}.strategy");
            var linkage = OneOf(Text(p, "workItemsStatus", where), Linkages, $"{where}.workItemsStatus");
            var unresolvedLinks = false;
            var items = Links(List(p, "workItems", where), "provider", ["pr-link"], $"{where}.workItems", warnings, githubRepository, () => unresolvedLinks = true);
            if (linkage != "verified" && items.Count > 0) throw new ArgumentException($"history: {where}: work items listed although workItemsStatus is {linkage}");
            if (unresolvedLinks) linkage = "unknown";
            if (status != "confirmed") warnings.Add($"PR #{id} is {status}{(TextOrNull(p, "problem", where) is { } why ? $" ({why})" : "")}: its work items are not used");
            prs.Add(new(id, Clean(TextOrNull(p, "title", where), MaxTitle, null), strategy, status, linkage, items));
        }
        if (prs.GroupBy(p => p.Id).FirstOrDefault(g => g.Count() > 1) is { } twice) throw new ArgumentException($"history: PR #{twice.Key} is listed twice");
        return [.. prs.OrderBy(p => p.Id)];
    }

    static JsonElement Parse(byte[] history)
    {
        JsonElement raw;
        try { raw = JsonDocument.Parse(history).RootElement.Clone(); }
        catch (JsonException e) { throw new ArgumentException($"history: not JSON ({e.Message})"); }
        var schema = SchemaOf(raw);
        if (schema == "gitwizz.history/v1")
            throw new ArgumentException($"history: gitwizz.history/v1 is no longer read (it was never a stable contract); export it again with gitwizz history export, which writes {HistorySchema}");
        if (schema != HistorySchema) throw new ArgumentException($"history: unknown schema {schema}; expected {HistorySchema}");
        return raw;
    }

    // ---- build ----

    // Builds the index. `modelAt` scans the code as of a commit (null when the commit isn't in the local repository);
    // `prefix` is the scanned directory's path inside the repository (history paths are repository-relative);
    // `previous` lends the touches of commits it already mapped under the same scope, so a refresh only scans new commits;
    // `progress` hears (mapped, commits to map, the commit and its file count) before each commit is scanned.
    public static Index Build(byte[] history, List<(JsonElement Item, string Digest, string Name)> workItems, string? scope,
        string prefix, Func<string, CodeModel?> modelAt, Func<string, bool> scanned, Index? previous = null,
        Action<int, int, string?>? progress = null, Action<Index>? checkpoint = null)
    {
        var raw = Parse(history);
        var provider = OneOf(Text(raw, "provider", "history"), Providers, "history.provider");
        var repository = TextOrNull(raw, "repository", "history");
        var range = Prop(raw, "range", "history");
        var (since, sinceSha, untilSha) = (TextOrNull(range, "since", "range"), TextOrNull(range, "sinceSha", "range"), Text(range, "untilSha", "range"));
        var (truncated, shallow) = (Flag(range, "truncated", "range"), Flag(range, "shallow", "range"));
        var warnings = new List<string>(List(raw, "warnings", "history").Select(w => Clean(w.ValueKind == JsonValueKind.String ? w.GetString() : null, 300, null)).OfType<string>());
        if (truncated) warnings.Add("the history export was truncated: older commits are not covered");
        if (shallow) warnings.Add("the history was exported from a shallow clone: commits beyond its boundary are not covered");

        var items = workItems.Select(w => Normalize(w.Item, w.Digest, w.Name)).ToList();
        var githubScope = RequirementId.GithubRepository(repository);
        if (items.Any(w => w.Id.IsGithub) && provider != "github")
            throw new ArgumentException("GitHub issue details require a github history export with its repository identity");
        if (items.Any(w => w.Id.IsGithub && w.Id.Repository != githubScope))
            throw new ArgumentException("GitHub issue details do not match the history repository; cross-repository imports are not supported");
        foreach (var dup in items.GroupBy(w => w.Id).Where(g => g.Count() > 1))
            warnings.Add(dup.Key.IsGithub ? $"{dup.Key.Display}: {dup.Count()} exports; using newest exported updatedAt"
                : $"WI-{dup.Key}: {dup.Count()} exports; using revision {dup.Max(w => w.Revision ?? -1)}");
        // Several exports of one item: the highest revision wins, then the digest, so the choice is stable.
        items = [.. items.GroupBy(w => w.Id).Select(g => g.OrderByDescending(w => w.Revision ?? -1).ThenByDescending(w => w.Id.IsGithub ? w.ChangedDate : null, StringComparer.Ordinal).ThenBy(w => w.Digest, StringComparer.Ordinal).First()).OrderBy(w => w.Id)];

        var pullRequests = PullRequests(raw, warnings, provider == "github" ? RequirementId.GithubRepository(repository) : null);
        // The export lists commits newest first; the index keeps them oldest first, so later evidence is newer.
        var exported = Commits(raw, warnings, provider == "github" ? RequirementId.GithubRepository(repository) : null);
        exported.Reverse();

        var reuse = previous is not null && previous.Inputs.Scope == scope
            ? previous.Commits.Where(c => c.Mapping != "missing").ToDictionary(c => c.Sha) : [];
        var commits = new List<Commit>();
        var pending = exported.Select(c => (Commit: c, Changes: Changes(c, prefix).Where(x => scanned(x.Path)).ToList())).ToList();
        var toMap = pending.Count(p => p.Commit.Kind != "merge" && p.Changes.Count > 0 && !reuse.ContainsKey(p.Commit.Sha));
        var mapped = 0;
        var inputs = new Inputs(HistorySchema, Digest(history), provider, repository, since, sinceSha, untilSha, truncated, shallow, scope,
            [.. items.Select(w => new WorkItemInput(w.Id, w.Revision, w.Digest))]);
        var fingerprint = Fingerprint(inputs);
        void SaveCheckpoint() => checkpoint?.Invoke(new(IndexSchema(inputs), fingerprint, inputs,
            [.. commits], pullRequests, items, [.. warnings.Distinct()]));
        progress?.Invoke(0, toMap, null);
        foreach (var (c, changes) in pending)
        {
            if (c.Kind == "merge" || changes.Count == 0)
            {
                commits.Add(new(c.Sha, c.Linkage, c.WorkItems, c.Mentions, c.PullRequests, "no-source", [], []));
                continue;
            }
            if (reuse.TryGetValue(c.Sha, out var known))
            {
                commits.Add(known with { Linkage = c.Linkage, WorkItems = c.WorkItems, Mentions = c.Mentions, PullRequests = c.PullRequests });
                continue;
            }
            progress?.Invoke(mapped++, toMap, $"{c.Sha[..7]} ({changes.Count} source file{(changes.Count == 1 ? "" : "s")})");
            if (modelAt(c.Sha) is not { } model)
            {
                warnings.Add($"commit {c.Sha[..7]} is not in the local repository: what it changed is unknown");
                commits.Add(new(c.Sha, c.Linkage, c.WorkItems, c.Mentions, c.PullRequests, "missing", [], []));
                continue;
            }
            var (touches, fileOnly) = Map(model, changes);
            commits.Add(new(c.Sha, c.Linkage, c.WorkItems, c.Mentions, c.PullRequests, "mapped", touches, fileOnly));
            if (mapped % 5 == 0) SaveCheckpoint();
        }

        // Nothing verified anywhere: an empty answer would read as "no requirements" when nobody could say.
        if (commits.Count > 0 && commits.All(c => c.Linkage != "verified") && pullRequests.All(p => p.Linkage != "verified" || p.Status != "confirmed"))
            warnings.Add($"no provider link was verified ({provider} history): a symbol without requirements is unknown, not unlinked");

        return new(IndexSchema(inputs), fingerprint, inputs, commits, pullRequests, items, [.. warnings.Distinct()]);
    }

    // Over everything the index is derived from: the history, each work item, the scan scope and this schema.
    static string IndexSchema(Inputs inputs) => inputs.Provider == "github" || inputs.WorkItems.Any(w => w.Id.IsGithub) ? GithubSchema : Schema;

    public static string Fingerprint(Inputs inputs) => Digest(Encoding.UTF8.GetBytes(string.Join("\n",
        [IndexSchema(inputs), inputs.HistorySchema, inputs.HistoryDigest, inputs.Scope ?? "", .. inputs.WorkItems.Select(w => $"{w.Id}:{w.Revision}:{w.Digest}")])))[..16];

    // The fingerprint the given inputs would produce, without scanning anything: equal to a saved index's means unchanged.
    public static string Fingerprint(byte[] history, List<(JsonElement Item, string Digest, string Name)> workItems, string? scope)
    {
        var items = workItems.Select(w => Normalize(w.Item, w.Digest, w.Name))
            .GroupBy(w => w.Id).Select(g => g.OrderByDescending(w => w.Revision ?? -1).ThenByDescending(w => w.Id.IsGithub ? w.ChangedDate : null, StringComparer.Ordinal).ThenBy(w => w.Digest, StringComparer.Ordinal).First()).OrderBy(w => w.Id);
        return Fingerprint(new Inputs(HistorySchema, Digest(history), Text(Parse(history), "provider", "history"), null, null, null, null, false, false, scope,
            [.. items.Select(w => new WorkItemInput(w.Id, w.Revision, w.Digest))]));
    }

    // The commit's changes under the scanned directory, paths relative to it. A rename keeps only the new path: the
    // symbols are mapped as of the commit, where the file lives at the new path.
    static IEnumerable<Change> Changes(HistoryCommit c, string prefix)
    {
        foreach (var (raw, status, granularity, ranges) in c.Files)
        {
            var path = raw.Replace('\\', '/');
            if (prefix.Length > 0)
            {
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                path = path[prefix.Length..];
            }
            yield return new(path, status == "deleted", granularity == "file", ranges);
        }
    }

    // Symbols that can carry a requirement: everything with a line range except structure and external things.
    static bool Mappable(Node n) => n.EndLine is not null && n.Kind is not ("external" or "config" or "package" or "project" or "module" or "route");

    // Each hunk to the innermost symbols whose lines it overlaps, as of the commit. A pure deletion (no new lines) touches
    // the symbol that contains the deletion point. A hunk that overlaps no symbol is file-level evidence only.
    static (List<Touch>, List<FileTouch>) Map(CodeModel model, List<Change> changes)
    {
        var byFile = model.Nodes.Where(Mappable).ToLookup(n => n.File);
        var touches = new List<Touch>();
        var fileOnly = new List<FileTouch>();
        foreach (var ch in changes)
        {
            // Only the file is known to have changed (binary, a rename or mode change): never a statement about a symbol.
            if (ch.FileLevel)
            {
                fileOnly.Add(new(ch.Path, ch.Deleted ? "deleted" : "file"));
                continue;
            }
            foreach (var h in ch.Hunks)
            {
                var lines = h.NewLines == 0 ? $"{h.NewStart}" : h.NewLines == 1 ? $"{h.NewStart}" : $"{h.NewStart}-{h.NewStart + h.NewLines - 1}";
                if (ch.Deleted)
                {
                    fileOnly.Add(new(ch.Path, $"deleted (old {h.OldStart}-{h.OldStart + Math.Max(h.OldLines, 1) - 1})"));
                    continue;
                }
                var (from, to) = h.NewLines == 0 ? (h.NewStart, h.NewStart + 1) : (h.NewStart, h.NewStart + h.NewLines - 1);
                var hit = byFile[ch.Path].Where(n => h.NewLines == 0 ? n.Line <= from && n.EndLine >= to : n.Line <= to && n.EndLine >= from).ToList();
                var inner = hit.Where(a => !hit.Any(b => b != a && a.Line <= b.Line && a.EndLine >= b.EndLine && (a.Line, a.EndLine) != (b.Line, b.EndLine))).ToList();
                if (inner.Count == 0) fileOnly.Add(new(ch.Path, lines));
                foreach (var n in inner) touches.Add(new(n.Id, n.Kind, n.Hash, n.File, lines, $"{n.Line}-{n.EndLine}"));
            }
            if (ch.Hunks.Count == 0 && ch.Deleted) fileOnly.Add(new(ch.Path, "deleted"));
        }
        return ([.. touches.DistinctBy(t => (t.Symbol, t.Lines)).OrderBy(t => t.File, StringComparer.Ordinal).ThenBy(t => t.Symbol, StringComparer.Ordinal).ThenBy(t => t.Lines, StringComparer.Ordinal)],
            [.. fileOnly.Distinct().OrderBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Lines, StringComparer.Ordinal)]);
    }

    // ---- work items ----

    static WorkItem Normalize(JsonElement e, string digest, string name)
    {
        if (IsGithubIssue(e)) return NormalizeGithub(e, digest, name);
        var warnings = new List<string>();
        string? Str(JsonElement o, string p) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Int(JsonElement o, string p) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(p, out var v) && v.TryGetInt32(out var i) ? i : null;
        bool? Bool(JsonElement o, string p) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(p, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        JsonElement Obj(string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

        var id = Int(e, "id") is { } i && i > 0 ? i : throw new ArgumentException($"{name}: work item without a positive id");
        var description = Obj("description");
        var criteria = Obj("acceptanceCriteria");
        var items = criteria.ValueKind == JsonValueKind.Object && criteria.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(x => Str(x, "text")).OfType<string>().ToList() : [];
        // A criteria field without a list: its non-empty lines, as ado-devops does, in order; never invented ones.
        if (items.Count == 0 && Str(criteria, "text") is { } text) items = [.. text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];
        var cleaned = items.Select(c => Clean(c, MaxCriterion, null)).OfType<string>().ToList();
        if (cleaned.Count > MaxCriteria)
        {
            warnings.Add($"{cleaned.Count} acceptance criteria; kept the first {MaxCriteria}");
            cleaned = cleaned[..MaxCriteria];
        }
        var desc = Str(description, "text");
        if (desc is not null && desc.Length > MaxDescription) warnings.Add($"description cut to {MaxDescription} characters");
        if (e.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array)
            warnings.AddRange(w.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => Clean(x.GetString(), 300, null)!).Where(x => x.Length > 0));
        return new(id, Int(e, "revision"), Int(e, "currentRevision"), Bool(e, "isCurrent"), Clean(Str(e, "type"), 100, null),
            Clean(Str(e, "state"), 100, null), Clean(Str(e, "title"), MaxTitle, null), Str(e, "webUrl") ?? Str(e, "url"), Str(e, "changedDate"),
            Str(description, "sourceField"), Clean(desc, MaxDescription, null), Str(criteria, "sourceField"), cleaned, digest, warnings);
    }

    // Work-item text as data: control characters out (newlines kept), whitespace runs collapsed per line, bounded.
    public static string? Clean(string? text, int max, string? fallback)
    {
        if (text is null) return fallback;
        var lines = Regex.Replace(text.Replace("\r\n", "\n"), @"[\p{Cc}\p{Cf}-[\n]]", " ").Split('\n')
            .Select(l => Regex.Replace(l, @"\s+", " ").Trim()).Where(l => l.Length > 0);
        var joined = string.Join("\n", lines);
        return joined.Length == 0 ? fallback : joined.Length <= max ? joined : joined[..max].TrimEnd() + "…";
    }

    // ---- file ----

    public static string Serialize(Index index) => JsonSerializer.Serialize(index, Write).Replace("\r\n", "\n") + "\n";

    // A saved index; another schema (an older docwizz, or not an index) is an error that says to import again.
    public static Index Load(string file)
    {
        JsonElement raw;
        try { raw = JsonDocument.Parse(File.ReadAllBytes(file)).RootElement.Clone(); }
        catch (JsonException e) { throw new ArgumentException($"{file}: not JSON ({e.Message})"); }
        if (SchemaOf(raw) != Schema && SchemaOf(raw) != GithubSchema) throw new ArgumentException($"{file}: unknown schema {SchemaOf(raw)}; expected {Schema} (run docwizz requirements import again)");
        try { return raw.Deserialize<Index>(Read) ?? throw new ArgumentException($"{file}: empty index"); }
        catch (JsonException e) { throw new ArgumentException($"{file}: invalid requirement index ({e.Message})"); }
    }
}
