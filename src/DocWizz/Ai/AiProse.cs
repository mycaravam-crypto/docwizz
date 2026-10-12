using System.Runtime.CompilerServices;
using System.Text.Json;

// Drafts documentation for items that need docs and have no summary, and an overview per module. Deterministic facts go
// in, sections of cited sentences come out: every sentence names the facts it rests on, and one that cites nothing in the
// facts is dropped. Results are cached per (symbol or module, body hash): unchanged code is never sent twice.
// The model is a self-hosted one behind IAiProvider (AiProvider.cs): every connection must go to a loopback or
// private-network address, and cloud-hosted models are refused, so source code never leaves the network.
static class AiProse
{
    const int Parallelism = 2; // Ollama queues requests beyond OLLAMA_NUM_PARALLEL anyway
    // A folder with fewer members than this gets no overview requested: its members' own docs already say it all, and
    // on a typical repo these small folders are most of the overview requests. Overviews already cached are still shown.
    const int MinModuleMembers = 3;

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

    // A reply with nothing usable in it (no cited sentence, no answer), cached like a result so the same code isn't asked
    // again: at temperature 0 it would answer the same. Never shown.
    static readonly Draft NoDraft = new("", [], []);
    static readonly Assessment NoAssessment = new(0, [], "");
    static bool Usable(Draft d) => d.Text.Length > 0 || d.Sections is not { Count: 0 };

