using System.Text.Json.Nodes;

namespace DocWizz.Tests.Unit;

// `docwizz requirements query` (#173): the gitwizz.requirement-input/v1 answer from a hand-built index, for the cases a
// real history rarely shows: an ambiguous PR association, inputs that don't cover everything, and every output cap.
public class RequirementQueryTests
{
    const string Place = "cs:Shop.OrderService.Place(int)";

    static string Sha(int n) => n.ToString("x").PadLeft(40, '0');

    static RequirementIndex.Commit Commit(int n, List<int>? workItems = null, string linkage = "verified", params RequirementIndex.PullRequestRef[] prs) =>
        new(Sha(n), linkage, [.. (workItems ?? []).Select(i => (RequirementId)i)], [], [.. prs], "mapped", [new(Place, "method", "h", "OrderService.cs", $"{n}", "1-9")], []);

    static RequirementIndex.WorkItem Item(int id, string title = "Credit limit") =>
        new(id, 1, 1, true, "User Story", "Done", title, null, null, null, null, null, [], "d", []);

    static RequirementIndex.Index Index(List<RequirementIndex.Commit> commits, List<RequirementIndex.WorkItem> items,
        List<RequirementIndex.PullRequest>? prs = null, bool truncated = false, bool shallow = false) =>
        new(RequirementIndex.Schema, "f", new(RequirementIndex.HistorySchema, "d", "azure-devops", null, null, null, Sha(99), truncated, shallow, null, []),
            commits, prs ?? [], items, []);

    static JsonObject Answer(RequirementIndex.Index index, params string[] symbols) =>
        RequirementQuery.Answer(index, [.. symbols.Select(s => new RequirementQuery.Symbol(s, null))], _ => null, Sha(99), scopeChanged: false);

    static List<JsonNode> Links(JsonObject answer) => [.. answer["links"]!.AsArray().Select(l => l!)];
    static List<string> Unavailable(JsonObject answer) => [.. answer["unavailable"]!.AsArray().Select(u => (string)u!["reason"]!)];

    [Fact]
    public void An_ambiguous_pr_association_is_a_candidate_that_says_so_never_verified()
    {
        var index = Index([Commit(1, prs: new RequirementIndex.PullRequestRef(5, "ambiguous"))], [Item(7)],
            [new(5, "PR 5", "squash", "confirmed", "verified", [7])]);
        var link = Assert.Single(Links(Answer(index, Place)));
        Assert.Equal(("AB#7", "candidate", "pr-link"), ((string)link["requirement"]!, (string)link["type"]!, (string)link["source"]!));
        Assert.Equal($"commit of linked PR 5 (ambiguous) {Sha(1)[..7]} changed OrderService.cs:1", (string)link["evidence"]![0]!);
    }

    // The symbol isn't in the current code (Answer gets no node), as for a method the PR adds or renames.
    [Fact]
    public void An_unconfirmed_pr_gives_no_link_and_says_why_even_for_a_symbol_not_in_the_current_code()
    {
        var index = Index([Commit(1, prs: new RequirementIndex.PullRequestRef(5, "exact"))], [Item(7)],
            [new(5, "PR 5", "squash", "unconfirmed", "verified", [7])]);
        var answer = Answer(index, Place);
        Assert.Empty(Links(answer));
        Assert.Contains($"{Place}: the work-item links of some commits that changed it are unknown", Unavailable(answer));
    }

    [Fact]
    public void Links_evidence_titles_and_unavailable_reasons_are_capped_and_the_cut_is_reported()
    {
        // 12 work items on one commit (2 over the link cap), one of them behind 7 commits (2 over the evidence cap),
        // and a title of 300 characters.
        List<RequirementIndex.Commit> commits = [Commit(1, [.. Enumerable.Range(1, 12)]), .. Enumerable.Range(2, 6).Select(n => Commit(n, [1]))];
        var index = Index(commits, [Item(1, new string('x', 300)), .. Enumerable.Range(2, 11).Select(i => Item(i))]);
        var answer = Answer(index, Place);
        var links = Links(answer);
        Assert.Equal(10, links.Count);
        Assert.Contains($"{Place}: 2 more linked work items left out", Unavailable(answer));
        var first = links.Single(l => (string)l["requirement"]! == "AB#1");
        Assert.Equal(5, first["evidence"]!.AsArray().Count);
        Assert.Equal(new string('x', 200) + "…", (string)first["title"]!);

        // 25 linked work items that weren't exported: 20 reasons, then one that counts the rest.
        var missing = Index([Commit(1, [.. Enumerable.Range(1, 25)])], []);
        var reasons = Unavailable(Answer(missing, Place));
        Assert.Equal(21, reasons.Count);
        Assert.Equal("6 more unavailable sources left out", reasons[^1]);
    }

    [Fact]
    public void Incomplete_inputs_and_unknown_symbols_are_unavailable_not_an_empty_answer()
    {
        var answer = Answer(Index([Commit(1, [7])], [Item(7)], truncated: true, shallow: true), Place, "cs:Shop.Nope()");
        Assert.Equal(["AB#7"], Links(answer).Select(l => (string)l["requirement"]!));   // only requested ids, the unknown one has none
        Assert.Contains("the history export was truncated: older commits are not covered", Unavailable(answer));
        Assert.Contains("the history was exported from a shallow clone: older commits are not covered", Unavailable(answer));
        Assert.Equal(Sha(99), (string)answer["analyzedCommit"]!);
        Assert.Equal("gitwizz.requirement-input/v1", (string)answer["schema"]!);
    }
}
