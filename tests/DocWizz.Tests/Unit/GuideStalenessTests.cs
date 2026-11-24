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
        Assert.Equal("revision 3", Assert.Single(stale).Now);
        Assert.Contains("WI-1 captured at revision 2, now revision 3", GuideStaleness.Section(guides, Index((1, 3))));
    }

    [Fact]
    public void Nothing_is_flagged_without_requirements_or_when_current()
    {
        Assert.Equal("", GuideStaleness.Section([Guide(1, 2)], null));
        Assert.Equal("", GuideStaleness.Section([Guide(1, 2)], Index((1, 2))));
    }

    static JourneyEvidence.Guide Captured(int wi, string? repository = null) =>
        new("sign-in", "Sign in", "/login", wi, 0, 0, 0, 0, "", [], [], JourneyFile: "01-sign-in.yaml", Repository: repository);

    static RequirementIndex.Index Github(int number, string changed) =>
        new("s", "f", default!, [], [], [new RequirementIndex.WorkItem(new RequirementId(number, "acme/shop"), null,
            null, null, null, null, null, null, changed, null, null, null, [], "d", [])], []);

    [Fact]
    public void A_captured_guide_records_its_github_issue_and_goes_stale_when_the_issue_is_updated()
    {
        var guide = GuideStaleness.Versioned(Captured(12, "acme/shop"), Github(12, "2026-11-01T10:00:00Z"));
        Assert.Equal("2026-11-01T10:00:00Z", guide.Changed);
        Assert.Empty(GuideStaleness.Stale([guide], Github(12, "2026-11-01T11:00:00+01:00")));   // same instant, other offset
        Assert.Empty(GuideStaleness.Stale([guide], Github(13, "2026-12-01T10:00:00Z")));          // another issue
        Assert.Contains("github#12 captured at update 2026-11-01T10:00:00Z, now update 2026-11-02T08:00:00Z",
            GuideStaleness.Section([guide], Github(12, "2026-11-02T08:00:00Z")));
        Assert.Equal("github#12 changed since the capture (update 2026-11-01T10:00:00Z, now update 2026-11-02T08:00:00Z)",
            GuideStaleness.Reason(guide, Github(12, "2026-11-02T08:00:00Z"), codeChanged: false));
    }

    [Fact]
    public void A_captured_guide_records_its_ado_revision_and_goes_stale_when_the_item_changes()
    {
        var guide = GuideStaleness.Versioned(Captured(1), Index((1, 4)));
        Assert.Equal(4, guide.WorkItemRevision);
        Assert.Empty(GuideStaleness.Stale([guide], Index((1, 4))));
        Assert.Equal("revision 5", Assert.Single(GuideStaleness.Stale([guide], Index((1, 5)))).Now);
        // A GitHub issue with the same number is a different requirement.
        Assert.Empty(GuideStaleness.Stale([GuideStaleness.Versioned(Captured(1, "acme/shop"), Index((1, 4)))], Index((1, 9))));
    }

    [Fact]
    public void A_guide_captured_before_its_requirement_was_imported_is_listed_as_not_tracked()
    {
        var guide = GuideStaleness.Versioned(Captured(12, "acme/shop"), null);
        Assert.Contains("## Requirement not tracked", GuideStaleness.Section([guide], Github(12, "2026-11-02T08:00:00Z")));
        Assert.Contains("- [Sign in](sign-in.md): github#12", GuideStaleness.Section([guide], null));
        Assert.Equal("", GuideStaleness.Section([Captured(0)], null));   // no requirement: nothing to track
    }
}
