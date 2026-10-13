using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// `docwizz requirements import`: a local, versioned index of which historical commits changed which code symbols, and
// which work items those commits and their pull requests were linked to. Inputs are files other tools exported:
// `gitwizz.history/v1` (commits, hunks, PR associations, links) and `ado.requirement-context/v1` (normalized work items).
// The index holds facts only (what each commit touched, what each link source said); whether a requirement is
// `confirmed` or a `candidate` for a symbol is decided at query time (RequirementResolver). It is separate from the
// CodeModel (no external text mixed into static facts) and from ProjectEvidenceCatalog (explicit, human-maintained links:
// nothing here is ever written there). No network: every input is a file.
static class RequirementIndex
{
    public const string Schema = "docwizz.requirement-index/v1";
    public const string HistorySchema = "gitwizz.history/v1";
    public const string WorkItemSchema = "ado.requirement-context/v1";
    public const string DefaultFile = "docs/.docwizz/requirements.json";

    // Linkage as the exporter reports it: `verified` (looked up; an empty list really means none), `unavailable` (the
    // provider couldn't answer), `denied` (no permission), `unknown` (never looked up). Only verified counts as an answer.
    public static readonly string[] Linkages = ["verified", "unavailable", "denied", "unknown"];
    static readonly string[] Confidences = ["exact", "ambiguous"];

    // Bounds on work-item text kept in the index: it is untrusted, and only a bounded slice is ever handed to a model.
    const int MaxTitle = 300, MaxDescription = 2000, MaxCriterion = 500, MaxCriteria = 50;

    public record Index(string Schema, string Fingerprint, Inputs Inputs, List<Commit> Commits, List<PullRequest> PullRequests,
        List<WorkItem> WorkItems, List<string> Warnings);

    // What the index was built from: digests, never paths, so the same inputs give the same bytes anywhere.
    public record Inputs(string HistorySchema, string HistoryDigest, string? Repository, string? Base, string? BaseSha,
        string? HeadSha, bool Truncated, string? Scope, List<WorkItemInput> WorkItems);
    public record WorkItemInput(int Id, int? Revision, string Digest);

    // One exported commit: its linkage, its PR associations and what it touched. Mapping: `mapped` (scanned as of the
    // commit), `missing` (not in the local repository: its touches are unknown), `no-source` (changed no scanned file).
    public record Commit(string Sha, string Linkage, List<int> WorkItems, List<PullRequestRef> PullRequests, string Mapping,
        List<Touch> Touches, List<FileTouch> FileOnly);
    public record PullRequestRef(int Id, string Confidence);

    // A symbol a hunk overlapped, as of the commit: its id and body hash then, the hunk's lines and the symbol's lines.
    public record Touch(string Symbol, string Kind, string? Hash, string File, string Lines, string SymbolLines);
    // A hunk that overlapped no symbol (usings, a deleted file): evidence about the file, never about a symbol.
    public record FileTouch(string File, string Lines);

    public record PullRequest(int Id, string? Title, string? MergeStrategy, string Linkage, List<int> WorkItems);

    // A work item as ado-devops normalized it, cleaned and bounded. Revision is the one exported; IsCurrent false means
    // a historical revision. Digest is over the exported file, so a changed revision changes the index fingerprint.
    public record WorkItem(int Id, int? Revision, int? CurrentRevision, bool? IsCurrent, string? Type, string? State,
        string? Title, string? Url, string? ChangedDate, string? DescriptionField, string? Description, string? CriteriaField,
        List<string> Criteria, string Digest, List<string> Warnings);

    // ---- inputs ----

    record HistoryFile(string? Schema, HistoryRepository? Repository, HistoryRange? Range, List<HistoryCommit>? Commits,
        List<HistoryPullRequest>? PullRequests, List<string>? Warnings, bool Truncated);
    record HistoryRepository(string? Provider, string? Id, string? Name, string? Url);
    record HistoryRange(string? Base, string? BaseSha, string? Head, string? HeadSha);
    record HistoryCommit(string? Sha, List<HistoryChange>? Changes, HistoryLinks? WorkItems, List<HistoryPullRequestRef>? PullRequests);
    record HistoryChange(string? Path, string? OldPath, string? Status, List<Hunk>? Hunks);
    public record Hunk(int OldStart, int OldLines, int NewStart, int NewLines);
    record HistoryLinks(string? Status, List<HistoryWorkItemRef>? Items);
    record HistoryWorkItemRef(int Id);
    record HistoryPullRequestRef(int Id, string? Confidence);
    record HistoryPullRequest(int Id, string? Title, string? Status, string? MergeStrategy, HistoryLinks? WorkItems);

