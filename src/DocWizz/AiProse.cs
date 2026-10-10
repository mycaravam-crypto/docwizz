using System.Text.Json;

// Drafts documentation for items that need docs and have no summary, and an overview per module. Deterministic facts go
// in, sections of cited sentences come out: every sentence names the facts it rests on, and one that cites nothing in the
// facts is dropped. Results are cached per (symbol or module, body hash): unchanged code is never sent twice.
// The model is a self-hosted one behind IAiProvider (AiProvider.cs): every connection must go to a loopback or
// private-network address, and cloud-hosted models are refused, so source code never leaves the network.
static class AiProse
{
    const int Parallelism = 2; // Ollama queues requests beyond OLLAMA_NUM_PARALLEL anyway

    const string Instructions = """
        You write reference documentation for a codebase. You get facts about one symbol, produced by static
        analysis, plus its source. Answer with one JSON object and nothing else:
        {"summary": [], "responsibilities": [], "behaviour": [], "side_effects": [], "errors": [], "usage": []}
        Each list holds sentences as {"text": "...", "from": ["..."]}. `from` names the facts the sentence rests on:
        symbol names exactly as they appear in the facts, or "source" for the symbol's own source code.
        summary: 1-3 sentences on what it does and when a caller would use it. responsibilities: what it is in
        charge of. behaviour: how it works, step by step where that helps. side_effects: database writes, HTTP
        calls, events. errors: what fails and how. usage: how callers use it.
        `inferred` facts come from heuristics: state them only when the source confirms them. Leave a list empty
        when the facts don't support it. Plain sentences: no markdown, don't restate the symbol's name, and don't
        guess at anything the facts and source don't support.
        """;

    const string ModuleInstructions = """
        You write the overview of one module (a folder of code) for developers new to it. You get facts from static
        analysis: its members with their documentation, what it depends on and what depends on it. Answer with one
        JSON object and nothing else: {"summary": [], "responsibilities": [], "usage": []}
        Each list holds sentences as {"text": "...", "from": ["..."]}, where `from` names the members or modules
        the sentence rests on, exactly as they appear in the facts. summary: 1-2 sentences on what the module is for.
        responsibilities: what it is in charge of. usage: who uses it and how. Leave a list empty when the facts
        don't support it; don't guess.
        """;

    const string AssessInstructions = """
        You review the existing documentation of one symbol for a developer who has to call or change it. You get facts
        from static analysis, its source, and its doc comment as `existingDocs`. Answer with one JSON object and nothing
        else: {"score": 1, "missing": [], "note": ""}
        score: 1 = says nothing the name doesn't; 2 = restates the signature; 3 = says what it does, not when or why;
        4 = also the purpose and the constraints a caller must respect; 5 = also the side effects and errors the facts show.
        missing: what a caller needs that the docs leave out, each one of "purpose", "constraints", "side effects",
        "errors", "usage"; name only what the facts or source show exists. note: one plain sentence on the biggest gap,
        or "" when there is none. Judge only `existingDocs`; don't rewrite them.
        """;

    static readonly string[] Gaps = ["purpose", "constraints", "side effects", "errors", "usage"];

    // An AI rating of a written doc comment. Advisory: never counted in doc quality, never fails a check.
    public record Assessment(int Score, List<string> Missing, string Note);

    // Sections a symbol draft may fill, in page order; `errors` fills the profile's `exception` section.
    public static readonly string[] SectionNames = ["summary", "responsibilities", "behaviour", "side_effects", "errors", "usage"];

    record Target(Node Node, DocumentationItem Item);

    // A sentence and the symbols it was drafted from.
    public record Sentence(string Text, List<string> From);

    // A draft: the summary text, the symbols its facts came from (provenance), and every section as cited sentences.
    // Drafts cached before sections existed have Sections = null.
    public record Draft(string Text, List<string> Sources, Dictionary<string, List<Sentence>>? Sections = null);

