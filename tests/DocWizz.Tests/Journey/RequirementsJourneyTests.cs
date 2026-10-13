using System.Text.Json;

namespace DocWizz.Tests.Journey;

// `docwizz requirements import|show` (#166) on a real repository: the exported history is mapped onto the symbols as
// they were at each commit, and show answers per current symbol, the same bytes every time, without touching sources.
public class RequirementsJourneyTests
{
    static Docwizz.Result Run(string cwd, params string[] args) => Docwizz.Run(cwd: cwd, env: null, args: args);

    const string V1 = """
        namespace Shop;

        public class OrderService
        {
            public int Place(int amount)
            {
                return amount;
            }

            public void Cancel(int id)
            {
            }
        }
        """;

    // Place gets a credit limit (WI-101, direct link); Cancel gets a log line (only PR 5 links WI-102).
    const string V2 = """
        namespace Shop;

        public class OrderService
        {
            public int Place(int amount)
            {
                if (amount > 1000) throw new System.InvalidOperationException("over the credit limit");
                return amount;
            }

            public void Cancel(int id)
            {
            }
        }
        """;

    const string V3 = """
        namespace Shop;

        public class OrderService
        {
            public int Place(int amount)
            {
                if (amount > 1000) throw new System.InvalidOperationException("over the credit limit");
                return amount;
            }

            public void Cancel(int id)
            {
                System.Console.WriteLine(id);
            }
        }
        """;