    // A commit as the index builder needs it: its source changes (paths relative to the scanned directory) and links.
    record Change(string Path, bool Deleted, List<Hunk> Hunks);

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
                if (SchemaOf(item) != WorkItemSchema)
                    throw new ArgumentException($"{file}: unknown schema {SchemaOf(item)}; expected {WorkItemSchema}");
                // An item's own digest: its canonical JSON, so an array file changing one item changes only that one.
                items.Add((item, Digest(Encoding.UTF8.GetBytes(item.GetRawText())), list.Count == 1 ? Path.GetFileName(file) : $"{Path.GetFileName(file)}[{i}]"));
            }
        }
        return items;
    }

    // ---- build ----

    // Builds the index. `modelAt` scans the code as of a commit (null when the commit isn't in the local repository);
    // `prefix` is the scanned directory's path inside the repository (history paths are repository-relative);
    // `previous` lends the touches of commits it already mapped under the same scope, so a refresh only scans new commits.
    public static Index Build(byte[] history, List<(JsonElement Item, string Digest, string Name)> workItems, string? scope,
        string prefix, Func<string, CodeModel?> modelAt, Func<string, bool> scanned, Index? previous = null, Action<string>? log = null)
    {
        JsonElement raw;
        try { raw = JsonDocument.Parse(history).RootElement.Clone(); }
        catch (JsonException e) { throw new ArgumentException($"history: not JSON ({e.Message})"); }
        if (SchemaOf(raw) != HistorySchema) throw new ArgumentException($"history: unknown schema {SchemaOf(raw)}; expected {HistorySchema}");
        var file = raw.Deserialize<HistoryFile>(Read)!;
        var warnings = new List<string>(file.Warnings ?? []);
        if (file.Truncated) warnings.Add("the history export was truncated: older commits are not covered");

        var items = workItems.Select(w => Normalize(w.Item, w.Digest, w.Name)).ToList();
        foreach (var dup in items.GroupBy(w => w.Id).Where(g => g.Count() > 1))
            warnings.Add($"WI-{dup.Key}: {dup.Count()} exports; using revision {dup.Max(w => w.Revision ?? -1)}");
        // Several exports of one item: the highest revision wins, then the digest, so the choice is stable.
        items = [.. items.GroupBy(w => w.Id).Select(g => g.OrderByDescending(w => w.Revision ?? -1).ThenBy(w => w.Digest, StringComparer.Ordinal).First()).OrderBy(w => w.Id)];

        var pullRequests = new List<PullRequest>();
        foreach (var pr in file.PullRequests ?? [])
        {
            if (pr.Status is { } s && s != "completed")
            {
                warnings.Add($"PR {pr.Id} is {s}, not completed: its links are not used");
                continue;
            }
            var (linkage, ids) = Links(pr.WorkItems, $"PR {pr.Id}");
            pullRequests.Add(new(pr.Id, Clean(pr.Title, MaxTitle, null), pr.MergeStrategy, linkage, ids));
        }
        if (pullRequests.GroupBy(p => p.Id).FirstOrDefault(g => g.Count() > 1) is { } twice) throw new ArgumentException($"history: PR {twice.Key} is listed twice");
        pullRequests = [.. pullRequests.OrderBy(p => p.Id)];

        var reuse = previous is not null && previous.Inputs.Scope == scope
            ? previous.Commits.Where(c => c.Mapping != "missing").ToDictionary(c => c.Sha) : [];
        var commits = new List<Commit>();
        var seen = new HashSet<string>();
        foreach (var c in file.Commits ?? [])
        {
            if (c.Sha is null || !Regex.IsMatch(c.Sha, "^[0-9a-f]{40}$"))
                throw new ArgumentException($"history: commit {c.Sha ?? "(none)"} is not a full 40-character SHA");
            if (!seen.Add(c.Sha)) throw new ArgumentException($"history: commit {c.Sha} is listed twice");
            var (linkage, ids) = Links(c.WorkItems, $"commit {c.Sha[..7]}");
            var prs = (c.PullRequests ?? []).Select(p => new PullRequestRef(p.Id, p.Confidence ?? "exact")).DistinctBy(p => p.Id).OrderBy(p => p.Id).ToList();
            if (prs.FirstOrDefault(p => !Confidences.Contains(p.Confidence)) is { } bad)
                throw new ArgumentException($"history: commit {c.Sha[..7]}: PR confidence '{bad.Confidence}' ({string.Join(" or ", Confidences)})");
            var changes = Changes(c, prefix).Where(x => scanned(x.Path)).ToList();
            if (changes.Count == 0)
            {
                commits.Add(new(c.Sha, linkage, ids, prs, "no-source", [], []));
                continue;
            }
            if (reuse.TryGetValue(c.Sha, out var known))
            {
                commits.Add(known with { Linkage = linkage, WorkItems = ids, PullRequests = prs });
                continue;
            }
            log?.Invoke($"mapping {c.Sha[..7]} ({changes.Count} source file{(changes.Count == 1 ? "" : "s")})");
            if (modelAt(c.Sha) is not { } model)
            {
                warnings.Add($"commit {c.Sha[..7]} is not in the local repository: what it changed is unknown");
                commits.Add(new(c.Sha, linkage, ids, prs, "missing", [], []));
                continue;
            }
            var (touches, fileOnly) = Map(model, changes);
            commits.Add(new(c.Sha, linkage, ids, prs, "mapped", touches, fileOnly));
        }

        var inputs = new Inputs(HistorySchema, Digest(history), file.Repository?.Url ?? file.Repository?.Name ?? file.Repository?.Id,
            file.Range?.Base, file.Range?.BaseSha, file.Range?.HeadSha, file.Truncated, scope,
            [.. items.Select(w => new WorkItemInput(w.Id, w.Revision, w.Digest))]);
        var fingerprint = Fingerprint(inputs);
        return new(Schema, fingerprint, inputs, commits, pullRequests, items, [.. warnings.Distinct()]);
    }

    // Over everything the index is derived from: the history, each work item, the scan scope and this schema.
    public static string Fingerprint(Inputs inputs) => Digest(Encoding.UTF8.GetBytes(string.Join("\n",
        [Schema, inputs.HistorySchema, inputs.HistoryDigest, inputs.Scope ?? "", .. inputs.WorkItems.Select(w => $"{w.Id}:{w.Revision}:{w.Digest}")])))[..16];

    // The fingerprint the given inputs would produce, without scanning anything: equal to a saved index's means unchanged.
    public static string Fingerprint(byte[] history, List<(JsonElement Item, string Digest, string Name)> workItems, string? scope)
    {
        var items = workItems.Select(w => Normalize(w.Item, w.Digest, w.Name))
            .GroupBy(w => w.Id).Select(g => g.OrderByDescending(w => w.Revision ?? -1).ThenBy(w => w.Digest, StringComparer.Ordinal).First()).OrderBy(w => w.Id);
        return Fingerprint(new Inputs(HistorySchema, Digest(history), null, null, null, null, false, scope,
            [.. items.Select(w => new WorkItemInput(w.Id, w.Revision, w.Digest))]));
    }

    static (string Linkage, List<int> Ids) Links(HistoryLinks? links, string what)
    {
        var status = links?.Status ?? "unknown";
        if (!Linkages.Contains(status)) throw new ArgumentException($"history: {what}: work-item status '{status}' ({string.Join(", ", Linkages)})");
        var ids = (links?.Items ?? []).Select(i => i.Id).Distinct().Order().ToList();
        // Ids from a lookup that didn't complete are kept: they are real links, the list just may be incomplete.
        return (status, ids);
    }

    // The commit's changes under the scanned directory, paths relative to it. A rename keeps only the new path: the
    // symbols are mapped as of the commit, where the file lives at the new path.
    static IEnumerable<Change> Changes(HistoryCommit c, string prefix)
    {
        foreach (var ch in c.Changes ?? [])
        {
            if (ch.Path is null) continue;
            var path = ch.Path.Replace('\\', '/');
            if (prefix.Length > 0)
            {
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                path = path[prefix.Length..];
            }
            var hunks = ch.Hunks ?? [];
            if (hunks.Any(h => h.NewStart < 0 || h.NewLines < 0 || h.OldStart < 0 || h.OldLines < 0))
                throw new ArgumentException($"history: commit {c.Sha![..7]}: negative hunk position in {ch.Path}");
            yield return new(path, ch.Status == "deleted", [.. hunks]);
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
        if (SchemaOf(raw) != Schema) throw new ArgumentException($"{file}: unknown schema {SchemaOf(raw)}; expected {Schema} (run docwizz requirements import again)");
        return raw.Deserialize<Index>(Read) ?? throw new ArgumentException($"{file}: empty index");
    }
}
