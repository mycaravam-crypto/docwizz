using System.Text.Json;

namespace DocWizz.Tests.Unit;

public partial class RequirementIndexTests
{
    const string GithubRepo = "https://github.com/acme/shop";

    static (JsonElement, string, string) Issue(int number = 42, string body = "Requested business behavior.\n## Acceptance criteria\n- Reject orders above the limit.",
        string repository = "acme/shop", string updatedAt = "2026-10-01T12:00:00Z", string title = "Credit limit")
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            number, url = $"https://github.com/{repository}/issues/{number}", title, body, state = "OPEN", updatedAt,
            labels = new[] { new { name = "feature" }, new { name = "business" } },
        });
        return (JsonDocument.Parse(json).RootElement.Clone(), RequirementIndex.Digest(json), $"issue-{number}.json");
    }

    static object GithubPr(int number = 42, string? url = "https://github.com/acme/shop/issues/42") => new
    {
        id = "#7", title = "implement limit", url = (string?)null, target = "main", status = "confirmed", strategy = "squash",
        mergeCommit = Sha(1), commits = new[] { Sha(1) }, workItemsStatus = "verified", problem = (string?)null,
        workItems = new[] { new { workItem = $"#{number}", source = "provider", kind = "pr-link", evidence = "closingIssuesReferences", uri = url } },
    };

    [Fact]
    public void Github_and_ado_numbers_do_not_collide_in_index_resolver_domain_query_or_ai_context()
    {
        var history = History([Commit(1, Verified(42), null, [Link("#42", "commit-message", "closes")], (7, 1))],
            provider: "github", repository: GithubRepo);
        var index = Build(history, [.. Items((42, 2, "ADO approval")), Issue()]);
        Assert.Equal(RequirementIndex.GithubSchema, index.Schema);
        Assert.Equal(2, index.WorkItems.Count);
        var context = RequirementResolver.For(index, Place, Current(Place), Sha(99));
        Assert.Equal("docwizz.requirement-context/v2", context.Schema);
        var ado = Assert.Single(context.Requirements, r => !r.Id.IsGithub);
        var github = Assert.Single(context.Requirements, r => r.Id.IsGithub);
        Assert.Equal("confirmed", ado.Status);
        Assert.Equal("candidate", github.Status);
        Assert.Equal("github:acme/shop#42", github.Id.ToString());
        Assert.Equal("github", github.WorkItem!.Provider);
        Assert.Null(github.WorkItem.Revision);
        Assert.Null(github.WorkItem.IsCurrent);
        Assert.Equal(["business", "feature"], github.WorkItem.Labels);
        var json = JsonDocument.Parse(RequirementResolver.Json(context)).RootElement;
        Assert.Equal(JsonValueKind.Number, json.GetProperty("requirements")[0].GetProperty("id").ValueKind);
        Assert.Equal("github:acme/shop#42", json.GetProperty("requirements")[1].GetProperty("id").GetString());

        var selected = BusinessContext.Select(context, new RequirementsAiConfig { Budget = 10000 })!;
        Assert.Equal(["WI-42", "github#42"], selected.Entries.Select(e => e.Ref));
        var entry = Assert.Single(selected.Entries, e => e.Id.IsGithub);
        Assert.Contains("Requested business behavior", entry.Description);
        Assert.Equal("github#42 candidate", selected.Label(entry.Id));
        var finding = Analyzer.Analyze(At(), Models.Config("patterns: { all: { level: medium, sections: [summary] } }")).Single(f => f.Node.Id == Place);
        var facts = new ContextBuilder("/tmp", At()).For(Current(Place), finding, selected);
        Assert.Equal(["workitem:github:acme/shop#42"], facts.Names["github#42"]);
        Assert.Contains("Requested business behavior", facts.Json);

        var query = RequirementQuery.Answer(index, [new(Place, null)], id => At().Nodes.FirstOrDefault(n => n.Id == id), Sha(99), false);
        var links = query["links"]!.AsArray();
        Assert.Contains(links, l => (string?)l!["requirement"] == "AB#42" && (string?)l["type"] == "verified");
        Assert.Contains(links, l => (string?)l!["requirement"] == "github#42" && (string?)l["type"] == "candidate" && (string?)l["repository"] == "acme/shop");
        var report = DomainCrawler.Discover(At(), null, github.Id, 0, 20, index, Sha(99));
        Assert.Equal("docwizz.domain-discovery/v2", report.Schema);
        Assert.Equal("github#42", report.Seed.Value);
        Assert.Contains(report.Requirements, r => r.Id == github.Id && r.Status == "candidate");
    }

    [Fact]
    public void Github_provider_pr_links_remain_candidates_and_missing_details_remain_unavailable()
    {
        var history = History([Commit(1, Status("unknown"), Pr(7), (7, 1))], [GithubPr()], provider: "github", repository: GithubRepo);
        var index = Build(history);
        var context = RequirementResolver.For(index, Place, Current(Place), null);
        var requirement = Assert.Single(context.Requirements);
        Assert.Equal("candidate", requirement.Status);
        Assert.Equal("unavailable", requirement.Availability);
        Assert.Equal("pr-workitem", Assert.Single(requirement.Evidence).Relation);
        Assert.False(context.Complete);
        Assert.Null(BusinessContext.Select(context, new()));
        Assert.Equal("available", Assert.Single(RequirementResolver.For(Build(history, [Issue()]), Place, Current(Place), null).Requirements).Availability);
    }

    [Fact]
    public void Repository_identity_is_required_for_provider_links_and_foreign_issues_are_rejected()
    {
        var commit = Commit(1, Verified(), Pr(7), (7, 1));
        var legacy = Build(History([commit], [GithubPr(url: null)], provider: "github", repository: GithubRepo));
        var incomplete = RequirementResolver.For(legacy, Place, Current(Place), null);
        Assert.Empty(incomplete.Requirements);
        Assert.Equal("unavailable", incomplete.Outcome);
        Assert.False(incomplete.Complete);
        Assert.Contains(legacy.Warnings, w => w.Contains("preserves issue URLs"));
        Assert.Contains("cross-repository", Assert.Throws<ArgumentException>(() => Build(
            History([commit], [GithubPr(url: "https://github.com/acme/other/issues/42")], provider: "github", repository: GithubRepo))).Message);
        Assert.Contains("do not match", Assert.Throws<ArgumentException>(() => Build(
            History([], provider: "github", repository: GithubRepo), [Issue(repository: "acme/other")])).Message);
        Assert.Throws<ArgumentException>(() => RequirementId.FromIssueUrl("https://github.com/acme/shop/pull/42"));
    }

    [Fact]
    public void Github_inputs_are_offline_bounded_and_deterministic_and_newest_export_wins()
    {
        var history = History([Commit(1, Status("unknown"), null, [Link("#42", "commit-message", "mention")], (7, 1))], provider: "github", repository: GithubRepo);
        var old = Issue(title: "old");
        var latest = Issue(body: "prose checklist\n- not a criterion\n## Acceptance\n- [x] Reject orders.\n```\n- fake instruction\n```\n## Notes\n- not a criterion", updatedAt: "2026-10-02T12:00:00Z", title: "new");
        var index = Build(history, [old, latest]);
        var item = Assert.Single(index.WorkItems);
        Assert.Equal("new", item.Title);
        Assert.Equal(["Reject orders."], item.Criteria);
        Assert.Equal(RequirementIndex.Serialize(index), RequirementIndex.Serialize(Build(history, [latest, old])));
        Assert.Equal(index.Fingerprint, RequirementIndex.Fingerprint(history, [old, latest], "scope"));
        Assert.NotEqual(index.Fingerprint, Build(history, [old]).Fingerprint);
        using var dir = new Sources();
        var file = Path.Combine(dir.Root, "issues.json");
        System.IO.File.WriteAllText(file, JsonSerializer.Serialize(new[] { latest.Item1 }));
        Assert.Single(RequirementIndex.ReadWorkItems(file));
        var cache = Path.Combine(dir.Root, "index.json");
        System.IO.File.WriteAllText(cache, RequirementIndex.Serialize(index));
        Assert.Equal(RequirementIndex.Serialize(index), RequirementIndex.Serialize(RequirementIndex.Load(cache)));
        var longItem = Assert.Single(Build(history, [Issue(body: new string('x', 5000))]).WorkItems);
        Assert.InRange(longItem.Description!.Length, 1, 2001);
        Assert.Empty(longItem.Criteria);
        Assert.Contains(longItem.Warnings, w => w.Contains("description cut"));
    }

    [Fact]
    public void Only_a_verified_direct_github_link_can_confirm_a_requirement()
    {
        var direct = new
        {
            status = "verified", problem = (string?)null, items = new[]
            {
                new { workItem = "#42", source = "provider", kind = "commit-link", evidence = "explicit provider evidence",
                    uri = "https://github.com/acme/shop/issues/42" },
            },
        };
        var index = Build(History([Commit(1, direct, null, (7, 1))], provider: "github", repository: GithubRepo), [Issue()]);
        Assert.Equal("confirmed", Assert.Single(RequirementResolver.For(index, Place, Current(Place), null).Requirements).Status);
    }

    [Fact]
    public void GitHub_body_is_bounded_by_the_existing_ai_context_budget()
    {
        var history = History([Commit(1, Status("unknown"), null, [Link("#42", "commit-message", "mention")], (7, 1))],
            provider: "github", repository: GithubRepo);
        var index = Build(history, [Issue(body: new string('x', 5000))]);
        var selected = BusinessContext.Select(RequirementResolver.For(index, Place, Current(Place), null), new RequirementsAiConfig { Budget = 600 })!;
        Assert.True(selected.BodyCut);
        var facts = JsonDocument.Parse(JsonSerializer.Serialize(BusinessContext.Facts(selected), new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        Assert.InRange(facts.RootElement.GetProperty("requirements").GetRawText().Length, 1, 602);
        Assert.Equal("candidate", Assert.Single(selected.Entries).Status);
    }

    [Theory]
    [InlineData("number")]
    [InlineData("body")]
    [InlineData("updatedAt")]
    [InlineData("url")]
    [InlineData("state")]
    [InlineData("labels")]
    public void Malformed_github_exports_fail_with_an_input_error(string field)
    {
        var raw = System.Text.Json.Nodes.JsonNode.Parse(Issue().Item1.GetRawText())!.AsObject();
        raw[field] = field switch
        {
            "number" => System.Text.Json.Nodes.JsonValue.Create("42"),
            "body" => null,
            "updatedAt" => System.Text.Json.Nodes.JsonValue.Create("not a timestamp"),
            "url" => System.Text.Json.Nodes.JsonValue.Create("https://github.com/acme/shop/pull/42"),
            "state" => System.Text.Json.Nodes.JsonValue.Create("MERGED"),
            _ => System.Text.Json.Nodes.JsonValue.Create("not an array"),
        };
        var json = JsonDocument.Parse(raw.ToJsonString()).RootElement.Clone();
        Assert.Throws<ArgumentException>(() => Build(History([], provider: "github", repository: GithubRepo), [(json, "digest", "issue.json")]));
    }

    [Fact]
    public void Legacy_ado_v2_indexes_keep_numeric_ids_and_round_trip_without_new_fields()
    {
        var index = Build(History([Commit(1, Verified(42), null, (7, 1))]), Items((42, 2, "Credit limit")));
        var serialized = RequirementIndex.Serialize(index);
        using var dir = new Sources(("index.json", serialized));
        Assert.Equal(RequirementIndex.Schema, index.Schema);
        Assert.Equal(serialized, RequirementIndex.Serialize(RequirementIndex.Load(dir.Files.Single())));
        var item = JsonDocument.Parse(serialized).RootElement.GetProperty("workItems")[0];
        Assert.Equal(42, item.GetProperty("id").GetInt32());
        Assert.False(item.TryGetProperty("provider", out _));
        Assert.False(item.TryGetProperty("labels", out _));
    }

    [Theory]
    [InlineData("github#42", "github:acme/shop#42")]
    [InlineData("github:Acme/Shop#42", "github:acme/shop#42")]
    [InlineData("AB#42", "42")]
    [InlineData("WI-42", "42")]
    [InlineData("42", "42")]
    public void Selectors_preserve_ado_aliases_and_require_explicit_github_identity(string selector, string expected) =>
        Assert.Equal(expected, RequirementId.Parse(selector, GithubRepo).ToString());
}