    // Returns node id → summary, from cache and (when allowed) fresh API calls.
    public static async Task<Dictionary<string, Draft>> Summaries(
        string root, CodeModel model, List<DocumentationItem> findings, string cacheFile, bool call, AiConfig? config = null)
    {
        config ??= new();
        var cache = Load(cacheFile);
        var tag = call ? AiProviders.Tag(config) : null;
        var targets = findings.Where(f => f.Missing.Contains("summary") && f.Node.Hash is not null)
            .Select(f => new Target(f.Node, f)).ToList();
        var result = new Dictionary<string, Draft>();
        var misses = new List<Target>();
        foreach (var t in targets)
        {
            var text = Cached(cache, Key(t.Node), tag);
            if (text is not null) result[t.Node.Id] = text;
            if (text is null || (call && text.Sections is null)) misses.Add(t); // with --ai, summary-only drafts get their sections
        }
        var modules = ModuleTargets(model);
        foreach (var (key, _, _) in modules)
            if (Cached(cache, key, tag) is { } d) result[key.Split('@')[0]] = d;
        var moduleMisses = modules.Where(m => Cached(cache, m.Key, tag) is null).ToList();

        if (call && misses.Count + moduleMisses.Count > 0)
        {
            if (AiProviders.Create(config, "summaries") is not { } ai) return result;
            using var _ = ai;
            Console.Error.WriteLine($"docwizz: drafting {misses.Count} symbols and {moduleMisses.Count} module overviews with {ai.Model} at {ai.Endpoint} " +
                $"({targets.Count - misses.Count} cached)");
            var facts = new ContextBuilder(root, model);
            using var gate = new SemaphoreSlim(Parallelism);
            var stopped = 0;
            var drafted = await Task.WhenAll(misses.Select(async t =>
            {
                await gate.WaitAsync();
                try
                {
                    if (Volatile.Read(ref stopped) == 1) return (t.Node, Draft: null);
                    var (json, sources, names) = facts.For(t.Node, t.Item);
                    return (t.Node, Draft: Parse(await ai.Complete(Instructions, json), sources, names));
                }
                catch (HttpRequestException e)
                {
                    // Not running, not local, model missing: every other call would fail the same way; say it once and stop.
                    if (Interlocked.Exchange(ref stopped, 1) == 0)
                        Console.Error.WriteLine($"docwizz: AI summaries skipped — {e.InnerException?.Message ?? e.Message}");
                    return (t.Node, Draft: null);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"docwizz: AI summary failed for {t.Node.Id}: {e.Message}");
                    return (t.Node, Draft: null);
                }
                finally { gate.Release(); }
            }));
            foreach (var (node, draft) in drafted.Where(d => d.Draft is not null))
                result[node.Id] = cache[$"{Key(node)}@{tag}"] = draft!;
            // Module overviews after the symbols, so they build on the fresh drafts; one at a time is plenty.
            foreach (var (key, folder, members) in moduleMisses)
            {
                if (Volatile.Read(ref stopped) == 1) break;
                try
                {
                    var (json, sources, names) = facts.ForModule(folder, members, result);
                    if (Parse(await ai.Complete(ModuleInstructions, json), sources, names) is { } d) result[key.Split('@')[0]] = cache[$"{key}@{tag}"] = d;
                }
                catch (HttpRequestException e) { Console.Error.WriteLine($"docwizz: AI module overviews skipped — {e.InnerException?.Message ?? e.Message}"); break; }
                catch (Exception e) { Console.Error.WriteLine($"docwizz: AI overview failed for {folder}: {e.Message}"); }
            }
            Save(cacheFile, cache);
        }
        else if (misses.Count > 0)
            Console.Error.WriteLine($"docwizz: {misses.Count} items lack docs; pass --ai to draft summaries");

