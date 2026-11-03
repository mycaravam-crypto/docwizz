using System.Text.Json;

namespace DocWizz.Tests.Journey;

public class VersionHistoryJourneyTests
{
    [Fact]
    public void Optional_version_history_is_linked_in_html_and_search()
    {
        using var repo = Repos.ConsoleApp();
        var absent = Docwizz.Run("generate", repo.Root, "--html");
        Assert.Equal(0, absent.Exit);
        Assert.False(File.Exists(Path.Join(repo.Root, "docs", "versions.md")));

        var source = Path.Join(repo.Root, "docs", "architecture", "versions.json");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, JsonSerializer.Serialize(new
        {
            schema = "gitwizz.version-history/v2",
            shallow = false,
            warnings = Array.Empty<string>(),
            versions = new[]
            {
                new
                {
                    version = "2.0.0", status = "released",
                    commitSha = new string('a', 40), committedAt = "2026-09-01T00:00:00+00:00",
                    taggedAt = default(string?), bases = Array.Empty<object>(),
                    truncated = false, warnings = Array.Empty<string>(),
                    pullRequests = new[]
                    {
                        new { id = "#9", description = "export CSV", category = "feature", breaking = false }
                    },
                    changes = new[]
                    {
                        new { sha = new string('b', 40), description = "export CSV",
                            kind = "commit", pullRequest = "#9", category = "feature", breaking = false }
                    }
                }
            }
        }));

        var generated = Docwizz.Run("generate", repo.Root, "--html");
        Assert.Equal(0, generated.Exit);
        var page = File.ReadAllText(Path.Join(repo.Root, "docs", "versions.md"));
        Assert.Contains("## 2.0.0", page);
        Assert.Contains("export CSV (#9)", page);
        Assert.DoesNotContain("`bbbbbbbb`", page); // PR's commit must not be listed twice.
        Assert.Contains("versions.html", File.ReadAllText(Path.Join(repo.Root, "docs", "index.html")));
        Assert.Contains("versions.md", File.ReadAllText(Path.Join(repo.Root, "docs", "search.json")));

        File.Delete(source);
        Assert.Equal(0, Docwizz.Run("generate", repo.Root, "--html").Exit);
        Assert.False(File.Exists(Path.Join(repo.Root, "docs", "versions.md")));
        Assert.False(File.Exists(Path.Join(repo.Root, "docs", "versions.html")));
        Assert.DoesNotContain("versions.html", File.ReadAllText(Path.Join(repo.Root, "docs", "index.html")));
    }

    [Fact]
    public void Version_history_keeps_other_categories_and_base_metadata()
    {
        using var repo = Repos.ConsoleApp();
        var file = Path.Join(repo.Root, "docs", "architecture", "versions.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """
            {
              "schema": "gitwizz.version-history/v2",
              "versions": [{
                "version": "2.0.0",
                "status": "released",
                "commitSha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "committedAt": "2026-09-01T00:00:00+00:00",
                "bases": [{"version": "1.9.0"}],
                "pullRequests": [{"id": "#7", "description": "update docs", "category": "docs", "breaking": false}],
                "changes": [{"sha": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                             "description": "refactor component", "category": "refactor",
                             "kind": "commit", "pullRequest": null, "breaking": false}]
              }]
            }
            """);
        var history = VersionHistoryPage.Load(repo.Root);
        Assert.NotNull(history);
        Assert.Contains("Commit: `aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa` · Based on: 1.9.0", history.Markdown);
        Assert.Contains("### Other changes", history.Markdown);
        Assert.Contains("update docs (#7)", history.Markdown);
        Assert.Contains("refactor component", history.Markdown);
    }

    [Fact]
    public void Version_history_parser_rejects_unknown_schema_and_marks_incomplete_exports()
    {
        using var repo = Repos.ConsoleApp();
        var file = Path.Join(repo.Root, "docs", "architecture", "versions.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{\"schema\":\"unknown\",\"versions\":[]}");
        Assert.Throws<ArgumentException>(() => VersionHistoryPage.Load(repo.Root));

        File.WriteAllText(file, "{\"schema\":\"gitwizz.version-history/v2\",\"versions\":[],\"shallow\":true}");
        var history = VersionHistoryPage.Load(repo.Root);
        Assert.NotNull(history);
        Assert.Contains("shallow", history.Markdown);
        Assert.Contains("No releases", history.Markdown);

        File.WriteAllText(file, "{\"schema\":\"gitwizz.version-history/v2\",\"versions\":[{\"version\":\"1.0.0\",\"status\":\"released\",\"commitSha\":\"short\",\"committedAt\":\"2026-09-01T00:00:00+00:00\",\"changes\":[{\"sha\":\"short\",\"description\":\"fix\",\"kind\":\"commit\",\"category\":\"fix\",\"breaking\":false}]}]}");
        Assert.Contains("`short`", VersionHistoryPage.Load(repo.Root)!.Markdown);
    }
}