    // Returns node id → summary, from cache and (when allowed) fresh API calls. progress: (drafts done, of total, the item
    // asked for most recently).
    public static async Task<Dictionary<string, Draft>> Summaries(
        string root, CodeModel model, List<DocumentationItem> findings, string cacheFile, bool call, AiConfig? config = null,
        Action<int, int, string?>? progress = null)
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
            if (text is not null && Usable(text)) result[t.Node.Id] = text;
            if (text is null || (call && text.Sections is null)) misses.Add(t); // with --ai, summary-only drafts get their sections
        }
        var modules = ModuleTargets(model);
        foreach (var (key, _, _) in modules)
            if (Cached(cache, key, tag) is { } d && Usable(d)) result[key.Split('@')[0]] = d;
        var moduleMisses = modules.Where(m => Cached(cache, m.Key, tag) is null).ToList();
        var small = moduleMisses.RemoveAll(m => m.Members.Count(ContextBuilder.Shown) < MinModuleMembers);

        if (call && misses.Count + moduleMisses.Count > 0)
        {
            if (AiProviders.Create(config, "summaries") is not { } ai) return result;
            using var _ = ai;
            Console.Error.WriteLine($"docwizz: drafting {misses.Count} symbols and {moduleMisses.Count} module overviews with {ai.Model} at {ai.Endpoint} " +
                $"({targets.Count - misses.Count} cached{(small > 0 ? $", {small} folders under {MinModuleMembers} members get no overview" : "")})");
            var facts = new ContextBuilder(root, model);
            var stop = new StrongBox<bool>();
            var step = Steps(misses.Count + moduleMisses.Count, progress);
            var drafted = await Ask(misses, async t =>
            {
                var (json, sources, names) = facts.For(t.Node, t.Item);
                return Parse(await ai.Complete(Instructions, json), sources, names) ?? NoDraft;
            }, "summaries", t => Label(t.Node), t => $"summary failed for {t.Node.Id}", stop, step);
            foreach (var (t, d) in misses.Zip(drafted))
            {
                if (d is null || (!Usable(d) && result.ContainsKey(t.Node.Id))) continue; // keep a summary-only draft over nothing
                cache[$"{Key(t.Node)}@{tag}"] = d;
                if (Usable(d)) result[t.Node.Id] = d;
            }
            // Module overviews after the symbols, so they build on the fresh drafts.
            var overviews = await Ask(moduleMisses, async m =>
            {
                var (json, sources, names) = facts.ForModule(m.Folder, m.Members, result);
                return Parse(await ai.Complete(ModuleInstructions, json), sources, names) ?? NoDraft;
            }, "module overviews", m => m.Folder, m => $"overview failed for {m.Folder}", stop, step);
            foreach (var (m, d) in moduleMisses.Zip(overviews).Where(x => x.Second is not null))
            {
                cache[$"{m.Key}@{tag}"] = d!;
                if (Usable(d!)) result[m.Key.Split('@')[0]] = d!;
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
        string root, CodeModel model, List<DocumentationItem> findings, string cacheFile, bool call, AiConfig? config = null,
        Action<int, int, string?>? progress = null)
    {
        config ??= new();
        var tag = call ? AiProviders.Tag(config) : null;
        Dictionary<string, Assessment> cache;
        try { cache = File.Exists(cacheFile) ? JsonSerializer.Deserialize<Dictionary<string, Assessment>>(File.ReadAllText(cacheFile), Web) ?? [] : []; }
        catch (JsonException) { cache = []; }
        static string Key(Node n) => $"{n.Id}@{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{n.Doc}|{n.Hash}")))[..12].ToLowerInvariant()}";
        var targets = findings.Where(f => f.Node.Doc is not null && f.Sections.GetValueOrDefault("summary")?.Origin == Origin.Written).ToList();
        var result = targets.Where(f => Cached(cache, Key(f.Node), tag) is { Score: > 0 }).ToDictionary(f => f.Node.Id, f => Cached(cache, Key(f.Node), tag)!);
        var misses = targets.Where(f => Cached(cache, Key(f.Node), tag) is null).ToList();
        if (!call || misses.Count == 0 || AiProviders.Create(config, "assessments") is not { } ai) return result;

        using var _ = ai;
        Console.Error.WriteLine($"docwizz: assessing {misses.Count} written docs with {ai.Model} at {ai.Endpoint} ({result.Count} cached)");
        var facts = new ContextBuilder(root, model);
        var answers = await Ask(misses, async f => ParseAssessment(await ai.Complete(AssessInstructions, facts.For(f.Node, f).Json)) ?? NoAssessment,
            "assessments", f => Label(f.Node), f => $"assessment failed for {f.Node.Id}", new(), Steps(misses.Count, progress));
        foreach (var (f, a) in misses.Zip(answers).Where(x => x.Second is not null))
        {
            cache[$"{Key(f.Node)}@{tag}"] = a!;
            if (a!.Score > 0) result[f.Node.Id] = a;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cacheFile))!);
        File.WriteAllText(cacheFile, JsonSerializer.Serialize(new SortedDictionary<string, Assessment>(cache), new JsonSerializerOptions(Web) { WriteIndented = true }));
        return result;
    }

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // Asks for every item, Parallelism at a time (requests beyond what the server runs at once just queue there), and
    // returns the answers in order: null for an item that failed or was skipped. An HttpRequestException means the server
    // can't be used at all (not running, not local, model missing): it is reported once, as `what` skipped, and the rest
    // are skipped, here and in later batches that share `stop`. `step` hears each item's `name` as it is asked, and null
    // after every item.
    static async Task<R?[]> Ask<T, R>(IEnumerable<T> items, Func<T, Task<R>> ask, string what, Func<T, string> name,
        Func<T, string> failed, StrongBox<bool> stop, Action<string?> step) where R : class
    {
        using var gate = new SemaphoreSlim(Parallelism);
        return await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync();
            try
            {
                if (Volatile.Read(ref stop.Value)) return null;
                step(name(item));
                return await ask(item);
            }
            catch (HttpRequestException e)
            {
                lock (stop)
                    if (!stop.Value)
                    {
                        stop.Value = true;
                        Console.Error.WriteLine($"docwizz: AI {what} skipped — {e.InnerException?.Message ?? e.Message}");
                    }
                return null;
            }
            catch (Exception e) { Console.Error.WriteLine($"docwizz: AI {failed(item)}: {e.Message}"); return null; }
            finally { gate.Release(); step(null); }
        }));
    }

    // What the progress bar names an item by: a method with its type (MaterialController.Get), else the name.
    static string Label(Node n) => n.Kind == "method" ? string.Join('.', Naming.Display(n).Split('(')[0].Split('.').TakeLast(2)) : n.Name;

    // Ask's `step` for `work` items: reports (finished, work, the item asked for most recently), from (0, work) on.
    static Action<string?> Steps(int work, Action<int, int, string?>? progress)
    {
        var finished = 0;
        string? current = null;
        progress?.Invoke(0, work, null);
        return started =>
        {
            if (started is null) Interlocked.Increment(ref finished); else current = started;
            progress?.Invoke(Volatile.Read(ref finished), work, current);
        };
    }

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
        catch (JsonException) { return new Draft(reply, sources, []); } // no sections, not a draft from before sections
        if (json.ValueKind != JsonValueKind.Object) return new Draft(reply, sources, []);

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
            .GroupBy(n => Naming.Folder(n.File)).OrderBy(g => g.Key)
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
        return model.Nodes.Where(n => n.Hash is not null && Cached(cache, Key(n), null) is { } d && Usable(d)).ToDictionary(n => n.Id, n => Cached(cache, Key(n), null)!);
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
