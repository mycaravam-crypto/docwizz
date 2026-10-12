// Security rules, opt-in (`security: { enabled: true }`): code facts a security review should look at. Each finding
// keeps what was detected in the code (Example) apart from the risk it may carry (Risk, an inference), says whether the
// detection itself is a fact or an inference (Basis), and names the concern: confidentiality, integrity or availability.
// Findings support a review. They are not an ISO/IEC 27001 assessment, and no rule claims compliance with anything.
//
// Authentication and authorization come from endpoint metadata: ASP.NET [Authorize]/[AllowAnonymous] (controller and
// action), RequireAuthorization()/AllowAnonymous() on minimal APIs and their MapGroup; Spring @PreAuthorize/@Secured/
// @RolesAllowed/@PermitAll. Policies configured elsewhere (a fallback policy, a security filter chain) are invisible.
class SecurityConfig
{
    public bool Enabled { get; set; }
    // rule → high/medium/low, overriding the defaults.
    public Dictionary<string, string> Severity { get; set; } = [];
    // External system categories SEC-005 treats as sensitive.
    public List<string> Sensitive { get; set; } = ["database", "storage", "identity", "email", "messaging", "cache"];
}

static class Security
{
    public record Rule(string Id, string Title, string Concern, Level Severity, Origin Basis, string Risk);

    public static readonly Rule[] Rules =
    [
        new("SEC-001", "Endpoint without authentication metadata", "confidentiality", Level.Medium, Origin.Fact,
            "Callable without signing in, unless a fallback policy or filter outside the endpoint requires it."),
        new("SEC-002", "Mutating endpoint without authorization", "integrity", Level.High, Origin.Fact,
            "Whoever can reach it can change state, unless a policy outside the endpoint stops them."),
        new("SEC-003", "API layer accesses persistence directly", "integrity", Level.Medium, Origin.Fact,
            "Skips the checks and invariants the application layer enforces; authorization has to be repeated at every such access."),
        new("SEC-004", "Domain coupled to a security or web framework", "integrity", Level.Medium, Origin.Fact,
            "Security decisions spread into the domain, where they are hard to review and easy to bypass from another entry point."),
        new("SEC-005", "Unprotected endpoint reaches a sensitive system", "confidentiality", Level.High, Origin.Inferred,
            "Data in that system may be read or changed through the endpoint without authentication."),
    ];

    static readonly string[] Mutating = ["POST", "PUT", "PATCH", "DELETE"];
    // Namespace prefixes of security and web frameworks (SEC-004).
    static readonly string[] Frameworks = ["Microsoft.AspNetCore", "Microsoft.Identity", "Microsoft.IdentityModel", "Microsoft.Owin.Security",
        "org.springframework.security", "org.springframework.web", "jakarta.servlet", "javax.servlet", "jakarta.ws.rs", "javax.ws.rs"];

    public static string Auth(Node endpoint) =>
        endpoint.Tags?.Contains("anonymous") == true ? "anonymous" : endpoint.Tags?.Contains("authorize") == true ? "authorize" : "none";

