namespace DocWizz.Tests.Unit;

// #311: a journey file is a guide's reviewed script; what it cannot express fails before a browser starts.
public class JourneyCaptureTests
{
    static JourneyCapture.Journey Parse(string yaml)
    {
        using var repo = new Sources(("journeys/01 Sign in.yaml", yaml));
        return JourneyCapture.Parse(repo.Files[0]);
    }

    const string SignIn = """
        title: Sign in
        requirement: 7
        intro: You need an account.
        result: The start page opens.
        steps:
          - open: /login
          - fill: Password
            with: ${DOCWIZZ_TEST_PASSWORD}
            note: |
              Ask your administrator
              when you have none.
          - click: css=button[type=submit]
        """;

    [Fact]
    public void A_journey_file_becomes_steps_and_never_prints_what_is_filled_in()
    {
        var journey = Parse(SignIn);
        Assert.Equal(("01-sign-in", "Sign in", 7), (journey.Id, journey.Title, journey.WorkItem));
        Assert.Equal(["Open /login", "Fill in “Password”", "Select “the highlighted control”"], journey.Steps.Select(JourneyCapture.Instruction));
        Assert.Equal("Ask your administrator when you have none.", journey.Steps[1].Note);

        // The value comes from the environment at capture time; unset, the journey fails instead of typing the placeholder.
        Assert.Contains("DOCWIZZ_TEST_PASSWORD is not set", Assert.Throws<InvalidDataException>(() => JourneyCapture.Value(journey.Steps[1])).Message);
        Assert.Equal("plain", JourneyCapture.Value(new("fill", "Name", "plain", "")));
    }

    [Theory]
    [InlineData("steps:\n  - open: /a", "needs a title")]
    [InlineData("title: T\nsteps:\n  - click: Save", "first step must open a page")]
    [InlineData("title: T\nsteps:\n  - open: https://elsewhere.test/", "open needs a path")]
    [InlineData("title: T\nsteps:\n  - open: /a\n  - click: Save\n    fill: Name", "exactly one of open, click or fill")]
    [InlineData("title: T\nsteps:\n  - open: /a\n  - click: Save\n    typo: x", "exactly one of open, click or fill")]
    [InlineData("title: T\nsteps:\n  - open: /a\n  - fill: Name", "fill needs it")]
    [InlineData("title: T\nsteps:\n  - open: /a\n  - click: Save\n    with: x", "'with' belongs to fill")]
    [InlineData("title: T\nunknown: 1\nsteps:\n  - open: /a", "01 Sign in.yaml")]
    public void An_invalid_journey_file_names_its_problem(string yaml, string problem) =>
        Assert.Contains(problem, Assert.Throws<InvalidDataException>(() => Parse(yaml)).Message);

    [Theory]
    [InlineData("sign-in.yaml", "Sign_in.yaml", "would write the same guide (sign-in)")]
    [InlineData("a.yaml", "index.yaml", "'index' is the name of a generated page")]
    public void Journey_files_that_would_overwrite_a_page_are_rejected(string first, string second, string problem)
    {
        using var repo = new Sources((first, "title: A\nsteps:\n  - open: /a"), (second, "title: B\nsteps:\n  - open: /b"));
        Assert.Contains(problem, Assert.Throws<InvalidDataException>(() => JourneyCapture.Load(repo.Root)).Message);
    }

    [Fact]
    public void A_captured_guide_explains_before_during_and_after_and_names_its_journey_file()
    {
        var guide = new JourneyEvidence.Guide("sign-in", "Sign in", "/login", 0, 0, 0, 0, 0, "", [],
            [new(1, "Open /login", "", "images/guide-sign-in-step-01.png", Note: "The page appears when you are signed out.")],
            "You need an account.", "The start page opens.", "images/guide-sign-in-result.png", "01-sign-in.yaml");
        var markdown = JourneyEvidence.Markdown(guide);
        Assert.Contains("`/login`\n\nYou need an account.\n\n1. **Open /login**\n\n   The page appears when you are signed out.\n\n   ![Step 1]", markdown);
        Assert.Contains("**Result:** The start page opens.\n\n![Result](images/guide-sign-in-result.png)", markdown);
        Assert.Contains("Captured by docwizz from `01-sign-in.yaml`", markdown);
        Assert.DoesNotContain("WI-", markdown);   // no requirement given
        Assert.DoesNotContain("Captured by docwizz", JourneyEvidence.Manual([guide]));

        // A captured guide has no work-item revision, so it is never "older" than the export.
        var index = new RequirementIndex.Index(RequirementIndex.Schema, "f", new(RequirementIndex.HistorySchema, "d", "ado", null, null, null, null, false, false, null, []),
            [], [], [new(7, 2, 2, true, "User Story", "Active", "Sign in", null, null, null, null, null, [], "x", [])], []);
        Assert.Empty(GuideStaleness.Stale([guide with { WorkItem = 7 }], index));
    }

    [Fact]
    public void Captured_pictures_are_removed_with_their_guide_and_manual_pictures_stay()
    {
        using var repo = new Sources(("docs/user-guides/images/guide-sign-in-step-01.png", ""), ("docs/user-guides/images/guide-sign-in-result.png", ""),
            ("docs/user-guides/images/team-photo.png", ""));
        JourneyEvidence.CleanupImages(Path.Combine(repo.Root, "docs"), []);
        Assert.Equal(["team-photo.png"], Directory.GetFiles(Path.Combine(repo.Root, "docs/user-guides/images")).Select(Path.GetFileName));
    }
}
