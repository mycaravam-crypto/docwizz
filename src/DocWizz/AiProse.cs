using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

// Drafts summaries for items that need docs and have none. Deterministic facts go in, prose comes out.
// Results are cached per (symbol, body hash): unchanged code is never sent twice.
// Only a self-hosted Ollama is used: every connection must go to a loopback or private-network address,
// and Ollama's cloud-hosted models are refused, so source code never leaves the network.
static class AiProse
{
    const string DefaultModel = "qwen2.5-coder:7b";
    const int MaxSourceLines = 150;
    const int Parallelism = 2; // Ollama queues requests beyond OLLAMA_NUM_PARALLEL anyway

    const string Instructions = """
        You write reference documentation for a codebase. You get facts about one symbol, produced by
        static analysis, plus its source. Write a 1-3 sentence summary of what it does and when a caller
        would use it. Mention side effects (database writes, HTTP calls, events) when the facts show them;
        `inferred` facts come from heuristics, so state them only when the source confirms them.
        Plain prose only: no headings, no lists, no code fences, don't restate the symbol's name, and
        don't guess at anything the facts and source don't support.
        """;

    record Target(Node Node, DocumentationItem Item);

    // A draft and the symbols whose facts it was generated from (provenance).
    public record Draft(string Text, List<string> Sources);

    // Returns node id → summary, from cache and (when allowed) fresh API calls.
    public static async Task<Dictionary<string, Draft>> Summaries(
        string root, CodeModel model, List<DocumentationItem> findings, string cacheFile, bool call)
    {
        var cache = Load(cacheFile);
        var targets = findings.Where(f => f.Missing.Contains("summary") && f.Node.Hash is not null)
            .Select(f => new Target(f.Node, f)).ToList();
        var result = new Dictionary<string, Draft>();
        var misses = new List<Target>();
        foreach (var t in targets)
            if (cache.TryGetValue(Key(t.Node), out var text)) result[t.Node.Id] = text;
            else misses.Add(t);

        if (call && misses.Count > 0)
        {
            var name = Environment.GetEnvironmentVariable("DOCWIZZ_MODEL") is { Length: > 0 } m ? m : DefaultModel;
            // Ollama's `…-cloud` / `…:cloud` models run on ollama.com: that would send the code out.
            if (name.Split(':').Last().EndsWith("cloud", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"docwizz: AI summaries skipped — {name} is an Ollama cloud model; use a local one");
                return result;
            }
            using var client = LocalOnly(OllamaUri());
            Console.Error.WriteLine($"docwizz: drafting {misses.Count} summaries with {name} at {client.BaseAddress} ({targets.Count - misses.Count} cached)");
            var facts = new Facts(root, model);
            using var gate = new SemaphoreSlim(Parallelism);
            var stopped = 0;
            var drafted = await Task.WhenAll(misses.Select(async t =>
            {
                await gate.WaitAsync();
                try
                {
                    if (Volatile.Read(ref stopped) == 1) return (t.Node, Draft: null);
                    var (json, sources) = facts.For(t.Node, t.Item);
                    return (t.Node, Draft: await Create(client, name, json) is { } text ? new Draft(text, sources) : null);
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
                result[node.Id] = cache[Key(node)] = draft!;
            Save(cacheFile, cache);
        }
        else if (misses.Count > 0)
            Console.Error.WriteLine($"docwizz: {misses.Count} items lack docs; pass --ai to draft summaries");

        return result;
    }

    static async Task<string?> Create(HttpClient client, string model, string facts)
    {
        using var response = await client.PostAsync("api/chat", new StringContent(JsonSerializer.Serialize(new
        {
            model,
            stream = false,
            options = new { temperature = 0 },
            messages = new[] { new { role = "system", content = Instructions }, new { role = "user", content = facts } },
        }), System.Text.Encoding.UTF8, "application/json"));
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException($"model {model} not found (ollama pull {model}, or set DOCWIZZ_MODEL)");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var text = body.GetProperty("message").GetProperty("content").GetString() ?? "";
        text = Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim(); // reasoning models
        return text.Length > 0 ? text : null;
    }

    // OLLAMA_HOST as Ollama itself reads it: `host`, `host:port` or a URL; default localhost:11434.
    public static Uri OllamaUri()
    {
        var v = Environment.GetEnvironmentVariable("OLLAMA_HOST") is { Length: > 0 } h ? h.Trim() : "localhost";
        var b = new UriBuilder(v.Contains("://") ? v : "http://" + v);
        if (!Regex.IsMatch(v, @":\d+(/|$)")) b.Port = 11434;
        if (b.Host is "0.0.0.0" or "[::]") b.Host = "localhost"; // bind-all address, as the server is often configured
        b.Path = b.Path.TrimEnd('/') + "/";
        return b.Uri;
    }

    // Connects only to loopback/private addresses, checked on the resolved address of every connection
    // (redirects and DNS answers included), and never through a proxy.
    static HttpClient LocalOnly(Uri baseAddress) => new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            if (addresses.Length == 0 || !addresses.All(IsPrivate))
                throw new IOException($"{ctx.DnsEndPoint.Host} is not a local or private address; docwizz only uses a self-hosted Ollama");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(addresses, ctx.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        },
    }) { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(10) }; // local models on CPU are slow

    // Loopback, RFC 1918, IPv6 unique-local.
    public static bool IsPrivate(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.IsIPv6UniqueLocal) return true;
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and < 32) || (b[0] == 192 && b[1] == 168);
    }

