using System.Text;
using System.Text.Json;

namespace DocWizz.Tests.Unit;

// The requirement index (#166): exported history + work items → which commit touched which symbol, and the resolver's
// confirmed/candidate/no-link/unavailable decisions. Models per commit are built in memory: no git, no scan.
public class RequirementIndexTests
{
    const string Svc = "cs:Shop.OrderService";
    const string Place = "cs:Shop.OrderService.Place(int)";
    const string Cancel = "cs:Shop.OrderService.Cancel(int)";
    const string File = "src/OrderService.cs";

    static string Sha(int n) => n.ToString("x").PadLeft(40, '0');

    // The service as it looks at every commit: Place on lines 5-12, Cancel on 14-20, inside the class 1-22.
    static CodeModel At(string? placeHash = "p1") => Models.Model([
        new Node(Svc, "class", "OrderService", File, 1, EndLine: 22, Hash: "c"),
        new Node(Place, "method", "Place", File, 5, EndLine: 12, Hash: placeHash),
        new Node(Cancel, "method", "Cancel", File, 14, EndLine: 20, Hash: "x1"),
    ]);

    static object Commit(int n, object workItems, object[]? prs = null, params (int Start, int Lines)[] hunks) => new
    {
        sha = Sha(n),
        changes = new[] { new { path = File, status = "modified", hunks = hunks.Select(h => new { oldStart = h.Start, oldLines = h.Lines, newStart = h.Start, newLines = h.Lines }) } },
        workItems,
        pullRequests = prs ?? [],
    };

    static object Verified(params int[] ids) => new { status = "verified", items = ids.Select(i => new { id = i, source = "commit-workitem" }) };

    static byte[] History(object[] commits, object[]? prs = null, string schema = RequirementIndex.HistorySchema) =>
        JsonSerializer.SerializeToUtf8Bytes(new { schema, repository = new { name = "shop" }, range = new { headSha = Sha(99) }, commits, pullRequests = prs ?? [], truncated = false });

    static List<(JsonElement, string, string)> Items(params (int Id, int Rev, string Title)[] items) => [.. items.Select(i =>
    {
        var json = JsonSerializer.Serialize(new
        {
            schema = RequirementIndex.WorkItemSchema, id = i.Id, revision = i.Rev, currentRevision = i.Rev, isCurrent = true, type = "User Story", state = "Closed",
            title = i.Title, acceptanceCriteria = new { sourceField = "Microsoft.VSTS.Common.AcceptanceCriteria", items = new[] { new { index = 0, text = "Orders over the limit are rejected" } } },
        });
        return (JsonDocument.Parse(json).RootElement.Clone(), RequirementIndex.Digest(Encoding.UTF8.GetBytes(json)), $"wi-{i.Id}.json");
    })];

    static RequirementIndex.Index Build(byte[] history, List<(JsonElement, string, string)>? items = null, Func<string, CodeModel?>? modelAt = null, RequirementIndex.Index? previous = null) =>
        RequirementIndex.Build(history, items ?? [], "scope", "", modelAt ?? (_ => At()), p => p.EndsWith(".cs"), previous);

    static Node Current(string id, string? hash = "p1") => At(hash).Nodes.Single(n => n.Id == id);

    [Fact]
    public void A_direct_commit_link_on_a_changed_method_is_confirmed_with_its_exact_source()
    {
        var index = Build(History([Commit(1, Verified(42), null, (7, 2))]), Items((42, 3, "Credit limit")));
        var c = RequirementResolver.For(index, Place, Current(Place), Sha(99));
        Assert.Equal("linked", c.Outcome);
        var r = Assert.Single(c.Requirements);
        Assert.Equal((42, "confirmed", "available", false), (r.Id, r.Status, r.Availability, r.ChangedSince));
        var e = Assert.Single(r.Evidence);
        Assert.Equal(("commit-workitem", Sha(1), File, "7-8", "5-12"), (e.Relation, e.Commit, e.File, e.Lines, e.SymbolLines));
        Assert.Equal("Credit limit", r.WorkItem!.Title);
        Assert.Equal(["Orders over the limit are rejected"], r.WorkItem.Criteria);
        // Only the innermost symbol: the class around it is not linked.
        Assert.Equal("not-in-history", RequirementResolver.For(index, Svc, Current(Svc), Sha(99)).Outcome);
    }

