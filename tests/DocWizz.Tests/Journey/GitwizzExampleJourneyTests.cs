using System.Diagnostics;
using System.Text.Json;

namespace DocWizz.Tests.Journey;

// #172: `requirements import` reads what `gitwizz history export` really writes. Golden/gitwizz/history-v2-example.json
// is gitwizz's docs/history/example.json, unchanged: the export of a scripted repository (gitwizz
// tests/Gitwizz.Tests/HistoryContractTests.cs). The repository is rebuilt here with the same commands, identity and
// clock, so its SHAs are the export's and the import maps real code, with no adapter in between.
// To refresh: copy gitwizz's docs/history/example.json over the golden file; a changed scenario fails the SHA check.
public class GitwizzExampleJourneyTests
{
    static string Golden()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "DocWizz.Tests", "Golden", "gitwizz", "history-v2-example.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("gitwizz history example not found in the test checkout");
    }

    // gitwizz's scenario, step for step: git with a fixed identity and a clock that ticks one minute per git call.
    sealed class Scenario : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("docwizz-gitwizz-").FullName;
        int tick;

        public string G(params string[] args)
        {
            var date = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero).AddMinutes(tick++).ToString("yyyy-MM-ddTHH:mm:ssZ");
            var info = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) info.ArgumentList.Add(a);
            foreach (var (k, v) in new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Ada", ["GIT_AUTHOR_EMAIL"] = "ada@example.com", ["GIT_AUTHOR_DATE"] = date,
                ["GIT_COMMITTER_NAME"] = "Ada", ["GIT_COMMITTER_EMAIL"] = "ada@example.com", ["GIT_COMMITTER_DATE"] = date,
                ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            }) info.Environment[k] = v;
            using var p = Process.Start(info)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            Assert.True(p.WaitForExit(TimeSpan.FromSeconds(30)), $"git {string.Join(' ', args)} did not finish");
            Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {stderr.Result}");
            return stdout.Result.Trim();
        }

        public void Write(string f, string c)
        {
            var p = Path.Combine(Root, f);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, c);
        }

        public string Commit(string m) { G("add", "-A"); G("commit", "-q", "--no-gpg-sign", "-m", m); return G("rev-parse", "HEAD"); }

        public Scenario()
        {
            G("init", "-q", "-b", "main");
            G("remote", "add", "origin", "https://tfs.example/coll/Platform/_git/backend");
            Write("src/Tax.cs", "class Tax\n{\n    decimal Rate() { return 0.19m; }\n\n    decimal Round(decimal v) { return v; }\n}\n");
            Write("README.md", "# demo\n");
            Commit("init");
            G("tag", "v1.0");
            G("checkout", "-q", "-b", "feature/round");
            Write("src/Tax.cs", "class Tax\n{\n    decimal Rate() { return 0.19m; }\n\n    decimal Round(decimal v) { return Math.Round(v, 2); }\n}\n");
            Commit("Round half up AB#100");
            Directory.CreateDirectory(Path.Combine(Root, "docs"));
            G("mv", "README.md", "docs/README.md");
            Commit("Move readme");
            File.WriteAllBytes(Path.Combine(Root, "logo.png"), [0, 1, 2, 0, 255]);
            Commit("Logo");
            G("checkout", "-q", "main");
            G("merge", "-q", "--no-ff", "--no-gpg-sign", "-m", "Merged PR 7: Rounding", "feature/round");
            G("rev-parse", "HEAD");
            Write("src/Tax.cs", "class Tax\n{\n    decimal Rate() { return 0.07m; }\n\n    decimal Round(decimal v) { return Math.Round(v, 2); }\n}\n");
            Commit("Merged PR 9: Reduced rate");
            Write("src/Tax.cs", "class Tax\n{\n    decimal Rate() { return 0.07m; }\n}\n");
            Commit("Drop Round, see #12");
            G("checkout", "-q", "-b", "dev");
            G("checkout", "-q", "-b", "shared");
            Write("src/Shared.cs", "class Shared { }\n");
            Commit("Shared helper");
            G("checkout", "-q", "main");
            G("merge", "-q", "--no-ff", "--no-gpg-sign", "-m", "Merged PR 10: shared to main", "shared");
            G("checkout", "-q", "dev");
            G("merge", "-q", "--no-ff", "--no-gpg-sign", "-m", "Merged PR 11: shared to dev", "shared");
            G("checkout", "-q", "main");
            G("merge", "-q", "--no-ff", "--no-gpg-sign", "-m", "Merge branch dev", "dev");
        }

        public void Dispose()
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
    }

    static Docwizz.Result Run(string cwd, params string[] args) => Docwizz.Run(cwd: cwd, env: null, args: args);

    static List<(int Id, string Status)> Requirements(string root, string symbol)
    {
        var show = Run(root, "requirements", "show", ".", "--for", symbol, "--format", "json");
        Assert.True(show.Exit == 0, show.ToString());
        return [.. JsonDocument.Parse(show.Out).RootElement.GetProperty("requirements").EnumerateArray()
            .Select(r => (r.GetProperty("id").GetInt32(), r.GetProperty("status").GetString()!))];
    }

    [Fact]
    public void The_real_gitwizz_export_imports_without_an_adapter_and_keeps_every_link_as_strong_as_it_is()
    {
        using var repo = new Scenario();
        var golden = Golden();
        var export = JsonDocument.Parse(File.ReadAllText(golden)).RootElement;
        // The same repository: otherwise everything below would test a coincidence.
        Assert.Equal(export.GetProperty("range").GetProperty("untilSha").GetString(), repo.G("rev-parse", "HEAD"));

        var import = Run(repo.Root, "requirements", "import", ".", golden);
        Assert.True(import.Exit == 0, import.ToString());
        Assert.Contains("10 commits", import.Out);
        Assert.Contains("PR #9 is unconfirmed", import.Err);

        // The squash commit's provider link AB#4712 is the only confirmed link.
        Assert.Equal([(4712, "confirmed")], Requirements(repo.Root, "Tax.Rate"));
        // The pure deletion that mentions #12 (AB#12 with Azure DevOps) touched the class around the gap: a candidate.
        Assert.Equal([(12, "candidate")], Requirements(repo.Root, "cs:Tax"));
        // Shared.cs came in with PR 10 or 11 (ambiguous, both unconfirmed): nothing, and not "no link" either.
        var shared = JsonDocument.Parse(Run(repo.Root, "requirements", "show", ".", "--for", "cs:Shared", "--format", "json").Out).RootElement;
        Assert.Equal(("unavailable", false), (shared.GetProperty("outcome").GetString(), shared.GetProperty("complete").GetBoolean()));

        // Round is gone at HEAD: its history is found from the work items. The AB#100 mention (provider lookup denied)
        // and PR 7's AB#4711 are candidates; neither is ever confirmed.
        var mention = Run(repo.Root, "requirements", "show", ".", "--work-item", "100");
        Assert.Contains("cs:Tax.Round(decimal) [candidate, not in the current code]", mention.Out);
        var pr = Run(repo.Root, "requirements", "show", ".", "--work-item", "4711");
        Assert.Contains("cs:Tax.Round(decimal) [candidate, not in the current code]", pr.Out);
        Assert.DoesNotContain("confirmed", mention.Out + pr.Out);

        // The rename and the binary file are file-level only: no symbol anywhere carries them.
        var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo.Root, "docs", ".docwizz", "requirements.json"))).RootElement;
        Assert.Equal("docwizz.requirement-index/v2", index.GetProperty("schema").GetString());
        Assert.DoesNotContain(index.GetProperty("commits").EnumerateArray().SelectMany(c => c.GetProperty("touches").EnumerateArray()),
            t => t.GetProperty("file").GetString() is "logo.png" or "docs/README.md");

        // Repeatable: the same inputs are recognized and the index is not rewritten.
        Assert.Contains("unchanged", Run(repo.Root, "requirements", "import", ".", golden).Out);
    }
}