        return result;
    }

    // Rates the written docs of items that have them, from cache and (when allowed) fresh calls, keyed by symbol, doc
    // and body: a doc or code change asks again.
    public static async Task<Dictionary<string, Assessment>> Assessments(
        string root, CodeModel model, List<DocumentationItem> findings, string cacheFile, bool call, AiConfig? config = null)
    {
        config ??= new();
        var tag = call ? AiProviders.Tag(config) : null;
        Dictionary<string, Assessment> cache;
        try { cache = File.Exists(cacheFile) ? JsonSerializer.Deserialize<Dictionary<string, Assessment>>(File.ReadAllText(cacheFile), Web) ?? [] : []; }
        catch (JsonException) { cache = []; }
        static string Key(Node n) => $"{n.Id}@{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{n.Doc}|{n.Hash}")))[..12].ToLowerInvariant()}";
        var targets = findings.Where(f => f.Node.Doc is not null && f.Sections.GetValueOrDefault("summary")?.Origin == Origin.Written).ToList();
        var result = targets.Where(f => Cached(cache, Key(f.Node), tag) is not null).ToDictionary(f => f.Node.Id, f => Cached(cache, Key(f.Node), tag)!);
        var misses = targets.Where(f => Cached(cache, Key(f.Node), tag) is null).ToList();
        if (!call || misses.Count == 0 || AiProviders.Create(config, "assessments") is not { } ai) return result;

        using var _ = ai;
        Console.Error.WriteLine($"docwizz: assessing {misses.Count} written docs with {ai.Model} at {ai.Endpoint} ({result.Count} cached)");
        var facts = new ContextBuilder(root, model);
        foreach (var f in misses)
        {
            try
            {
                if (ParseAssessment(await ai.Complete(AssessInstructions, facts.For(f.Node, f).Json)) is { } a)
                    result[f.Node.Id] = cache[$"{Key(f.Node)}@{tag}"] = a;
            }
            catch (HttpRequestException e) { Console.Error.WriteLine($"docwizz: AI assessments skipped — {e.InnerException?.Message ?? e.Message}"); break; }
            catch (Exception e) { Console.Error.WriteLine($"docwizz: AI assessment failed for {f.Node.Id}: {e.Message}"); }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cacheFile))!);
        File.WriteAllText(cacheFile, JsonSerializer.Serialize(new SortedDictionary<string, Assessment>(cache), new JsonSerializerOptions(Web) { WriteIndented = true }));
        return result;
    }

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // {"score", "missing", "note"}; anything else (not JSON, score outside 1-5) is no assessment.
    static Assessment? ParseAssessment(string? reply)
    {
        try
        {
            var json = JsonDocument.Parse(reply ?? "").RootElement;
            if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("score", out var s) || !s.TryGetInt32(out var score) || score is < 1 or > 5)
                return null;
            var missing = json.TryGetProperty("missing", out var m) && m.ValueKind == JsonValueKind.Array
                ? m.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim().ToLowerInvariant())
                    .Where(Gaps.Contains).Distinct().ToList() : [];
            var note = json.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : "";
            return new(score, missing, note);
        }
        catch (JsonException) { return null; }
    }

    // Sections of cited sentences. `names` maps what the facts call a symbol (and "source") to its id; a sentence keeps
    // only citations found there and is dropped without one. A reply that isn't the JSON asked for is taken as a summary.
    static Draft? Parse(string? reply, List<string> sources, Dictionary<string, string> names)
    {
        if (reply is null) return null;
        JsonElement json;
        try { json = JsonDocument.Parse(reply).RootElement; }
        catch (JsonException) { return new Draft(reply, sources); }
        if (json.ValueKind != JsonValueKind.Object) return new Draft(reply, sources);

        var sections = new Dictionary<string, List<Sentence>>();
        foreach (var section in json.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array))
        {
            var kept = new List<Sentence>();
            foreach (var s in section.Value.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object))
            {
                var text = s.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()!.Trim() : "";
                var from = s.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Array
                    ? f.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? names.GetValueOrDefault(x.GetString()!) : null).OfType<string>().Distinct().ToList()
                    : [];
                if (text.Length > 0 && from.Count > 0) kept.Add(new(text, from));
            }
            if (kept.Count > 0) sections[section.Name] = kept;
        }
        if (sections.Count == 0) return null;
        var summary = string.Join(" ", sections.GetValueOrDefault("summary")?.Select(x => x.Text) ?? []);
        return new Draft(summary, [.. sections.Values.SelectMany(l => l).SelectMany(x => x.From).Distinct()], sections);
    }

    // Every folder with code, keyed by folder and the hashes of what is in it: a changed member re-drafts its module.
    static List<(string Key, string Folder, List<Node> Members)> ModuleTargets(CodeModel model) =>
        [.. model.Nodes.Where(n => n.Hash is not null && n.Kind is not ("config" or "project" or "package" or "external" or "route"))
            .GroupBy(n => Generator.Folder(n.File)).OrderBy(g => g.Key)
            .Select(g => (Key: $"module:{g.Key}@{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(string.Join("|", g.OrderBy(n => n.Id).Select(n => $"{n.Id}={n.Hash}")))))[..12].ToLowerInvariant()}",
                Folder: g.Key, Members: g.ToList()))];

    static string Key(Node n) => $"{n.Id}@{n.Hash}";

    // A cache entry is "<subject>@<hash>@<provider>/<model>"; entries from before providers were configurable have no
    // provider and were drafted by Ollama. With a provider (`tag`, from --ai) only its own entries count, so another model
    // drafts afresh; without one, any draft of the same code is used (the first by key, so the choice is stable).
    public static T? Cached<T>(Dictionary<string, T> cache, string key, string? tag) where T : class =>
        tag is not null
            ? cache.GetValueOrDefault($"{key}@{tag}") ?? (tag.StartsWith("ollama/") ? cache.GetValueOrDefault(key) : null)
            : cache.GetValueOrDefault(key) ?? cache.Where(kv => kv.Key.StartsWith(key + "@", StringComparison.Ordinal))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).FirstOrDefault();

    // Cached drafts for the code as it is now, never a call: agent context packages show them only when asked to.
    public static Dictionary<string, Draft> Cached(CodeModel model, string cacheFile)
    {
        var cache = Load(cacheFile);
        return model.Nodes.Where(n => n.Hash is not null && Cached(cache, Key(n), null) is not null).ToDictionary(n => n.Id, n => Cached(cache, Key(n), null)!);
    }

    // Cache: "<id>@<hash>" → { text, sources }. Entries from before provenance was recorded are plain strings.
    static Dictionary<string, Draft> Load(string file)
    {
        try
        {
            if (!File.Exists(file)) return [];
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(file)) ?? [];
            return raw.ToDictionary(kv => kv.Key, kv => kv.Value.ValueKind == JsonValueKind.String
                ? new Draft(kv.Value.GetString()!, [kv.Key.Split('@')[0]])
                : kv.Value.Deserialize<Draft>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        }
        catch (JsonException) { return []; }
    }

    static void Save(string file, Dictionary<string, Draft> cache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file, JsonSerializer.Serialize(new SortedDictionary<string, Draft>(cache),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }
}
