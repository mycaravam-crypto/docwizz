// The code intelligence model: language-neutral facts only. Analyzers (C#, Vue/TS, projects) populate it;
// documentation analysis, architecture checks and generators read it. Nothing interpretive or AI-written lives here.

// Parameters are "name: Type", prefixed with the binding source for endpoints ("[body] request: CreateRequest").
// Returns is the unwrapped result type (Task<ActionResult<T>> → T), null for void/Task. Events are emitted event names.
// State: a component's reactive state (refs, `x (computed)`, `watch x`).
// Tags carry framework concepts and roles (controller, endpoint, service, repository, entity, middleware, store, …).
// Language (csharp, typescript, vue, msbuild, npm) is the source language; ids keep their scanner prefix for stability.
record Node(string Id, string Kind, string Name, string File, int Line,
    string? Visibility = null, string? Doc = null, int? Complexity = null,
    int? Params = null, string? Hash = null, List<string>? Tags = null, string? Route = null, int? EndLine = null,
    List<string>? Parameters = null, string? Returns = null, List<string>? Throws = null, List<string>? Events = null,
    string? Language = null, List<string>? State = null, List<string>? Hooks = null,
    List<string>? Responses = null);

// Label carries the detail a kind alone doesn't (e.g. the event name of a `subscribes` edge).
record Edge(string From, string To, string Kind, string? Label = null);

record CodeModel(string? Commit, List<Node> Nodes, List<Edge> Edges)
{
    // Where a symbol lives: "file:line" or "file:line-endLine" — the evidence behind a documentation statement.
    public static string Location(Node n) => n.EndLine is { } end && end > n.Line ? $"{n.File}:{n.Line}-{end}" : $"{n.File}:{n.Line}";

    // Edge kinds that are a dependency of From on To (contains/registers/references/depends-on/tests are not).
    // `creates`: From instantiates To (`new T(..)`).
    public static readonly string[] DependencyKinds = ["calls", "injects", "implements", "inherits", "imports", "renders",
        "routes-to", "persists", "publishes", "subscribes", "http", "creates"];

    // A symbol declared in several files (partial class, the same table in two scripts) is kept as one node, the first.
    // Give every declaration a hash over all of them, so the survivor changes when any declaration does and doesn't depend
    // on which one comes first. `side` keeps groups apart that must not mix (test vs production code).
    public static IEnumerable<Node> MergeHashes(IEnumerable<Node> nodes, Func<Node, bool>? side = null) =>
        nodes.GroupBy(n => (n.Id, side?.Invoke(n) ?? false)).SelectMany(g => g.Skip(1).Any() && g.Any(n => n.Hash is not null)
            ? g.Select(n => n with { Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                string.Join("|", g.Select(x => x.Hash ?? "").Order(StringComparer.Ordinal)))))[..12].ToLowerInvariant() })
            : g);
}
