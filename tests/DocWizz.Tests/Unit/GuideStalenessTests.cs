using Xunit;

public class GuideStalenessTests
{
    static JourneyEvidence.Guide Guide(int wi, int rev) =>
        new($"wi-{wi}-case-1-aaaaaaaaaaaa", "Sign in", "/login", wi, rev, 1, 1, 1, "P", [], []);

    static RequirementIndex.Index Index(params (int Wi, int? Rev)[] items) =>
        new("s", "f", default!, [], [], [.. items.Select(i => new RequirementIndex.WorkItem(new RequirementId(i.Wi), i.Rev,
            null, null, null, null, null, null, null, null, null, null, [], "d", []))], []);

    [Fact]
    public void Flags_only_guides_older_than_the_export()
    {
        var guides = new[] { Guide(1, 2), Guide(2, 5), Guide(3, 1) };
        var stale = GuideStaleness.Stale(guides, Index((1, 3), (2, 5), (3, null)));
        Assert.Equal(1, Assert.Single(stale).Current - 2);
        Assert.Contains("WI-1 captured at revision 2, now 3", GuideStaleness.Section(guides, Index((1, 3))));
    }

    [Fact]
    public void Nothing_is_flagged_without_requirements_or_when_current()
    {
        Assert.Equal("", GuideStaleness.Section([Guide(1, 2)], null));
        Assert.Equal("", GuideStaleness.Section([Guide(1, 2)], Index((1, 2))));
    }
}
