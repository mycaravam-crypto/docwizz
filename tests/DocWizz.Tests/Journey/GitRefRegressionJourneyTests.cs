using System.Diagnostics;

namespace DocWizz.Tests.Journey;

// #133: a historic group-B CI run intermittently reported "not a git repo or unknown ref: HEAD".
// Exercise the actual CLI process from the fixture's cwd with real commits and preserve failure details.
public class GitRefRegressionJourneyTests
{
    static Docwizz.Result RunFrom(string cwd, params string[] args) =>
        Docwizz.Run(cwd: cwd, env: null, args: args);

    static void Git(string root, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(20)),
            "git command did not finish: " + string.Join(" ", arguments));
        Assert.True(process.ExitCode == 0,
            $"git {string.Join(" ", arguments)} exited {process.ExitCode}: {stderr.Result}\n{stdout.Result}");
    }

    [Fact]
    public void Committed_head_and_parent_are_resolved_from_the_repo_directory()
    {
        using var repo = new Sources(
            ("Tool.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
            ("Logic.cs", """
                /// <summary>Domain utility.</summary>
                public class Logic { public int Value() => 1; }
                """)
        );
        Git(repo.Root, "init", "-q");
        Git(repo.Root, "add", "-A");
        Git(repo.Root, "-c", "user.name=DocWizz", "-c", "user.email=docwizz@example.invalid", "-c", "commit.gpgsign=false", "commit", "-qm", "base");

        var first = RunFrom(repo.Root, "check", ".", "--since", "HEAD");
        Assert.True(first.Exit == 0, first.ToString());
        Assert.Contains("check: PASS", first.Out);

        File.WriteAllText(Path.Combine(repo.Root, "NewType.cs"), """
            /// <summary>Additional documented type.</summary>
            public class NewType { public string Name() => "ok"; }
            """);
        Git(repo.Root, "add", "-A");
        Git(repo.Root, "-c", "user.name=DocWizz", "-c", "user.email=docwizz@example.invalid", "-c", "commit.gpgsign=false", "commit", "-qm", "change");
        Git(repo.Root, "rev-parse", "--verify", "HEAD");
        Git(repo.Root, "rev-parse", "--verify", "HEAD~1");

        // "diff HEAD~1 HEAD" deliberately omits [dir]. The CLI must resolve "." against cwd.
        var diff = RunFrom(repo.Root, "diff", "HEAD~1", "HEAD");
        Assert.True(diff.Exit == 0, diff.ToString());
        Assert.Contains("vs HEAD~1, at HEAD", diff.Out);
        Assert.Contains("NewType", diff.Out);
        Docwizz.NoStackTrace(diff);

        // Ensure a subsequent check still sees a valid HEAD and doesn't confuse a generated model with a git ref.
        var since = RunFrom(repo.Root, "check", ".", "--since", "HEAD");
        Assert.True(since.Exit == 0, since.ToString());
        Assert.Contains("check: PASS", since.Out);
    }

    [Fact]
    public void Missing_git_repository_reports_a_clean_reference_error()
    {
        using var repo = new Sources(("README.md", "# not a git repository"));
        var diff = RunFrom(repo.Root, "diff", "HEAD~1", "HEAD");
        Assert.Equal(1, diff.Exit);
        Assert.Contains("not a git repo or unknown ref", diff.Err);
        Docwizz.NoStackTrace(diff);
    }
}
