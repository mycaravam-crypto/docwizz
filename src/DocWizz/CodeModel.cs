// The code intelligence model: language-neutral facts only. Analyzers (C#, Vue/TS, projects) populate it;
// documentation analysis, architecture checks and generators read it. Nothing interpretive or AI-written lives here.

// Parameters are "name: Type", prefixed with the binding source for endpoints ("[body] request: CreateRequest").
// Returns is the unwrapped result type (Task<ActionResult<T>> → T), null for void/Task. Events are emitted event names.
// Tags carry framework concepts and roles (controller, endpoint, service, repository, entity, middleware, store, …).
// Language (csharp, typescript, vue, msbuild, npm) is the source language; ids keep their scanner prefix for stability.
record Node(string Id, string Kind, string Name, string File, int Line,
    string? Visibility = null, string? Doc = null, int? Complexity = null,
    int? Params = null, string? Hash = null, List<string>? Tags = null, string? Route = null, int? EndLine = null,
    List<string>? Parameters = null, string? Returns = null, List<string>? Throws = null, List<string>? Events = null,
    string? Language = null);

// Label carries the detail a kind alone doesn't (e.g. the event name of a `subscribes` edge).
record Edge(string From, string To, string Kind, string? Label = null);

record CodeModel(string? Commit, List<Node> Nodes, List<Edge> Edges)
{
    // Edge kinds that are a dependency of From on To (contains/registers/references/depends-on/tests are not).
    // `creates`: From instantiates To (`new T(..)`).
    public static readonly string[] DependencyKinds = ["calls", "injects", "implements", "inherits", "imports", "renders",
        "routes-to", "persists", "publishes", "subscribes", "http", "creates"];
}