    [Fact]
    public void A_pr_only_link_stays_a_candidate_and_a_direct_link_confirms_it()
    {
        object[] prs = [new { id = 7, status = "completed", mergeStrategy = "squash", workItems = Verified(42, 43) }];
        var index = Build(History([Commit(1, Verified(), [new { id = 7, confidence = "exact" }], (7, 1)), Commit(2, Verified(43), null, (8, 1))], prs));
        var c = RequirementResolver.For(index, Place, Current(Place), Sha(99));
        Assert.Equal([(43, "confirmed"), (42, "candidate")], c.Requirements.Select(r => (r.Id, r.Status)));
        Assert.Equal(["pr-workitem"], c.Requirements.Single(r => r.Id == 42).Evidence.Select(e => e.Relation));
        Assert.All(c.Requirements, r => Assert.Equal("unavailable", r.Availability));   // not exported: no content
    }

    [Fact]
    public void An_ambiguous_pr_association_is_a_candidate_marked_ambiguous()
    {
        object[] prs = [new { id = 7, workItems = Verified(42) }];
        var index = Build(History([Commit(1, Verified(), [new { id = 7, confidence = "ambiguous" }], (7, 1))], prs));
        var r = Assert.Single(RequirementResolver.For(index, Place, Current(Place), null).Requirements);
        Assert.Equal(("candidate", "ambiguous"), (r.Status, r.Evidence.Single().Confidence));
    }

    [Fact]
    public void A_hunk_that_overlaps_no_member_makes_no_method_level_assertion()
    {
        // Line 13 sits between Place and Cancel: only the class contains it.
        var index = Build(History([Commit(1, Verified(42), null, (13, 1))]));
        Assert.Equal("not-in-history", RequirementResolver.For(index, Place, Current(Place), null).Outcome);
        Assert.Equal("not-in-history", RequirementResolver.For(index, Cancel, Current(Cancel), null).Outcome);
        Assert.Equal("linked", RequirementResolver.For(index, Svc, Current(Svc), null).Outcome);
    }

    [Fact]
    public void A_pure_deletion_touches_the_method_it_happened_in()
    {
        var index = Build(History([Commit(1, Verified(42), null, (9, 0))]));
        Assert.Equal("linked", RequirementResolver.For(index, Place, Current(Place), null).Outcome);
        Assert.Equal("not-in-history", RequirementResolver.For(index, Cancel, Current(Cancel), null).Outcome);
    }

    [Fact]
    public void Verified_empty_unavailable_denied_and_missing_commits_are_different_outcomes()
    {
        Assert.Equal("no-link", RequirementResolver.For(Build(History([Commit(1, Verified(), null, (7, 1))])), Place, Current(Place), null).Outcome);
        foreach (var status in new[] { "unavailable", "denied", "unknown" })
        {
            var c = RequirementResolver.For(Build(History([Commit(1, new { status }, null, (7, 1))])), Place, Current(Place), null);
            Assert.Equal(("unavailable", false), (c.Outcome, c.Complete));
            Assert.Contains(c.Warnings, w => w.Contains(status));
        }
        var missing = Build(History([Commit(1, Verified(42), null, (7, 1))]), modelAt: _ => null);
        Assert.Equal("missing", missing.Commits.Single().Mapping);
        Assert.Equal("unavailable", RequirementResolver.For(missing, Place, Current(Place), null).Outcome);
    }

    [Fact]
    public void A_symbol_that_changed_after_the_linked_commit_is_marked_and_a_renamed_one_is_not_current()
    {
        var index = Build(History([Commit(1, Verified(42), null, (7, 1))]));
        Assert.True(RequirementResolver.For(index, Place, Current(Place, hash: "p2"), null).Requirements.Single().ChangedSince);
        var gone = RequirementResolver.For(index, Place, null, null);
        Assert.Equal(("not-current", (string?)null), (gone.Outcome, gone.Symbol.Location));
        Assert.Single(gone.Requirements);   // the history is still shown, for the id it had then
    }

    [Fact]
    public void Reverse_lookup_lists_the_symbols_of_a_work_item()
    {
        var index = Build(History([Commit(1, Verified(42), null, (7, 1), (15, 1))]));
        var r = RequirementResolver.Symbols(index, 42, At(), null);
        Assert.Equal([Cancel, Place], r.Symbols.Select(s => s.Id));
        Assert.Empty(RequirementResolver.Symbols(index, 41, At(), null).Symbols);
    }