    static string Key(Node n) => $"{n.Id}@{n.Hash}";

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

    // Facts JSON for one symbol: what the graph knows, plus its own source lines.
    class Facts(string root, CodeModel model)
    {
        readonly Dictionary<string, Node> nodes = model.Nodes.ToDictionary(n => n.Id);
        readonly ILookup<string, Edge> outgoing = model.Edges.ToLookup(e => e.From);
        readonly ILookup<string, Edge> incoming = model.Edges.ToLookup(e => e.To);

        string Name(string id) => nodes.TryGetValue(id, out var n) ? Generator.Display(n) : id.Replace("http:", "");

        // The facts JSON and the symbols it mentions (the draft's provenance).
        public (string Json, List<string> Sources) For(Node n, DocumentationItem f)
        {
            var sources = new List<string> { n.Id };
            IEnumerable<string> Pick(IEnumerable<string> ids) { var l = ids.Distinct().ToList(); sources.AddRange(l.Where(nodes.ContainsKey)); return l.Select(Name); }
            IEnumerable<string> Out(params string[] kinds) => Pick(outgoing[n.Id].Where(e => kinds.Contains(e.Kind)).Select(e => e.To));
            IEnumerable<string> In(params string[] kinds) => Pick(incoming[n.Id].Where(e => kinds.Contains(e.Kind)).Select(e => e.From));

            var json = JsonSerializer.Serialize(new
            {
                symbol = Generator.Display(n),
                kind = n.Kind,
                file = n.File,
                route = n.Route is null ? null : $"{n.Tags?.ElementAtOrDefault(1)} {n.Route}".Trim(),
                visibility = n.Visibility,
                complexity = n.Complexity,
                parameters = n.Parameters,
                returns = n.Returns,
                throws = n.Throws,
                emits = n.Events,
                existingDocs = n.Doc,
                calls = Out("calls"),
                httpCalls = Out("http"),
                injects = Out("injects"),
                renders = Out("renders"),
                publishes = Out("publishes"),
                calledBy = In("calls", "http"),
                renderedBy = In("renders"),
                derived = f.Sections.Where(kv => kv.Value.Origin is Origin.Fact or Origin.Inferred)
                    .ToDictionary(kv => kv.Key, kv => $"{kv.Value.Text} ({kv.Value.Origin.ToString().ToLowerInvariant()})"),
                whyItNeedsDocs = f.Reasons,
                source = Source(n),
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep source readable: no \u003C
            });
            return (json, sources.Concat(f.Sources).Distinct().ToList());
        }

        string? Source(Node n)
        {
            var path = Path.Combine(root, n.File);
            if (!File.Exists(path)) return null;
            var end = Math.Min(n.EndLine ?? n.Line + MaxSourceLines, n.Line + MaxSourceLines);
            return string.Join('\n', File.ReadLines(path).Skip(n.Line - 1).Take(end - n.Line + 1));
        }
    }
}
