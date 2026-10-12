using System.Text.Json;
using System.Text.Json.Nodes;

// `docwizz mcp <dir>`: a local, read-only Model Context Protocol server on stdio over the code model. Coding agents ask
// for facts (where a symbol is, who calls it, what an endpoint reaches, what a change affects) instead of searching
// files. docwizz stays AI-free: it answers with facts, the model runs elsewhere. There is no network listener and no
// tool writes anything: generate and setup are deliberately not tools. Every answer carries the commit the model was
// scanned at and whether it is stale; AI drafts stay out unless a tool is asked for them.
sealed class McpServer(McpServer.Host host)
{
    // What the server needs from the CLI: loading and scanning the model, git, and the diff and check reports.
    public record Host(string Root, Config Config, bool AutoRescan,
        Func<(CodeModel Model, string Source, DateTime Since)> Load,
        Func<CodeModel> Scan,
        Func<string?> Head,
        Func<DateTime, int> ChangedSince,
        Func<string, object?> Impact,
        Func<string?, object?> Check);

    public const string ProtocolVersion = "2025-06-18";
    static readonly string[] Supported = [ProtocolVersion, "2025-03-26", "2024-11-05"];
    const int DefaultBudget = 4000;
    const int DefaultLimit = 50;

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    CodeModel model = null!;
    string source = "";
    DateTime since;
    // Derived from the model, rebuilt on a rescan.
    List<DocumentationItem> findings = null!;
    ArchitectureResult arch = null!;
    ContextBuilder graph = null!;
    Dictionary<string, AiProse.Draft> drafts = null!;

    sealed class ToolError(string message) : Exception(message);

    // A tool: its name and description as agents see them (short and precise: local models pick tools by these), the
    // JSON schema of its arguments, and what it does.
    record Tool(string Name, string Description, JsonObject Schema, Func<JsonObject, object> Run);

