using System.IO.Enumeration;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

class Config
{
    public Dictionary<string, Pattern> Patterns { get; set; } = [];
    public CheckConfig Check { get; set; } = new();
    public List<string> Exclude { get; set; } = [];
    public bool CommentDocs { get; set; }
    public List<string> Tests { get; set; } = [];
    public ArchitectureConfig Architecture { get; set; } = new();
    public SecurityConfig Security { get; set; } = new();
    public string Profile { get; set; } = "default";

    // Human-authored architecture sections the profile expects under docs/architecture/<name>.md.
    public List<string> ArchitectureSections { get; set; } = [];

    public const string Default = """
        # Documentation profile: default, software, aspnet, vue, api, architecture, technical-publication, iso-42010, iso-15289.
        # `--profile` overrides it. Add `patterns:` (same shape as in Profiles.cs) to replace the profile's patterns.
        profile: default
        check:
          min_coverage: 80
          # min_quality: 90     # fail when fewer % of items with written docs are free of quality flags
          max_critical: 0
          max_violations: 0
          max_cycles: 0
          fail_on: low          # minimum violation severity that fails check: low, medium, high
          # max_complexity: 20  # fail when a symbol's cyclomatic complexity exceeds this
          # require_tests: high # check --since: fail when the change adds a symbol at this level or above with no linked test
        architecture:
          layers:               # path globs, first match wins; unmatched files have no layer
            ui: ["*.vue", "*.tsx", "*.jsx", "*.component.ts"]
            state: ["*/stores/*.ts", "*/composables/*.ts"]
            client: ["*/api/*.ts", "*/services/*.ts", "*.service.ts"]
            api: ["*/Api/*.cs", "*.Api/*.cs", "*/Controllers/*.cs", "*/Endpoints/*.cs", "*/controller/*.java", "*/web/*.java"]
            application: ["*/Application/*.cs", "*.Application/*.cs", "*/service/*.java"]
            domain: ["*/Domain/*.cs", "*.Domain/*.cs", "*.Core/*.cs", "*/domain/*.java", "*/model/*.java", "*/entity/*.java"]
            infrastructure: ["*/Infrastructure/*.cs", "*.Infrastructure/*.cs", "*/Persistence/*.cs"]   # Spring Data repositories are ports: no layer by default
          allow:                # `http` = calling HTTP directly
            ui: [state, client]
            state: [client]
            client: [http]
            api: [application, domain, infrastructure]
            application: [domain]
            domain: []
            infrastructure: [application, domain]
          severity: {}          # rule → high/medium/low; defaults ARCH-001 high, ARCH-002 medium, ARCH-004 low
          # rules:              # your own dependency rules (README: Custom rules)
          #   - { id: ARCH-DOMAIN-001, severity: high, from: { layer: domain }, forbid: { package: [Microsoft.EntityFrameworkCore] } }
        # Security rules SEC-001…005: findings for a security review, from code facts (README: Security rules). Off by default.
        security:
          enabled: false
        # Test code (path globs): scanned only to link tests to the code they exercise; never analyzed or documented.
        tests: ["tests/*", "test/*", "*.Tests/*", "*.Test/*", "*/__tests__/*", "*.test.ts", "*.spec.ts", "*/e2e/*", "*/src/test/*"]
        # Path globs (relative, `/`-separated) left out of the model entirely.
        exclude: []
        # Count a plain // comment block directly above a C# member as its summary (for code that doesn't use /// XML docs).
        comment_docs: false
        """;

    static readonly IDeserializer Yaml = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build();

    // docwizz.yaml in the scanned dir (or cwd), else the default. The profile (CLI > file > default) supplies the
    // patterns unless the file defines its own and no profile was chosen on the command line.
    public static Config Load(string dir, string? profile = null)
    {
        var file = new[] { Path.Combine(dir, "docwizz.yaml"), "docwizz.yaml" }.FirstOrDefault(File.Exists);
        return Parse(file is null ? Default : File.ReadAllText(file), dir, profile, file);
    }

