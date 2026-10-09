namespace DocWizz.Tests.Unit;

public class HtmlVersionTests
{
    [Fact]
    public void Missing_or_different_version_triggers_refresh_and_same_version_does_not()
    {
        using var repo = Repos.Empty();
        var output = Path.Combine(repo.Root, "docs");

        Assert.True(HtmlVersion.NeedsRefresh(output, "1.2.3"));
        HtmlVersion.Record(output, "1.2.3");
        Assert.Equal("1.2.3", File.ReadAllText(HtmlVersion.PathFor(output)).Trim());
        Assert.False(HtmlVersion.NeedsRefresh(output, "1.2.3"));
        Assert.True(HtmlVersion.NeedsRefresh(output, "1.2.4"));

        HtmlVersion.Record(output, "1.2.4");
        Assert.False(HtmlVersion.NeedsRefresh(output, "1.2.4"));
    }
}