    static FakeHistory Repository(out string history, out string workItems)
    {
        var repo = new FakeHistory();
        repo.Write("Shop.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        repo.Write("OrderService.cs", V1);
        repo.Commit("orders", FakeHistory.Verified(100));
        repo.Write("OrderService.cs", V2);
        repo.Commit("credit limit", FakeHistory.Verified(101));
        repo.Write("OrderService.cs", V3);
        repo.Commit("log cancels", FakeHistory.Verified(), new { id = 5, confidence = "exact" });
        repo.Write("README.md", "# Shop\n");
        repo.Commit("readme", new { status = "unavailable" });

        var exports = Path.Combine(Path.GetTempPath(), $"docwizz-exports-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(exports, "work-items"));
        history = Path.Combine(exports, "history.json");
        File.WriteAllText(history, repo.History([new { id = 5, status = "completed", mergeStrategy = "squash", workItems = FakeHistory.Verified(102) }]));
        File.WriteAllText(Path.Combine(exports, "work-items", "100.json"), FakeHistory.WorkItem(100, "Place and cancel orders"));
        File.WriteAllText(Path.Combine(exports, "work-items", "101.json"), FakeHistory.WorkItem(101, "Credit limit", 4, "Orders above 1000 are rejected"));
        workItems = Path.Combine(exports, "work-items");
        return repo;
    }

    [Fact]
    public void Import_then_show_resolves_direct_links_candidates_and_history_per_symbol()
    {
        using var repo = Repository(out var history, out var workItems);
        var import = Run(repo.Root, "requirements", "import", ".", history, workItems);
        Assert.True(import.Exit == 0, import.ToString());
        Assert.Contains("→ docs/.docwizz/requirements.json: 4 commits", import.Out);

        var show = Run(repo.Root, "requirements", "show", ".", "--for", "Shop.OrderService.Place", "--format", "json");
        Assert.True(show.Exit == 0, show.ToString());
        var json = JsonDocument.Parse(show.Out).RootElement;
        Assert.Equal("docwizz.requirement-context/v1", json.GetProperty("schema").GetString());
        Assert.Equal("linked", json.GetProperty("outcome").GetString());
        var reqs = json.GetProperty("requirements").EnumerateArray().ToList();
        Assert.Equal([(100, "confirmed", true), (101, "confirmed", false)],
            reqs.Select(r => (r.GetProperty("id").GetInt32(), r.GetProperty("status").GetString(), r.GetProperty("changedSince").GetBoolean())));
        var credit = reqs[1];
        Assert.Equal(4, credit.GetProperty("workItem").GetProperty("revision").GetInt32());
        Assert.Equal("Orders above 1000 are rejected", credit.GetProperty("workItem").GetProperty("criteria")[0].GetString());
        var evidence = credit.GetProperty("evidence")[0];
        Assert.Equal(("commit-workitem", "OrderService.cs", "7", "5-9"),
            (evidence.GetProperty("relation").GetString(), evidence.GetProperty("file").GetString(), evidence.GetProperty("lines").GetString(), evidence.GetProperty("symbolLines").GetString()));
        Assert.Equal(40, evidence.GetProperty("commit").GetString()!.Length);

        // Cancel: the PR's work item is only a candidate, and its content wasn't exported.
        var cancel = JsonDocument.Parse(Run(repo.Root, "requirements", "show", ".", "--for", "Shop.OrderService.Cancel", "--format", "json").Out).RootElement;
        Assert.Equal([(100, "confirmed", "available"), (102, "candidate", "unavailable")],
            cancel.GetProperty("requirements").EnumerateArray().Select(r => (r.GetProperty("id").GetInt32(), r.GetProperty("status").GetString(), r.GetProperty("availability").GetString())));

        // Reverse lookup, and the text form.
        var reverse = Run(repo.Root, "requirements", "show", ".", "--work-item", "100");
        Assert.True(reverse.Exit == 0, reverse.ToString());
        Assert.Contains("Shop.OrderService.Cancel(int) [confirmed", reverse.Out);
        Assert.Contains("Shop.OrderService.Place(int) [confirmed", reverse.Out);
        var text = Run(repo.Root, "requirements", "show", ".", "--for", "Shop.OrderService.Place");
        Assert.Contains("WI-101 [confirmed] User Story Closed: Credit limit (revision 4)", text.Out);
    }

    [Fact]
    public void Repeated_runs_are_byte_identical_and_unchanged_inputs_write_nothing()
    {
        using var repo = Repository(out var history, out var workItems);
        Assert.Equal(0, Run(repo.Root, "requirements", "import", ".", history, workItems).Exit);
        var index = Path.Combine(repo.Root, "docs", ".docwizz", "requirements.json");
        var bytes = File.ReadAllBytes(index);
        var written = File.GetLastWriteTimeUtc(index);
        var again = Run(repo.Root, "requirements", "import", ".", history, workItems);
        Assert.Contains("unchanged: docs/.docwizz/requirements.json", again.Out);
        Assert.Equal(written, File.GetLastWriteTimeUtc(index));
        string Show() => Run(repo.Root, "requirements", "show", ".", "--for", "Shop.OrderService.Place", "--format", "json").Out;
        Assert.Equal(Show(), Show());

        // A rebuilt index (the cache gone) has the same bytes.
        File.Delete(index);
        Assert.Equal(0, Run(repo.Root, "requirements", "import", ".", history, workItems).Exit);
        Assert.Equal(bytes, File.ReadAllBytes(index));
        // Sources untouched: only docs/ is new.
        Assert.Equal("?? docs/", repo.Git("status", "--porcelain").Trim());
    }

    [Fact]
    public void Missing_index_unknown_schema_and_unknown_symbols_fail_with_a_reason()
    {
        using var repo = Repository(out var history, out var workItems);
        var none = Run(repo.Root, "requirements", "show", ".", "--for", "Shop.OrderService.Place");
        Assert.Equal(1, none.Exit);
        Assert.Contains("no requirement index", none.Err);

        var v2 = Path.Combine(Path.GetDirectoryName(history)!, "v2.json");
        File.WriteAllText(v2, repo.History(schema: "gitwizz.history/v2"));
        var bad = Run(repo.Root, "requirements", "import", ".", v2);
        Assert.Equal(1, bad.Exit);
        Assert.Contains("unknown schema gitwizz.history/v2", bad.Err);
        Docwizz.NoStackTrace(bad);

        Assert.Equal(0, Run(repo.Root, "requirements", "import", ".", history, workItems).Exit);
        var unknown = Run(repo.Root, "requirements", "show", ".", "--for", "Shop.Nope");
        Assert.Equal(1, unknown.Exit);
        var usage = Run(repo.Root, "requirements", "list", ".");
        Assert.Equal(1, usage.Exit);
        Assert.Contains("requirements needs import or show", usage.Err);
    }

    [Fact]
    public void A_later_commit_outside_the_export_is_reported_as_not_covered()
    {
        using var repo = Repository(out var history, out var workItems);
        Assert.Equal(0, Run(repo.Root, "requirements", "import", ".", history, workItems).Exit);
        repo.Write("OrderService.cs", V3.Replace("return amount;", "return amount + 1;"));
        repo.Commit("later");
        var json = JsonDocument.Parse(Run(repo.Root, "requirements", "show", ".", "--for", "Shop.OrderService.Place", "--format", "json").Out).RootElement;
        Assert.False(json.GetProperty("index").GetProperty("coversHead").GetBoolean());
        Assert.Contains(json.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("later commits are not covered"));
        // Both requirements now predate the change.
        Assert.All(json.GetProperty("requirements").EnumerateArray(), r => Assert.True(r.GetProperty("changedSince").GetBoolean()));
    }
}
