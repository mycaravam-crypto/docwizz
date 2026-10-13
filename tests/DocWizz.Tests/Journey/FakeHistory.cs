using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocWizz.Tests.Journey;

// Stands in for the exporters DocWizz reads (#166): a git repository built commit by commit, then `gitwizz.history/v1`
// written from its real diffs (git diff -U0, as GitWizz would) and `ado.requirement-context/v1` work items. Links are
// whatever the test says they are: this is a fake, it never asks a server.
sealed class FakeHistory : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("docwizz-history-").FullName;
    readonly List<(string Sha, object WorkItems, object[] PullRequests)> commits = [];

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

    public static object Verified(params int[] ids) => new { status = "verified", items = ids.Select(id => new { id, source = "commit-workitem" }) };

    // Commits everything with the given links; returns the full SHA.
    public string Commit(string message, object? workItems = null, params object[] pullRequests)
    {
        Git("add", "-A");
        Git("commit", "-qm", message);
        var sha = Git("rev-parse", "HEAD").Trim();
        commits.Add((sha, workItems ?? Verified(), pullRequests));
        return sha;
    }

    static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@");

    // The commit's changes as GitWizz exports them: path, old path on a rename, status and zero-context hunks.
    List<object> Changes(string sha)
    {
        var diff = Git("show", "--format=", "-U0", "-M", "--no-color", sha);
        var changes = new List<object>();
        string? path = null, oldPath = null, status = null;
        var hunks = new List<object>();
        void Flush()
        {
            if (path is not null) changes.Add(new { path, oldPath, status, hunks = hunks.ToList() });
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
                    oldStart = int.Parse(h.Groups[1].Value), oldLines = h.Groups[2].Success ? int.Parse(h.Groups[2].Value) : 1,
                    newStart = int.Parse(h.Groups[3].Value), newLines = h.Groups[4].Success ? int.Parse(h.Groups[4].Value) : 1,
                });
        }
        Flush();
        return changes;
    }

    // gitwizz.history/v1 over every commit so far, oldest first.
    public string History(object[]? pullRequests = null, string schema = "gitwizz.history/v1") => JsonSerializer.Serialize(new
    {
        schema,
        repository = new { provider = "azure-devops", name = "shop" },
        range = new { @base = (string?)null, head = "main", headSha = commits[^1].Sha },
        commits = commits.Select(c => new { sha = c.Sha, changes = Changes(c.Sha), workItems = c.WorkItems, pullRequests = c.PullRequests }),
        pullRequests = pullRequests ?? [],
        warnings = Array.Empty<string>(),
        truncated = false,
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