    // A config from YAML text (`file` names it in errors), validated and with the profile's patterns applied.
    public static Config Parse(string yaml, string dir, string? profile = null, string? file = null)
    {
        Config config;
        try { config = Yaml.Deserialize<Config>(yaml) ?? new Config(); }
        catch (YamlDotNet.Core.YamlException e) { throw new ArgumentException($"{file ?? "docwizz.yaml"}: {e.Message}{(e.InnerException is { } inner ? $" {inner.Message}" : "")}"); }
        config.Profile = profile ?? config.Profile;
        var builtIn = Yaml.Deserialize<Config>(Profiles.Yaml(config.Profile, dir));
        if (profile is not null || config.Patterns.Count == 0) config.Patterns = builtIn.Patterns;
        if (config.ArchitectureSections.Count == 0) config.ArchitectureSections = builtIn.ArchitectureSections;
        // Fail fast on bad severities rather than mid-check.
        foreach (var s in config.Architecture.Severity.Values.Append(config.Check.FailOn)) global::Architecture.ParseSeverity(s);
        global::Architecture.Validate(config.Architecture);
        if (config.Check.RequireTests is { } rt && rt.ToLowerInvariant() is not ("high" or "medium"))
            throw new ArgumentException($"check.require_tests: '{rt}' (high or medium; lower levels need no docs and aren't tracked)");
        global::Security.Validate(config.Security);
        return config;
    }
}

class Pattern
{
    public Match Match { get; set; } = new();
    public string? Level { get; set; }
    public List<string> Sections { get; set; } = ["summary"];
}

// Globs (`*`, `?`) against the node's name, containing type name, kind, tag, visibility.
class Match
{
    public string? Kind { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Tag { get; set; }
    public string? Visibility { get; set; }
}

class CheckConfig
{
    public double MinCoverage { get; set; } = 80;
    public int MaxCritical { get; set; }
    public int MaxViolations { get; set; }
    public int MaxCycles { get; set; }
    // Minimum violation severity that counts against max_violations (lower ones are reported only).
    public string FailOn { get; set; } = "low";
    // Fail when any symbol's cyclomatic complexity exceeds this (unset = no limit).
    public int? MaxComplexity { get; set; }
    // Minimum doc quality % (share of documented items with no quality flags); unset = no gate.
    public double? MinQuality { get; set; }
    // `check --since`: fail when the change adds a symbol at this documentation level or above (high, medium) that no
    // test code is linked to. Unset = report only.
    public string? RequireTests { get; set; }
}

enum Level { None, Low, Medium, High }
enum Status { Documented, Partial, Undocumented }

// Where a documentation section comes from. Only Written, Fact and Inferred count as documented;
// Ai drafts are shown (marked) but never close a gap.
enum Origin { Written, Fact, Inferred, Ai }

// From: for AI drafts, the symbols whose facts the draft was generated from; Sentences: each sentence with its own.
record Section(Origin Origin, string Text, List<string>? From = null, List<AiProse.Sentence>? Sentences = null);

// A problem with documentation that exists: it contradicts the code (Fact) or looks useless (Inferred, a heuristic).
record QualityFlag(string Rule, Origin Origin, string Detail);

// One entry of the documentation model: what a symbol must document under the selected profile, what it has
// (and where each part comes from), and the code it was derived from (Sources) for traceability.
record DocumentationItem(Node Node, string Profile, string Pattern, Level Level, List<string> Required,
    Dictionary<string, Section> Sections, List<string> Missing, Status Status, List<string> Reasons,
    List<string> Sources, bool Tested = false, List<QualityFlag>? Flags = null);

static partial class Analyzer
{
    static readonly string[] Analyzed = ["class", "record", "struct", "interface", "type", "enum", "method", "endpoint",
        "component", "function", "store", "delegate", "procedure", "sql-function", "sql-view", "trigger"];
    static readonly string[] TypeKinds = ["class", "record", "struct", "interface", "type", "enum", "delegate"];

    // Profile section names → the canonical (XML doc tag) name.
    static readonly Dictionary<string, string> Aliases = new()
    {
        ["parameters"] = "param", ["props"] = "param", ["exceptions"] = "exception", ["examples"] = "example",
        ["side-effects"] = "side_effects", ["response"] = "output", ["request"] = "input", ["emits"] = "events",
    };