    [Fact]
    public void Identical_inputs_give_identical_bytes_and_a_known_commit_is_not_scanned_again()
    {
        var history = History([Commit(1, Verified(42), null, (7, 1)), Commit(2, Verified(), null, (15, 1))]);
        var scans = 0;
        CodeModel Count(string _) { scans++; return At(); }
        var first = Build(history, Items((42, 1, "A")), Count);
        Assert.Equal(2, scans);
        Assert.Equal(RequirementIndex.Serialize(first), RequirementIndex.Serialize(Build(history, Items((42, 1, "A")))));
        Assert.Equal(first.Fingerprint, RequirementIndex.Fingerprint(history, Items((42, 1, "A")), "scope"));

        var more = History([Commit(1, Verified(42), null, (7, 1)), Commit(2, Verified(), null, (15, 1)), Commit(3, Verified(43), null, (8, 1))]);
        Build(more, Items((42, 1, "A")), Count, previous: first);
        Assert.Equal(3, scans);   // only commit 3
    }

    [Fact]
    public void A_new_work_item_revision_changes_the_fingerprint_and_an_unrelated_one_keeps_symbol_links()
    {
        var history = History([Commit(1, Verified(42), null, (7, 1))]);
        var before = Build(history, Items((42, 1, "Limit"), (50, 1, "Other")));
        var after = Build(history, Items((42, 2, "Limit, revised"), (50, 1, "Other")));
        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        Assert.Equal(2, RequirementResolver.For(after, Place, Current(Place), null).Requirements.Single().WorkItem!.Revision);
        // Several exports of one item: the highest revision wins.
        var both = Build(history, [.. Items((42, 1, "Old")), .. Items((42, 3, "New"))]);
        Assert.Equal("New", both.WorkItems.Single().Title);
    }

    [Fact]
    public void Work_item_text_is_cleaned_and_bounded()
    {
        var json = JsonSerializer.Serialize(new
        {
            schema = RequirementIndex.WorkItemSchema, id = 9, title = "Limit\u0000\u001b[31m  check\t now",
            description = new { sourceField = "System.Description", text = new string('x', 5000) },
            acceptanceCriteria = new { text = "first\n\n  second  \n" },
        });
        var item = Build(History([]), [(JsonDocument.Parse(json).RootElement.Clone(), "d", "f")]).WorkItems.Single();
        Assert.Equal("Limit [31m check now", item.Title);
        Assert.Equal(2001, item.Description!.Length);
        Assert.Equal(["first", "second"], item.Criteria);
        Assert.Contains(item.Warnings, w => w.Contains("cut"));
    }

    [Fact]
    public void Unknown_schemas_and_malformed_history_are_rejected_with_the_reason()
    {
        Assert.Contains("unknown schema gitwizz.history/v2", Assert.Throws<ArgumentException>(() => Build(History([], schema: "gitwizz.history/v2"))).Message);
        Assert.Contains("full 40-character SHA", Assert.Throws<ArgumentException>(() => Build(History([new { sha = "abc123" }]))).Message);
        Assert.Contains("listed twice", Assert.Throws<ArgumentException>(() => Build(History([Commit(1, Verified()), Commit(1, Verified())]))).Message);
        Assert.Contains("work-item status 'maybe'", Assert.Throws<ArgumentException>(() => Build(History([Commit(1, new { status = "maybe" })]))).Message);
        var wrong = JsonDocument.Parse("""{"schema":"ado.requirement-context/v9","id":1}""").RootElement.Clone();
        using var dir = new Sources(("wi.json", wrong.GetRawText()));
        Assert.Contains("unknown schema ado.requirement-context/v9", Assert.Throws<ArgumentException>(() => RequirementIndex.ReadWorkItems(dir.Root)).Message);
    }

    [Fact]
    public void Open_pull_requests_are_not_used()
    {
        object[] prs = [new { id = 7, status = "active", workItems = Verified(42) }];
        var index = Build(History([Commit(1, Verified(), [new { id = 7, confidence = "exact" }], (7, 1))], prs));
        var c = RequirementResolver.For(index, Place, Current(Place), null);
        Assert.Empty(c.Requirements);
        Assert.Contains(index.Warnings, w => w.Contains("PR 7 is active"));
    }
}
