// Structural test traceability: which test code `tests` edges link to a symbol. Direct: an edge to the symbol itself.
// Indirect: an edge to its interface, an implementation, or one of its members (Via names which). A link says test code
// uses the symbol. It is not evidence of code coverage, nor of what the test checks.
record LinkedTest(string Test, string? Via = null);

class TestLinks(CodeModel model)
{
    public const string Note = "a link means test code uses the symbol; it is not code coverage";

    readonly ILookup<string, string> testsOf = model.Edges.Where(e => e.Kind == "tests").ToLookup(e => e.To, e => e.From);
    readonly ILookup<string, string> implOf = model.Edges.Where(e => e.Kind == "implements").ToLookup(e => e.From, e => e.To);
    readonly ILookup<string, string> implBy = model.Edges.Where(e => e.Kind == "implements").ToLookup(e => e.To, e => e.From);
    readonly ILookup<string, string> children = model.Edges.Where(e => e.Kind == "contains").ToLookup(e => e.From, e => e.To);

    public List<LinkedTest> Of(string id) => testsOf[id].Select(t => new LinkedTest(t))
        .Concat(implOf[id].Concat(implBy[id]).Concat(children[id]).SelectMany(x => testsOf[x].Select(t => new LinkedTest(t, x))))
        .DistinctBy(l => l.Test).OrderBy(l => l.Via is not null).ThenBy(l => l.Test, StringComparer.Ordinal).ToList();

    public bool Any(string id) => Of(id).Count > 0;

    // `cs:Fixture.Tests.MaterialServiceTests.Creates(…)` → `Fixture.Tests.MaterialServiceTests.Creates`.
    public static string Name(string id) => id[(id.IndexOf(':') + 1)..].Split('(')[0];

    // For pages: `MaterialServiceTests.Creates`, or a test file's name (`MaterialTable.test.ts`).
    public static string Short(string id) => Name(id) is var n && n.Contains('/') ? n.Split('#')[0].Split('/').Last()
        : string.Join(".", n.Split('.').TakeLast(2));
}
