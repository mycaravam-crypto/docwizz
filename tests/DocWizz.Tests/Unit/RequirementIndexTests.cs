using System.Text;
using System.Text.Json;

namespace DocWizz.Tests.Unit;

// The requirement index (#166): exported history + work items → which commit touched which symbol, and the resolver's
// confirmed/candidate/no-link/unavailable decisions. Models per commit are built in memory: no git, no scan.
public partial class RequirementIndexTests
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

    // gitwizz.history/v2 pieces (gitwizz docs/history/README.md). Tests list commits oldest first; History writes them
    // newest first, as gitwizz does.
    static object Commit(int n, object providerLinks, object? pullRequest = null, params (int Start, int Lines)[] hunks) =>
        Commit(n, providerLinks, pullRequest, [], hunks);

    static object Commit(int n, object providerLinks, object? pullRequest, object[] mentions, params (int Start, int Lines)[] hunks) => new
    {
        sha = Sha(n), parents = new[] { Sha(n + 1000) }, subject = $"change {n}", committedAt = "2026-10-01T08:00:00+00:00", kind = "commit",
        files = new[] { Changed(hunks) },
        workItems = mentions,
        providerLinks,
        pullRequest = pullRequest ?? NoPr,
    };

    static object Changed((int Start, int Lines)[] hunks, string granularity = "lines") => new
    {
        path = File, oldPath = (string?)null, status = "modified", similarity = (int?)null, granularity,
        ranges = hunks.Select(h => new { oldStart = h.Start, oldCount = h.Lines, newStart = h.Start, newCount = h.Lines }),
    };

    static object Link(string id, string source, string kind) => new { workItem = id, source, kind, evidence = id };
    static object Mention(int id) => Link($"AB#{id}", "commit-message", "ab");
    static object Verified(params int[] ids) => new { status = "verified", items = ids.Select(i => Link($"AB#{i}", "provider", "commit-link")), problem = (string?)null };
    static object Status(string status) => new { status, items = (object?)null, problem = $"lookup {status}" };

    static readonly object NoPr = new { id = (string?)null, resolution = "unknown", basis = Array.Empty<string>(), candidates = Array.Empty<string>() };
    static object Pr(int id, string resolution = "exact") => new { id = $"#{id}", resolution, basis = new[] { "merge-message" }, candidates = Array.Empty<string>() };
    static object Ambiguous(params int[] ids) => new { id = (string?)null, resolution = "ambiguous", basis = new[] { "merged-branch" }, candidates = ids.Select(i => $"#{i}") };

    static object PullRequest(int id, string status = "confirmed", string linkage = "verified", params int[] items) => new
    {
        id = $"#{id}", title = $"PR {id}", url = (string?)null, target = "main", status, strategy = "squash", mergeCommit = Sha(id),
        commits = new[] { Sha(id) }, workItems = items.Select(i => Link($"AB#{i}", "provider", "pr-link")), workItemsStatus = linkage,
        problem = status == "confirmed" ? null : "not confirmed",
    };

    static byte[] History(object[] commits, object[]? prs = null, string schema = RequirementIndex.HistorySchema, bool truncated = false, bool shallow = false,
        string provider = "azure-devops", string repository = "https://ado.example/shop") =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema, provider, repository,
            range = new { since = "v1", sinceSha = Sha(98), until = "main", untilSha = Sha(99), limit = 200, commits = commits.Length, truncated, shallow },
            sources = new { commitMessages = "complete", providerCommitLinks = "ado", pullRequests = "ado", pullRequestWorkItems = "ado" },
            commits = commits.Reverse(), pullRequests = prs ?? [], warnings = Array.Empty<string>(),
        });

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
        object[] prs = [PullRequest(7, items: [42, 43])];
        var index = Build(History([Commit(1, Verified(), Pr(7), (7, 1)), Commit(2, Verified(43), null, (8, 1))], prs));
        var c = RequirementResolver.For(index, Place, Current(Place), Sha(99));
        Assert.Equal([(43, "confirmed"), (42, "candidate")], c.Requirements.Select(r => (r.Id, r.Status)));
        Assert.Equal(["pr-workitem"], c.Requirements.Single(r => r.Id == 42).Evidence.Select(e => e.Relation));
        Assert.All(c.Requirements, r => Assert.Equal("unavailable", r.Availability));   // not exported: no content
    }

    [Fact]
    public void An_ambiguous_pr_association_is_a_candidate_marked_ambiguous()
    {
        object[] prs = [PullRequest(7, items: [42]), PullRequest(8, items: [])];
        var index = Build(History([Commit(1, Verified(), Ambiguous(7, 8), (7, 1))], prs));
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
            var c = RequirementResolver.For(Build(History([Commit(1, Status(status), null, (7, 1))])), Place, Current(Place), null);
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
    public void A_rename_carries_the_old_ids_links_over_as_candidates()
    {
        // Commit 1 links WI-42 to Place; commit 2 renames Place to Submit in place (same type, parameters and lines).
        const string Submit = "cs:Shop.OrderService.Submit(int)";
        var renamed = Models.Model([.. At().Nodes.Select(n => n.Id == Place ? n with { Id = Submit, Name = "Submit", Hash = "s1" } : n)]);
        var index = Build(History([Commit(1, Verified(42), null, (7, 1)), Commit(2, Verified(), null, (5, 1))]),
            modelAt: sha => sha == Sha(2) ? renamed : At());
        var now = renamed.Nodes.Select(n => n.Id).ToHashSet();
        var submit = renamed.Nodes.Single(n => n.Id == Submit);

        var r = Assert.Single(RequirementResolver.For(index, Submit, submit, null, now.Contains).Requirements);
        Assert.Equal((42, "candidate", false), (r.Id, r.Status, r.ChangedSince));
        Assert.Equal(("renamed-from", Place, Sha(1)), (r.Evidence.Single().Relation, r.Evidence.Single().Symbol, r.Evidence.Single().Commit));
        Assert.Contains($"renamed-from {Place} via", RequirementResolver.Text(RequirementResolver.For(index, Submit, submit, null, now.Contains)));
        Assert.Equal([Place, Submit], RequirementResolver.Symbols(index, 42, renamed, null).Symbols.Select(s => s.Id).Order());

        // Not a rename: the old method still exists, or nobody said what exists.
        Assert.Empty(RequirementResolver.For(index, Submit, submit, null, _ => true).Requirements);
        Assert.Empty(RequirementResolver.For(index, Submit, submit, null).Requirements);
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
        Assert.Contains("gitwizz.history/v1 is no longer read", Assert.Throws<ArgumentException>(() => Build(History([], schema: "gitwizz.history/v1"))).Message);
        Assert.Contains("unknown schema gitwizz.history/v3", Assert.Throws<ArgumentException>(() => Build(History([], schema: "gitwizz.history/v3"))).Message);
        Assert.Contains("full 40-character SHA", Assert.Throws<ArgumentException>(() => Build(History([new { sha = "abc123" }]))).Message);
        Assert.Contains("listed twice", Assert.Throws<ArgumentException>(() => Build(History([Commit(1, Verified()), Commit(1, Verified())]))).Message);
        Assert.Contains("'maybe' is not one of", Assert.Throws<ArgumentException>(() => Build(History([Commit(1, Status("maybe"))]))).Message);
        Assert.Contains("missing 'providerLinks'", Assert.Throws<ArgumentException>(() => Build(History([new { sha = Sha(1), kind = "commit", files = Array.Empty<object>(), workItems = Array.Empty<object>() }]))).Message);
        // Only verified carries items; a message mention is never a provider link.
        Assert.Contains("verified without a list", Assert.Throws<ArgumentException>(() => Build(History([Commit(1, new { status = "verified", items = (object?)null, problem = (string?)null })]))).Message);
        Assert.Contains("items must be null", Assert.Throws<ArgumentException>(() => Build(History([Commit(1, new { status = "unavailable", items = new[] { Link("AB#1", "provider", "commit-link") }, problem = "x" })]))).Message);
        Assert.Contains("never a provider link", Assert.Throws<ArgumentException>(() => Build(History([Commit(1, new { status = "verified", items = new[] { Mention(1) }, problem = (string?)null })]))).Message);
        Assert.Contains("'rebase' is not one of", Assert.Throws<ArgumentException>(() => Build(History([Commit(1, Verified(), Pr(7, "rebase"))]))).Message);
        Assert.Contains("'wizard' is not one of", Assert.Throws<ArgumentException>(() => Build(History([Commit(1, Verified())], provider: "wizard"))).Message);
        var wrong = JsonDocument.Parse("""{"schema":"ado.requirement-context/v9","id":1}""").RootElement.Clone();
        using var dir = new Sources(("wi.json", wrong.GetRawText()));
        Assert.Contains("unknown schema ado.requirement-context/v9", Assert.Throws<ArgumentException>(() => RequirementIndex.ReadWorkItems(dir.Root)).Message);
    }

    [Fact]
    public void Unconfirmed_pull_requests_are_not_used_and_say_so()
    {
        object[] prs = [PullRequest(7, status: "unconfirmed", items: [42])];
        var index = Build(History([Commit(1, Verified(), Pr(7, "inferred"), (7, 1))], prs));
        var c = RequirementResolver.For(index, Place, Current(Place), null);
        Assert.Empty(c.Requirements);
        Assert.Equal(("unavailable", false), (c.Outcome, c.Complete));   // not "no-link": the PR's links are unknown
        Assert.Contains(index.Warnings, w => w.Contains("PR #7 is unconfirmed"));
        Assert.Contains(c.Warnings, w => w.Contains("unconfirmed, its work items are not used"));
    }

    [Fact]
    public void A_message_mention_is_a_candidate_and_only_a_provider_link_confirms()
    {
        var index = Build(History([Commit(1, Verified(), null, [Mention(42)], (7, 1)), Commit(2, Status("denied"), null, [Mention(43)], (8, 1)),
            Commit(3, Verified(43), null, [Mention(43)], (9, 1))]));
        var c = RequirementResolver.For(index, Place, Current(Place), null);
        Assert.Equal([(43, "confirmed"), (42, "candidate")], c.Requirements.Select(r => (r.Id, r.Status)));
        Assert.Equal(["commit-message"], c.Requirements.Single(r => r.Id == 42).Evidence.Select(e => e.Relation).Distinct());
        Assert.False(c.Complete);   // commit 2's provider links were denied
        Assert.Equal([42], index.Commits[0].Mentions);
        Assert.Empty(index.Commits[0].WorkItems);
    }

    [Fact]
    public void A_file_level_change_never_becomes_a_symbol_link()
    {
        var commit = new
        {
            sha = Sha(1), parents = new[] { Sha(2) }, subject = "rename", committedAt = "2026-10-01T08:00:00+00:00", kind = "commit",
            files = new[] { Changed([], granularity: "file") }, workItems = Array.Empty<object>(), providerLinks = Verified(42), pullRequest = NoPr,
        };
        var index = Build(History([commit]));
        Assert.Empty(index.Commits.Single().Touches);
        Assert.Equal([new RequirementIndex.FileTouch(File, "file")], index.Commits.Single().FileOnly);
        Assert.Equal("not-in-history", RequirementResolver.For(index, Place, Current(Place), null).Outcome);
        Assert.Contains("file granularity with line ranges", Assert.Throws<ArgumentException>(() => Build(History([new
        {
            sha = Sha(1), parents = new[] { Sha(2) }, subject = "x", committedAt = "x", kind = "commit", files = new[] { Changed([(3, 1)], granularity: "file") },
            workItems = Array.Empty<object>(), providerLinks = Verified(), pullRequest = NoPr,
        }]))).Message);
    }

    [Fact]
    public void Github_mentions_are_candidates_and_unverified_history_is_not_an_empty_answer()
    {
        var index = Build(History([Commit(1, Status("unknown"), null, [Link("#12", "commit-message", "closes")], (7, 1))], provider: "github", repository: "https://github.com/acme/shop", truncated: true, shallow: true));
        Assert.Equal(new RequirementId(12, "acme/shop"), Assert.Single(index.Commits.Single().Mentions));
        Assert.Contains(index.Warnings, w => w.Contains("no provider link was verified (github history)"));
        Assert.Contains(index.Warnings, w => w.Contains("shallow clone"));
        var c = RequirementResolver.For(index, Place, Current(Place), null);
        Assert.Equal("linked", c.Outcome);
        Assert.Equal("candidate", Assert.Single(c.Requirements).Status);
        Assert.False(c.Complete);
        Assert.Contains(c.Warnings, w => w.Contains("truncated"));
        Assert.Contains(c.Warnings, w => w.Contains("shallow clone"));
        Assert.Equal(("github", true), (index.Inputs.Provider, index.Inputs.Shallow));
    }

    [Fact]
    public void A_merge_commit_carries_no_changes_of_its_own()
    {
        var merge = new
        {
            sha = Sha(2), parents = new[] { Sha(1), Sha(3) }, subject = "Merged PR 7", committedAt = "2026-10-01T08:00:00+00:00", kind = "merge",
            files = Array.Empty<object>(), workItems = Array.Empty<object>(), providerLinks = Verified(42), pullRequest = Pr(7),
        };
        var index = Build(History([Commit(1, Verified(), Pr(7), (7, 1)), merge], [PullRequest(7, items: [43])]));
        Assert.Equal(["mapped", "no-source"], index.Commits.Select(c => c.Mapping));
        // The merge's own link does not reach Place; the PR's does, as a candidate.
        Assert.Equal([(43, "candidate")], RequirementResolver.For(index, Place, Current(Place), null).Requirements.Select(r => (r.Id, r.Status)));
    }
}
