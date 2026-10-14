namespace DocWizz.Tests.Unit;

public class ReviewLogTests
{
    static readonly HashSet<string> States = ["vorgelegt", "fertiggestellt"];
    const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    static ReviewLog Parse(string yaml) => ReviewLog.Parse(new StringReader(yaml), States);

    [Fact]
    public void Reads_who_set_which_state_when_for_which_content()
    {
        var log = Parse($"schema: 1\nreviews:\n  - state: vorgelegt\n    by: Erika Muster\n    date: 2026-10-14\n    sha256: {Sha}\n    note: zur QS\n");
        var review = Assert.Single(log.Reviews);
        Assert.Equal(new Review("vorgelegt", "Erika Muster", new DateOnly(2026, 10, 14), Sha, "zur QS"), review);
        Assert.Empty(Parse("schema: 1\nreviews: []\n").Reviews);
    }

    [Theory]
    [InlineData("schema: '1'\nreviews: []\n", "unquoted 1")]
    [InlineData("schema: 1\n", "must be a list")]
    [InlineData("schema: 1\nreviews: []\napproved: true\n", "unknown key 'approved'")]
    [InlineData("schema: 1\nreviews:\n  - state: freigegeben\n    by: A\n    date: 2026-10-14\n    sha256: SHA\n", "state must be one of fertiggestellt, vorgelegt")]
    [InlineData("schema: 1\nreviews:\n  - state: vorgelegt\n    date: 2026-10-14\n    sha256: SHA\n", "by must be a non-empty scalar")]
    [InlineData("schema: 1\nreviews:\n  - state: vorgelegt\n    by: A\n    date: 14.10.2026\n    sha256: SHA\n", "date must be a date")]
    [InlineData("schema: 1\nreviews:\n  - state: vorgelegt\n    by: A\n    date: 2026-10-14\n    sha256: ABC\n", "lowercase SHA-256")]
    [InlineData("schema: 1\nreviews:\n  - state: vorgelegt\n    by: A\n    date: 2026-10-14\n    sha256: SHA\n  - state: fertiggestellt\n    by: B\n    date: 2026-10-13\n    sha256: SHA\n", "before the previous review")]
    [InlineData("schema: 1\nreviews:\n  - state: vorgelegt\n    by: A\n    date: 2026-10-14\n    sha256: SHA\n    reviewer: B\n", "unknown key 'reviewer'")]
    public void Rejects_anything_but_a_complete_dated_record(string yaml, string message)
    {
        var e = Assert.Throws<ArgumentException>(() => Parse(yaml.Replace("SHA", Sha)));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void Hash_is_the_sha256_of_the_file_bytes()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "abc");
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ReviewLog.Hash(file));
        }
        finally { File.Delete(file); }
    }

    // The state belongs to exactly the reviewed content and follows V-Modell XT order: vorgelegt, then fertiggestellt.
    [Fact]
    public void State_comes_only_from_reviews_of_this_exact_draft_in_order()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var draft = Path.Combine(dir, "sw-architecture.md");
            File.WriteAllText(draft, "# draft\n");
            var sha = ReviewLog.Hash(draft);
            var log = Path.Combine(dir, "sw-architecture.review.yaml");
            (ProductCheck.State State, List<ProductCheck.Finding> Findings) Check(string reviews)
            {
                File.WriteAllText(log, "schema: 1\nreviews:\n" + reviews);
                var findings = new List<ProductCheck.Finding>();
                return (ProductCheck.ReviewState(dir, draft, findings), findings);
            }
            string Entry(string state, string by, string date, string hash) =>
                $"  - state: {state}\n    by: {by}\n    date: {date}\n    sha256: {hash}\n";

            File.Delete(log);
            var none = ProductCheck.ReviewState(dir, draft, []);
            Assert.Equal(("in Bearbeitung", null, sha, "sw-architecture.review.yaml"), (none.Value, none.By, none.DraftSha256, none.ReviewLog));

            var submitted = Check(Entry("vorgelegt", "A", "2026-10-14", sha));
            Assert.Equal(("vorgelegt", "A"), (submitted.State.Value, submitted.State.By));
            Assert.Empty(submitted.Findings);

            var completed = Check(Entry("vorgelegt", "A", "2026-10-14", sha) + Entry("fertiggestellt", "B", "2026-10-15", sha));
            Assert.Equal(("fertiggestellt", "B", new DateOnly(2026, 10, 15)), (completed.State.Value, completed.State.By, completed.State.Date));

            var skipped = Check(Entry("fertiggestellt", "B", "2026-10-15", sha));
            Assert.Equal("in Bearbeitung", skipped.State.Value);
            Assert.Equal("PROD-009", Assert.Single(skipped.Findings).Rule);

            // Submitted for other content: the earlier review doesn't carry over to this draft.
            var other = new string('a', 64);
            var stale = Check(Entry("vorgelegt", "A", "2026-10-14", other) + Entry("fertiggestellt", "B", "2026-10-15", other));
            Assert.Equal("in Bearbeitung", stale.State.Value);
            var finding = Assert.Single(stale.Findings);
            Assert.Equal(("PROD-008", "warning"), (finding.Rule, finding.Severity));
            Assert.Contains("fertiggestellt, B, 2026-10-15", finding.Message);

            // A submission of this draft after one of the earlier content: vorgelegt, not the earlier fertiggestellt.
            Assert.Equal("vorgelegt", Check(Entry("fertiggestellt", "B", "2026-10-15", other).Replace("fertiggestellt", "vorgelegt")
                + Entry("fertiggestellt", "B", "2026-10-15", other) + Entry("vorgelegt", "A", "2026-10-16", sha)).State.Value);

            var invalid = Check("  - state: freigegeben\n");
            Assert.Equal("in Bearbeitung", invalid.State.Value);
            var error = Assert.Single(invalid.Findings);
            Assert.Equal(("PROD-007", "error"), (error.Rule, error.Severity));
            Assert.StartsWith("sw-architecture.review.yaml: ", error.Message);
        }
        finally { Directory.Delete(dir, true); }
    }
}
