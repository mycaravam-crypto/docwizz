namespace DocWizz.Tests.Journey;

public class HtmlVersionJourneyTests
{
    [Fact]
    public void Refreshes_html_only_when_generator_version_changes_and_preserves_markdown()
    {
        using var repo = Repos.ConsoleApp();
        var outDir = Path.Combine(repo.Root, "docs");
        var first = Docwizz.Run("generate", repo.Root, "--refresh-html-on-version-change");
        Assert.Equal(0, first.Exit);
        var html = Path.Combine(outDir, "index.html");
        var markdown = Path.Combine(outDir, "index.md");
        Assert.True(File.Exists(html));
        Assert.Equal(AppVersion.Number, File.ReadAllText(HtmlVersion.PathFor(outDir)).Trim());

        var htmlStamp = File.GetLastWriteTimeUtc(html);
        var mdStamp = File.GetLastWriteTimeUtc(markdown);
        var unchanged = Docwizz.Run("generate", repo.Root, "--refresh-html-on-version-change");
        Assert.Equal(0, unchanged.Exit);
        Assert.Equal(htmlStamp, File.GetLastWriteTimeUtc(html));

        File.WriteAllText(HtmlVersion.PathFor(outDir), "0.0.0\n");
        var refreshed = Docwizz.Run("generate", repo.Root, "--refresh-html-on-version-change");
        Assert.Equal(0, refreshed.Exit);
        Assert.Contains("changed", refreshed.Out);
        Assert.Equal(AppVersion.Number, File.ReadAllText(HtmlVersion.PathFor(outDir)).Trim());
        Assert.Equal(mdStamp, File.GetLastWriteTimeUtc(markdown));
    }

    [Fact]
    public void Forced_progress_is_visible_only_on_stderr()
    {
        using var repo = Repos.ConsoleApp();
        var result = Docwizz.Run("generate", repo.Root, "--progress");
        Assert.Equal(0, result.Exit);
        Assert.Contains("7/7 metadata", result.Err);
        Assert.DoesNotContain("docwizz [", result.Out);
    }
}