    static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(props.Select(p => KeyValuePair.Create(p.Name, (JsonNode?)new JsonObject { ["type"] = p.Type, ["description"] = p.Description }))),
        ["required"] = new JsonArray([.. props.Where(p => p.Required).Select(p => (JsonNode?)JsonValue.Create(p.Name))]),
    };

    static readonly (string, string, string, bool) Budget = ("budget", "integer", $"answer size in tokens (characters / 4), default {DefaultBudget}", false);
    static readonly (string, string, string, bool) Offset = ("offset", "integer", "skip this many results (pagination), default 0", false);
    static readonly (string, string, string, bool) Limit = ("limit", "integer", $"at most this many results, default {DefaultLimit}", false);

    List<Tool> Tools() =>
    [
        new("find_symbol", "Find symbols, endpoints and modules by name: exact, then prefix, then substring. Returns ids for the other tools.",
            Schema(("query", "string", "a name, qualified name, `VERB /route` or folder", true), ("kind", "string", "only this kind: class, method, endpoint, module, component, ...", false), Offset, Limit, Budget), FindSymbol),
        new("context", "Token-budgeted facts about one symbol, file, folder or endpoint: signature, neighbours, flows, tests, gaps, files to read.",
            Schema(("target", "string", "symbol id or qualified name, file, folder, or `VERB /route`", true), ("hops", "integer", "neighbour depth 1-4, default 2", false),
                Budget, ("include_ai", "boolean", "include cached AI drafts (marked ai-drafted); default false", false)), Context),
        new("callers", "Who calls, injects or calls over HTTP the given symbol (and its members).",
            Schema(("id", "string", "symbol id or qualified name", true), Offset, Limit, Budget), a => Edges(a, incoming: true)),
        new("callees", "What the given symbol (and its members) calls, injects or calls over HTTP.",
            Schema(("id", "string", "symbol id or qualified name", true), Offset, Limit, Budget), a => Edges(a, incoming: false)),
        new("trace_endpoint", "The request flow of one endpoint down to databases and external systems, and its frontend callers.",
            Schema(("verb", "string", "HTTP method, e.g. POST", true), ("route", "string", "route, e.g. /api/orders/{id}", true)), TraceEndpoint),
        new("module_summary", "One folder: its symbols, layer, endpoints, the modules it depends on and that depend on it, gap and finding counts.",
            Schema(("path", "string", "folder relative to the repository, e.g. src/Orders", true), Budget), ModuleSummary),
        new("impact", "What changed against a git ref (default HEAD: uncommitted changes): symbols, affected doc pages, linked tests, new gaps.",
            Schema(("ref", "string", "git ref, default HEAD", false), Budget), a => host.Impact(Str(a, "ref") ?? "HEAD") ?? throw new ToolError($"not a git repository or unknown ref: {Str(a, "ref") ?? "HEAD"}")),
        new("gaps", "Documentation gaps, critical first, optionally under a path.",
            Schema(("path", "string", "file or folder prefix", false), Offset, Limit, Budget), Gaps),
        new("violations", "Architecture violations and module cycles, optionally under a path.",
            Schema(("path", "string", "file or folder prefix", false), Offset, Limit, Budget), Violations),
        new("check", "The quality gate as JSON: pass/fail and why. With since, only problems introduced since that ref (definition of done).",
            Schema(("since", "string", "git ref, e.g. origin/main", false), Budget), a => host.Check(Str(a, "since")) ?? throw new ToolError($"not a git repository or unknown ref: {Str(a, "since")}")),
        new("rescan", "Scan the code again (in memory; nothing is written). Use when an answer says stale: true.",
            Schema(), _ => { Reload(scan: true); return new { nodes = model.Nodes.Count, edges = model.Edges.Count }; }),
    ];

    void Reload(bool scan)
    {
        if (scan) (model, source, since) = (host.Scan(), "a rescan", DateTime.UtcNow);
        else (model, source, since) = host.Load();
        findings = Analyzer.Analyze(model, host.Config);
        arch = Architecture.Check(model, host.Config);
        graph = new ContextBuilder(host.Root, model);
        drafts = AiProse.Cached(model, Path.Combine(host.Root, "docs", ".docwizz", "ai-cache.json"));
    }

    // Freshness of the model, rescanning first when --auto-rescan is on and it is stale.
    (string? Head, List<string> Reasons) Freshness()
    {
        var head = host.Head();
        var reasons = new List<string>();
        if (model.Commit is { } c && head is not null && !head.StartsWith(c) && !c.StartsWith(head)) reasons.Add($"HEAD is {head}, the model was scanned at {c}");
        if (host.ChangedSince(since) is var n && n > 0) reasons.Add($"{n} source file{(n == 1 ? "" : "s")} changed since the model was {(source == "a rescan" ? "rescanned" : "loaded")}");
        if (reasons.Count > 0 && host.AutoRescan)
        {
            Reload(scan: true);
            return (host.Head(), []);
        }
        return (head, reasons);
    }

    // Reads JSON-RPC messages, one per line, until stdin closes. Only responses go to stdout; anything else to stderr.
    public async Task<int> Run(TextReader input, TextWriter output)
    {
        Reload(scan: false);
        var tools = Tools().ToDictionary(t => t.Name);
        while (await input.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? message;
            try { message = JsonNode.Parse(line); }
            catch (JsonException) { await Send(output, null, error: (-32700, "parse error")); continue; }
            if (message is not JsonObject m || m["method"]?.GetValue<string>() is not { } method) continue;   // a response: we send no requests
            var id = m["id"];
            if (id is null) continue;   // a notification (notifications/initialized, cancelled): nothing to answer
            try { await Send(output, id, result: Handle(method, m["params"] as JsonObject ?? [], tools)); }
            catch (ToolError e) { await Send(output, id, error: (-32602, e.Message)); }
            catch (KeyNotFoundException) { await Send(output, id, error: (-32601, $"method not found: {method}")); }
        }
        return 0;
    }

    static async Task Send(TextWriter output, JsonNode? id, JsonNode? result = null, (int Code, string Message)? error = null)
    {
        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone() };
        if (error is { } e) response["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message };
        else response["result"] = result;
        await output.WriteLineAsync(response.ToJsonString(Json));
        await output.FlushAsync();
    }

    JsonNode Handle(string method, JsonObject args, Dictionary<string, Tool> tools)
    {
        switch (method)
        {
            case "initialize":
                var asked = args["protocolVersion"]?.GetValue<string>();
                return new JsonObject
                {
                    ["protocolVersion"] = Supported.Contains(asked) ? asked : ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "docwizz", ["version"] = AppVersion.Number },
                    ["instructions"] = "Read-only facts about this repository's code from static analysis. Start with find_symbol or " +
                        "context; every answer's `model` names the commit and whether the model is stale (then call rescan). Facts are a map: " +
                        "read the code before changing it. Origins: detected > inferred; ai-drafted is a draft, not a fact.",
                };
            case "ping":
                return new JsonObject();
            case "tools/list":
                return new JsonObject { ["tools"] = new JsonArray([.. tools.Values.Select(t => (JsonNode?)new JsonObject
                    { ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.Schema.DeepClone() })]) };
            case "tools/call":
                var name = args["name"]?.GetValue<string>() ?? throw new ToolError("tools/call needs a name");
                if (!tools.TryGetValue(name, out var tool)) throw new ToolError($"unknown tool: {name}");
                return Call(tool, args["arguments"] as JsonObject ?? []);
            default:
                throw new KeyNotFoundException(method);
        }
    }

    // A tool's answer, with the model's commit and freshness; a failure is a tool error the agent can read, not a protocol error.
    JsonNode Call(Tool tool, JsonObject args)
    {
        JsonObject body;
        bool failed;
        try
        {
            var (head, stale) = Freshness();
            var result = JsonSerializer.SerializeToNode(tool.Run(args), Json) ?? new JsonObject();
            // Freshness under its own key: results have fields of their own (impact's `stale` lists docs, not the model).
            var freshness = new JsonObject { ["commit"] = model.Commit, ["head"] = head, ["stale"] = stale.Count > 0 };
            if (stale.Count > 0) freshness["staleBecause"] = new JsonArray([.. stale.Select(s => (JsonNode?)s)]);
            freshness["source"] = source;
            body = new JsonObject { ["model"] = freshness };
            if (result is JsonObject o) foreach (var (k, v) in o.ToList()) { o.Remove(k); body[k] = v; }
            else body["result"] = result;
            Fit(body, Int(args, "budget", DefaultBudget, 100, 1_000_000) * AgentContext.CharsPerToken);
            failed = false;
        }
        catch (ToolError e)
        {
            body = new JsonObject { ["error"] = e.Message };
            failed = true;
        }
        var text = body.ToJsonString(Json);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["structuredContent"] = body.DeepClone(),
            ["isError"] = failed,
        };
    }

    // Keeps an answer within the budget: drops elements from the end of the largest list until it fits, and says how many
    // under `truncated` (by the list's path). Paged tools fit already; this bounds impact and check on large changes.
    static void Fit(JsonObject body, int budget)
    {
        var dropped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        static IEnumerable<(string Path, JsonArray Array)> Arrays(JsonNode? node, string path) => node switch
        {
            JsonArray a => a.SelectMany((x, i) => Arrays(x, $"{path}[{i}]")).Prepend((path, a)),
            JsonObject o => o.SelectMany(kv => Arrays(kv.Value, path.Length == 0 ? kv.Key : $"{path}.{kv.Key}")),
            _ => [],
        };
        for (var length = body.ToJsonString(Json).Length; length > budget; length = body.ToJsonString(Json).Length)
        {
            if (Arrays(body, "").Where(x => x.Array.Count > 0).OrderByDescending(x => x.Array.ToJsonString(Json).Length).ThenBy(x => x.Path, StringComparer.Ordinal)
                .FirstOrDefault() is not { Array: { } largest } target) break;
            // Drop as many elements as the overshoot needs at the list's average element size, at least one.
            var cut = Math.Clamp((int)Math.Ceiling((length - budget) / Math.Max(1.0, largest.ToJsonString(Json).Length / (double)largest.Count)), 1, largest.Count);
            for (var i = 0; i < cut; i++) largest.RemoveAt(largest.Count - 1);
            dropped[target.Path] = dropped.GetValueOrDefault(target.Path) + cut;
        }
        if (dropped.Count > 0) body["truncated"] = new JsonObject(dropped.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
    }

    static string? Str(JsonObject a, string name) => a[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
    static string Required(JsonObject a, string name) => Str(a, name) ?? throw new ToolError($"missing argument: {name}");

    // A whole-number argument within [min, max], `fallback` when absent; anything else is a tool error naming the range.
    static int Int(JsonObject a, string name, int fallback, int min, int max)
    {
        if (a[name] is null) return fallback;
        if (a[name] is JsonValue v && (v.TryGetValue<int>(out var i) || (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && (i = (int)d) == d)) && i >= min && i <= max) return i;
        throw new ToolError($"{name} must be a whole number from {min} to {max}");
    }

    // A page of `items` that fits the budget: what was left out is counted, with the offset to continue from.
    static object Page<T>(JsonObject a, IEnumerable<T> all, string what)
    {
        var offset = Int(a, "offset", 0, 0, int.MaxValue);
        var limit = Int(a, "limit", DefaultLimit, 1, 500);
        var budget = Int(a, "budget", DefaultBudget, 100, 1_000_000) * AgentContext.CharsPerToken;
        var list = all.ToList();
        var items = list.Skip(offset).Take(limit).ToList();
        while (items.Count > 0 && JsonSerializer.Serialize(items, Json).Length > budget - 200) items.RemoveAt(items.Count - 1);
        var next = offset + items.Count;
        return new Dictionary<string, object?>
        {
            ["total"] = list.Count, ["offset"] = offset, [what] = items,
            ["nextOffset"] = next < list.Count ? next : null,
        };
    }

    Node Symbol(string query) => AgentContext.Resolve(model, query) switch
    {
        { Target.Primary: { } n } => n,
        { Target: { } t } => throw new ToolError($"{query} is a {t.Kind}, not a symbol; use module_summary or context"),
        var r => throw new ToolError(r.Error + (r.Candidates.Count > 0 ? $"; candidates: {string.Join(", ", r.Candidates)}" : "")),
    };

    object Describe(Node n) => new
    {
        id = n.Id, kind = n.Tags?.Contains("endpoint") == true && n.Kind != "endpoint" ? $"{n.Kind} (endpoint)" : n.Kind,
        name = n.Tags?.Contains("endpoint") == true && n.Tags.Count > 1 ? Generator.EndpointLabel(n) : Generator.Display(n),
        location = CodeModel.Location(n),
    };

    object FindSymbol(JsonObject a)
    {
        var query = Required(a, "query");
        var kind = Str(a, "kind");
        var q = query.Trim();
        static int Rank(string name, string q) => name.Equals(q, StringComparison.OrdinalIgnoreCase) ? 0
            : name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1 : name.Contains(q, StringComparison.OrdinalIgnoreCase) ? 2 : 3;
        var symbols = model.Nodes.Where(n => n.Kind is not ("external" or "config" or "package" or "project"))
            .Where(n => kind is null || n.Kind == kind || (kind == "endpoint" && n.Tags?.Contains("endpoint") == true))
            .Select(n =>
            {
                var label = n.Tags?.Contains("endpoint") == true && n.Tags.Count > 1 ? Generator.EndpointLabel(n) : null;
                var rank = new[] { Rank(n.Name, q), Rank(Generator.Display(n), q), label is null ? 3 : Rank(label, q) }.Min();
                return (Rank: rank, Key: Generator.Display(n), Item: Describe(n));
            });
        var modules = kind is null or "module"
            ? model.Nodes.Where(n => n.Kind is not ("external" or "config")).Select(n => Generator.Folder(n.File)).Distinct()
                .Select(f => (Rank: Rank(f, q), Key: f, Item: (object)new { id = f, kind = "module", name = f, location = f }))
            : [];
        return Page(a, symbols.Concat(modules).Where(x => x.Rank < 3).OrderBy(x => x.Rank).ThenBy(x => x.Key.Length).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Item), "matches");
    }

    object Context(JsonObject a)
    {
        var resolved = AgentContext.Resolve(model, Required(a, "target"));
        if (resolved.Target is not { } t)
            throw new ToolError(resolved.Error + (resolved.Candidates.Count > 0 ? $"; candidates: {string.Join(", ", resolved.Candidates)}" : ""));
        var includeAi = a["include_ai"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        var blocks = AgentContext.Blocks(graph, host.Config, t, Int(a, "hops", AgentContext.DefaultHops, 1, AgentContext.MaxHops), includeAi, findings, arch, drafts);
        var package = new AgentContext.Package(Path.GetFileName(Path.GetFullPath(host.Root).TrimEnd('/', '\\')), model.Commit, new(source, null, 0), t,
            Int(a, "budget", AgentContext.DefaultBudget, 100, 1_000_000), includeAi, includeAi ? 0 : t.Scope.Count(n => drafts.ContainsKey(n.Id)), blocks, []);
        // Freshness is in the envelope; the package's own header fields would repeat it.
        var json = JsonNode.Parse(AgentContext.Render(package, json: true))!.AsObject();
        foreach (var k in new[] { "commit", "head", "model", "stale", "warnings" }) json.Remove(k);
        return json;
    }

    static readonly string[] CallKinds = ["calls", "injects", "http"];

    object Edges(JsonObject a, bool incoming)
    {
        var target = Symbol(Required(a, "id"));
        var scope = AgentContext.Resolve(model, target.Id).Target!.Scope.Select(n => n.Id).ToHashSet();
        var edges = scope.SelectMany(id => incoming ? graph.In(id, CallKinds).Select(e => (e.Kind, Other: e.From, Self: id)) : graph.Out(id, CallKinds).Select(e => (e.Kind, Other: e.To, Self: id)))
            .Where(x => !scope.Contains(x.Other)).Distinct()
            .OrderBy(x => x.Other, StringComparer.Ordinal).ThenBy(x => x.Kind, StringComparer.Ordinal)
            .Select(x => new
            {
                edge = x.Kind, id = x.Other, name = graph.Name(x.Other),
                location = graph.Nodes.TryGetValue(x.Other, out var n) ? CodeModel.Location(n) : null,
                via = x.Self == target.Id ? null : graph.Name(x.Self),
            });
        return Page(a, edges, incoming ? "callers" : "callees");
    }

    object TraceEndpoint(JsonObject a)
    {
        var label = $"{Required(a, "verb")} {Required(a, "route")}";
        var endpoint = AgentContext.Resolve(model, label) is { Target: { Kind: "endpoint", Primary: { } n } } ? n
            : throw new ToolError($"no endpoint {label}; find_symbol with kind endpoint lists them");
        var flow = Generator.FlowChains(model, host.Config, [endpoint.Id]).FirstOrDefault(f => f.Entry.Id == endpoint.Id);
        return new
        {
            endpoint = Describe(endpoint),
            flow = flow.Chain,
            externalSystems = (flow.Externals ?? []).Select(x => new { name = x.Name, category = Externals.Category(x), certainty = Externals.Certainty(x) }),
            calledBy = graph.In(endpoint.Id, "http").Select(e => e.From).Distinct().Order(StringComparer.Ordinal)
                .Select(id => new { id, name = graph.Name(id), location = graph.Nodes.TryGetValue(id, out var c) ? CodeModel.Location(c) : null }),
            note = "reached means the code can get there, not that every request does",
        };
    }

    object ModuleSummary(JsonObject a)
    {
        var path = Required(a, "path").Replace('\\', '/').Trim('/');
        var members = model.Nodes.Where(n => n.Kind is not ("external" or "config") && (Generator.Folder(n.File) == path || Generator.Folder(n.File).StartsWith(path + "/"))).ToList();
        if (members.Count == 0) throw new ToolError($"no code under {path}; find_symbol with kind module lists the folders");
        var ids = members.Select(n => n.Id).ToHashSet();
        var parent = model.Edges.Where(e => e.Kind == "contains").GroupBy(e => e.To).ToDictionary(g => g.Key, g => g.First().From);
        string? Module(string id) => graph.Nodes.TryGetValue(id, out var n) && n.Kind is not ("external" or "config") ? Generator.Folder(n.File) : null;
        var deps = model.Edges.Where(e => CodeModel.DependencyKinds.Contains(e.Kind)).ToList();
        var layers = members.Select(n => host.Config.Architecture.Layers.FirstOrDefault(kv => kv.Value.Any(g => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(g, n.File))).Key)
            .OfType<string>().Distinct().Order(StringComparer.Ordinal);
        var symbols = members.Where(n => !parent.TryGetValue(n.Id, out var p) || !ids.Contains(p) || graph.Nodes[p].Kind == "module")
            .Where(n => n.Kind is not ("property" or "event" or "constructor"))
            .OrderBy(n => n.File, StringComparer.Ordinal).ThenBy(n => n.Line)
            .Select(n => new { id = n.Id, kind = n.Kind, location = CodeModel.Location(n), summary = ContextBuilder.WrittenSummary(n) });
        var budget = Int(a, "budget", DefaultBudget, 100, 1_000_000) * AgentContext.CharsPerToken;
        var shown = symbols.ToList();
        object Body() => new
        {
            module = path, layers, files = members.Select(n => n.File).Distinct().Count(),
            endpoints = members.Where(n => n.Tags?.Contains("endpoint") == true && n.Tags.Count > 1).Select(Generator.EndpointLabel).Order(StringComparer.Ordinal),
            dependsOn = deps.Where(e => ids.Contains(e.From) && !ids.Contains(e.To)).Select(e => Module(e.To)).OfType<string>().Distinct().Order(StringComparer.Ordinal),
            usedBy = deps.Where(e => ids.Contains(e.To) && !ids.Contains(e.From)).Select(e => Module(e.From)).OfType<string>().Distinct().Order(StringComparer.Ordinal),
            externalSystems = model.Edges.Where(e => e.Kind == "connects" && ids.Contains(e.From)).Select(e => graph.Nodes.GetValueOrDefault(e.To)?.Name).OfType<string>().Distinct().Order(StringComparer.Ordinal),
            gaps = findings.Count(f => ids.Contains(f.Node.Id) && f.Status != Status.Documented),
            criticalGaps = findings.Count(f => ids.Contains(f.Node.Id) && Analyzer.IsCritical(f)),
            violations = arch.Violations.Count(v => members.Any(n => n.File == v.FromFile)),
            symbols = shown,
            symbolsLeftOut = symbols.Count() - shown.Count,
        };
        while (shown.Count > 0 && JsonSerializer.Serialize(Body(), Json).Length > budget - 200) shown.RemoveAt(shown.Count - 1);
        return Body();
    }

    static bool Under(string file, string? path) => path is null || file == path.Trim('/') || file.StartsWith(path.Trim('/') + "/");

    object Gaps(JsonObject a)
    {
        var path = Str(a, "path")?.Replace('\\', '/');
        return Page(a, findings.Where(f => f.Status != Status.Documented && Under(f.Node.File, path))
            .OrderByDescending(Analyzer.IsCritical).ThenByDescending(f => f.Level).ThenBy(f => f.Node.Id, StringComparer.Ordinal)
            .Select(f => new { id = f.Node.Id, location = CodeModel.Location(f.Node), level = f.Level, critical = Analyzer.IsCritical(f), missing = f.Missing, reasons = f.Reasons }), "gaps");
    }

    object Violations(JsonObject a)
    {
        var path = Str(a, "path")?.Replace('\\', '/');
        var cycles = arch.Cycles.Where(c => path is null || c.Any(m => Under(m, path))).Select(c => new { rule = "ARCH-003", cycle = c });
        var page = (Dictionary<string, object?>)Page(a, arch.Violations.Where(v => Under(v.FromFile, path) || Under(v.To, path))
            .OrderBy(v => v.Rule, StringComparer.Ordinal).ThenBy(v => v.FromFile, StringComparer.Ordinal).ThenBy(v => v.Example, StringComparer.Ordinal), "violations");
        page["cycles"] = cycles;
        return page;
    }
}