    public static List<Violation> Check(CodeModel model, Config config)
    {
        var sec = config.Security;
        var arch = config.Architecture;
        var nodes = model.Nodes.ToDictionary(n => n.Id);
        var parent = model.Edges.Where(e => e.Kind == "contains").GroupBy(e => e.To).ToDictionary(g => g.Key, g => g.First().From);
        string Top(string id) => parent.TryGetValue(id, out var p) && nodes.ContainsKey(p) ? Top(p) : id;
        string? LayerOf(string file) => arch.Layers.FirstOrDefault(kv => kv.Value.Any(g => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(g, file))).Key;
        var found = new List<Violation>();
        void Add(string rule, Node at, string toLayer, string to, string detected) => found.Add(new(rule, LayerOf(at.File) ?? "—", toLayer, at.File, to, detected));

        var endpoints = model.Nodes.Where(n => n.Tags?.Contains("endpoint") == true).OrderBy(n => n.File).ThenBy(n => n.Line).ToList();
        foreach (var e in endpoints)
        {
            var verb = e.Tags!.ElementAtOrDefault(1) ?? "";
            var label = $"{verb} /{e.Route?.TrimStart('/')}";
            var auth = Auth(e);
            if (Mutating.Contains(verb) && auth != "authorize")
                Add("SEC-002", e, "endpoint", label, auth == "anonymous"
                    ? $"{label} changes state and is explicitly anonymous" : $"{label} changes state and declares no authorization");
            else if (auth == "none")
                Add("SEC-001", e, "endpoint", label, $"{label} declares neither authorization nor anonymous access");
        }

        // SEC-003: code in the api layer, a controller or an endpoint that injects, accesses, calls or creates a DbContext.
        foreach (var edge in model.Edges.Where(x => x.Kind is "injects" or "accesses" or "calls" or "creates"))
        {
            if (!nodes.TryGetValue(edge.From, out var from) || !nodes.TryGetValue(edge.To, out var to)) continue;
            var owner = nodes[Top(from.Id)];
            if (nodes[Top(to.Id)].Tags?.Contains("dbcontext") != true || owner.Tags?.Contains("dbcontext") == true) continue;
            if (LayerOf(from.File) != "api" && owner.Tags?.Contains("controller") != true && from.Tags?.Contains("endpoint") != true) continue;
            // Per minimal-API endpoint or per type (a controller's actions count as the controller), so new ones show up in `check --since`.
            var unit = from.Kind == "endpoint" ? from : owner;
            Add("SEC-003", from, LayerOf(to.File) ?? "persistence", $"{Short(unit)} → {nodes[Top(to.Id)].Name}",
                $"{Short(from)} {edge.Kind} {nodes[Top(to.Id)].Name}");
        }

        // SEC-004: domain types importing security or web frameworks.
        foreach (var edge in model.Edges.Where(x => x.Kind == "uses-namespace"))
            if (nodes.TryGetValue(edge.From, out var from) && LayerOf(from.File) == "domain"
                && Frameworks.Any(f => edge.To[3..] == f || edge.To[3..].StartsWith(f + ".")))
                Add("SEC-004", from, "package", edge.To[3..], $"{Short(from)} uses {edge.To[3..]}");

        // SEC-005: an endpoint without required authorization whose flow can reach a sensitive external system.
        var g = new Generator("", "", model, [], null!, config, []);
        // One finding per endpoint and system (`to` names both), so a second endpoint in the same file is a new finding.
        foreach (var e in endpoints.Where(e => Auth(e) != "authorize"))
            foreach (var ext in g.ReachedFrom(e.Id, n => n.Kind == "external" && sec.Sensitive.Contains(Externals.Category(n))))
            {
                var label = $"{e.Tags![1]} /{e.Route?.TrimStart('/')}";
                Add("SEC-005", e, Externals.Category(ext), $"{label} → {ext.Name}",
                    $"{label} ({(Auth(e) == "anonymous" ? "anonymous" : "no authorization declared")}) can reach {ext.Name} ({Externals.Certainty(ext)})");
            }

        var rules = Rules.ToDictionary(r => r.Id);
        return found.DistinctBy(v => (v.Rule, v.FromFile, v.To)).Select(v => v with
        {
            Severity = sec.Severity.TryGetValue(v.Rule, out var s) ? Architecture.ParseSeverity(s) : rules[v.Rule].Severity,
            Basis = rules[v.Rule].Basis, Concern = rules[v.Rule].Concern, Risk = rules[v.Rule].Risk,
        }).ToList();
    }

    public static void Validate(SecurityConfig config)
    {
        foreach (var (rule, severity) in config.Severity)
        {
            if (!Rules.Any(r => r.Id == rule)) throw new ArgumentException($"security.severity: unknown rule '{rule}' ({string.Join(", ", Rules.Select(r => r.Id))})");
            Architecture.ParseSeverity(severity);
        }
    }

    static string Short(Node n) => n.Tags?.Contains("endpoint") == true && n.Kind == "endpoint" ? n.Name : n.Id[(n.Id.IndexOf(':') + 1)..].Split('(')[0];
}