    public static string Canonical(string section) => Aliases.GetValueOrDefault(section.ToLowerInvariant(), section.ToLowerInvariant());

    public static List<DocumentationItem> Analyze(CodeModel model, Config config)
    {
        var nodes = model.Nodes.ToDictionary(n => n.Id);
        var parent = model.Edges.Where(e => e.Kind == "contains").ToDictionary(e => e.To, e => e.From);
        var implOf = model.Edges.Where(e => e.Kind == "implements").ToLookup(e => e.From, e => e.To); // impl → interface
        var implBy = model.Edges.Where(e => e.Kind == "implements").ToLookup(e => e.To, e => e.From); // interface → impl
        var calls = model.Edges.Where(e => e.Kind is "calls" or "http").ToLookup(e => e.From, e => e.To);
        var http = model.Edges.Where(e => e.Kind == "http").ToLookup(e => e.From);
        var injects = model.Edges.Where(e => e.Kind == "injects").ToLookup(e => e.From, e => e.To);
        var connects = model.Edges.Where(e => e.Kind == "connects").ToLookup(e => e.From, e => e.To);
        var inbound = model.Edges.Where(e => e.Kind is "calls" or "injects" or "renders" or "http").ToLookup(e => e.To, e => e.From);
        var outbound = model.Edges.Where(e => CodeModel.DependencyKinds.Contains(e.Kind)).ToLookup(e => e.From);
        var children = model.Edges.Where(e => e.Kind == "contains").ToLookup(e => e.From, e => e.To);
        // A test through the interface, an implementation, or any member counts.
        var links = new TestLinks(model);
        bool Tested(string id) => links.Any(id);
        string Top(string id) => parent.TryGetValue(id, out var p) ? Top(p) : id;

        var effectsCache = new Dictionary<string, HashSet<string>>();

        // INFERENCE, not fact: side effects by naming heuristics (DbContext injection, Publish*/Send* calls, raised events).
        // ponytail: swap for symbol-based rules if noisy.
        HashSet<string> Effects(string id)
        {
            if (effectsCache.TryGetValue(id, out var cached)) return cached;
            var found = effectsCache[id] = [];
            if (!nodes.TryGetValue(id, out var n)) return found;
            var injected = parent.TryGetValue(id, out var owner) ? injects[owner].Concat(injects[id]) : injects[id];
            if (injected.Any(t => nodes.GetValueOrDefault(t)?.Tags?.Contains("dbcontext") == true))
                found.Add("db");
            if (n.Name.StartsWith("Publish") || n.Name.StartsWith("Send") || outbound[id].Any(e => e.Kind == "publishes")) found.Add("event");
            if (http[id].Any()) found.Add("http");
            // Talks to an external system itself (or through its type): database → db, http-api → http, else the category.
            foreach (var ext in connects[id].Concat(parent.TryGetValue(id, out var o) ? connects[o] : []).Select(nodes.GetValueOrDefault).OfType<Node>())
                found.Add(Externals.Category(ext) switch { "database" => "db", "http-api" => "http", var c => c });
            foreach (var next in calls[id].Concat(implBy[id]))
                found.UnionWith(Effects(next));
            return found;
        }

        // FACT: what a symbol (or, for a type, any of its members) depends on, by top-level symbol.
        List<string> Dependencies(Node n) => outbound[n.Id].Concat(children[n.Id].SelectMany(c => outbound[c]))
            .Where(e => e.Kind != "contains").Select(e => e.To.StartsWith("http:") ? e.To : Top(e.To))
            .Where(t => t != Top(n.Id) && (nodes.ContainsKey(t) || t.StartsWith("http:"))).Distinct().ToList();

        List<Node> Endpoints(Node n) => n.Tags?.Contains("endpoint") == true ? [n]
            : children[n.Id].Select(nodes.GetValueOrDefault).OfType<Node>().Where(c => c.Tags?.Contains("endpoint") == true).ToList();

        bool Applies(string section, Node n) => section switch
        {
            "param" or "input" => n.Params > 0,
            "returns" or "output" => n.Returns is not null,
            "exception" => n.Throws is not null,
            "endpoint" or "authorization" => Endpoints(n).Count > 0,
            "state" => n.State is { Count: > 0 },
            "events" => n.Events is { Count: > 0 } || outbound[n.Id].Any(e => e.Kind == "publishes"),
            _ => true,
        };

        string Name(string id) => nodes.TryGetValue(id, out var x) ? x.Name : id.Replace("http:", "HTTP ");

        // Sections DocWizz can state from the model itself, with the symbols they were derived from.
        (Section, IEnumerable<string>)? Derive(string section, Node n) => section switch
        {
            "dependencies" when Dependencies(n) is var d => (new(Origin.Fact, d.Count > 0 ? string.Join(", ", d.Select(Name)) : "none"), d),
            "side_effects" when Effects(n.Id) is var fx => (new(Origin.Inferred, fx.Count > 0 ? string.Join(", ", fx.Order()) : "none detected"), []),
            "endpoint" => (new(Origin.Fact, string.Join(", ", Endpoints(n).Select(e => $"{e.Tags![1]} /{e.Route?.TrimStart('/')}"))), []),
            "authorization" => (new(Origin.Fact, string.Join(", ", Endpoints(n).Select(e =>
                e.Tags!.Contains("anonymous") ? "anonymous" : e.Tags.Contains("authorize") ? "required" : "none declared").Distinct())), []),
            "input" => (new(Origin.Fact, string.Join(", ", n.Parameters ?? [])), []),
            "output" => (new(Origin.Fact, n.Returns!), []),
            "state" => (new(Origin.Fact, string.Join(", ", n.State!)), []),
            "events" => (new(Origin.Fact, string.Join(", ", outbound[n.Id].Where(e => e.Kind == "publishes").Select(e => Name(e.To))
                .Concat(n.Events ?? []).Distinct())), outbound[n.Id].Where(e => e.Kind == "publishes").Select(e => e.To)),
            _ => null,
        };

        var items = new List<DocumentationItem>();
        foreach (var node in model.Nodes.Where(n => Analyzed.Contains(n.Kind)))
        {
            var typeName = parent.TryGetValue(node.Id, out var p) ? nodes[p].Name : null;
            var (patternName, pattern) = config.Patterns.FirstOrDefault(kv => Matches(kv.Value.Match, node, typeName));
            pattern ??= new Pattern();

            var reasons = new List<string>();
            var score = 0;
            if (node.Visibility is "public" or "protected") { score += 2; reasons.Add(node.Visibility); }
            if (node.Complexity >= 15) { score += 3; reasons.Add($"complexity {node.Complexity}"); }
            else if (node.Complexity >= 8) { score += 2; reasons.Add($"complexity {node.Complexity}"); }
            else if (node.Complexity >= 4) { score += 1; reasons.Add($"complexity {node.Complexity}"); }
            if (node.Params >= 4) { score += 1; reasons.Add($"{node.Params} {(node.Kind == "component" ? "props" : "params")}"); }
            var fanIn = inbound[node.Id].Concat(implOf[node.Id].SelectMany(i => inbound[i])).Distinct().Count();
            if (fanIn >= 3) { score += 1; reasons.Add($"{fanIn} callers"); }
            if (!TypeKinds.Contains(node.Kind) && Effects(node.Id) is { Count: > 0 } fx)
            {
                score += 1;
                reasons.Add("side effects: " + string.Join(", ", fx.Order()));
            }

            // Tuned on a real repo: public + one weak signal (e.g. touches the DB) stays Low.
            var level = score >= 6 ? Level.High : score >= 4 ? Level.Medium : score >= 1 ? Level.Low : Level.None;
            if (Enum.TryParse<Level>(pattern.Level, true, out var min) && min > level)
            {
                level = min;
                reasons.Add($"pattern {patternName}");
            }
            if (level < Level.Medium) continue;

            // An interface member's docs cover its implementations.
            var sources = new List<string> { node.Id };
            var doc = node.Doc;
            if (doc is null && implOf[node.Id].FirstOrDefault(i => nodes.GetValueOrDefault(i)?.Doc is not null) is { } iface)
            {
                doc = nodes[iface].Doc;
                sources.Add(iface);
            }
            var xml = ParseDoc(doc);
            var names = ParamNames(node);
            // FACT: a bare <inheritdoc/> with nothing behind it (the scanner records what it can inherit from), or only
            // interfaces in the model that have no docs themselves. It documents nothing.
            var inherit = xml?.Element("inheritdoc") is { } ih && ih.Attribute("cref") is null ? (string?)ih.Attribute("source") : null;
            var emptyInherit = inherit switch
            {
                "none" => "`<inheritdoc/>`, but there is nothing to inherit from",
                "interface" when implOf[node.Id].Any() && !implOf[node.Id].Any(i => nodes.GetValueOrDefault(i)?.Doc is not null)
                    => $"`<inheritdoc/>` from `{Generator.Display(nodes[implOf[node.Id].First()])}`, which has no docs",
                _ => null,
            };
            var required = pattern.Sections.Select(Canonical).Distinct().Where(s => Applies(s, node)).ToList();
            var sections = new Dictionary<string, Section>();
            foreach (var s in required)
            {
                if (Written(xml, s, node.Params ?? 0, names, emptyInherit is null) is { } text) sections[s] = new(Origin.Written, text);
                else if (Derive(s, node) is var (section, from))
                {
                    sections[s] = section;
                    sources.AddRange(from);
                }
            }
            var missing = required.Where(s => !sections.ContainsKey(s)).ToList();
            var status = missing.Count == 0 ? Status.Documented
                : sections.Values.Any(x => x.Origin == Origin.Written) ? Status.Partial : Status.Undocumented;
            var flags = Flags(xml, node, names, typeName);
            if (emptyInherit is not null) flags.Insert(0, new("empty-inheritdoc", Origin.Fact, emptyInherit));
            items.Add(new(node, config.Profile, patternName ?? "", level, required, sections, missing, status, reasons,
                sources.Distinct().ToList(), Tested(node.Id), flags));
        }
        return items;
    }

