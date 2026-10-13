using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocWizz.Tests.Journey;

// Stands in for the exporters DocWizz reads (#166, #172): a git repository built commit by commit, then
// `gitwizz.history/v2` written from its real diffs (git diff -U0 -M, as gitwizz history export does) and
// `ado.requirement-context/v1` work items. Links are whatever the test says they are: this is a fake, it never asks a
// server. GitwizzExampleJourneyTests checks the import against gitwizz's own export instead.
sealed class FakeHistory : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("docwizz-history-").FullName;
    readonly List<(string Sha, string Subject, object ProviderLinks, object PullRequest)> commits = [];

    public FakeHistory() { Git("init", "-q"); }

    public string Git(params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["-c", "user.name=DocWizz", "-c", "user.email=docwizz@example.invalid", "-c", "commit.gpgsign=false", .. args]) info.ArgumentList.Add(a);
        info.Environment["GIT_AUTHOR_DATE"] = info.Environment["GIT_COMMITTER_DATE"] = "2026-01-01T00:00:00Z";
        using var p = Process.Start(info)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(TimeSpan.FromSeconds(30)), $"git {string.Join(' ', args)} did not finish");
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {stderr.Result}");
        return stdout.Result;
    }

    public void Write(string path, string text)
    {
        var full = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text.Replace("\r\n", "\n"));
    }

    // What the provider links to a commit: verified with these work items, or a status without an answer.
    public static object Verified(params int[] ids) => new
    {
        status = "verified", items = ids.Select(id => new { workItem = $"AB#{id}", source = "provider", kind = "commit-link", evidence = "ado commit context: commit-workitem" }),
        problem = (string?)null,
    };
    public static object Status(string status) => new { status, items = (object?)null, problem = $"ado commit context: {status}" };

    // The pull request a commit came in with.
    public static object Pr(int id, string resolution = "exact") => new { id = $"#{id}", resolution, basis = new[] { "merged-branch", "provider" }, candidates = Array.Empty<string>() };
    static readonly object NoPr = new { id = (string?)null, resolution = "unknown", basis = Array.Empty<string>(), candidates = Array.Empty<string>() };

    // A completed pull request and the work items the provider links to it.
    public static object PullRequest(int id, string status = "confirmed", params int[] workItems) => new
    {
        id = $"#{id}", title = $"PR {id}", url = (string?)null, target = "main", status, strategy = "squash", mergeCommit = (string?)null,
        commits = Array.Empty<string>(),
        workItems = workItems.Select(w => new { workItem = $"AB#{w}", source = "provider", kind = "pr-link", evidence = "ado pr context" }),
        workItemsStatus = "verified", problem = (string?)null,
    };

    // Commits everything with the given links; returns the full SHA.
    public string Commit(string message, object? providerLinks = null, object? pullRequest = null)
    {
        Git("add", "-A");
        Git("commit", "-qm", message);
        var sha = Git("rev-parse", "HEAD").Trim();
        commits.Add((sha, message, providerLinks ?? Verified(), pullRequest ?? NoPr));
        return sha;
    }

    static readonly Regex AbMention = new(@"AB#(\d+)");

    static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@");

    // The commit's files as gitwizz exports them: path, old path on a rename, status, granularity and zero-context ranges.
    List<object> Changes(string sha)
    {
        var diff = Git("show", "--format=", "-U0", "-M", "--no-color", sha);
        var changes = new List<object>();
        string? path = null, oldPath = null, status = null;
        var hunks = new List<object>();
        void Flush()
        {
            if (path is not null)
                changes.Add(new { path, oldPath, status, similarity = oldPath is null ? (int?)null : 100, granularity = hunks.Count == 0 ? "file" : "lines", ranges = hunks.ToList() });
            hunks.Clear();
        }
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git "))
            {
                Flush();
                var m = Regex.Match(line, @"^diff --git a/(.+) b/(.+)$");
                (path, oldPath, status) = (m.Groups[2].Value, null, "modified");
            }
            else if (line.StartsWith("new file mode")) status = "added";
            else if (line.StartsWith("deleted file mode")) status = "deleted";
            else if (line.StartsWith("rename from ")) (oldPath, status) = (line["rename from ".Length..], "renamed");
            else if (HunkHeader.Match(line) is { Success: true } h)
                hunks.Add(new
                {
                    oldStart = int.Parse(h.Groups[1].Value), oldCount = h.Groups[2].Success ? int.Parse(h.Groups[2].Value) : 1,
                    newStart = int.Parse(h.Groups[3].Value), newCount = h.Groups[4].Success ? int.Parse(h.Groups[4].Value) : 1,
                });
        }
        Flush();
        return changes;
    }

    // gitwizz.history/v2 over every commit so far, newest first. Message mentions (AB#n) come from the subject.
    public string History(object[]? pullRequests = null, string schema = "gitwizz.history/v2") => JsonSerializer.Serialize(new
    {
        schema, provider = "azure-devops", repository = "https://ado.example.invalid/shop",
        range = new { since = (string?)null, sinceSha = (string?)null, until = "main", untilSha = commits[^1].Sha, limit = 200, commits = commits.Count, truncated = false, shallow = false },
        sources = new { commitMessages = "complete", providerCommitLinks = "fake", pullRequests = "fake", pullRequestWorkItems = "fake" },
        commits = Enumerable.Reverse(commits).Select(c => new
        {
            sha = c.Sha, parents = Array.Empty<string>(), subject = c.Subject, committedAt = "2026-01-01T00:00:00+00:00", kind = "commit",
            files = Changes(c.Sha),
            workItems = AbMention.Matches(c.Subject).Select(m => new { workItem = m.Value, source = "commit-message", kind = "ab", evidence = m.Value }),
            providerLinks = c.ProviderLinks, pullRequest = c.PullRequest,
        }),
        pullRequests = pullRequests ?? [],
        warnings = Array.Empty<string>(),
    }, new JsonSerializerOptions { WriteIndented = true });

    public static string WorkItem(int id, string title, int revision = 1, params string[] criteria) => JsonSerializer.Serialize(new
    {
        schema = "ado.requirement-context/v1", id, revision, currentRevision = revision, isCurrent = true,
        url = $"https://ado.example.invalid/_apis/wit/workItems/{id}", type = "User Story", state = "Closed", title,
        changedDate = "2026-01-01T00:00:00Z",
        description = new { sourceField = "System.Description", html = $"<p>{title}</p>", text = title },
        acceptanceCriteria = new { sourceField = "Microsoft.VSTS.Common.AcceptanceCriteria", html = "", text = string.Join("\n", criteria), items = criteria.Select((c, i) => new { index = i, text = c }) },
        relations = new { status = "available", items = Array.Empty<object>() },
        warnings = Array.Empty<string>(),
    });

    public void Dispose()
    {
        // git makes its objects read-only; clear that so the folder can go.
        foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(Root, recursive: true);
    }
}