    static XElement? ParseDoc(string? doc)
    {
        if (doc is null) return null;
        try { return XElement.Parse(doc); }
        catch { return null; }
    }

    // The written text of a section, or null. <inheritdoc/> covers everything, when there is something to inherit.
    static string? Written(XElement? xml, string section, int paramCount, List<string>? names, bool inherits = true)
    {
        if (xml is null) return null;
        if (xml.Element("inheritdoc") is not null && inherits) return "inherited";
        if (section == "param")
        {
            var documented = DocumentedParams(xml);
            // By name when the scanner knows the names, so a doc left over from a renamed parameter doesn't count.
            if (names is not null) return names.All(documented.Contains) ? $"{names.Count} documented" : null;
            return documented.Count >= paramCount ? $"{documented.Count} documented" : null;
        }
        var text = xml.Element(section) is { } e ? Text(e) : null;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    static HashSet<string> DocumentedParams(XElement xml) => xml.Elements("param")
        .Where(e => !string.IsNullOrWhiteSpace(e.Value)).Select(e => (string?)e.Attribute("name") ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Parameter names from "[source] name: Type" (C#, Java), "@name: type" (SQL), "name?: type" (TS props); null when
    // any isn't a plain identifier (a destructured TS parameter), so callers fall back to counting.
    static List<string>? ParamNames(Node n)
    {
        if (n.Parameters is null) return null;
        var names = n.Parameters.Select(p => Regex.Replace(p, @"^\[[^\]]*\]\s*", "").Split(':')[0].Trim().TrimStart('@').TrimEnd('?')).ToList();
        return names.All(x => Regex.IsMatch(x, @"^[\w$]+$")) ? names : null;
    }

    // Written docs that contradict the code (facts), or that look like they add nothing (inferred).
    static List<QualityFlag> Flags(XElement? xml, Node n, List<string>? names, string? typeName)
    {
        var flags = new List<QualityFlag>();
        if (xml is null) return flags;
        if (names is not null)
            foreach (var p in xml.Elements("param").Select(e => (string?)e.Attribute("name")).OfType<string>()
                         .Where(p => !names.Contains(p, StringComparer.OrdinalIgnoreCase)).Distinct())
                flags.Add(new("param-drift", Origin.Fact, $"documents parameter `{p}`, which doesn't exist"));
        // Only where scanners record every return type: C# and Java methods (Task and void → null).
        if (n.Kind == "method" && n.Language is "csharp" or "java" && n.Returns is null
            && xml.Element("returns") is { } r && !string.IsNullOrWhiteSpace(Text(r)))
            flags.Add(new("returns-on-void", Origin.Fact, "documents a return value, but returns nothing"));
        if (xml.Element("summary") is { } s && Text(s) is { Length: > 0 } summary)
        {
            if (Placeholder().IsMatch(summary))
                flags.Add(new("placeholder", Origin.Inferred, $"summary is a placeholder: \"{summary}\""));
            else if (Words(summary).Count < 3)
                flags.Add(new("placeholder", Origin.Inferred, $"summary is too short to explain anything: \"{summary}\""));
            else if (Echoes(summary, [n.Name, typeName ?? "", .. names ?? []]))
                flags.Add(new("echo", Origin.Inferred, $"summary only restates the name: \"{summary}\""));
        }
        return flags;
    }

    // INFERENCE: TODO markers, generator boilerplate, "summary" left in by a template.
    // ponytail: extend the list as real repos show more.
    [GeneratedRegex(@"^\W*(todo|fixme|tbd|xxx|hack)\b|^\W*(summary|description)\W*$|^\W*add (a )?(summary|description)\b|^\W*initializes a new instance of\b|\b(todo|fixme)\b:",
        RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    static List<string> Words(string text) => Regex.Matches(text, @"[\p{L}\p{N}]+").Select(m => m.Value).ToList();

    // Words that carry no meaning of their own in a summary: articles, glue, and the generic verbs every accessor uses.
    static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "this", "that", "these", "its", "it", "of", "for", "to", "from", "by", "with", "in", "on", "at",
        "as", "and", "or", "is", "are", "be", "given", "specified", "provided", "current", "method", "function", "class",
        "property", "value", "object", "instance", "get", "return", "retrieve", "fetch", "set", "handle", "perform", "do",
        "call", "invoke", "execute", "run", "async", "asynchronously", "new",
    };

    // Crude stem: case and a plural/third-person "s", so "Gets materials" meets GetMaterial.
    static string Stem(string w)
    {
        w = w.ToLowerInvariant();
        return w.EndsWith("ies") && w.Length > 4 ? w[..^3] + "y" : w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss") ? w[..^1] : w;
    }

    // INFERENCE: every meaningful word of the summary is already in the name, the type's name or a parameter's.
    static bool Echoes(string summary, IEnumerable<string> known)
    {
        var have = known.SelectMany(k => Regex.Split(k, @"(?<=[a-z0-9])(?=[A-Z])|[^\p{L}\p{N}]+")).Where(w => w.Length > 0).Select(Stem).ToHashSet();
        return Words(summary).Where(w => !Filler.Contains(w) && !Filler.Contains(Stem(w))).Select(Stem).All(have.Contains);
    }

    // A doc element's text with <see cref/>, <paramref name/> etc. rendered as their target (`XElement.Value` drops them).
    public static string Text(XElement e) => System.Text.RegularExpressions.Regex.Replace(string.Concat(e.Nodes().Select(n => n switch
    {
        XText t => t.Value,
        XElement { IsEmpty: true } r when (r.Attribute("cref") ?? r.Attribute("name") ?? r.Attribute("langword")) is { } a
            => $"`{a.Value.Split(':').Last().Split('(')[0].Split('.').Last()}`",
        XElement x => Text(x),
        _ => "",
    })), @"\s+", " ").Trim();

    static bool Matches(Match m, Node n, string? typeName) =>
        Glob(m.Kind, n.Kind) && Glob(m.Name, n.Name) && Glob(m.Type, typeName) && Glob(m.Visibility, n.Visibility)
        && (m.Tag is null || n.Tags?.Any(t => Glob(m.Tag, t)) == true);

    static bool Glob(string? pattern, string? value) =>
        pattern is null || (value is not null && FileSystemName.MatchesSimpleExpression(pattern, value));

    public static double Coverage(IEnumerable<DocumentationItem> fs) =>
        fs.Any() ? 100 * fs.Sum(f => f.Status switch { Status.Documented => 1, Status.Partial => 0.5, _ => 0 }) / fs.Count() : 100;

    // Items with any written documentation (flagged docs count, even an <inheritdoc/> that documents nothing): the
    // ones doc quality is measured on.
    public static List<DocumentationItem> WithWrittenDocs(IEnumerable<DocumentationItem> fs) =>
        fs.Where(f => f.Sections.Values.Any(s => s.Origin == Origin.Written) || f.Flags is { Count: > 0 }).ToList();

    // Doc quality: the share of items with written documentation that carries no quality flag.
    public static double Quality(IEnumerable<DocumentationItem> fs) =>
        WithWrittenDocs(fs) is { Count: > 0 } w ? 100.0 * w.Count(f => f.Flags is not { Count: > 0 }) / w.Count : 100;

    public static void Report(List<DocumentationItem> findings, TextWriter o)
    {
        var cov = Coverage(findings);
        var bar = (int)Math.Round(cov / 5);
        o.WriteLine($"Documentation  {new string('█', bar)}{new string('░', 20 - bar)} {cov:0}%");
        var quality = Quality(findings);
        var qbar = (int)Math.Round(quality / 5);
        o.WriteLine($"Doc quality    {new string('█', qbar)}{new string('░', 20 - qbar)} {quality:0}%  ({WithWrittenDocs(findings).Count} with written docs)");
        o.WriteLine();
        foreach (var g in findings.GroupBy(f => f.Pattern).OrderBy(g => g.Key))
            o.WriteLine($"  {(g.Key == "" ? "(none)" : g.Key),-16} {Coverage(g),4:0}%  ({g.Count()})");

        Section("Critical", findings.Where(IsCritical));
        Section("Warnings", findings.Where(f => f.Level == Level.Medium && f.Status != Status.Documented));

        var flagged = findings.Where(f => f.Flags is { Count: > 0 }).ToList();
        if (flagged.Count > 0)
        {
            o.WriteLine();
            o.WriteLine($"Doc quality ({flagged.Count})");
            o.WriteLine(new string('─', 40));
            foreach (var f in flagged)
            {
                o.WriteLine($"  {f.Node.File}:{f.Node.Line}  {f.Node.Id[(f.Node.Id.IndexOf(':') + 1)..]}");
                foreach (var q in f.Flags!)
                    o.WriteLine($"    {q.Rule}{(q.Origin == Origin.Inferred ? " (inferred)" : "")}: {q.Detail}");
            }
        }

        o.WriteLine();
        o.WriteLine($"{findings.Count(f => f.Status == Status.Documented)} documented, " +
                    $"{findings.Count(f => f.Status == Status.Partial)} partial, " +
                    $"{findings.Count(f => f.Status == Status.Undocumented)} undocumented; " +
                    $"{findings.Count(f => f.Tested)} of {findings.Count} have tests");

        void Section(string title, IEnumerable<DocumentationItem> fs)
        {
            var list = fs.ToList();
            o.WriteLine();
            o.WriteLine($"{title} ({list.Count})");
            o.WriteLine(new string('─', 40));
            foreach (var f in list)
            {
                o.WriteLine($"  {f.Node.File}:{f.Node.Line}  {f.Node.Id[(f.Node.Id.IndexOf(':') + 1)..]}");
                o.WriteLine($"    {f.Status.ToString().ToLowerInvariant()}, missing: {string.Join(", ", f.Missing)}  [{string.Join("; ", f.Reasons)}]");
            }
        }
    }

    public static bool IsCritical(DocumentationItem f) => f.Level == Level.High && f.Status != Status.Documented;
}
